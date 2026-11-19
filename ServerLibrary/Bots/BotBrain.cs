using Library;
using Library.SystemModels;
using Server.DBModels;
using Server.Envir;
using Server.Models;
using Server.Models.AutoPath;
using Server.Models.Monsters;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using C = Library.Network.ClientPackets;

namespace Server.Bots
{
    public enum BotState
    {
        /// <summary>待机（刚上线 / 打累了）。</summary>
        Idle = 0,
        /// <summary>朝刷怪点走。</summary>
        GoHunt = 1,
        /// <summary>附近有怪，靠近并攻击。</summary>
        Fight = 2,
        /// <summary>捡地上的掉落物。</summary>
        Loot = 3,
        /// <summary>死亡，等复活。</summary>
        Dead = 4,
        /// <summary>回城买药（红药快没了）。</summary>
        Restock = 5,
        /// <summary>跨图前往别的练级地图。</summary>
        Travel = 6,
        /// <summary>回城卖货 / 修理（P1-2、P1-3）。</summary>
        Trade = 7,
        /// <summary>挖取尸体（鸡 / 猪 / 牛的掉落不落地，必须挖）。</summary>
        Harvest = 8,
        /// <summary>用主城的六合圣石传送到目标图（跨图首选，避免走路被打死）。</summary>
        Teleport = 9,
        /// <summary>满级（Config.MaxLevel）后去市政厅找管理员转生，转生后从 0 级重来。</summary>
        Rebirth = 10,
    }

    /// <summary>
    /// 单个假人的大脑。由 <see cref="PlayerObject.Process"/> 每帧调用（真人不会走到这里）。
    /// <para>
    /// 设计要点：
    /// 1. **全部同步执行、必须在游戏线程内**：直接遍历 <c>Map.Objects</c>、直接调 <c>Player.Move</c>，
    ///    不开后台线程。A 端踩过的坑就是 bot 线程直调引擎，10 分钟内把主循环搞崩 3 次。
    /// 2. **动作节流**：每 250ms 才做一次决策。同图 A* 不便宜，30 个假人 × 4 次/秒是上限，
    ///    再密会明显拉高主循环耗时。
    /// 3. **不做特权**：走 <c>Move</c> / <c>Attack</c> / <c>PickUp</c>，与真人共用同一套校验。
    /// </para>
    /// </summary>
    public sealed class BotBrain
    {
        private const int ActionIntervalMs = 250;
        private const int HuntRadius = 12;
        private const int LootRadius = 8;

        /// <summary>
        /// 金币的额外捡取半径（2026-11-18 用户现场：地上有金币没捡）。
        /// <para>
        /// 金币不占格、不占重，捡一件的代价远小于漏掉的损失，所以允许比普通掉落多跑 6 格；
        /// 普通掉落仍按 <see cref="LootRadius"/>，否则假人会为了捡垃圾满图跑。
        /// </para>
        /// </summary>
        private const int CurrencyLootBonus = 6;

        /// <summary>等刷新时换点的概率（%）。同图还有别的刷怪区就别死守一处（2026-11-18）。</summary>
        private const int IdleRelocateChance = 55;

        /// <summary>两次「等刷新换点」的最小间隔（秒），避免刚换完又换、一直在路上。</summary>
        private const int IdleRelocateCooldownSeconds = 20;

        private const int AttackRange = 1;

        /// <summary>
        /// 放风筝触发距离（2026-11-14，用户现场反馈）：道士有远程技能时，
        /// 怪进入这个距离就优先后退拉开，不等贴到 AttackRange(1格)才躲。
        /// 部分怪攻击范围 2 格，在 2 格距离站着挨打 = 和不躲没区别。
        /// 取 3：覆盖 2 格攻击怪，留 1 格缓冲。
        /// </summary>
        private const int KiteTriggerRange = 3;

        /// <summary>
        /// 放风筝保持距离下限（2026-11-16，D14）：后退到 7 格即达标，不再继续后退。
        /// 法师、道士放风筝时与怪保持 7~8 格 —— 尽量远离危险（近战怪攻击范围 1~2 格），
        /// 同时仍在远程射程（Globals.MagicRange=10 格）内，能正常输出不空放。
        /// </summary>
        private const int KiteKeepMin = 7;

        /// <summary>
        /// 放风筝保持距离上限（2026-11-16，D14）：后退不超过 8 格。
        /// 超过会拉出射程边缘，打不到怪、技能空放白扣蓝；到达上限后维持距离靠走位微调，不再后退。
        /// </summary>
        private const int KiteKeepMax = 8;

        /// <summary>
        /// 放风筝所需最小蓝量（2026-11-15，生产诊断）：没蓝时放风筝毫无意义 ——
        /// 退了也放不出远程技能，只会在怪前来回跑、永不平砍
        /// （诊断证据：BOT#23 mp=0/133 连续放风筝 3 分钟 138 次，零平砍零攻击）。
        /// 有蓝才值得保持距离用远程打，没蓝直接落平砍/靠近分支。
        /// 取 1：只要还有 1 点蓝就允许放风筝（够一次施法起步）。
        /// </summary>
        private const int KiteMinMana = 1;

        /// <summary>
        /// 连续放风筝脱困上限（2026-11-15，生产诊断）：连续放风筝超过这个时长仍未解决战斗，
        /// 强制脱离放风筝（落到平砍/靠近走位），避免长时间无效风筝。
        /// 取 5 秒：足够拉开距离放 2~3 次远程技能，再久就是无效消耗。
        /// </summary>
        private const int KiteMaxDurationSeconds = 5;

        /// <summary>检测「密集怪群」的半径（现象 3，2026-11-14）：去刷怪点路上，这个半径内怪多就绕开。</summary>
        private const int HuntPackRadius = 6;

        /// <summary>
        /// 威胁检测半径（2026-11-15）：被怪攻击就切战斗还击。
        /// <para>
        /// 判据是 <c>monster.Target == _player</c>（怪已锁定并正在攻击本假人），
        /// 半径只作性能过滤 —— 覆盖常见攻击范围（1~3 格）并留缓冲，取 6。
        /// </para>
        /// </summary>
        private const int ThreatRadius = 6;

        /// <summary>密集怪群的阈值（现象 3）：半径内怪数达到这个数就绕开，避免一路拉仇恨冲进怪堆被围。</summary>
        private const int HuntPackMinCount = 3;

        /// <summary>
        /// 负载负权重点级半径（D5，2026-11-16）：候选点周围这个半径内的假人计入点级负载。
        /// 取 5 格：覆盖一个刷怪点周边的常规活动范围，太近起不到分散作用，太远会把整片区域全算成高负载。
        /// </summary>
        private const int LoadPointRadius = 5;

        /// <summary>去刷怪点的最长时限。实测从主城走到城外刷怪区要 60 秒以上（每格约 0.6 秒），
        /// 30 秒会导致「永远走不到、一直换点」，假人在城里原地打转。</summary>
        private const int HuntTimeoutSeconds = 90;

        /// <summary>
        /// 离 NPC 超过这么多格，就改用引擎的 <see cref="AutoPathService"/> 走过去。
        /// <para>
        /// 为什么不能一直用贪心走格：<see cref="BotNavigator.TryStepTowardsNear"/> 只按方向逼近，
        /// 城内拐角 / 城墙 / 房屋会让它原地打转。实测（2026-11-12，生产配置）
        /// 有假人卡在「走不到药店 凯茜（117 格外始终过不去）」，5 分钟都走不到，
        /// 补给直接失败。AutoPath 是按格子图算最短路，终点还取 NPC 的相邻格。
        /// </para>
        /// <para>
        /// <b>为什么是 3 而不是 20</b>：这个门槛原来是 20，等于「自动寻路只负责城外的路，
        /// 进城最后 20 格交回贪心走格」—— 而城镇里恰恰是拐角、房屋、挤成一堆的假人最多的地方。
        /// 实测（2026-11-13，30 假人）47 条被放弃的自动寻路里，有 46 条的落点集中在距终点
        /// <b>14~21 格</b>（众数 18/19 格）、路径还剩 17~26 点 —— 也就是**路本来是对的**，
        /// 只是在 20 格处被拆掉换成了贪心走格，然后卡住 30 秒 →
        /// 「走不到商人，换张图卖」白扔一张传送卷还把整张图拉黑 5 分钟。
        /// 现在只剩最后 1~2 格交给贪心（NPC 自己是阻挡物，A* 的终点只能是它周围的可走格）。
        /// </para>
        /// </summary>
        private const int AutoPathToNpcDistance = 3;

        /// <summary>
        /// 走到卖货 NPC 的耐心：这么多秒还没能靠近一格，就认定这条路堵死，换一张图卖。
        /// <para>30 秒 ≈ 正常走 100~300 格，远大于城内任何绕楼距离；</para>
        /// </summary>
        private const int TradeApproachStallSeconds = 30;

        /// <summary>
        /// 装备剩余耐久低于这个百分比就去修。
        /// <para>
        /// 原来是「正好 0 才修」，但界面按整数百分比显示 —— 剩 0.4% 也显示 0%，
        /// 判定却过不去，于是那件装备一直挂在 0% 没人管（实测 <c>老酒坛</c> 衣服 0%）。
        /// 15% 是留一次余量的经验值：一次修理几十金币，而耐久见底的装备会被引擎整件跳过、属性全失效。
        /// </para>
        /// </summary>
        private const int RepairDurabilityPercent = 15;

        /// <summary>
        /// 地图补给「齐全」的分数线（D8，2026-11-17）：<see cref="BotPerception.GetMapSupplyLevel"/>
        /// 满分 5（药店 2 + 杂货商 2 + 修理铺 1）。只有满分图（主城）才允许就地补给 ——
        /// 缺杂货商/修理铺的图就地补给会「补一半再跑一趟」，正是要优先回主城避免的二次跨图。
        /// </summary>
        private const int SupplyFullLevel = 5;

        /// <summary>同一张图上最多同时有几个假人在练级，超了就分流（后台可调）。</summary>
        private int MapCapacity => Math.Max(1, Options.MapCapacity);
        /// <summary>本图连续几次找不到怪才考虑跨图（避免刷怪间隙就乱跑）。</summary>
        private const int EmptyHuntsBeforeTravel = 3;

        /// <summary>
        /// 跨图主判据：连续多少秒打不到怪才考虑换图（2026-11-14 改动点 3）。
        /// <para>
        /// 原来按「累计 <see cref="EmptyHuntsBeforeTravel"/> 次空手」判：每次找怪失败后原地等
        /// 8~16 秒刷怪，间隙就被攒成一次「空手」，实测跨图 720 次/时（约每 5 秒一次）。
        /// 改成时间判据后，刷怪刷新、捡东西、挖尸体的合理等待不会再被算成「空手」，
        /// 只有真打不到怪（图被清空 / 等级不合适）才走。
        /// </para>
        /// </summary>
        private const int EmptyHuntSecondsBeforeTravel = 60;

        /// <summary>
        /// 跨图「防折返」窗口：离开一张图后这么久之内不再选它当目标。
        /// 5 分钟 ≈ 一次正常练级停留时间；太短挡不住折返，太长会让白名单小的配置无处可去。
        /// </summary>
        private static readonly TimeSpan AntiOscillationWindow = TimeSpan.FromMinutes(5);

        /// <summary>换到新图后至少待这么久才允许再跨图（见 <see cref="_travelCooldown"/>）。</summary>
        private static readonly TimeSpan TravelCooldownAfterArrival = TimeSpan.FromSeconds(90);

        private readonly PlayerObject _player;

        public BotProfile Profile { get; }

        public BotState State { get; private set; } = BotState.Idle;
        public string LastDetail { get; private set; } = "刚上线";
        public DateTime StateSince { get; private set; }
        public int Kills { get; private set; }
        public int Deaths { get; private set; }

        /// <summary>
        /// 成功回城补给的次数（2026-11-11，D5 量化用）。
        /// <para>
        /// 这个数是「断药能回城买」这条闭环的直接证据：
        /// 每跑一次补给就 +1，且只在**真的买到了东西**时才算（见 <see cref="FinishRestock"/>）。
        /// </para>
        /// </summary>
        public int RestockCount { get; private set; }

        /// <summary>补给失败次数（超时 / 没钱 / 找不到药店）。失败率过高说明闭环有问题。</summary>
        public int RestockFailCount { get; private set; }

        /// <summary>卖货 / 修理的完成次数。</summary>
        public int TradeCount { get; private set; }

        /// <summary>挖取（Harvest）完成次数。</summary>
        public int HarvestCount { get; private set; }

        /// <summary>累计买到的回城卷张数。</summary>
        public int TownScrollBuyCount { get; private set; }

        /// <summary>累计使用回城卷的次数（衡量「瞬移回城」这条捷径的利用量）。</summary>
        public int TownScrollUseCount { get; private set; }

        /// <summary>
        /// 最近一次**成功放出**的攻击魔法（P5 详情用，照片里的「最近技能 Thrusting」）。
        /// <para>只在 <c>BotSkills.TryCastAttack</c> 返回 true 的那一笔记，没放出去的不算 ——
        /// 否则「蓝不够在等冷却」也会被记成放过技能。</para>
        /// </summary>
        public string LastMagic { get; private set; } = string.Empty;

        public decimal ExperienceStart { get; private set; }

        private DateTime _nextActionTime;
        private DateTime _stateDeadline;
        private DateTime _restockCooldown;

        /// <summary>
        /// 被攻击打断的待办状态（2026-11-17 用户决策，修 Fight/Restock 震荡）：
        /// 补给回城途中被怪攻击切 Fight 还击时记下 Restock，战斗结束后由 EnterState
        /// 统一接续回补货，避免 Fight → Restock → Fight 的反复横跳耗药。
        /// 只用于 Restock 场景，其余状态为 default。
        /// </summary>
        private BotState _interruptedState;

        /// <summary>Restock 遇袭「继续回城」的位置推进判据（2026-11-17）：记录上次检查位置，连续多步没动 = 被围走不动。</summary>
        private Point _restockFleeCheckPos;

        /// <summary>Restock 遇袭后位置连续没动的步数（达到阈值算被围，切 Fight 还击）。</summary>
        private int _restockFleeStuckCount;

        /// <summary>
        /// 战斗结束接续回 Restock 的冷却截止时刻（2026-11-17 修野外被围震荡）。
        /// <para>
        /// 接续后 20 秒内，威胁检测不立即切 Fight —— 否则「被围 → 切 Fight → 怪快速死亡/丢失 →
        /// 接续回 Restock → 威胁仍在又切 Fight」形成 0.5 秒级循环（5-3 后续观察：
        /// BOT#28/#17/#05 各 ~1981 次/16 分钟）。冷却期内继续尝试逃离/回城，
        /// 被围死（位置持续不动达到阈值）才允许还击。补货完成（FinishRestock）时清零。
        /// </para>
        /// </summary>
        private DateTime _restockResumeUntil;

        /// <summary>位置没动的起始时刻（被围死判据用）。位置变化时归零。</summary>
        private DateTime _restockStuckSince;

        /// <summary>接续回 Restock 的冷却窗口（秒）：窗口内不因威胁立即切 Fight。</summary>
        private const double RestockResumeCooldownSeconds = 20;

        /// <summary>冷却期内允许还击的「被围死」判据（秒）：位置持续不动达到此时长，才允许切 Fight。</summary>
        private const double RestockStuckAllowSeconds = 10;

        /// <summary>
        /// 最近一次**真的把攻击动作打出去**的时刻（平砍或技能）。
        /// 用来区分「站着输出」和「站在那儿够不到」—— 两者位置都不变，
        /// 但前者每 1~2 秒会刷新这个时间，后者不会。
        /// </summary>
        private DateTime _lastAttackTime;

        /// <summary>连续补给失败次数。用来做退避：失败越多，下次重试间隔越长。</summary>
        private int _restockFailStreak;

        /// <summary>
        /// 下一次允许说话的时间（P2-1）。首次为 <see cref="DateTime.MinValue"/>，
        /// 由 <see cref="ProcessChat"/> 第一次调用时按「15~40 分钟」随机排期 ——
        /// 这样 30 个假人不会在同一分钟集体开口。
        /// </summary>
        private DateTime _nextChatTime;

        /// <summary>上一帧的坐标（P2-2 靠「位置变了」来数走了几步，不用改 10 个走格调用点）。</summary>
        private Point _lastPosition;

        /// <summary>距离上一次「走路停顿」走了多少步。</summary>
        private int _stepsSincePause;

        /// <summary>下次停顿在第几步（5~15 随机）。</summary>
        private int _nextPauseAt = 8;

        /// <summary>停顿结束时间；<see cref="DateTime.MinValue"/> = 没在停顿。</summary>
        private DateTime _walkPauseUntil;

        /// <summary>开始停顿时记的血量百分比，用来判断「被打断」。</summary>
        private int _pauseHealthPercent;

        /// <summary>城里闲逛到什么时候（P2-3）。<see cref="DateTime.MinValue"/> = 没在闲逛。</summary>
        private DateTime _strollUntil;

        /// <summary>闲逛时下一次挪步的时间。</summary>
        private DateTime _nextStrollStep;

        /// <summary>榜首降速期「走回安全区」时下一次挪步的时间（P4-4）。</summary>
        private DateTime _nextSlowStep;

        /// <summary>降速期上一次走的分支，用来「分支变化时打一条日志」（P4-4 排障用）。</summary>
        private string _slowBranch;

        /// <summary>下一次「走岔一步」的时间（P2-5）。<see cref="DateTime.MinValue"/> = 还没排期。</summary>
        private DateTime _nextMistakeTime;

        /// <summary>上一次看到的所在地图，用来发现「刚换图」（P2-4）。</summary>
        private MapInfo _lastMapInfo;

        /// <summary>进入当前地图的时刻。</summary>
        private DateTime _mapEnteredAt;

        /// <summary>这张图待多久之后考虑换一张（25~45 分钟随机）。</summary>
        private TimeSpan _mapRotateAfter = TimeSpan.FromMinutes(30);

        /// <summary>下一次允许主动 PK 的时间（P3-4）。</summary>
        private DateTime _nextPkTime;

        /// <summary>本次在线累计 PK 次数（只用于日志与后台观察）。</summary>
        private int _pkCount;

        /// <summary>开始连续占据排行榜前 N 名的时刻（P4-4）。<see cref="DateTime.MinValue"/> = 不在前 N。</summary>
        private DateTime _topSince;

        /// <summary>降速结束时间（P4-4）。<see cref="DateTime.MinValue"/> = 没在降速。</summary>
        private DateTime _slowUntil;
        private DateTime _nextOptionsRefresh;
        private BotOptions _options;
        private Point _huntPoint;
        private int _stuckCount;
        private int _blockedCount;

        /// <summary>
        /// 最近 <see cref="StuckWindow"/> 秒走过的位置轨迹（每 500ms 采一个点），用于识别「绕圈」。
        /// <para>
        /// 为什么需要：原来的 <c>TrackStuck</c> 只比「当前格 vs 上一格」，只要迈出去一步就清零。
        /// 而卡在遮挡物 / 人群前的典型表现恰恰是<b>左右横跳</b> —— 每步都在动，10 秒后还在原地 3 格内，
        /// 于是永远判不出来（用户 2026-11-13 录屏复现）。
        /// </para>
        /// </summary>
        private readonly List<(DateTime Time, Point Location)> _trail = new List<(DateTime, Point)>();

        /// <summary>避让活物的 A* 下次允许时间（节流，见 <see cref="TryStepAvoidingLive"/> 的注释）。</summary>
        private DateTime _nextAvoidStepTime;

        /// <summary>
        /// 拾取绕行（避开活物 A*）的下次允许时间（节流）。
        /// <para>
        /// 2026-11-15 生产诊断（DIAG-C）：掉落物 dist=3、周围零怪、值得捡，
        /// 仍因 A* 终点不可达被放弃。绕行重算整图 A*（20~30ms），必须节流，
        /// 否则每帧重算会制造主循环尖峰。
        /// </para>
        /// </summary>
        private DateTime _nextLootBypassTime;

        /// <summary>施毒术补毒检查的下次允许时间（节流 200ms）。毒消失后 200ms 内补上，对战斗无感，避免每帧查技能表。</summary>
        private DateTime _nextPoisonCheckTime;

        /// <summary>
        /// 道士补召宠物的下次允许时间（节流 1 秒，3-4 召唤逻辑，2026-11-17）。
        /// 引擎召唤有 500ms 延迟且施放失败不写冷却，节流必须大于延迟，
        /// 否则延迟窗口内每帧重试、空放烧蓝耗符。
        /// </summary>
        private DateTime _nextSummonCheckTime;

        /// <summary>放风筝后退的下次允许时间（节流 200ms）。</summary>
        private DateTime _nextKiteTime;

        /// <summary>
        /// 本轮连续放风筝的开始时间（2026-11-15，脱困上限用）。
        /// 每次成功放风筝后退时刷新；一旦脱离放风筝（平砍命中 / 换目标 / 进入新战斗）清零。
        /// 超过 <see cref="KiteMaxDurationSeconds"/> 仍未解决战斗，强制脱离放风筝落平砍/靠近。
        /// </summary>
        private DateTime _kiteStartTime;

        /// <summary>
        /// 上一次「因抗性跳过某技能」的说明文本。只用来**去重**：
        /// 同一只怪连续几十帧的说明是一样的，没必要每帧写一行日志（Debug 档会刷爆 Logs.txt）。
        /// </summary>
        private string _lastSkillNote;

        /// <summary>
        /// 连续「绕圈」次数，用来做分级脱困（1 轻 / 2 中 / 3 重）。
        /// <para>
        /// 为什么必须分级：绕圈脱困只走一步的话，<b>8 秒后必然原地再绕一圈</b> ——
        /// 本地实测 BOT#14 在比奇城 (163,222) 卡了 2 分多钟，每 8 秒打一条
        /// 「原地绕圈…主动脱困」，位置一动不动。只挪一步治不了「目标本身走不到」。
        /// </para>
        /// </summary>
        private int _circleStrikes;

        /// <summary>绕圈日志节流（30 秒最多一条），否则一个卡住的假人能把 Logs.txt 刷满。</summary>
        private DateTime _nextStuckLogTime;

        /// <summary>下次允许「等刷新时换点」的时间（2026-11-18，见 <see cref="IdleRelocateChance"/>）。</summary>
        private DateTime _nextIdleRelocateTime;

        /// <summary>「卖不掉的货」明细日志节流（60 秒一条，避免每轮补给都刷一遍）。</summary>
        private DateTime _nextUnsoldLogTime;

        /// <summary>战斗中从背包补装弹药的节流（2 秒，2026-11-18）。</summary>
        private DateTime _nextAmmoEquipTime;

        /// <summary>分批买弹药时中间批次的日志节流（2 秒，2026-11-18）。</summary>
        private DateTime _nextAmmoLogTime;

        /// <summary>
        /// 卖货/修理期间因挨打切战斗的次数与统计起点（60 秒窗口，2026-11-18）。
        /// 用来熔断「Trade → 挨打切 Fight → 没目标又接续回 Trade」的死循环。
        /// </summary>
        private int _tradeInterruptCount;
        private DateTime _tradeInterruptSince;

        /// <summary>
        /// 最近一次「被怪打」的时间（2026-11-18）。
        /// 用于「被打但找不到可见目标」时脱身：食人花这类会藏到地下的怪，
        /// 打你的时候 <c>Visible=false</c> 打不着，站着不动只能挨打。
        /// </summary>
        private DateTime _lastHurtTime;

        /// <summary>
        /// 上次「赶路有进展」的时间（D7，30 秒无进展判据）。
        /// <para>
        /// 进展 = 距刷怪点更近 / 到达刷怪点 / 击杀怪 / 换刷怪点或跨图。
        /// 无进展超过 <see cref="NoProgressSeconds"/> 秒触发分级脱困。
        /// </para>
        /// <para>
        /// <b>为什么需要（与 <see cref="TrackStuck"/> 互补）</b>：TrackStuck 用「8 秒包围盒 ≤ 4 格」
        /// 只抓<b>小范围原地绕圈</b>；但障碍前的典型表现是<b>大范围反复横跳</b> —— 活动范围远超 4 格，
        /// 包围盒判据永不触发，假人却始终没朝刷怪点靠近（用户 2026-11-13 观察：障碍前 10 分钟反复移动）。
        /// 本判据抓的是「有没有在朝目标靠近」，不关心活动范围大小，两者互补。
        /// </para>
        /// </summary>
        private DateTime _lastProgressTime;

        /// <summary>
        /// 本次朝当前刷怪点行进中，观察到的<b>最小距离</b>（用于判定「有没有更近」）。
        /// 只有刷新最小距离才算进展 —— 绕着障碍打转距离不降，不算进展。
        /// 换刷怪点时重置为当时的实时距离。
        /// </summary>
        private int _huntPointBestDistance;

        /// <summary>无进展超时（秒）。30 秒内没更近 / 没打到怪 / 没换点换图，判为无进展。</summary>
        private const double NoProgressSeconds = 30;

        /// <summary>无进展分级脱困次数（1 重算避让 / 2 换点 / 3 跨图），与 <see cref="_circleStrikes"/> 同构。</summary>
        private int _noProgressStrikes;

        /// <summary>无进展脱困日志节流（30 秒最多一条），否则卡住的假人会把 Logs.txt 刷满。</summary>
        private DateTime _nextNoProgressLogTime;

        /// <summary>
        /// 本次朝当前刷怪点赶路的出发时刻（2026-11-17 赶路总时限封顶）。
        /// <para>
        /// 只在选点落定（<see cref="TryPickHuntPoint"/> 成功）时记录，到达刷怪点/击杀时清零。
        /// 不挂 <see cref="MarkHuntProgress"/> —— 距刷怪点每帧更近都会刷进度计时，
        /// 挂上会导致总时限永不触发。
        /// </para>
        /// </summary>
        private DateTime _huntStartTime;

        /// <summary>
        /// 赶路总时限（秒，2026-11-17 用户决策）。从选点出发累计超过此时长，
        /// 即使期间有微小进展也强制换点/跨图脱困，封顶极端长途多跳赶路
        /// （5-3 观察：青灯客 500.1s 穿越石刻墓穴 1→4 层等）。
        /// </summary>
        private const double HuntTravelLimitSeconds = 120;

        /// <summary>
        /// 近期判定为「走不到」的刷怪点黑名单 —— 见 <see cref="BotBadHuntPoints"/>。
        /// <para>
        /// <b>跨假人共享</b>（D6）：不可达是地图地形属性，一个假人确认某点走不到，
        /// 同图其它假人也基本走不到。标记写进共享表，全图假人选点时自动避开，
        /// 避免群体在**同一个障碍点**上反复绕圈。带时效（3 分钟），过期重新验算。
        /// 原来这里是每假人自己的实例字典，别的假人不知道，会反复选同一个走不到的点。
        /// </para>
        /// </summary>

        /// <summary>
        /// 连续捡不到东西的次数（P1）。
        /// 背包满 / 负重超 / 被别人抢先，都会导致引擎拒绝拾取；
        /// 连着失败 3 次就别再死磕这一件，换目标或回城卖货。
        /// </summary>
        private int _lootFailCount;

        /// <summary>
        /// 已确认「够不到」的掉落物（2026-11-15，生产诊断 DIAG-C）。
        /// <para>
        /// 掉落物 dist=3、周围零怪、值得捡，仍可能因 A* 终点不可达（地形/其它阻挡物）
        /// 走不到。记下来在拾取查找时排除，换次近的；30 秒后过期重试
        /// （挡路的怪可能已走开，给原目标一次机会）。
        /// </para>
        /// </summary>
        private ItemObject _lootExclude;

        /// <summary><see cref="_lootExclude"/> 的过期时间。</summary>
        private DateTime _lootExcludeExpire;

        private MonsterObject _target;

        private MapInfo _travelMap;

        /// <summary>
        /// 两跳传送的中转图（null = 不需要中转，直达或走 A*）。
        /// <para>
        /// 本服六合石的按钮是「按城给」的，不是全连通：沙漠土城只有「失乐园 / 沙巴克城 / 诺玛村庄」，
        /// **没有潘夜村**，而诺玛村落的石头能到潘夜村。没有这一层时只能「烧回城卷回比奇城再传」，
        /// 多花一张卷 + 一趟城内长途，还可能在路上被打死。
        /// </para>
        /// <para>
        /// 语义：先传 <see cref="_travelVia"/>，到了之后再传 <see cref="_travelMap"/>；
        /// 中转图本身不改变最终目标，所以 <see cref="ProcessTeleport"/> 的「到达」判定仍看 <see cref="_travelMap"/>。
        /// </para>
        /// </summary>
        private MapInfo _travelVia;

        /// <summary>
        /// 上一次「因为跨图而离开」的地图 + 时间，用来挡住 A→B→A 的来回折返。
        /// <para>
        /// 2026-11-12 实测：两跳传送把跨图成本从「烧一张回城卷 + 走 1~3 分钟」降到「花钱 1 秒到」，
        /// 于是 BOT#08 在 90 秒内 潘夜村 → 失乐园 → 潘夜村 各跑一趟（每趟 15,000 金币），
        /// 全程在传送，怪一只没打。有了这条，5 分钟内不会回到刚离开的图。
        /// </para>
        /// </summary>
        private MapInfo _lastTravelFrom;

        /// <summary>上一次跨图离开的时间（配合 <see cref="_lastTravelFrom"/>）。</summary>
        private DateTime _lastTravelTime;

        /// <summary>
        /// 刚换完图之后的「先待一会儿」冷却。
        /// <para>
        /// 两跳传送把跨图成本从「烧卷 + 走 1~3 分钟」降到「花钱 1 秒到」之后，
        /// 「到了 → 图里人满/怪被清 → 立刻再走」变成零成本循环：
        /// 2026-11-12 实测 30 个假人 5 分钟里跨图 38 次，其中同一批人反复来回。
        /// 这条让它们在新图至少待 90 秒（期间仍会正常换刷怪点、打怪），
        /// 只有「新图确实没有自己这个等级的怪」才允许立刻走。
        /// </para>
        /// </summary>
        private DateTime _travelCooldown;


        private Point _travelPoint;
        private int _travelFailures;
        private DateTime _travelDebugTime;
        private int _emptyHunts;

        /// <summary>
        /// 上次「打到怪」的时间（2026-11-14 改动点 3）：跨图判据从「累计 N 次空手」
        /// 改成「连续 <see cref="EmptyHuntSecondsBeforeTravel"/> 秒打不到怪」。
        /// 击杀成功 / 找到新怪时刷新；跨图、传送、换刷怪点到达时重置 ——
        /// 否则带着旧图的时间戳到新图，会瞬间满足判据又触发一次跨图。
        /// </summary>
        private DateTime _lastHuntSuccessTime;

        /// <summary>
        /// 最近一次击杀的掉落中心（怪物死亡位置）。
        /// <para>
        /// 掉落物落在怪物死亡位置周围 <see cref="Config.DropDistance"/> 格内，且**同步生成**。
        /// 远程击杀（法师/道士射程 10 格）时，掉落物离玩家可能超过 <c>LootRadius</c>，
        /// 若只以玩家位置为中心找，会找不到 → 不进拾取态 → 直接打怪，遗留战利品永不回收
        ///（用户 2026-11-14 观察）。记录击杀位置作为中心，让假人能走到并捡起。
        /// </para>
        /// </summary>
        private Point _lastKillLocation;

        /// <summary>
        /// 跨图中的「回城中转」：本图没有六合圣石，先回主城再用石头传送。
        /// <para>
        /// 为什么需要它：假人一旦离开主城（A* 走到通道/沙漠土城这类中途图），
        /// 就没法再传送了 —— 这些图**没有六合石**，只能继续 A* 逐格走，
        /// 于是被沿途的怪打死 → 复活 → 再走。D5 实测 死 441 / 杀 599，
        /// 11 个假人困在起点 225/9/8 到潘夜村[6] 的长途上，六合石传送 0 次。
        /// </para>
        /// <para>
        /// 置位后 <see cref="ProcessTeleport"/> 不再找石头，而是先回主城；
        /// 回到主城立即清位，交回正常传送流程（主城必有石头）。
        /// </para>
        /// </summary>
        private bool _teleportViaHome;

        /// <summary>连续几次回城中转失败（没卷 / 走不回去 / 没绑定主城）。</summary>
        private int _returnHomeFailStreak;

        /// <summary>
        /// 本次补给的目的城（D8，2026-11-17：「走得到的、补给最全的、有药店的城」）。
        /// <para>
        /// 只在补给状态里用，<see cref="_supplyDestSourceMap"/> 记住是按哪张图算出来的 ——
        /// 一旦跨图（走到半路进了下一张图）就重算，因为「最近」是相对当前图说的。
        /// </para>
        /// </summary>
        private MapInfo _supplyDest;

        /// <summary>上面那个目的地是按哪张图算出来的（-1 = 还没算过）。</summary>
        private int _supplyDestSourceMap = -1;

        /// <summary>
        /// 补给/卖货收尾后，下一次进 <see cref="BotState.GoHunt"/> 时打一行背包快照。
        /// <para>
        /// 2026-11-18 用户要求：在「卖完身上的物品 → 去刷怪点」这一跳看背包里到底还剩什么，
        /// 才能从日志判断货是不是卖干净了（只该剩回城卷 + 红蓝药，道士多符和毒药）。
        /// 只在收尾后第一跳打，不会每帧刷。
        /// </para>
        /// </summary>
        private bool _logBagOnNextHunt;

        /// <summary>本轮补给「怎么回城的（用卷 / 跑路 / 就地）」只记一次。</summary>
        private bool _restockTravelLogged;

        /// <summary>
        /// 卖货收尾时背包还有可卖物品 → 下次冷却结束再换一家收购商接着卖（2026-11-18）。
        /// 避免「卖完肉就出城，装备/材料跟着练级」。
        /// </summary>
        private bool _sellPending;

        /// <summary>「换一家继续卖」连续尝试次数，超过 3 次放弃本轮（防无限跑）。</summary>
        private int _sellPendingAttempts;

        /// <summary>
        /// 回城中转的退避冷却。
        /// 没有它会出现「回城 → 主城石头传不了 → 跑远 → 又回城」的烧卷空转。
        /// </summary>
        private DateTime _returnHomeCooldown;

        /// <summary>累计回城中转次数（D5 量化：长途跨图有没有真的走上传送而不是腿儿着）。</summary>
        public int ReturnHomeForTeleportCount { get; private set; }

        /// <summary>上次同步技能时的等级。升级时补技能（法师 7 级火球、道士 13 级火符）。</summary>
        private int _syncedLevel = -1;

        /// <summary>
        /// 上一次看到的所在图索引（<c>-1</c> = 还没取过）。
        /// <para>
        /// 只用来给 <see cref="BotMetrics.MapChanges"/> 计数：<b>真的换了 CurrentMap 才算跨图</b>，
        /// 而不是「决定要走」的次数 —— 后者会把「想走但走不到」也算成跨图，指标就失去意义了。
        /// </para>
        /// </summary>
        private int _lastMapIndex = -1;

        /// <summary>后台「立即补给」投递的一次性请求。</summary>
        private bool _restockRequested;

        /// <summary>本轮交易要处理的物品类型（P1-2：按类型找对应商店）。</summary>
        private ItemType _tradeItemType;

        /// <summary>本轮交易是修理模式（P1-3）还是卖货模式（P1-2）。</summary>
        private bool _tradeIsRepair;

        /// <summary>
        /// 本轮交易的**目的地地图**（不一定是绑定主城）。
        ///
        /// 为什么需要：不是每座主城都收所有货 —— 沙漠土城、诺玛村落、沙巴克城堡
        /// **没有肉类收购商**（实测 mapq traders：收 Meat 的只有 比奇城墨菲 / 失乐园雷尼尔 / 潘夜村帕克）。
        /// 而假人的挖取物全是 Meat，硬回沙漠土城卖 → "里没有收 Meat 的商人" →
        /// 收尾冷却 → 又触发 → 每帧重进 Trade，实测单个假人交易计数 4100+、30 个假人 66,635 次
        /// 交易全是空跑，18 个假人被判定 stuck。
        /// </summary>
        private MapInfo _tradeDest;

        /// <summary>
        /// 卖货冷却：卖完别马上又跑一趟（20 秒 —— 背包还满的话下一拍就该再卖）。
        /// </summary>
        private DateTime _sellCooldown;

        /// <summary>
        /// 本轮交易「走到商人身边」的最好成绩与达成时刻。
        /// <para>
        /// <b>为什么需要它</b>：A* 每帧重算，一旦唯一通路被动态占位（别的假人、怪、地上的掉落），
        /// 就会「A* 说往东 → <c>Move</c> 被引擎拒 → 兜底 <c>TryStepAdjacent</c> 往西 →
        /// 下一帧 A* 又说往东」来回摆。实测（2026-11-12）<c>夜行人</c> 在潘夜村
        /// 距肉类商人帕克 9 格处摆了 14 分钟，每 3 分钟一次「交易超时」，
        /// 负重 70/63（111%）一直降不下来 —— 它压根没走到 NPC 面前。
        /// </para>
        /// <para>
        /// 判定用「距离有没有创新低」而不是「有没有移动」：绕楼走一段远路是正常的，
        /// 但 30 秒都没靠近一格就说明这条路走不通，该换一张图卖。
        /// </para>
        /// </summary>
        private int _tradeApproachBest;

        /// <summary>上一次刷新 <see cref="_tradeApproachBest"/> 的时刻。</summary>
        private DateTime _tradeApproachSince;

        /// <summary>
        /// 「这些图的商人我走不到 / 去不了」的退避表：图索引 → 解禁时刻。
        /// 置位后 5 分钟内不再把它当卖货目的地，让 <see cref="ResolveTradeDestination"/> 换一张图
        /// （比如去比奇城找墨菲）。用表而不是单个字段 —— 一次卖货可能连续试错好几张图。
        /// </summary>
        private readonly Dictionary<int, DateTime> _tradeMapBlockedUntil = new Dictionary<int, DateTime>();

        /// <summary>
        /// 修理冷却：耐久掉得慢，修完 10 分钟内不用再跑。
        /// <para>
        /// ⚠ 必须和卖货冷却**分开**。原先共用一个 <c>_tradeCooldown</c>，实测后果是：
        /// 假人每 10 分钟才有一次交易机会，而那一次被「修装备」占掉（修理优先），
        /// 于是负重 111%、48/48 满格的角色**永远卖不出东西**，卡死在挖取态。
        /// </para>
        /// </summary>
        private DateTime _repairCooldown;

        /// <summary>转生失败后的冷却（满级但去不了市政厅时，别每帧重试）。</summary>
        private DateTime _rebirthCooldown;

        /// <summary>
        /// 进入转生流程时的转生次数基线。
        /// 判据必须用它而不是「等级归 1 / 点没点按钮」：Rebirth 动作在**进入「执行转生」页的那一瞬**
        /// 就执行了（等级当场变 1），等下一拍再检查页面时，等级早就不是满级了 ——
        /// 之前就是这样把「已成功」误报成「等级没到上限」。
        /// </summary>
        private int _rebirthBaseCount;

        /// <summary>
        /// 转生前拍的角色数据快照（装备 / 背包 / 仓库 / 技能 / 货币 / 任务 / 宠物）。
        /// 转生成功后与当前状态比对：<see cref="PlayerObject.AdjustRebirth"/> 按设计只该动
        /// 等级 / 经验 / 转生次数 / 属性点 / 隐士点 / 修行，其余一项都不该少。
        /// 没有这个快照，「转生后有没有丢装备」只能靠人工盯 60 小时。
        /// </summary>
        private BotRebirthSnapshot _rebirthSnapshot;

        /// <summary>
        /// 本轮「补给」要办的事：买药之外，**还要不要顺路买回城卷**。
        /// <para>
        /// 两种东西分属两个 NPC（药店卖药、杂货商卖卷），所以一次回城要跑两趟。
        /// 用一个阶段位来串，避免状态机再开一个状态。
        /// </para>
        /// </summary>
        private RestockStage _restockStage = RestockStage.Potion;

        /// <summary>
        /// 进门时背包里的药 + 卷数量（-1 = 本轮还没记）。
        /// 收尾时用增量判定「这次补给到底买到东西没有」。
        /// </summary>
        private int _restockBaselinePotions = -1;
        private int _restockBaselineScrolls;
        // 本轮补给买到的弹药增量（护身符 + 毒药粉）。
        // 2026-11-15：弹药是道士的刚需（断弹 = 技能全空放），买到了就算补给有实质进展，
        // 不能因为药没买到（金币不足等）就把整轮判「失败」——日志上会误读成「什么都没买到」。
        private int _restockAmmoGain;

        /// <summary>
        /// 本轮补给是否**尝试过买药**（2026-11-14 改动点 4）。
        /// <para>
        /// 收尾判「补给成功」时要求药真的变多：如果本轮试过买药但药 0→0，
        /// 即使顺手买到回城卷也不算成功 —— 否则假人以为补给好了，实际药还是 0，
        /// 继续空跑（原来按「药+卷总量增量」判，药 0→0 + 买卷 = 报成功）。
        /// </para>
        /// </summary>
        private bool _restockTriedPotion;

        /// <summary>补给流程的子阶段。</summary>
        private enum RestockStage
        {
            /// <summary>去药店买药。</summary>
            Potion = 0,
            /// <summary>去杂货商买回城卷（下次断药就能秒回城）。</summary>
            TownScroll = 1,
            /// <summary>去杂货商买护身符 / 毒药粉（只有道士需要，没弹药 = 技能全空放）。</summary>
            Ammo = 2,
        }

        /// <summary>
        /// 这一阶段是不是在**杂货商**那儿办（买卷 / 买弹药共用一个 NPC，可以一次跑完）。
        /// </summary>
        private bool IsGrocerStage => _restockStage == RestockStage.TownScroll || _restockStage == RestockStage.Ammo;

        /// <summary>当前要挖的尸体。</summary>
        private MonsterObject _harvestTarget;

        /// <summary>
        /// 「走不到」的尸体（2026-11-18）：60 秒内不再选它。
        /// 没有这个排除，假人会每 0.3 秒重试同一具走不到的尸体 —— 实测 40 分钟 7791 次，
        /// 表现就是站在尸体旁发呆 / 来回蹭。
        /// </summary>
        private MonsterObject _harvestExclude;

        /// <summary><see cref="_harvestExclude"/> 的过期时间。</summary>
        private DateTime _harvestExcludeExpire;

        /// <summary>本具尸体开始「走不到」的时刻（时间判据，替代按帧计数）。</summary>
        private DateTime _harvestBlockedSince;

        /// <summary>「走不到尸体」日志节流（10 秒一条）。</summary>
        private DateTime _nextHarvestBlockedLogTime;

        /// <summary>下次挖取的最早时间（引擎的 Harvest 有冷却动作）。</summary>
        private DateTime _nextHarvestTime;

        public BotBrain(PlayerObject player, BotProfile profile)
        {
            _player = player;
            Profile = profile;
            StateSince = SEnvir.Now;
            ExperienceStart = player?.Experience ?? 0;
            // 改动点 3：上线即记「刚打到怪」，避免一上线就满足「连续 N 秒无怪」判据。
            _lastHuntSuccessTime = SEnvir.Now;

            // 上线先修一次存量：买来的药带 Locked、启动资源发的药带 Bound|Worthless，
            // 两种标记引擎/假人自己都不肯卖，会把负重永久占死。
            RepairPotionFlags(log: true);

            // 新手装（Bound|Worthless 的 StartItem）同样永远卖不掉，上线顺手清标记（2026-11-18）。
            RepairStarterJunkFlags(log: true);

            EnterState(BotState.Idle, "刚上线，先站一会儿", TimeSpan.FromSeconds(SEnvir.Random.Next(2, 6)));
        }

        /// <summary>
        /// 把背包里「药」身上那些「卖不掉」的标记清掉
        /// （<see cref="UserItemFlags.Locked"/> / <see cref="UserItemFlags.Bound"/> / <see cref="UserItemFlags.Worthless"/>）。
        ///
        /// <para><b>为什么必须做</b>：引擎 <c>NPCBuy</c> 给<b>所有</b>买来的物品打 <c>Locked</c>
        /// （<c>PlayerObject.cs</c> 里 <c>UserItemFlags flags = UserItemFlags.Locked;</c>），
        /// 而 <c>NPCSell</c> 直接拒收 Locked / Worthless，<c>ItemDrop</c> 也拒收 Locked ——
        /// 于是补给买的药<b>既卖不掉也丢不掉</b>，只能等喝掉。</para>
        ///
        /// <para>第二类来源是<b>启动资源</b>（<c>BotFactory.GivePotion</c>）发的药，
        /// 老版本给的是 <c>Bound | Worthless</c>：引擎同样拒收，而且假人自己的卖货口径
        /// （<see cref="BotItemRules.IsSellBlocked"/>）也跳过带这三个标记的格子。
        /// 实测（2026-11-12）<c>三更雪</c>（战士，负重 153/84）：
        /// 40 瓶绑定魔法药 + 20 瓶绑定金创药 = 60 点<b>永久</b>负重，怎么卖都降不下来。</para>
        ///
        /// <para>假人没有交易行为，不存在「买了立刻卖回去」的套利，这层保护对它们只有副作用。
        /// 修完仍由 <see cref="BotPotionBudget"/> 兜住「至少留够一轮补给量」，不会把药卖光。</para>
        ///
        /// <para>只修药水（<c>Consumable</c> 且回血/回蓝）。装备的 <c>Locked</c> 保持不动 ——
        /// 装备还带着 <c>NonRefinable</c>，解锁会破坏精炼与卖店的经济口径。</para>
        /// </summary>
        private void RepairPotionFlags(bool log)
        {
            if (_player?.Inventory == null) return;

            int repaired = 0;

            for (int i = 0; i < _player.Inventory.Length; i++)
            {
                UserItem item = _player.Inventory[i];

                if (item?.Info == null) continue;
                if (!BotItemRules.IsPotion(item.Info.ItemType, item.Info.Stats[Stat.Health], item.Info.Stats[Stat.Mana])) continue;

                UserItemFlags before = item.Flags;
                UserItemFlags after = BotItemRules.RepairPotionFlags(before);

                if (after == before) continue;

                item.Flags = after;

                repaired++;
            }

            if (log && repaired > 0)
                BotLog.Info(Profile.Seq, string.Format("修复 {0} 堆药的卖不掉标记（原 Locked/Bound/Worthless，卖不掉也丢不掉）", repaired));
        }

        /// <summary>
        /// 清掉「开局绑定新手装」的卖不掉标记（2026-11-18，用户问「能否中键解绑后卖掉」）。
        ///
        /// <para><b>实测结论：解绑也卖不掉。</b>木剑 / 布衣 / 练功服 / 练习长刀 / 蜡烛 这些
        /// <c>StartItem</c> 是 <c>PlayerObject.NewCharacter()</c> 发的
        /// <c>Bound | Worthless</c>（<c>PlayerObject.cs:1569</c>），**没有 Locked** ——
        /// 真人客户端的鼠标中键只解 <c>Locked</c>（<c>C.ItemLock</c>），对它们无效；
        /// 而 <c>NPCSell</c> 见到 <c>Worthless</c> 直接 return（价格 0），所以永远卖不掉。</para>
        ///
        /// <para>假人不做玩家交易，这层保护对它们只剩副作用：每个假人白占 3 格（2 装备 + 1 蜡烛）
        /// 外加几十点负重。这里按「药水」的同一套口径把标记清掉（<see cref="RepairPotionFlags"/>），
        /// 清完卖货链路下一趟就会把它们卖掉，格子腾出来。</para>
        /// </summary>
        private void RepairStarterJunkFlags(bool log)
        {
            if (_player?.Inventory == null) return;

            int repaired = 0;

            for (int i = 0; i < _player.Inventory.Length; i++)
            {
                UserItem item = _player.Inventory[i];

                if (item?.Info == null) continue;

                // 只动「商店本来愿意收」的东西：CanSell=false 的清了也没用。
                if (!item.Info.CanSell) continue;
                if ((item.Flags & UserItemFlags.Worthless) != UserItemFlags.Worthless) continue;

                // 药和回城卷归 RepairPotionFlags / IsKeepItem 管，别在这里插手。
                if (BotActions.IsTownScroll(item)) continue;
                if (BotItemRules.IsPotion(item.Info.ItemType, item.Info.Stats[Stat.Health], item.Info.Stats[Stat.Mana])) continue;

                item.Flags &= ~(UserItemFlags.Bound | UserItemFlags.Worthless);

                repaired++;
            }

            if (log && repaired > 0)
                BotLog.Info(Profile.Seq, string.Format(
                    "清理 {0} 件绑定零价物品的标记（新手装残留，原 Bound|Worthless 永远卖不掉）", repaired));
        }

        /// <summary>
        /// 配置快照，5 秒刷新一次。后台改补给阈值/开关能在 5 秒内生效，
        /// 又不用每帧给 30 个假人各创建一个 BotOptions。
        /// </summary>
        private BotOptions Options
        {
            get
            {
                if (_options == null || SEnvir.Now >= _nextOptionsRefresh)
                {
                    _options = BotOptions.FromConfig();
                    _nextOptionsRefresh = SEnvir.Now.AddSeconds(5);
                }

                return _options;
            }
        }

        #region 主循环

        /// <summary>
        /// 后台「立即补给」：下一次决策直接切补给状态（清掉 60 秒冷却）。
        /// **必须在游戏线程内调用**，由 <see cref="BotRoster"/> 统一投递。
        /// </summary>
        public void RequestRestock()
        {
            _restockRequested = true;
            _restockCooldown = DateTime.MinValue;
            // 后台强制补给视为「人工介入」，把自动退避的计数清掉，否则会沿用上次的 10 分钟冷却
            _restockFailStreak = 0;

            LastDetail = "后台要求立即补给";
        }

        public void Process()
        {
            if (_player == null || _player.Character == null) return;

            // 跨图计数（P4-5 性能卡片）。放在节流判断之前：换图可能发生在两次决策之间，
            // 放在后面会被 ActionIntervalMs 延后一拍，指标的时间点就不准了。
            int mapIndex = _player.CurrentMap?.Info?.Index ?? -1;

            if (_lastMapIndex >= 0 && mapIndex >= 0 && mapIndex != _lastMapIndex)
                BotMetrics.MapChange();

            _lastMapIndex = mapIndex;

            if (SEnvir.Now < _nextActionTime) return;

            _nextActionTime = SEnvir.Now.AddMilliseconds(ActionIntervalMs);

            try
            {
                // 升级就补技能：法师 7 级有火球、道士 13 级有火符，一次补齐当前等级能学的。
                // Sync 本身幂等，但遍历 190 条技能不便宜，所以只在等级真的变了才调。
                if (_player.Level != _syncedLevel)
                {
                    BotSkills.Sync(_player);
                    _syncedLevel = _player.Level;
                }

                if (_player.Dead)
                {
                    ProcessDead();
                    return;
                }

                // 返回 true 表示本帧已经切到别的状态（例如药没了要回城），后面不用再处理旧状态。
                if (ProcessSurvival()) return;

                // 榜首降速（P4-4）：占了排行榜前几名的假人先歇着，把位置让给真人。
                if (ProcessTopSlowdown()) return;

                // 拟人化三件套（P2-2 / P2-5）：数步、停顿、偶发走错一步。
                // 放在生存之后、状态机之前 —— 喝药不能被停顿挡住。
                TrackSteps();
                if (ShouldHoldForPause()) return;
                if (TryMistake()) return;

                // 说话与状态机无关（打怪、走路、城里发呆都可能开口），单独一路。
                ProcessChat();

                // 红名 PK（P3-4）：偶尔掺一脚；真动手了本帧就不再走状态机。
                if (TryPk()) return;

                switch (State)
                {
                    case BotState.Idle:
                        ProcessIdle();
                        break;
                    case BotState.GoHunt:
                        ProcessGoHunt();
                        break;
                    case BotState.Fight:
                        ProcessFight();
                        break;
                    case BotState.Loot:
                        ProcessLoot();
                        break;
                    case BotState.Dead:
                        ProcessDead();
                        break;
                    case BotState.Restock:
                        ProcessRestock();
                        break;
                    case BotState.Travel:
                        ProcessTravel();
                        break;
                    case BotState.Teleport:
                        ProcessTeleport();
                        break;
                    case BotState.Trade:
                        ProcessTrade();
                        break;
                    case BotState.Rebirth:
                        ProcessRebirth();
                        break;
                    case BotState.Harvest:
                        ProcessHarvest();
                        break;
                }
            }
            catch (Exception ex)
            {
                // 单个假人出错绝不能冒泡到主循环（一个异常会把整个游戏服拖垮）。
                BotLog.Error(Profile.Seq, string.Format("状态 {0} 处理异常：{1}", State, ex.Message));
                SEnvir.Log(ex.ToString());

                EnterState(BotState.Idle, "异常后回到待机", TimeSpan.FromSeconds(5));
            }
        }

        #endregion

        #region 各状态

        private void ProcessIdle()
        {
            if (SEnvir.Now < _stateDeadline) return;

            // 城里闲逛（P2-3）：还没到点就继续在城里晃，不急着出城。
            if (SEnvir.Now < _strollUntil)
            {
                ProcessTownStroll();
                return;
            }

            _strollUntil = DateTime.MinValue;

            if (!TryPickHuntPoint())
            {
                // 三种情况都会走到这里：本图没有等级合适的点、不在白名单、或者人已经满了。
                // 统一尝试跨图；跨不过去就退回本图（忽略容量），别站着不动。
                if (TryTravelToHuntMap()) return;

                if (!TryPickHuntPoint(true))
                {
                    EnterState(BotState.Idle, "本图没有可达的刷怪点，10 秒后再试", TimeSpan.FromSeconds(10));
                    return;
                }
            }

            EnterState(BotState.GoHunt, string.Format("去刷怪点 {0},{1}", _huntPoint.X, _huntPoint.Y), TimeSpan.FromSeconds(HuntTimeoutSeconds));
        }

        private void ProcessGoHunt()
        {
            // P1 修的硬伤：这个状态原来不检查期限，<c>HuntTimeoutSeconds</c> 形同虚设。
            // 走到刷怪点可能要好几分钟（跨图更久），但如果路走不通 / 反复被挡，
            // 没有超时出口就会永远耗在 GoHunt 里 —— 既不会打怪也不会去补给。
            if (SEnvir.Now >= _stateDeadline)
            {
                EnterState(BotState.Idle, "去刷怪点超时，换个地方", TimeSpan.FromSeconds(3));
                return;
            }

            // 无进展检测（D7）：每帧量一次距刷怪点的距离，刷新最小距离才算进展；
            // 30 秒没更近 → 分级脱困。与 TrackStuck（8 秒/4 格绕圈）互补，
            // 专治「大范围反复横跳但活动范围超 4 格、始终没靠近目标」。
            // 返回 true 表示本帧已被脱困动作接管（换点/跨图/转 Idle），目标或状态已变，
            // 本帧不再按旧状态继续，直接返回等下一帧重算。
            if (TrackNoProgress()) return;

            // 路上看到怪就先打，不必走到点上（1 级号 52 血，闷头走容易被围死）。
            MonsterObject monster = BotPerception.FindHuntTarget(_player, HuntRadius, Options.RespectRealPlayerTags, Options.SkipBoss, Profile.Seq);

            if (monster != null)
            {
                MarkHuntProgress();   // 路上遇到怪 = 有进展（不用干等刷怪点）

                EnterState(BotState.Fight, string.Format("路上遇到 {0}", MonsterName(monster)), TimeSpan.FromSeconds(60));
                SetTarget(monster);
                return;
            }

            // 现象 3（2026-11-14，路上不清怪冲堆）：上面 FindHuntTarget 返回 null，说明半径内
            // **没有可打的怪**（都被等级过滤 / 认领惩罚 / 守卫 / BOSS 过滤掉了），但周围怪可能很多。
            // 此时若直接 TryStepTowards 直冲，它内部 A* 用 avoidLiveObjects=false —— **不把怪当障碍**，
            // 规划的路径直接穿过怪群，假人一路拉几十只怪的仇恨冲进怪堆被围殴死。
            //
            // 改法：检测到密集怪群（半径内怪数达阈值）时，改用避让 A*（avoidLiveObjects=true，
            // 怪格当阻挡）绕开怪群，而不是硬穿。整图 A* 20~30ms，必须节流（复用 _nextAvoidStepTime）。
            //
            // 为什么不用额外距离阈值：能走到这里说明附近**没有可打的怪**（有就会在上面切 Fight），
            // 那些怪全是要避让的；刷怪点若真有可打的怪，早就被 FindHuntTarget 选中开打了。
            if (BotPerception.CountMonstersAround(_player, HuntPackRadius) >= HuntPackMinCount)
            {
                if (SEnvir.Now >= _nextAvoidStepTime)
                {
                    _nextAvoidStepTime = SEnvir.Now.AddMilliseconds(400);

                    if (BotNavigator.TryStepAvoidingLive(_player, _huntPoint))
                    {
                        TrackStuck();
                        _blockedCount = 0;
                        return;
                    }
                    // 避让算不出路（被怪完全围死，无路可绕）→ 不卡死，落到下面正常走位，
                    // 让原有的 Blocked→侧移→避让→换点脱困链兜底。
                }
                else
                {
                    // 节流窗口内：不再算整图 A*，但也不能硬冲怪堆。
                    // 用轻量侧移沿怪群边缘挪一步（TryStepAdjacent 不踩怪格，只会挑可走格）；
                    // 挪不动就原地等节流到点，由上面的避让 A* 大范围绕开。
                    if (BotNavigator.TryStepAdjacent(_player, _huntPoint))
                    {
                        TrackStuck();
                        _blockedCount = 0;
                        return;
                    }
                    return;
                }
            }

            BotMoveResult result = BotNavigator.TryStepTowards(_player, _huntPoint);

            switch (result)
            {
                case BotMoveResult.Moved:
                    TrackStuck();
                    return;

                case BotMoveResult.Arrived:
                    _target = null;
                    _circleStrikes = 0;   // 真走到了 = 脱困成功，绕圈计数归零
                    MarkHuntProgress();   // D7：到达刷怪点 = 明确进展，重置无进展计时
                    _huntStartTime = default;   // 赶路总时限：到达，赶路计时归零
                    BotLog.Debug(Profile.Seq, string.Format(
                        "到达刷怪点 {0},{1}，30 格内怪 {2} 只、全图怪 {3} 只",
                        _huntPoint.X, _huntPoint.Y,
                        BotPerception.GetMonstersAround(_player, 30).Count,
                        CountMapMonsters()));
                    EnterState(BotState.Fight, "到达刷怪点，开始找怪", TimeSpan.FromSeconds(60));
                    return;

                case BotMoveResult.Busy:
                    return;

                case BotMoveResult.Blocked:
                {
                    // 格子刚被别的怪/人踩过会短暂不可进（IsBlocking 带时序），这是常态。
                    // 先尝试侧移绕一步；连续 10 次（约 2.5 秒）都过不去才重新选点。
                    _blockedCount++;

                    if (BotNavigator.TryStepAdjacent(_player, _huntPoint))
                    {
                        TrackStuck();
                        _blockedCount = 0;
                        return;
                    }

                    // 侧移都不行 —— 多半是下一步被别的假人/宝宝长期占着。
                    // 这时用「避让活物」的 A* 重算一次，让它绕开人群而不是一直撞同一格
                    // （一直撞 → 侧移 → 下一帧又撞 → 原地打转，用户 2026-11-13 录屏）。
                    // 必须节流：整图 A* 一次 20~30ms，30 个假人每帧都算会把主线程吃光。
                    if (SEnvir.Now >= _nextAvoidStepTime)
                    {
                        _nextAvoidStepTime = SEnvir.Now.AddMilliseconds(400);

                        if (BotNavigator.TryStepAvoidingLive(_player, _huntPoint))
                        {
                            TrackStuck();
                            _blockedCount = 0;
                            return;
                        }
                    }

                    if (_blockedCount < 10) return;

                    _blockedCount = 0;

                    if (TryPickHuntPoint()) return;

                    EnterState(BotState.Idle, "走不到刷怪点，换个地方", TimeSpan.FromSeconds(3));
                    return;
                }
            }
        }

        private void ProcessFight()
        {
            // 待久了换一张图（P2-4）。真换成功的话状态已经切走，本帧到此为止。
            MaybeRotateMap();

            if (State != BotState.Fight) return;

            MonsterObject monster = _target;

            // 目标还活着就续期认领（TTL 12 秒，正常战斗每帧都会走到这里）。
            // 不续期的话，打着打着认领就过期了，别的假人又会扑上来一起砍。
            if (monster != null && !monster.Dead)
                BotClaims.Claim(monster, Profile.Seq);

            // 上一刀把它砍死了：记一次击杀，再去找下一只。
            if (monster != null && monster.Dead)
            {
                Kills++;

                _circleStrikes = 0;   // 真砍死了 = 打到了东西，绕圈计数归零
                MarkHuntProgress();   // D7：击杀 = 明确进展，重置无进展计时
                _huntStartTime = default;   // 赶路总时限：打到怪 = 本段赶路结束，计时归零

                // 改动点 3：打到怪 = 这张图有货，「空手」计数与跨图计时一起归零。
                // 原来 _emptyHunts 只在跨图/传送路径清零，打怪处不清，
                // 于是「打一只怪 → 等刷新 → 找不到怪 → 攒次数 → 跨图」反复发生（跨图 720 次/时）。
                _emptyHunts = 0;
                _lastHuntSuccessTime = SEnvir.Now;

                // 死了就没必要再占着，让别的假人立刻可以去打（虽然 Prune 也会清）。
                BotClaims.Release(monster, Profile.Seq);

                // 累计击杀落库（2026-11-11）：进程内的 Kills 重启就归零，
                // 落库后后台「假人档案」才能显示跨重启的历史战绩。
                if (_player?.Character != null)
                    _player.Character.BotKills++;

                BotLog.Debug(Profile.Seq, string.Format("击杀 {0}（累计 {1}），经验 {2}/{3}，等级 {4}",
                    MonsterName(monster), Kills, _player.Experience, _player.MaxExperience, _player.Level));

                // 记录击杀位置：掉落物落在怪物死亡位置周围 Config.DropDistance 格内，且同步生成。
                _lastKillLocation = monster.CurrentLocation;

                // P1：击杀后**顺手捡战利品**（真人打完怪就会捡，不会等到周围没怪）。
                // 原来只在「附近没怪了」才去捡 —— 怪刷得勤时假人永远不会进 Loot 态，
                // 于是背包一直不涨、卖货与换装两条链路全是死的。
                //
                // 注意以**击杀位置**为中心找掉落物：掉落物落在怪物死亡位置周围
                // Config.DropDistance 格内，远程击杀（法师/道士射程 10 格）时掉落物离玩家
                // 可能超过 LootRadius，若只以玩家位置为中心会找不到 → 不进拾取态 → 直接打怪，
                // 遗留战利品永不回收（用户 2026-11-14 观察）。
                // 捡战利品：先查玩家身边的遗留掉落（之前打怪留下的、被中断没捡的），
                // 再查新击杀位置的掉落（远程击杀时掉落可能落在死亡位置，离玩家超过 LootRadius）。
                // 用户 2026-11-15 观察：原来只查击杀位置，身边的遗留永远捡不到，清完怪后战利品堆积。
                ItemObject nearbyLoot = BotPerception.FindNearestLoot(_player, LootRadius);

                if (nearbyLoot == null)
                    nearbyLoot = BotPerception.FindNearestLoot(_player, _lastKillLocation, LootRadius + Config.DropDistance);

                // 金币单独再看一圈（2026-11-18）：不占格不占重，稍微远一点也值得跑。
                if (nearbyLoot == null)
                    nearbyLoot = BotPerception.FindNearestCurrency(_player, LootRadius + CurrencyLootBonus);

                if (nearbyLoot != null)
                {
                    _target = null;

                    EnterState(BotState.Loot, "打完了，捡战利品", TimeSpan.FromSeconds(30));

                    return;
                }

                // P1：鸡 / 猪 / 牛的掉落不落地，留在尸体里 —— 必须走过去挖。
                // 没有这一步，假人的背包永远是空的（实测 27 次击杀 0 拾取）。
                MonsterObject corpse = FindHarvestableCorpse();

                if (corpse != null)
                {
                    _target = null;
                    _harvestTarget = corpse;
                    _nextHarvestTime = DateTime.MinValue;

                    EnterState(BotState.Harvest, "去挖尸体", TimeSpan.FromSeconds(30));

                    return;
                }

                _target = null;
                monster = null;
            }

            // 目标失效（死了 / 节点清了 / **不可见**）：清除后重新找。
            // 不可见 = 食人花等地下怪未冒出地面，打不着，必须换目标，否则会一直贴着对空气打（用户 2026-11-14 观察）。
            if (monster == null || monster.Node == null || !monster.Visible)
            {
                monster = BotPerception.FindHuntTarget(_player, HuntRadius, Options.RespectRealPlayerTags, Options.SkipBoss, Profile.Seq);
                SetTarget(monster);

                // 改动点 3：找到怪 = 这张图有货，跨图计时重新起算
                //（避免「找到怪 → 打着打着目标消失 → 刚好攒够秒数 → 误跨图」）。
                if (monster != null)
                    _lastHuntSuccessTime = SEnvir.Now;
            }

            if (monster == null)
            {
                // 附近没怪了，先看有没有东西可捡，再原地等一会儿 —— 刷怪有间隔，
                // 立刻换点会导致「到达 → 没怪 → 再到达」的来回空转。
                if (BotPerception.FindNearestLoot(_player, LootRadius) != null
                    || BotPerception.FindNearestCurrency(_player, LootRadius + CurrencyLootBonus) != null)
                {
                    EnterState(BotState.Loot, "附近没怪，先捡东西", TimeSpan.FromSeconds(30));
                    return;
                }

                // P1：附近还有可挖的尸体（鸡 / 猪 / 牛的掉落不落地）→ 去挖。
                MonsterObject corpse = FindHarvestableCorpse();

                if (corpse != null)
                {
                    _harvestTarget = corpse;
                    _nextHarvestTime = DateTime.MinValue;

                    EnterState(BotState.Harvest, "附近没怪，去挖尸体", TimeSpan.FromSeconds(30));
                    return;
                }

                // 刚被打、但找不到可见目标（食人花这类会藏到地下的怪）→ 先挪开，别站着挨打。
                // 现场（2026-11-18 BOT#02 毒经生）：被食人花围住，攻击者一隐身就打不着，
                // 而 AutoPath 又被活物挡住，结果原地站桩十几秒。
                if (_lastHurtTime != default
                    && (SEnvir.Now - _lastHurtTime).TotalSeconds <= 3)
                {
                    _lastHurtTime = default;

                    TryShuffleStep();

                    EnterState(BotState.GoHunt, "被打却看不到目标，先挪开", TimeSpan.FromSeconds(2));

                    return;
                }

                // 等刷新时也随机换点（2026-11-18 用户要求）：
                // 同图往往还有别的刷怪区没人去，没必要几十个假人全围在同一处等 8~16 秒。
                // TryPickHuntPoint 自带「排除脚边 8 格 + 按负载打分」，所以换到的必然是另一个点。
                if (SEnvir.Now >= _nextIdleRelocateTime
                    && BotPerception.HasHuntValue(_player.CurrentMap?.Info, _player.Level)
                    && SEnvir.Random.Next(100) < IdleRelocateChance
                    && TryPickHuntPoint())
                {
                    _nextIdleRelocateTime = SEnvir.Now.AddSeconds(IdleRelocateCooldownSeconds);
                    _lastHuntSuccessTime = SEnvir.Now;   // 新点给缓冲期，别刚落地就判跨图

                    EnterState(BotState.GoHunt,
                        string.Format("换个刷怪点看看 {0},{1}", _huntPoint.X, _huntPoint.Y),
                        TimeSpan.FromSeconds(HuntTimeoutSeconds));

                    return;
                }

                // 连续一段时间都打不到怪，说明这张图确实没货（或被别人清光了）→ 换个图。                //
                // 改动点 3：主判据从「累计 N 次空手」改成「连续 N 秒打不到怪」。
                // 原判据下每次找怪失败后原地等 8~16 秒刷怪，间隙就被攒成一次「空手」，
                // 刷怪间隙的合理等待被算成空手，实测跨图 720 次/时（约每 5 秒一次）。
                _emptyHunts++;

                if ((SEnvir.Now - _lastHuntSuccessTime).TotalSeconds >= EmptyHuntSecondsBeforeTravel)
                {
                    // 本图还有等级合适的刷怪区 → 先在本图换个刷怪点，别急着跨图。
                    //
                    // 为什么：跨图一趟 1~3 分钟，洞穴图（沃玛 / 祖玛 / 赤月这类）**没有六合石**，
                    // 走了就得烧一张回城卷回主城再传回来 —— 刷怪间隙就走，会让假人在两张图
                    // 之间来回折返，实际打怪时间反而更少（2026-11-12 交接文档记的「中转过于积极」）。
                    //
                    // 不重置 _emptyHunts：它现在是「本图重挑点次数」的计数，最多重挑 3 次（3→6），
                    // 之后照样按时间判据离开，不会在真被清空的图里无限换点。
                    if (_emptyHunts < EmptyHuntsBeforeTravel * 2
                        && BotPerception.HasHuntValue(_player.CurrentMap?.Info, _player.Level)
                        && TryPickHuntPoint())
                    {
                        // 换点成功：跨图计时重新起算（新刷怪点给一个缓冲期，
                        // 60 秒内打到怪就继续，打不到再跨图，避免刚到新点就满足判据）。
                        _lastHuntSuccessTime = SEnvir.Now;

                        EnterState(BotState.GoHunt,
                            string.Format("本图还有怪，换刷怪点 {0},{1}", _huntPoint.X, _huntPoint.Y),
                            TimeSpan.FromSeconds(HuntTimeoutSeconds));

                        return;
                    }

                    if (TryTravelToHuntMap()) return;
                }

                EnterState(BotState.Idle, "附近没怪，原地等刷新", TimeSpan.FromSeconds(SEnvir.Random.Next(8, 16)));
                BotLog.Debug(Profile.Seq, "找怪失败：" + DescribeNearbyMonsters(HuntRadius));
                return;
            }

            int distance = BotNavigator.Distance(_player.CurrentLocation, monster.CurrentLocation);

            // 战斗中从背包补装弹药（2026-11-18）：
            // 装备槽受 StackSize 限制，10000 份里大部分躺在背包；槽空了不补装，
            // 明明「库存 9000 份」也会断弹（技能有动画、扣蓝、零伤害）。
            if (_player.Class == MirClass.Taoist && SEnvir.Now >= _nextAmmoEquipTime)
            {
                _nextAmmoEquipTime = SEnvir.Now.AddSeconds(2);

                foreach ((EquipmentSlot slot, MagicType _) in BotSkills.AmmoSkills(_player))
                {
                    if (BotSkills.CountEquip(_player, slot) >= 100) continue;

                    BotActions.TryEquipAmmo(_player, slot);
                }
            }

            // 道士补召宠物（3-4，2026-11-17）：战斗前补召 + 宠物死亡补召，共用一个检查点。
            // 宠物战死时引擎 Die() 自动从 Player.Pets 移除，所以「宠物位没满」这一个判据
            // 同时覆盖两个场景：开打前没宠物 → 补召；打着打着宠物死了 → 补召。
            //
            // 节流 1 秒：引擎召唤有 500ms 延迟（DelayedAction）且施放失败不写冷却，
            // 不节流会在延迟窗口内每帧重试、空放烧蓝耗符（见 TrySummonPet 注释）。
            // 守卫区（安全区）不召、宠物位满不召、蓝/符不够不召——门控全在 TrySummonPet 里。
            if (_player.Class == MirClass.Taoist && SEnvir.Now >= _nextSummonCheckTime)
            {
                _nextSummonCheckTime = SEnvir.Now.AddMilliseconds(1000);

                MagicType summon = BotSkills.TrySummonPet(_player);

                if (summon != MagicType.None)
                {
                    _lastAttackTime = SEnvir.Now;
                    LastMagic = BotText.Magic(summon);

                    LastDetail = string.Format("召唤 {0}（宠物 {1} 只，蓝 {2}/{3}）",
                        BotText.Magic(summon), _player.Pets.Count, _player.CurrentMP, _player.Stats[Stat.Mana]);

                    BotLog.Debug(Profile.Seq, LastDetail);

                    return;
                }
            }

            // 道士补毒：绿毒 / 红毒缺一种就放一次施毒术（节流 200ms，毒消失后 200ms 内补上）。
            //
            // 施毒术是**纯 debuff**（只 ApplyPoison，无直接伤害），所以它不在攻击技能表里，
            // 只在这条循环里补毒。以前它是「开场放一次」，毒效果消失后不再补，与真人道士不符。
            //
            // 为什么每帧只放一次、要靠节流兜底：引擎实现里一次施毒**随机只给一种毒**
            // （`UsePoison(1, out shape)`，`shape==0 ? 绿 : 红`），「上 2 种毒」必须连续施毒到
            // 绿红都齐；而 MagicCast 会立刻写冷却，同帧连放第二次必然被挡 —— 所以连续施毒
            // 天然跨帧进行，本分支每帧检查一次即可，200ms 节流避免每帧查技能表。
            if (_player.Class == MirClass.Taoist && SEnvir.Now >= _nextPoisonCheckTime)
            {
                _nextPoisonCheckTime = SEnvir.Now.AddMilliseconds(200);

                if (BotSkills.TryApplyPoison(_player, monster))
                {
                    _lastAttackTime = SEnvir.Now;
                    LastMagic = BotText.Magic(MagicType.PoisonDust);

                    LastDetail = string.Format("给 {0} 补毒（距离 {1}）", MonsterName(monster), distance);

                    BotLog.Debug(Profile.Seq, LastDetail);

                    return;
                }
            }

            // 法师/道士先用魔法：射程 10 格，不用贴脸挨打。
            // 技能不可用（蓝不够/冷却中）时**原地等**，别往前凑 —— 法师近战等于送死。
            if (_player.Class != MirClass.Warrior && distance <= Globals.MagicRange && distance > AttackRange)
            {
                // 风筝走位（D14，2026-11-17 用户决策：魔法技能可用时边输出边后退）：
                // 法师/道士有远程技能、怪进入 KiteTriggerRange(3格) 时，先尝试后退一步保持 7~8 格，
                // 再施放魔法 —— 不再「技能可用就站桩输出、怪贴脸才退」。
                // 退不动（被围/贴墙/已达标 7 格）就留在原地输出，200ms 节流后再试。
                // 技能不可用（没蓝/冷却/没弹药）时不在这里退，落到下面 1329 的风筝分支纯后退。
                if ((_player.Class == MirClass.Taoist || _player.Class == MirClass.Wizard)
                    && HasRangedAttackReady(_player) && distance <= KiteTriggerRange
                    && _player.CurrentMP >= KiteMinMana)
                {
                    if (TryKiteStep(monster))
                    {
                        _kiteStartTime = SEnvir.Now;

                        LastDetail = string.Format("放风筝后退，拉开 {0} 距离",
                            BotNavigator.Distance(_player.CurrentLocation, monster.CurrentLocation));

                        BotLog.Debug(Profile.Seq, LastDetail);
                    }
                }

                // 周围 ≥2 只怪 → 优先群攻；群攻冷却/蓝不够会自动退回单体，不会干等。
                // 这条判断照客户端辅助挂机的 MapControl.PickAutoSkillType 抄的。
                bool preferGroup = BotPerception.CountMonstersAround(_player, Globals.MagicRange) >= 2;

                // 传 monster 进去是为了按**元素抗性**挑技能：
                // 本库 309 只怪里 204 只有非零抗性（最高 10 = 完全免疫），
                // 拿火系技能打火抗+7 的怪只剩 30% 伤害，还照样扣蓝 —— 就是用户说的「半天打不死、浪费蓝药」。
                MagicType attack = BotSkills.PickAttack(_player, monster, preferGroup, out string skillNote);

                // 同一段说明只记一次（连续几十帧内容相同），换了怪/换了抗性关系才会再记一行。
                if (!string.IsNullOrEmpty(skillNote))
                {
                    if (skillNote != _lastSkillNote)
                    {
                        _lastSkillNote = skillNote;
                        BotLog.Debug(Profile.Seq, string.Format("{0}：{1}", MonsterName(monster), skillNote));
                    }
                }
                else _lastSkillNote = null;

                if (attack != MagicType.None)
                {
                    if (!_player.CanCast)
                    {
                        // 没蓝、而且**身上一瓶蓝药都没有**：远程彻底打不了。
                        // 这时候不能原地站着（2026-11-18 用户现场：「没蓝也不反击」），
                        // 落到下面的走位/平砍分支贴脸砍 —— 道士平砍是能打死怪的。
                        // 有蓝药还留在原地等，是因为下一拍就会喝药，不用白跑一趟。
                        if (BotActions.CountPotion(_player, Stat.Mana) > 0)
                        {
                            LastDetail = string.Format("等 {0} 冷却（蓝 {1}/{2}）", BotText.Magic(attack), _player.CurrentMP, _player.Stats[Stat.Mana]);
                            return;
                        }
                    }
                    else if (BotSkills.TryCastAttack(_player, monster, preferGroup))
                    {
                        LastMagic = BotText.Magic(attack);
                        _lastAttackTime = SEnvir.Now;

                        LastDetail = string.Format("{0} 打 {1}（距离 {2}，蓝 {3}/{4}，{5}）",
                            attack, MonsterName(monster), distance, _player.CurrentMP, _player.Stats[Stat.Mana],
                            BotSkills.ElementNote(_player, monster, attack));

                        BotLog.Debug(Profile.Seq, LastDetail);

                        return;
                    }
                }
            }

            // 脱困上限（2026-11-15，生产诊断）：连续放风筝超过 KiteMaxDurationSeconds 仍未解决战斗，
            // 强制脱离放风筝（清零计时，本帧落平砍/靠近走位），避免长时间无效风筝。
            // 清零后下帧若条件满足可重新进入放风筝，重新计时。
            if (_kiteStartTime != default
                && SEnvir.Now - _kiteStartTime > TimeSpan.FromSeconds(KiteMaxDurationSeconds))
            {
                _kiteStartTime = default;
            }

            // 放风筝触发（2026-11-14 用户现场反馈，2026-11-16 D14 扩展法师）：
            // 法师/道士有远程技能时，怪进入 KiteTriggerRange(3格) 就优先后退拉开距离，
            // 等贴到 AttackRange(1格)才躲。部分怪攻击范围 2 格，在 2 格距离站着挨打 = 和不躲没区别。
            // 魔法分支技能不可用（没蓝/冷却/没弹药）时也会落到这，避免 fall through 到「朝怪靠近」的走位。
            // 保持距离目标 7~8 格（D14，见 TryKiteStep）：既躲开近战怪，又在射程（10格）内正常输出。
            //
            // 蓝量门（2026-11-15，生产诊断）：没蓝时放风筝毫无意义 —— 退了也放不出远程技能，
            // 只会在怪前来回跑、永不平砍（诊断证据：BOT#23 mp=0/133 连续放风筝 3 分钟零平砍）。
            // 有蓝才进放风筝，没蓝直接落后面的平砍/靠近分支。
            if ((_player.Class == MirClass.Taoist || _player.Class == MirClass.Wizard)
                && HasRangedAttackReady(_player) && distance <= KiteTriggerRange
                && _player.CurrentMP >= KiteMinMana)
            {
                if (TryKiteStep(monster))
                {
                    _kiteStartTime = SEnvir.Now;

                    LastDetail = string.Format("放风筝后退，拉开 {0} 距离",
                        BotNavigator.Distance(_player.CurrentLocation, monster.CurrentLocation));

                    return;
                }

                // 退不动（被围/贴墙）：贴脸（≤1格）才平砍，2~3 格够不着就原地等，
                // 下一帧 TryKiteStep 节流到点后再试后退，不硬冲怪堆。
                // 平砍命中即脱离放风筝，清零计时，下次再进放风筝重新计时。
                if (distance <= AttackRange && BotActions.TryAttack(_player, monster))
                {
                    _lastAttackTime = SEnvir.Now;
                    _kiteStartTime = default;

                    LastDetail = string.Format("砍 {0}（HP {1}/{2}）", MonsterName(monster), monster.CurrentHP, monster.Stats[Stat.Health]);
                }

                return;
            }

            // 战士技能分支（D13，2026-11-16）：刺杀剑术/半月弯刀/十方斩都是 Toggle 型，
            // 必须显式 Attack(dir, MagicType.Xxx) 触发（传 MagicType.None 是纯平砍，不触发技能）。
            // 技能不可用（没学/没开/没蓝/刺杀有遮挡）时退回后面的平砍。
            //
            // 与法师/道士的魔法分支不同：战士技能是近身触发（AttackRange=1 格贴脸），
            // 但刺杀剑术的二次攻击打 2 格直线（隔位刺杀），半月弯刀打三向近身，十方斩打正前方 1~7 格。
            if (_player.Class == MirClass.Warrior && distance <= AttackRange)
            {
                MirDirection dir = Functions.DirectionFromPoint(_player.CurrentLocation, monster.CurrentLocation);

                // 周围 ≥2 只怪 → 优先群攻（十方斩正前方直线 / 半月弯刀三向近身）。
                // 与法师/道士的 preferGroup 逻辑同构：群攻不可用就退回单体/平砍，别干等。
                if (BotPerception.CountMonstersAround(_player, AttackRange) >= 2)
                {
                    // 十方斩（40级，正前方1~7格直线群攻）优先于半月弯刀（24级，三向近身群攻）。
                    if (TryWarriorSkillAttack(_player, monster, dir, MagicType.DestructiveSurge))
                    {
                        _lastAttackTime = SEnvir.Now;
                        LastMagic = BotText.Magic(MagicType.DestructiveSurge);
                        LastDetail = string.Format("十方斩 打 {0}（距离 {1}）", MonsterName(monster), distance);
                        BotLog.Debug(Profile.Seq, LastDetail);
                        return;
                    }

                    if (TryWarriorSkillAttack(_player, monster, dir, MagicType.HalfMoon))
                    {
                        _lastAttackTime = SEnvir.Now;
                        LastMagic = BotText.Magic(MagicType.HalfMoon);
                        LastDetail = string.Format("半月弯刀 打 {0}（距离 {1}）", MonsterName(monster), distance);
                        BotLog.Debug(Profile.Seq, LastDetail);
                        return;
                    }
                }

                // 刺杀剑术（19级，2格正前方直线隔位刺杀）：D13 需 2 格正前方直线无遮挡。
                // 有遮挡（墙/怪挡在中间）时不施放，退回平砍 —— 否则打出去也是空放。
                if (BotNavigator.IsThrustingLineClear(_player, dir, monster.CurrentLocation)
                    && TryWarriorSkillAttack(_player, monster, dir, MagicType.Thrusting))
                {
                    _lastAttackTime = SEnvir.Now;
                    LastMagic = BotText.Magic(MagicType.Thrusting);
                    LastDetail = string.Format("刺杀剑术 打 {0}（距离 {1}）", MonsterName(monster), distance);
                    BotLog.Debug(Profile.Seq, LastDetail);
                    return;
                }
            }

            // 刺客技能分支（3-5，2026-11-16）：花开富贵/白莲/红莲/刺蔷薇/烈焰飞溅都是 AttackCast 型，
            // 必须显式 Attack(dir, MagicType.Xxx) 触发（传 MagicType.None 是纯平砍，不触发技能）。
            // 技能不可用（没学/没开 Toggle/没蓝/冷却中）时退回后面的平砍。
            //
            // 刺客的 MagicCast 型技能（出血/烈焰飞刀/撕裂/地狱火/闪光术）不在这里处理 ——
            // 它们走上面法师/道士的魔法分支（distance > AttackRange 时），由 PickAttack 自动选中。
            // 贴脸（distance <= AttackRange）时魔法分支不触发，这里只处理 AttackCast 型近战技能。
            if (_player.Class == MirClass.Assassin && distance <= AttackRange)
            {
                MirDirection dir = Functions.DirectionFromPoint(_player.CurrentLocation, monster.CurrentLocation);

                // 按目标抗性挑期望伤害最高的 AttackCast 型技能（与战士固定顺序不同，刺客技能多，按抗性选更合理）。
                MagicType attack = BotSkills.PickAssassinMeleeAttack(_player, monster);

                if (attack != MagicType.None && TryAssassinSkillAttack(_player, monster, dir, attack))
                {
                    _lastAttackTime = SEnvir.Now;
                    LastMagic = BotText.Magic(attack);
                    LastDetail = string.Format("{0} 打 {1}（距离 {2}）",
                        BotText.Magic(attack), MonsterName(monster), distance);
                    BotLog.Debug(Profile.Seq, LastDetail);
                    return;
                }
            }

            // 近身（或没蓝/没技能、非道士）：贴脸平砍。
            if (distance <= AttackRange)
            {
                if (BotActions.TryAttack(_player, monster))
                {
                    _lastAttackTime = SEnvir.Now;

                    LastDetail = string.Format("砍 {0}（HP {1}/{2}）", MonsterName(monster), monster.CurrentHP, monster.Stats[Stat.Health]);
                }

                return;
            }

            // 贴身（≤3 格）直接朝怪走一步，不跑整图 A*。
            //
            // 客户端自动挂机的 MoveTowardTarget 也是这个口径（distance > 3 才寻路，
            // 且寻路结果缓存 600ms）—— 近战追击时绝大多数帧都在这条路径上，
            // 每帧一次整图 A*（20~30ms）纯属浪费，30 个假人同时追击就能把主线程吃光。
            BotMoveResult result = distance <= 3
                ? (BotNavigator.TryStepAdjacent(_player, monster.CurrentLocation) ? BotMoveResult.Moved : BotMoveResult.Blocked)
                : BotNavigator.TryStepTowards(_player, monster.CurrentLocation);

            if (result == BotMoveResult.Moved)
            {
                TrackStuck();
                LastDetail = string.Format("靠近 {0}（距离 {1}）", MonsterName(monster), distance);
                return;
            }

            if (result == BotMoveResult.Busy) return;

            // A* 算不出路，或者终点就是怪自己站的格子（怪是阻挡物，最后一步永远迈不进去）：
            // 改成直线逼近，挑一个「可走、没被挡、确实更近」的相邻格。
            if (BotNavigator.TryStepAdjacent(_player, monster.CurrentLocation))
            {
                TrackStuck();
                LastDetail = string.Format("贴向 {0}（距离 {1}）", MonsterName(monster), distance);
                return;
            }

            // 侧移也不行 —— 多半是下一步被别的假人/宝宝长期占着。
            // 用「避让活物」的 A* 朝怪身边重算一次（终点是怪周围的可走格，不是怪自己那格）。
            //
            // 这一段之前只在 ProcessGoHunt 里有，追击路径上缺了它，
            // 结果就是 BOT#21 在潘夜村追 11 格外的猪时 8 秒只挪了 1 格（2026-11-13 第三轮实测）。
            // 同样要节流：整图 A* 一次 20~30ms。
            if (SEnvir.Now >= _nextAvoidStepTime)
            {
                _nextAvoidStepTime = SEnvir.Now.AddMilliseconds(400);

                if (BotNavigator.TryStepAvoidingLiveNear(_player, monster.CurrentLocation, AttackRange))
                {
                    TrackStuck();
                    LastDetail = string.Format("绕行靠近 {0}（距离 {1}）", MonsterName(monster), distance);
                    return;
                }
            }

            // 这只真的够不到，换一片区域。
            //
            // 只清 _target 是不够的：怪就在旁边，下一帧又会重新选中同一只，
            // 形成「够不到 → 换怪 → 又选中它」的死循环。实测 BOT#25 连续 57 次采样
            // （整轮 40 分钟）一直停在「目标够不到，换怪」，一动不动、零击杀。
            // 所以这里连刷怪点一起弃掉，并累积计数：连续多次就随机挪一步脱困。
            _target = null;
            _huntPoint = default;
            _stuckCount++;

            if (_stuckCount >= 3)
            {
                _stuckCount = 0;

                TryShuffleStep();

                EnterState(BotState.GoHunt, "连续够不到怪，换个区域", TimeSpan.FromSeconds(2));

                return;
            }

            EnterState(BotState.GoHunt, "目标够不到，换个刷怪点", TimeSpan.FromSeconds(10));
        }

        /// <summary>
        /// 随机往附近挪一步（走不通就换方向）。
        /// 用来把「贴着墙/河岸原地打转」的假人从死点弹出来。
        /// </summary>
        private bool TryShuffleStep()
        {
            Point origin = _player.CurrentLocation;

            for (int i = 0; i < 8; i++)
            {
                int dx = SEnvir.Random.Next(-6, 7);
                int dy = SEnvir.Random.Next(-6, 7);

                if (dx == 0 && dy == 0) continue;

                Point target = new Point(origin.X + dx, origin.Y + dy);

                if (BotNavigator.TryStepTowards(_player, target) == BotMoveResult.Moved)
                {
                    TrackStuck();

                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 法师/道士是否已学至少一个**可用**的远程攻击技能 —— 放风筝的前提。
        /// <para>
        /// 决定贴脸时是「放风筝后退」还是「近身平砍」：有远程技能才值得拉开距离继续放技能；
        /// 只有施毒术（12 级没学火符）或没弹药时，后退毫无意义，直接砍。
        /// 法师技能不需要弹药（<see cref="BotSkills.HasAmmo"/> 对未登记技能恒 true），
        /// 已学攻击技能即视为可用。
        /// </para>
        /// <para>
        /// 只查「学没学会 + 有没有弹药」，**不查蓝量/冷却** —— 蓝量门由调用方
        /// （放风筝触发条件 <c>_player.CurrentMP &gt;= KiteMinMana</c>）把关，
        /// 没蓝时不应进入放风筝分支（2026-11-15 生产诊断：没蓝放风筝 = 永不平砍）。
        /// </para>
        /// </summary>
        private bool HasRangedAttackReady(PlayerObject player)
        {
            foreach (MagicType type in BotSkills.AttackSkillsOf(player.Class))
            {
                if (!player.GetMagic(type, out MagicObject magic)) continue;

                if (!BotSkills.HasAmmo(player, type)) continue;

                // 蓝不够也放不出来（2026-11-18 用户现场：道士没蓝还一直放风筝，
                // 在怪面前来回跑、既不输出也不回城）。
                // 原来这里只查弹药，于是「蓝 0 但兜里有符」也判成「远程可用」，
                // 退到 7~8 格后 TryCastAttack 因蓝不足失败，下一帧又退 —— 无限风筝。
                if (!magic.CheckCost()) continue;

                return true;
            }

            return false;
        }

        /// <summary>
        /// 战士 Toggle 技能施放（D13，2026-11-16）：刺杀剑术/半月弯刀/十方斩都是开关型，
        /// 必须显式 <c>Attack(dir, MagicType.Xxx)</c> 触发（传 <see cref="MagicType.None"/> 是纯平砍，不触发技能）。
        /// <para>
        /// 施放前三重校验，与引擎 <c>AttackCast</c> 的判据一致，避免 <c>Attack()</c> 内
        /// <c>attackMagic != validMagic</c> 打错误日志且不攻击：
        /// <list type="bullet">
        /// <item><b>已学</b>：<see cref="PlayerObject.GetMagic"/> 能取到 MagicObject；</item>
        /// <item><b>已开</b>：Toggle 型技能的 <c>CanXxx</c> 为 true（未开启时 AttackCast 返回 Cast=false）；</item>
        /// <item><b>蓝够</b>：<see cref="MagicObject.CheckCost"/>（Cost ≤ CurrentMP，不够时 AttackCast 返回 Cast=false）。</item>
        /// </list>
        /// </para>
        /// <para>
        /// 注意：战士技能不需要弹药（不在 <see cref="BotSkills.AmmoCost"/> 里），
        /// <see cref="BotSkills.HasAmmo"/> 对未登记技能恒 true，这里不重复检查。
        /// </para>
        /// <para>通过校验则调 <c>Attack(dir, type)</c>，返回 true；任一不满足返回 false，调用方退回平砍。</para>
        /// </summary>
        private static bool TryWarriorSkillAttack(PlayerObject player, MonsterObject monster, MirDirection dir, MagicType type)
        {
            if (player == null || monster == null) return false;
            if (player.Dead || !player.CanAttack) return false;
            if (monster.Dead || monster.Node == null || !monster.Visible) return false;

            if (!player.GetMagic(type, out MagicObject magic)) return false;

            // Toggle 型技能：必须已开启（CanXxx=true），否则 AttackCast 检查 !CanXxx 返回 Cast=false。
            bool toggleOn = type switch
            {
                MagicType.Thrusting => player.Character.CanThrusting,
                MagicType.HalfMoon => player.Character.CanHalfMoon,
                MagicType.DestructiveSurge => player.Character.CanDestructiveSurge,
                _ => false,
            };
            if (!toggleOn) return false;

            // 蓝量门：AttackCast 检查 cost <= CurrentMP，不够则 Cast=false → Attack() 内打错误日志。
            if (!magic.CanUseMagic()) return false;
            if (!magic.CheckCost()) return false;

            player.Attack(dir, type);
            return true;
        }

        /// <summary>
        /// 刺客 AttackCast 型技能施放（3-5，2026-11-16）：花开富贵/白莲/红莲/刺蔷薇/烈焰飞溅
        /// 都是 AttackCast 型，必须显式 <c>Attack(dir, MagicType.Xxx)</c> 触发
        /// （传 <see cref="MagicType.None"/> 是纯平砍，不触发技能）。
        /// <para>
        /// 施放前三重校验，与引擎 <c>AttackCast</c> 的判据一致，避免 <c>Attack()</c> 内
        /// <c>attackMagic != validMagic</c> 打错误日志且不攻击：
        /// <list type="bullet">
        /// <item><b>已学</b>：<see cref="PlayerObject.GetMagic"/> 能取到 MagicObject；</item>
        /// <item><b>已开</b>：Toggle 型技能（烈焰飞溅）的 <c>CanFlameSplash</c> 为 true（未开启时 AttackCast 返回 Cast=false）；</item>
        /// <item><b>蓝够</b>：<see cref="MagicObject.CheckCost"/>（Cost ≤ CurrentMP，不够时 AttackCast 返回 Cast=false）。</item>
        /// </list>
        /// </para>
        /// <para>
        /// 注意：刺客 AttackCast 型技能不需要弹药（不在 <see cref="BotSkills.AmmoCost"/> 里），
        /// <see cref="BotSkills.HasAmmo"/> 对未登记技能恒 true，这里不重复检查。
        /// 冷却由 <see cref="BotSkills.PickAssassinMeleeAttack"/>（PickBest）提前筛掉。
        /// </para>
        /// <para>通过校验则调 <c>Attack(dir, type)</c>，返回 true；任一不满足返回 false，调用方退回平砍。</para>
        /// </summary>
        private static bool TryAssassinSkillAttack(PlayerObject player, MonsterObject monster, MirDirection dir, MagicType type)
        {
            if (player == null || monster == null) return false;
            if (player.Dead || !player.CanAttack) return false;
            if (monster.Dead || monster.Node == null || !monster.Visible) return false;

            if (!player.GetMagic(type, out MagicObject magic)) return false;

            // Toggle 型技能（烈焰飞溅）：必须已开启（CanFlameSplash=true），否则 AttackCast 检查 !CanFlameSplash 返回 Cast=false。
            bool toggleOn = type switch
            {
                MagicType.FlameSplash => player.Character.CanFlameSplash,
                _ => true,
            };
            if (!toggleOn) return false;

            // 蓝量门：AttackCast 检查 cost <= CurrentMP，不够则 Cast=false → Attack() 内打错误日志。
            if (!magic.CanUseMagic()) return false;
            if (!magic.CheckCost()) return false;

            player.Attack(dir, type);
            return true;
        }

        /// <summary>
        /// 放风筝后退一步：朝**远离怪**的方向挑一格，且避开怪群，成功返回 true。
        /// <para>
        /// 道士/法师贴脸时（<c>distance &lt;= AttackRange</c>）不硬砍，先退一格拉开距离，
        /// 下一帧回到魔法分支继续放远程技能 —— 这就是「放风筝」。
        /// </para>
        /// <para>
        /// <b>保持距离 7~8 格（2026-11-16，D14）</b>：已退到 <see cref="KiteKeepMin"/>(7格)
        /// 即达标不再后退；候选格以 <see cref="KiteKeepMax"/>(8格) 为目标区间评分 ——
        /// 距离越接近 8 格分越高，超过 8 格扣分（拉出射程边缘打不到怪、空放白扣蓝）。
        /// </para>
        /// <para>
        /// <b>避免退入怪群</b>（用户 2026-11-14 要求）：候选格周围 2 格内活怪 ≥2 只直接跳过，
        /// 评分 = 距离分 − 周围怪数 × 5，取最高分的方向。八个方向都不可走 / 不增距 / 怪群密集
        /// 时返回 false，调用方退回平砍（被逼到墙角就该打，不能无限后退）。
        /// </para>
        /// <para>整图扫怪要节流（<see cref="_nextKiteTime"/>，200ms），否则 30 个假人贴脸时每帧扫全图对象。</para>
        /// </summary>
        private bool TryKiteStep(MonsterObject monster)
        {
            if (SEnvir.Now < _nextKiteTime) return false;

            _nextKiteTime = SEnvir.Now.AddMilliseconds(200);

            Point origin = _player.CurrentLocation;
            Point danger = monster.CurrentLocation;

            int currentDist = BotNavigator.Distance(origin, danger);

            // D14：已退到保持距离下限（7 格）即达标，不再后退 —— 维持 7~8 格靠走位微调，
            // 继续退会拉出射程边缘打不到怪。
            if (currentDist >= KiteKeepMin) return false;

            // 正对「远离怪」的方向，向两边各偏最多 4 个角度找候选。
            MirDirection away = Functions.DirectionFromPoint(danger, origin);

            int[] offsets = { 0, 1, -1, 2, -2, 3, -3, 4 };

            int bestScore = -1;
            MirDirection bestDir = away;

            foreach (int offset in offsets)
            {
                MirDirection dir = (MirDirection)(((int)away + offset + 8) % 8);
                Point next = Functions.Move(origin, dir, 1);

                Cell cell = _player.CurrentMap?.GetCell(next);

                if (cell == null) continue;
                if (cell.IsBlocking(_player, false)) continue;

                // 必须比现在更远，否则等于没拉开。
                if (BotNavigator.Distance(next, danger) <= currentDist) continue;

                // 避免退入怪群：候选格周围 2 格内活怪 ≥2 只不退。
                int mobCount = BotPerception.CountMonstersAt(_player.CurrentMap, next, 2);

                if (mobCount >= 2) continue;

                // 距离分：8 格以内越远越高（满分 80）；超过 8 格逐格扣 20（退过头、射程外打不到）。
                int dist = BotNavigator.Distance(next, danger);
                int distScore = dist <= KiteKeepMax
                    ? dist * 10
                    : KiteKeepMax * 10 - (dist - KiteKeepMax) * 20;

                int score = distScore - mobCount * 5;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestDir = dir;
                }
            }

            if (bestScore < 0) return false;

            Point before = _player.CurrentLocation;

            _player.Move(bestDir, 1);

            if (_player.CurrentLocation == before) return false;

            TrackStuck();

            return true;
        }

        private void ProcessLoot()
        {
            // P1 修的硬伤：原���这个状态没有超时出口。
            // 背包满时引擎捡不动，假人却以为捡到了（TryPickUp 原来无条件返回 true），
            // 于是每一拍都重新找到同一个东西再捡一次 → 永久卡在拾取态、再也不练级。
            // 现在有三道保险：状态期限、背包容量检查、拾取结果的真实判定。
            //
            // 注意：期限是「兜底超时出口」，不是「执行延迟」。
            // （原来这里 `if (SEnvir.Now < _stateDeadline) return;` 会让假人进入 Loot 态后
            //   冻结整整 30 秒 —— 站在掉落物前不捡、不走位，正是「打怪爆物后半天不捡、
            //   站在物品前发呆」的根因。现在改为每帧立即捡取/走位，期限只在超时未捡到时兜底退出。）
            if (SEnvir.Now >= _stateDeadline)
            {
                EnterState(BotState.Idle, "捡拾超时，回去打怪", TimeSpan.FromSeconds(2));
                return;
            }

            // 背包满了就先别捡了 —— 去回城卖货腾格子（P1-2）。
            // 货币不进背包、直接进角色货币表，所以背包满时也允许继续捡货币。
            if (BotPerception.FreeInventorySlots(_player) <= 0)
            {
                EnterState(BotState.Idle, "背包满了，回城卖东西腾格子", TimeSpan.FromSeconds(3));
                return;
            }

            // 排除项过期就清掉（挡路的怪可能已走开，给原目标一次机会）。
            if (_lootExclude != null && SEnvir.Now >= _lootExcludeExpire)
                _lootExclude = null;

            ItemObject item = _lootExclude != null
                ? BotPerception.FindNearestLootExcluding(_player, LootRadius, _lootExclude)
                : BotPerception.FindNearestLoot(_player, LootRadius);

            // 普通掉落半径内没有 → 再看远一点有没有金币（2026-11-18 用户现场：地上有金币没捡）。
            // 金币不占格不占重，多跑几格也值；普通掉落不会因此扩大范围。
            if (item == null)
                item = BotPerception.FindNearestCurrency(_player, LootRadius + CurrencyLootBonus);

            if (item == null)
            {
                // 地上没东西可捡，但附近还有可挖的尸体（鸡/猪/牛掉落不落地）→ 去挖。
                // 用户 2026-11-15 观察：原来直接进 Idle，可挖尸体被留在现场。
                MonsterObject corpse = FindHarvestableCorpse();

                if (corpse != null)
                {
                    _harvestTarget = corpse;
                    _nextHarvestTime = DateTime.MinValue;

                    EnterState(BotState.Harvest, "没东西捡，去挖尸体", TimeSpan.FromSeconds(30));
                    return;
                }

                EnterState(BotState.Idle, "没东西可捡了", TimeSpan.FromSeconds(2));
                return;
            }

            int distance = BotNavigator.Distance(_player.CurrentLocation, item.CurrentLocation);

            if (distance <= 1)
            {
                // 货币不占背包格，背包满时也要捡（金币收入来源）。
                bool isCurrency = item.Item?.Info?.ItemType == ItemType.Currency;

                if (!isCurrency && BotPerception.FreeInventorySlots(_player) <= 0)
                {
                    EnterState(BotState.Idle, "背包满了，回城卖东西腾格子", TimeSpan.FromSeconds(3));
                    return;
                }

                // 负重也要留余量。引擎的 CanGainItems 只拦「装不下」，允许**刚好装满** ——
                // 于是假人捡到 66/66，之后去药店余量 0，一瓶药都买不到（NPCBuy 静默 return）。
                // 2026-11-12 生产配置（11 倍爆率）实测：这是补给失败的最后一条尾巴。
                if (!isCurrency && item.Item?.Info != null)
                {
                    long capacity = _player.Stats[Stat.BagWeight];
                    long after = _player.BagWeight + (long)item.Item.Info.Weight * Math.Max(1, item.Item.Count);

                    if (capacity > 0 && after > capacity * 9 / 10)
                    {
                        EnterState(BotState.Idle, "负重快满了，先回去卖东西", TimeSpan.FromSeconds(3));
                        return;
                    }
                }

                bool picked = BotActions.TryPickUp(_player, item);

                if (!picked)
                {
                    // 引擎没捡走（背包满、负重超了、或者被别人先捡了）。
                    // 连续失败就别死磕这一件，换个目标或直接回去。
                    // 2026-11-18：失败即排除 30 秒 —— 引擎拒绝的件重试也不会成功，
                    // 原地连按只会卡在「这个捡不到」的循环里，把别的掉落物一起耽误。
                    _lootExclude = item;
                    _lootExcludeExpire = SEnvir.Now.AddSeconds(30);

                    // 2026-11-18：把「为什么捡不到」一次说清（只在失败路径触发，噪音可控）。
                    // 现场见 BOT#69 每 10 秒进一次 Loot 又立刻失败 3 次 —— 靠这行定位是
                    // 归属 / 装不下 / 负重 / 堆叠哪一道把引擎拦住了。
                    if (item.Item != null)
                    {
                        UserItem ui = item.Item;
                        ItemCheck diagCheck = new ItemCheck(ui, ui.Count, ui.Flags, ui.ExpireTime);

                        BotLog.Debug(Profile.Seq, string.Format(
                            "捡取失败：{0} dist={1} 归属={2} 可装={3} 空格={4} 负重={5}/{6} 堆叠={7}/{8}",
                            ui.Info?.ItemName ?? "?", distance, item.CanPickUpItem(_player),
                            _player.CanGainItems(false, diagCheck),
                            BotPerception.FreeInventorySlots(_player), _player.BagWeight,
                            _player.Stats[Stat.BagWeight], ui.Count, ui.Info?.StackSize ?? 0));
                    }

                    _lootFailCount++;

                    if (_lootFailCount >= 3)
                    {
                        _lootFailCount = 0;

                        EnterState(BotState.Idle, BotPerception.FreeInventorySlots(_player) <= 0
                            ? "背包满了，回城卖东西腾格子"
                            : "这个捡不到，换一个", TimeSpan.FromSeconds(3));

                        return;
                    }

                    EnterState(BotState.Loot, "没捡到，再试一次", TimeSpan.FromSeconds(2));

                    return;
                }

                _lootFailCount = 0;
                _lootExclude = null;  // 捡到了，清掉排除表，下次重新按「最近」选目标

                // P1-4：捡到更好的装备就换上（真人也会这么干）。
                // 不换的话假人会一直用 1 级新手装 —— 效率低，而且背包被旧装备占满。
                TryEquipBetter();

                LastDetail = string.Format("捡 {0}", item.Item?.Info?.ItemName ?? "物品");

                // 捡到了也留 30 秒窗口继续捡附近的其它东西，但有上限，不会永久停在这。
                EnterState(BotState.Loot, LastDetail, TimeSpan.FromSeconds(30));

                return;
            }

            BotMoveResult result = BotNavigator.TryStepTowards(_player, item.CurrentLocation);

            if (result == BotMoveResult.Moved) TrackStuck();

            if (result == BotMoveResult.Blocked)
            {
                // 三级退路（2026-11-15，生产诊断 DIAG-C）：掉落物 dist=3、周围零怪、
                // 值得捡，仍可能因 A* 终点不可达（地形/其它阻挡物，非怪挡）走不到。
                // 原来 Blocked 直接放弃，物品永久遗留。现在：
                // ① 节流绕行：避开活物重算整图 A*（20~30ms，400ms 节流），绕开阻挡；
                // ② 换次近的：当前这件够不到就记入排除表（30 秒内不重试），换下一件；
                // ③ 都走不通才放弃。
                if (SEnvir.Now >= _nextLootBypassTime && BotNavigator.TryStepAvoidingLive(_player, item.CurrentLocation))
                {
                    _nextLootBypassTime = SEnvir.Now.AddMilliseconds(400);

                    EnterState(BotState.Loot, "绕行去捡掉落物", TimeSpan.FromSeconds(30));

                    return;
                }

                _lootExclude = item;
                _lootExcludeExpire = SEnvir.Now.AddSeconds(30);

                ItemObject next = BotPerception.FindNearestLootExcluding(_player, LootRadius, item);

                if (next != null)
                {
                    EnterState(BotState.Loot, "这件够不到，换一件捡", TimeSpan.FromSeconds(30));

                    return;
                }

                EnterState(BotState.Idle, "掉落物够不到", TimeSpan.FromSeconds(2));
            }
        }

        private void ProcessDead()
        {
            if (State != BotState.Dead)
            {
                Deaths++;
                EnterState(BotState.Dead, "死了，准备回城复活", TimeSpan.FromSeconds(2));
                BotLog.Info(Profile.Seq, string.Format("死亡（累计 {0} 次），2 秒后回城复活", Deaths));
                return;
            }

            if (SEnvir.Now < _stateDeadline) return;

            // 死亡惩罚不豁免（已决策）：经验/物品按引擎规则照掉，这里只负责复活。
            if (BotActions.TryTownRevive(_player))
                BotLog.Info(Profile.Seq, "已回城复活");

            // 复活没成功（金币不够/引擎拒绝）：别往下走，否则会在「死人」状态里去补给。
            if (_player.Dead)
            {
                EnterState(BotState.Dead, "复活失败，3 秒后重试", TimeSpan.FromSeconds(3));
                return;
            }

            // 复活会把人挪回绑定主城，死前那条路线的起点已经作废。
            // 不清掉的话，`if (_player.AutoPath != null) return;` 这类守卫会拦住
            // 复活后所有的重新规划，假人就一直站在原地。
            if (_player.AutoPath != null)
                AutoPathService.Instance.Cancel(_player);

            // 复活即回主城，中转的意义已经由「人回到主城」实现了 —— 清位，重新选图。
            _teleportViaHome = false;

            // 复活后**第一件事是补给**（2026-11-18 用户要求）。
            //
            // 原因：死亡按引擎规则掉背包物品，药和回城卷都可能掉光。原来复活后直接
            // 回练级点，于是出现「没药硬扛 + 没卷只能跑路」的死亡循环（用户现场观察）。
            // 复活点本来就在城里，就地补一趟只要十几秒，比再死一次划算得多。
            // 补给完成（FinishRestock）会清掉 stage/baseline，不影响后续正常补给。
            _restockStage = RestockStage.Potion;
            _restockBaselinePotions = -1;

            EnterState(BotState.Restock, "复活后先补给（死亡可能掉了药和回城卷）", TimeSpan.FromMinutes(5));
        }

        #endregion

        #region 生存（D3 会扩成完整的补给逻辑）

        /// <summary>血量过低就喝红药。找不到药就硬扛 —— D3 再加「回城买药」。</summary>
        #region 跨图

        /// <summary>
        /// 本图没怪了，找一张等级合适、人还不满的图跨过去。
        /// 跨图靠 <see cref="AutoPathService.TryStart"/>（244 张图的 CanAutoPath 已全开，
        /// 真正的进出限制仍由 <c>MovementInfo</c> 把关：等级、钥匙、副本、BOSS）。
        /// </summary>
        private bool TryTravelToHuntMap()
        {
            Map current = _player.CurrentMap;

            if (current?.Info == null) return false;

            // 刚换完图：先在本图待够 90 秒再考虑走（只有本图确实没这个等级的怪才放行）。
            // 见 _travelCooldown 的注释 —— 两跳把跨图成本降到 1 秒后，不设这条会形成零成本折返。
            if (SEnvir.Now < _travelCooldown && BotPerception.HasHuntValue(current.Info, _player.Level))
                return false;

            // 白名单（Config.BotHuntMaps）同样约束跨图目标：不填 = 不限制，填了就只在这些图之间流动。
            BotOptions options = Options;

            // 走的时候记一笔：下一趟不许立刻回到刚离开的这张图（见下方「防折返」）。
            void NoteDeparture()
            {
                _lastTravelFrom = current.Info;
                _lastTravelTime = SEnvir.Now;
            }

            // 等级窗口：能打得动（≤ 自己+4）且不至于太低（≥ 自己-8，否则经验少得没意义）。
            //
            // **必须按地图去重**：RespawnInfo 是「刷怪区」不是「地图」，一张图有十几个区域。
            // 原来直接 OrderBy(地图索引距离).Take(12)，潘夜村[6] 的十几个刷怪区会把
            // 12 个候选位全部占满，失乐园[5] 根本进不了候选 ——
            // 于是明明能从沙漠土城 3K 直传失乐园，假人却只能 A* 硬走潘夜村（2026-11-12 实测）。
            List<RespawnInfo> candidates = SEnvir.RespawnInfoList.Binding
                .Where(x => x.Region?.Map != null && x.Region.Map != current.Info && x.Monster != null)
                .Where(x => options.HuntMaps.Count == 0 || options.HuntMaps.Contains(x.Region.Map.Index))
                .Where(x => x.Monster.Level <= _player.Level + 4 && x.Monster.Level >= _player.Level - 8)
                .Where(x => x.Region.Map.MinimumLevel <= _player.Level)
                .GroupBy(x => x.Region.Map)
                .Select(g => g.OrderBy(x => Math.Abs(x.Monster.Level - _player.Level)).First())
                // 排序四要素：① 怪等级最贴近自己（经验效率）② 补给能力（D9：优先补给齐全的图/主城周边，
                // 减少「练级图没补给 → 回城 → 再跨图」的来回）③ 有六合石（能传送，不用腿儿着）
                // ④ 地图索引距离（近似「离得近」）。
                // 全服练级图有 60 多张，只按索引距离排会挑到八竿子打不着的地图。
                .OrderBy(x => Math.Abs(x.Monster.Level - _player.Level))
                .ThenByDescending(x => BotPerception.GetMapSupplyLevel(x.Region.Map))
                .ThenByDescending(x => BotPerception.HasTeleportStone(x.Region.Map))
                .ThenBy(x => Math.Abs(x.Region.Map.Index - current.Info.Index))
                .Take(12)
                .ToList();

            // 全服（白名单内）根本没有更合适的图 → **一张卷都别烧**。
            //
            // 原来「先判回城中转、后算候选」，于是本图练不动 + 别处也没好图时，
            // 假人照样烧一张 500 金币的回城卷跑回主城，在主城再选一遍还是原地，
            // 形成「洞穴 → 主城 → 洞穴」的来回折返（2026-11-12 交接文档记的就是这条）。
            if (candidates.Count == 0)
            {
                BotLog.Debug(Profile.Seq, string.Format(
                    "跨图放弃：白名单 {{{0}}} 里没有比 {1} 更合适的练级图（当前 {2} 级）",
                    string.Join(",", options.HuntMaps), current.Info.Description, _player.Level));

                return false;
            }

            // 防折返：刚离开的图，5 分钟内不再选。
            //
            // 两跳传送把跨图成本从「烧一张卷 + 走 1~3 分钟」降到「花点钱 1 秒到」，
            // 折返变得毫无痛感 —— 2026-11-12 实测 BOT#08 在 90 秒内
            // 潘夜村 → 失乐园 → 潘夜村 各跑一趟（两跳 15,000 金币/次），
            // 人一直在传送、怪一只没打。有其它候选就换一张，没有才允许回头。
            if (_lastTravelFrom != null && SEnvir.Now - _lastTravelTime < AntiOscillationWindow)
            {
                List<RespawnInfo> forward = candidates.Where(x => x.Region.Map != _lastTravelFrom).ToList();

                if (forward.Count > 0) candidates = forward;
            }

            // 现象 1（2026-11-14，群体同步跨图）：选图是「等级贴近→有石头→索引距离」的**确定性**排序，
            // 同一等级段的假人各自独立算，算出来是**同一张**目标图；pass=1 又放开容量、无负载均衡，
            // 群体同时满足跨图条件就**同时涌向同一张图**。用选图认领表打散：跳过已被别的假人认领的
            // 目标图，让后来的假人选下一张。若过滤后候选为空（极端：候选全被占），回退原候选，
            // 保证不卡死跨图（软认领语义，见 BotMapClaims）。
            List<RespawnInfo> unclaimed = candidates
                .Where(x => !BotMapClaims.IsClaimedByOther(x.Region.Map.Index, Profile.Seq))
                .ToList();

            if (unclaimed.Count > 0)
                candidates = unclaimed;

            // ① 本图没有六合圣石 → 先回主城中转。
            //
            // 为什么必须放在挑目标图之前：假人一旦离开主城，就再也传不了 ——
            // 「南方通道 / 沙漠土城 / 诺玛村落」这些中途图**根本没有六合石**，
            // 于是在那里只能 A* 逐格走，一路被怪打死 → 复活 → 再走，形成活锁。
            // D5 实测：11 个假人困在 225/9/8 → 潘夜村[6] 的长途上，传送 0 次、死 441 / 杀 599。
            //
            // 回城后 _teleportViaHome 立即清位，下一次决策就在主城用石头直达目标图。
            if (BotPerception.FindTeleportStone(current) == null && TryStartReturnHomeForTeleport())
            {
                NoteDeparture();
                return true;
            }

            // 两轮 × 两个阶段：
            //   轮 0 = 只挑没满的图；轮 1 = 放开容量（宁可超员也别让人卡在城里不动）。
            //   阶段 A = 能直达传送的图；阶段 B = 只能 A* 走过去的图。
            //
            // **阶段 A 必须先整体扫完**：2026-11-12 实测，沙漠土城的六合石只有
            // 「失乐园 / 沙巴克城 / 诺玛村庄」三个按钮，**没有潘夜村**。
            // 原来的写法是「候选 #1 传不过去就地 A* 并 return」，于是假人放着
            // 能免费传送的失乐园不去，硬走 A* 去潘夜村，路上被打死 ——
            // 白名单 {1,5,6} 里明明有一张能直达的图。
            for (int pass = 0; pass < 2; pass++)
            {
                // 阶段 A：优先挑「六合石能直达」的图（不走路，也就不会被路上的怪打死）。
                foreach (RespawnInfo candidate in candidates)
                {
                    MapInfo map = candidate.Region.Map;

                    if (pass == 0 && CountBotsOnMap(map) >= MapCapacity) continue;

                    // 懒加载：地图没加载时区域点列表是空的，必须先 GetMap 触发一次。
                    if (SEnvir.GetMap(map) == null) continue;

                    List<Point> points = candidate.Region.PointList;

                    if (points == null || points.Count == 0) continue;

                    if (TryStartTeleport(map, points[SEnvir.Random.Next(points.Count)]))
                    {
                        NoteDeparture();
                        return true;
                    }
                }

                // 阶段 A 全灭才走到这里。
                if (pass == 0)
                {
                    if (candidates.Count > 0)
                        LogTeleportCandidates(candidates, current);

                    // 本图**有石头却一个目标图都传不到**（石头只通内城 / 按钮里没有这些图）
                    // → 回主城中转。主城的石头按钮更全：比奇城能直达潘夜村和失乐园。
                    // 这比在沙漠土城硬走 A* 去潘夜村安全得多。
                    if (BotPerception.FindTeleportStone(current) != null &&
                        TryStartReturnHomeForTeleport())
                    {
                        NoteDeparture();
                        return true;
                    }
                }

                // 阶段 B：一张能直达的都没有（本图没石头 / 没直达按钮）→ A* 逐格跨图。
                foreach (RespawnInfo candidate in candidates)
                {
                    MapInfo map = candidate.Region.Map;

                    if (pass == 0 && CountBotsOnMap(map) >= MapCapacity) continue;

                    if (SEnvir.GetMap(map) == null) continue;

                    List<Point> points = candidate.Region.PointList;

                    if (points == null || points.Count == 0) continue;

                    Point point = points[SEnvir.Random.Next(points.Count)];

                    if (!AutoPathService.Instance.TryStart(_player, map, point)) continue;

                    _travelMap = map;
                    _travelPoint = point;
                    _travelVia = null;
                    _travelFailures = 0;
                    _emptyHunts = 0;
                    _lastHuntSuccessTime = SEnvir.Now;

                    // 现象 1：A* 跨图提交同样认领目标图，打散群体同步跨图。
                    BotMapClaims.Claim(map.Index, Profile.Seq);

                    BotLog.Info(Profile.Seq, string.Format(
                        "跨图(A*)：{0}[{1}] → {2}[{3}]，白名单 {{{4}}}，当前图六合石={5}",
                        current.Info.Description, current.Info.Index,
                        map.Description, map.Index,
                        string.Join(",", options.HuntMaps),
                        BotPerception.FindTeleportStone(current) != null ? "有" : "无"));

                    EnterState(BotState.Travel, string.Format("跨图前往 {0}", map.Description), TimeSpan.FromMinutes(5));

                    NoteDeparture();

                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 阶段 A（能直达传送的图）全灭时打一条诊断：候选有哪几张图，每张为什么传不了。
        /// <para>
        /// 存在的意义：跨图机制出问题时，「明明有石头却还在 A*」是最常见的现象，
        /// 而光看 A* 那行日志无法区分「本图没石头」「石头没直达标图」「钱不够」
        /// 「候选里根本没有那张能传的图」——这四种原因的修法完全不同。
        /// </para>
        /// </summary>
        private void LogTeleportCandidates(List<RespawnInfo> candidates, Map current)
        {
            NPCObject stone = BotPerception.FindTeleportStone(current);

            if (stone == null) return;

            List<string> parts = new List<string>();

            foreach (RespawnInfo candidate in candidates)
            {
                MapInfo map = candidate.Region.Map;

                int button = BotPerception.FindTeleportButton(stone.NPCInfo, map.Index);

                if (button < 0)
                {
                    parts.Add(string.Format("{0}[{1}]=无直达", map.Description, map.Index));
                    continue;
                }

                int cost = BotPerception.CheapestTeleportCost(stone.NPCInfo, map.Index);

                parts.Add(cost > 0 && GoldAmount() < cost
                    ? string.Format("{0}[{1}]=没钱({2})", map.Description, map.Index, cost)
                    : string.Format("{0}[{1}]=可传", map.Description, map.Index));
            }

            BotLog.Info(Profile.Seq, string.Format(
                "跨图候选 {0} 个（本图 {1} 有石头）：{2}",
                candidates.Count, current.Info.Description, string.Join("，", parts)));
        }

        private void ProcessTravel()
        {
            if (_travelMap == null)
            {
                EnterState(BotState.Idle, "没有跨图目标", TimeSpan.FromSeconds(5));
                return;
            }

            if (_player.CurrentMap?.Info == _travelMap)
            {
                AutoPathService.Instance.Cancel(_player);

                ReleaseTravelClaim();

                _travelMap = null;
                _travelVia = null;
                _emptyHunts = 0;
                _lastHuntSuccessTime = SEnvir.Now;

                _travelCooldown = SEnvir.Now + TravelCooldownAfterArrival;

                EnterState(BotState.GoHunt, "到达目标地图，开始找怪", TimeSpan.FromSeconds(HuntTimeoutSeconds));

                return;
            }

            if (SEnvir.Now >= _stateDeadline)
            {
                FinishTravel(string.Format("跨图超时（5 分钟没走到 {0}），先在本图练级", _travelMap.Description));
                return;
            }

            if (_player.AutoPath != null)
            {
                if (SEnvir.Now >= _travelDebugTime)
                {
                    _travelDebugTime = SEnvir.Now.AddSeconds(10);

                    BotLog.Info(Profile.Seq, string.Format(
                        "跨图进行中：在 {0}（{1},{2}），最短路点 {3} 个，等待起步={4}，封包等待={5}，可移动={6}",
                        _player.CurrentMap?.Info?.Description ?? "-",
                        _player.CurrentLocation.X, _player.CurrentLocation.Y,
                        _player.AutoPath.Path?.Count ?? -1,
                        _player.AutoPath.WaitingForMovementStart,
                        _player.PacketWaiting,
                        _player.CanMove));
                }

                LastDetail = string.Format("前往 {0}", _travelMap.Description);
                return;
            }

            // AutoPath 已经结束但人没到：要么被怪打断，要么路径失效，重规划几次。
            _travelFailures++;

            if (_travelFailures > 3 || !AutoPathService.Instance.TryStart(_player, _travelMap, _travelPoint))
            {
                FinishTravel(string.Format("去 {0} 的路线走不通", _travelMap.Description));
                return;
            }

            LastDetail = string.Format("重新规划去 {0}", _travelMap.Description);
        }

        /// <summary>
        /// 释放当前跨图目标图的选图认领（现象 1，2026-11-14）。
        /// <para>
        /// 到达目标图、放弃跨图（<see cref="FinishTravel"/>）、回城重选
        /// （<see cref="TryStartReturnHomeForTeleport"/>）时调用 —— 认领只在「正在跨向该图」
        /// 这段时间有效，跨图结束就该让出，否则别的假人会一直跳过这张图。
        /// </para>
        /// </summary>
        private void ReleaseTravelClaim()
        {
            if (_travelMap != null)
                BotMapClaims.Release(_travelMap.Index, Profile.Seq);
        }

        private void FinishTravel(string detail)
        {
            AutoPathService.Instance.Cancel(_player);

            ReleaseTravelClaim();

            _travelMap = null;
            _travelVia = null;
            _travelFailures = 0;
            _emptyHunts = 0;
            _lastHuntSuccessTime = SEnvir.Now;
            _teleportViaHome = false;

            BotLog.Info(Profile.Seq, detail);

            EnterState(BotState.Idle, detail, TimeSpan.FromSeconds(30));
        }

        /// <summary>
        /// 用主城的「六合圣石」传送到目标图。
        /// <para>
        /// **为什么优先传送**：A\* 跨图只能走「北方通道」这类刷怪图，低级假人一路被打死
        /// → 复活 → 再跨图，形成活锁（D5 实测 死 77 / 杀 16）。传送石直接送到目标图。
        /// </para>
        /// <para>
        /// 传送要钱（3K~10K）。钱不够 / 没有直达按钮 / 本图没有传送石 → 退回 A\* 跨图。
        /// </para>
        /// </summary>
        private void ProcessTeleport()
        {
            // ★ 回城中转优先于一切：本图没有六合石，先回主城再说。
            //   放在 _travelMap 判空之前 —— 中转阶段是「先回家、回家后重新选图」，
            //   此刻还没有目标图（_travelMap == null 是正常状态，不是错误）。
            if (_teleportViaHome)
            {
                ProcessReturnHomeForTeleport();
                return;
            }

            if (_travelMap == null)
            {
                EnterState(BotState.Idle, "没有传送目标", TimeSpan.FromSeconds(5));
                return;
            }

            // 已经到了
            if (_player.CurrentMap?.Info == _travelMap)
            {
                ReleaseTravelClaim();

                _travelMap = null;
                _travelVia = null;
                _emptyHunts = 0;
                _lastHuntSuccessTime = SEnvir.Now;
                _blockedCount = 0;
                _teleportViaHome = false;

                _travelCooldown = SEnvir.Now + TravelCooldownAfterArrival;

                EnterState(BotState.GoHunt, "传送到达，开始找怪", TimeSpan.FromSeconds(HuntTimeoutSeconds));
                return;
            }

            // 两跳：还没到中转图就先去中转图；到了中转图再换回最终目标。
            // 判定用「当前图 == 中转图」，所以到了中转图这一帧自动切到最终目标，不需要额外状态。
            bool atHop = _travelVia != null && _player.CurrentMap?.Info == _travelVia;

            if (atHop)
            {
                // 到了中转城：给第二跳重新计时（城内走到石头可能要 60 秒以上）。
                if (_stateDeadline < SEnvir.Now.AddMinutes(2))
                    _stateDeadline = SEnvir.Now.AddMinutes(2);
            }

            MapInfo hop = atHop || _travelVia == null ? _travelMap : _travelVia;

            if (SEnvir.Now >= _stateDeadline)
            {
                FinishTravel(string.Format("传送超时（{0} 分钟没送到 {1}），先在本图练级",
                    _travelVia == null ? 2 : 5, _travelMap.Description));
                return;
            }

            NPCObject stone = BotPerception.FindTeleportStoneFor(_player.CurrentMap, hop.Index);

            if (stone == null)
            {
                // 中转图突然找不到石头（数据被改 / 中转城没石头了）→ 丢掉中转，退回直传或 A*。
                if (hop != _travelMap)
                {
                    _travelVia = null;
                    BotLog.Warn(Profile.Seq, string.Format(
                        "两跳中转失效：{0} 没有能直达 {1} 的六合石，改回直传 / A*",
                        hop.Description, _travelMap.Description));
                }

                FallbackToTravel("当前地图没有能直达目标图的六合圣石，改走 A* 跨图");
                return;
            }

            int distance = BotNavigator.Distance(_player.CurrentLocation, stone.CurrentLocation);

            // 石头在城另一头时（主城动辄上百格）同样交给 AutoPath，贪心走格会卡在拐角。
            if (distance > AutoPathToNpcDistance)
            {
                if (_player.AutoPath != null)
                {
                    LastDetail = string.Format("自动寻路走向{0}（{1} 格）", stone.Name, distance);
                    return;
                }

                if (AutoPathService.Instance.TryStart(_player, stone.NPCInfo))
                {
                    _blockedCount = 0;
                    LastDetail = string.Format("自动寻路走向{0}（{1} 格）", stone.Name, distance);
                    return;
                }
            }

            if (_player.AutoPath != null)
                AutoPathService.Instance.Cancel(_player);

            // 同补给流程：NPC 自己是阻挡物，终点用「周围一格」，距离门槛 2。
            if (distance > 2)
            {
                BotMoveResult move = BotNavigator.TryStepTowardsNear(_player, stone.CurrentLocation, 1);

                if (move == BotMoveResult.Moved)
                {
                    _blockedCount = 0;
                    LastDetail = string.Format("走向{0}（{1} 格）", stone.Name, distance);
                    return;
                }

                if (move == BotMoveResult.Busy) return;

                if (BotNavigator.TryStepAdjacent(_player, stone.CurrentLocation))
                {
                    _blockedCount = 0;
                    LastDetail = string.Format("贴近{0}（{1} 格）", stone.Name, distance);
                    return;
                }
            }

            // 对话没开 → 先开，开完等下一帧再按按钮（避免每帧重复 NPCCall 把页重置回入口）
            if (_player.NPC == null || _player.NPC.ObjectID != stone.ObjectID)
            {
                if (!BotActions.TryOpenNpc(_player, stone))
                {
                    _blockedCount++;

                    if (_blockedCount > 20)
                        FallbackToTravel(string.Format("走不到传送石 {0}（{1} 格外始终过不去）", stone.Name, distance));

                    return;
                }

                _blockedCount = 0;
                LastDetail = string.Format("打开 {0}，准备传送", stone.Name);
                return;
            }

            int button = BotPerception.FindTeleportButton(stone.NPCInfo, hop.Index);

            if (button < 0)
            {
                BotActions.CloseNpc(_player);

                if (hop != _travelMap)
                {
                    _travelVia = null;
                    BotLog.Warn(Profile.Seq, string.Format(
                        "两跳中转失效：{0} 的石头没有直达 {1} 的按钮，改回直传 / A*",
                        hop.Description, _travelMap.Description));
                }

                FallbackToTravel(string.Format("传送石没有直达 {0} 的按钮", hop.Description));
                return;
            }

            // 按下按钮后引擎会在 DoActions 里同步执行 Teleport，玩家当帧就换图了；
            // 此时 NPCPage 已被清空，所以**不能用返回值判断成败**。
            BotActions.TryPressButton(_player, button);

            LastDetail = hop == _travelMap
                ? string.Format("传送至 {0}", _travelMap.Description)
                : string.Format("经 {0} 中转，前往 {1}", hop.Description, _travelMap.Description);
        }

        /// <summary>传送走不通时退回 A* 跨图（保留目标，只换交通方式）。</summary>
        private void FallbackToTravel(string reason)
        {
            BotActions.CloseNpc(_player);

            _blockedCount = 0;
            _travelFailures = 0;
            _teleportViaHome = false;
            _travelVia = null;

            BotLog.Info(Profile.Seq, reason);

            if (_travelMap != null && AutoPathService.Instance.TryStart(_player, _travelMap, _travelPoint))
            {
                EnterState(BotState.Travel,
                    string.Format("改走 A* 跨图前往 {0}", _travelMap.Description), TimeSpan.FromMinutes(5));
                return;
            }

            FinishTravel(reason + "，放弃跨图");
        }

        /// <summary>
        /// 装备「先回主城、再用六合石传送」这次中转。
        /// <para>
        /// 触发条件（缺一不可）：
        /// ① 有绑定主城，且人不在主城；
        /// ② 主城**真的有**六合石（否则回去也是白烧一张卷）；
        /// ③ 不在退避冷却里（上一次中转刚失败过）。
        /// </para>
        /// <para>
        /// 返回 true = 已切到 <see cref="BotState.Teleport"/>，调用方直接 return。
        /// 返回 false = 中转不可用，调用方继续走 A* 逐格跨图（保持原有降级路径）。
        /// </para>
        /// </summary>
        private bool TryStartReturnHomeForTeleport()
        {
            if (SEnvir.Now < _returnHomeCooldown) return false;

            MapInfo home = _player.Character?.BindPoint?.BindRegion?.Map;

            if (home == null) return false;

            Map current = _player.CurrentMap;

            // 已经在主城还回不去 —— 主城自己没石头的话，中转这条路本来就不成立。
            if (current?.Info == null || current.Info == home) return false;

            Map homeMap = SEnvir.GetMap(home);

            if (homeMap == null || BotPerception.FindTeleportStone(homeMap) == null) return false;

            _teleportViaHome = true;

            // 目标图作废：回城后按当时的等级 / 拥挤度重新选，别死守一个可能已经不合适的目标。
            // 释放对该目标图的选图认领，让别的假人可以选它（现象 1）。
            ReleaseTravelClaim();
            _travelMap = null;
            _travelVia = null;
            _travelPoint = default;
            _travelFailures = 0;
            _emptyHunts = 0;
            _lastHuntSuccessTime = SEnvir.Now;
            _blockedCount = 0;

            ReturnHomeForTeleportCount++;

            int scrolls = BotActions.CountTownScroll(_player);

            BotLog.Info(Profile.Seq, string.Format(
                "跨图中转：{0}[{1}] 没有可用的六合石传送，先回 {2}[{3}] 再选图（回城卷 {4} 张）",
                current.Info.Description, current.Info.Index,
                home.Description, home.Index, scrolls));

            // 5 分钟：用卷只要 1 秒，但没卷时要走回去（跨图 AutoPath 实测 60 秒+）。
            EnterState(BotState.Teleport,
                string.Format("回 {0} 中转，再用六合石传送", home.Description), TimeSpan.FromMinutes(5));

            return true;
        }

        /// <summary>
        /// 中转的第一个阶段：不管用什么办法，先把自己弄回主城。
        /// 到主城后清掉 <see cref="_teleportViaHome"/>，下一帧交回 <see cref="ProcessTeleport"/> 找石头。
        /// </summary>
        private void ProcessReturnHomeForTeleport()
        {
            MapInfo home = _player.Character?.BindPoint?.BindRegion?.Map;

            if (home == null)
            {
                AbortReturnHome("角色没有绑定主城");
                return;
            }

            // ① 已经回到主城：交回正常传送流程（主城必有六合石）。
            if (_player.CurrentMap?.Info == home)
            {
                if (_player.AutoPath != null)
                    AutoPathService.Instance.Cancel(_player);

                _teleportViaHome = false;
                _blockedCount = 0;
                _travelFailures = 0;

                BotLog.Info(Profile.Seq, string.Format("已回到 {0}，改用六合石传送", home.Description));

                LastDetail = string.Format("已回到 {0}，准备用六合石传送", home.Description);
                return;
            }

            // ② 优先用回城卷瞬移：1 秒回城，而走图要 60 秒以上，还可能在路上被打死。
            if (TryReturnHomeByScroll(home))
            {
                LastDetail = string.Format("用回城卷回 {0} 中转", home.Description);
                return;
            }

            // ③ 没卷 / 本图禁止回城（幽灵船、副本）→ 走回去。
            //    方向是朝安全区，通常穿过的是低级图，比继续往高级图走安全得多。
            if (_player.AutoPath != null)
            {
                LastDetail = string.Format("走回 {0} 中转（到城后再用六合石传送）", home.Description);
                return;
            }

            if (!AutoPathService.Instance.TryStart(_player, home, HomePoint(home)))
            {
                AbortReturnHome(string.Format("回 {0} 的路线规划失败（等级/道具限制）", home.Description));
                return;
            }

            LastDetail = string.Format("走回 {0} 中转", home.Description);
        }

        /// <summary>
        /// 放弃中转：清标记 + 退避 + 退回练级。
        /// <para>
        /// **退避不能省**：没有它会出现「回城失败 → 立刻再选图 → 又触发回城中转」的烧卷空转，
        /// 一卷 500 金币，假人会在几分钟内把家底烧光。
        /// </para>
        /// </summary>
        private void AbortReturnHome(string reason)
        {
            _teleportViaHome = false;
            _returnHomeFailStreak++;

            int seconds = Math.Min(300, 60 << Math.Min(_returnHomeFailStreak - 1, 3));

            _returnHomeCooldown = SEnvir.Now.AddSeconds(seconds);

            AutoPathService.Instance.Cancel(_player);

            ReleaseTravelClaim();

            _travelMap = null;
            _travelVia = null;
            _travelFailures = 0;

            BotLog.Warn(Profile.Seq, string.Format(
                "回城中转放弃：{0}（{1} 秒内不再尝试，累计 {2} 次）", reason, seconds, _returnHomeFailStreak));

            EnterState(BotState.Idle, reason + "，先在本图练级", TimeSpan.FromSeconds(30));
        }

        /// <summary>能不能用传送石去目标图（有石头 + 有直达按钮 + 钱够；本图没有直传时再试两跳）。</summary>
        private bool TryStartTeleport(MapInfo map, Point point)
        {
            MapInfo current = _player.CurrentMap?.Info;

            // 必须找「能直达这张图」的那颗石头：一个城里常常有好几颗，
            // 内传送员的按钮只通向城内，外传送员才有跨城按钮（沙漠土城实测）。
            NPCObject stone = BotPerception.FindTeleportStoneFor(_player.CurrentMap, map.Index);

            MapInfo via = null;
            int cost = 0;

            if (stone != null)
            {
                int button = BotPerception.FindTeleportButton(stone.NPCInfo, map.Index);

                if (button < 0) return false;

                cost = BotPerception.CheapestTeleportCost(stone.NPCInfo, map.Index);

                if (cost > 0 && GoldAmount() < cost) return false;
            }
            else
            {
                // 本图没有直达标图的石头 → 试「本图 → 中转图 → 目标图」两跳。
                if (current == null) return false;

                if (!BotPerception.TryFindTwoHopRoute(current, map.Index, out int viaIndex)) return false;

                via = BotPerception.GetMapInfo(viaIndex);

                if (via == null) return false;

                int cost1 = BotPerception.CheapestTeleportCostFrom(current, via.Index);
                int cost2 = BotPerception.CheapestTeleportCostFrom(via, map.Index);

                if (cost1 < 0 || cost2 < 0) return false;

                cost = cost1 + cost2;

                // 两跳要一次性确认总价付得起：第一跳买得起、第二跳买不起，
                // 会把人扔在中转城里（还可能是个没药的城），比不走更糟。
                if (cost > 0 && GoldAmount() < cost) return false;
            }

            _travelMap = map;
            _travelPoint = point;
            _travelVia = via;
            _travelFailures = 0;
            _emptyHunts = 0;
            _lastHuntSuccessTime = SEnvir.Now;
            _blockedCount = 0;

            // 现象 1：提交跨图即认领目标图，让同帧后来的假人跳过它、选下一张，打散群体同步跨图。
            BotMapClaims.Claim(map.Index, Profile.Seq);

            // 能直接传就别再走中转分支（中转标记可能从上一次补给打断里残留）。
            _teleportViaHome = false;

            // 用 Warn 而不是 Info：生产配置 BotLogLevel=2（Warn），Info 会被整体过滤，
            // 而「这次跨图到底走的是传送还是腿儿着」是判断跨图机制有没有生效的唯一线索。
            if (via == null)
            {
                BotLog.Info(Profile.Seq, string.Format("{0} 已不适合继续练级（{1} 级），传送去 {2}（花费 {3}）",
                    _player.CurrentMap.Info.Description, _player.Level, map.Description,
                    cost > 0 ? cost.ToString() : "免费"));

                EnterState(BotState.Teleport, string.Format("传送前往 {0}", map.Description), TimeSpan.FromMinutes(2));
            }
            else
            {
                BotLog.Info(Profile.Seq, string.Format(
                    "{0} 没有直达 {1} 的六合石，走两跳：{0} → {2} → {1}（总花费 {3}）",
                    _player.CurrentMap.Info.Description, map.Description, via.Description,
                    cost > 0 ? cost.ToString() : "免费"));

                EnterState(BotState.Teleport, string.Format("经 {0} 中转前往 {1}", via.Description, map.Description),
                    TimeSpan.FromMinutes(5));
            }

            return true;
        }

        /// <summary>指定地图上现在有几个假人（含自己）。</summary>
        private int CountBotsOnMap(MapInfo map)
        {
            int count = 0;
            foreach (PlayerObject player in SEnvir.Players)
            {
                if (player?.Bot == null) continue;
                if (player.CurrentMap?.Info != map) continue;

                count++;
            }

            return count;
        }

        #endregion

        /// <summary>
        /// 保命与补给。返回 true = 本帧已经切到别的状态，调用方应直接 return。
        /// </summary>
        private bool ProcessSurvival()
        {
            // 转生进行中：不要再被补给 / 卖货 / 跨图抢走。
            // 实测（2026-11-12 14:35）：BOT#01 走到市政厅、刚打开管理员，
            // 就被「负重快满了先卖货」切到 Trade，转生流程被打断，
            // 结果在市政厅里找卖 Meat 的商人，然后被跨图中转又送回比奇城。
            if (State == BotState.Rebirth) return false;

            // 0.5) 威胁检测：被怪攻击就切战斗还击（2026-11-15）。
            //
            // 原来假人只有「主动找怪」才会进入 Fight（GoHunt 路径的两处 EnterState），
            // 在捡取、挖尸、走位途中被怪偷袭时不会还击，只能挨打 —— 用户观察的
            // 「中途被其他怪攻击中断」场景，中断后假人既不还击也不清剿遗留。
            //
            // 判据用 monster.Target == _player（怪已锁定并正在攻击本假人），
            // 比「附近有怪」精确：路过的怪、刚刷出来的怪不会误触发。
            // 传送中不切：传送是跨图动作，切战斗会打断传送流程。
            if (State != BotState.Fight && State != BotState.Teleport && State != BotState.Rebirth)
            {
                MonsterObject attacker = BotPerception.FindAttacker(_player, ThreatRadius);

                if (attacker != null)
                {
                    _lastHurtTime = SEnvir.Now;
                    // Restock 优先回城（2026-11-17 用户决策，修 Fight/Restock 震荡）：
                    // 补给回城途中被怪攻击，只要还在往目的地移动（AutoPath 推进中、位置在变），
                    // 就继续回城不还击 —— 怪跟着跑会跟丢/被守卫清掉/进安全区脱战，
                    // 硬切 Fight 只会在「回城 ↔ 还击」间反复横跳、红药越耗越多。
                    // 被围走不动（位置连续多步没变）或没路可走（AutoPath 为空），才切 Fight 还击，
                    // 同时记下被打断的补货待办，战斗结束由 EnterState 统一接续回 Restock。
                    // 2026-11-18：Trade 与 Restock 同属「出城办事」——`needClear = HasSellableItems()`
                    // 之后补给前必先进 Trade 卖货，野外被怪咬就切 Fight、打完又回 Trade，
                    // 实测 BOT#10 250ms 级 Trade↔Fight 横跳 612 次/6 分钟。故两者共用同一套豁免。
                    if (State == BotState.Restock || State == BotState.Trade)
                    {
                        string errand = State == BotState.Trade ? "卖货" : "补给";

                        // 安全区豁免（2026-11-17 修复城内买药震荡）：
                        // 城内买药时站在商人旁，AutoPath 本就为空，原判据把「没路」一律当
                        // 「被围」切 Fight；而安全区怪打不到，战斗结束回 Restock 后威胁仍在
                        // 又切 Fight，形成 250ms 级横跳（5-3 观察 BOT#01 连续 12 次）。
                        // 安全区内遇袭不还击，直接继续补给。
                        if (BotPerception.InSafeZone(_player))
                        {
                            LastDetail = string.Format("{0}中遇袭 {1}，在安全区继续{0}", errand, MonsterName(attacker));
                            return false;
                        }

                        // 位置没动计时（被围死判据，2026-11-17）：每帧更新，
                        // 位置变了就重置起始时刻。复用 _restockFleeCheckPos 作位置基准
                        //（EnterState 接续时已重置为当时位置）。
                        if (_player.CurrentLocation == _restockFleeCheckPos)
                        {
                            if (_restockStuckSince == default) _restockStuckSince = SEnvir.Now;
                        }
                        else
                        {
                            _restockFleeCheckPos = _player.CurrentLocation;
                            _restockStuckSince = default;
                        }

                        // 冷却窗口（2026-11-17 修野外被围震荡）：接续回 Restock 后 20 秒内，
                        // 不因威胁立即切 Fight —— 否则「被围 → 切 Fight → 怪快速死亡/丢失 →
                        // 接续回 Restock → 威胁仍在又切 Fight」形成 0.5 秒级循环
                        //（5-3 后续观察：BOT#28/#17/#05 各 ~1981 次/16 分钟）。
                        // 冷却期内继续尝试逃离/回城；被围死（位置持续 10 秒没动）才允许还击，
                        // 避免被围住干挨打。
                        if (SEnvir.Now < _restockResumeUntil)
                        {
                            bool surrounded = _restockStuckSince != default
                                && SEnvir.Now - _restockStuckSince >= TimeSpan.FromSeconds(RestockStuckAllowSeconds);

                            if (!surrounded)
                            {
                                LastDetail = string.Format("{0}中持续遇袭 {1}，冷却期继续逃离", errand, MonsterName(attacker));
                                return false;
                            }

                            // 被围死：允许还击，正常走下方切 Fight 流程。
                        }
                        else
                        {
                            bool onMove = false;

                            if (_player.AutoPath != null)
                            {
                                // 位置推进判据：连续 5 步没动 = 被围住走不动，不再算「在逃离」。
                                if (_player.CurrentLocation == _restockFleeCheckPos) _restockFleeStuckCount++;
                                else
                                {
                                    _restockFleeCheckPos = _player.CurrentLocation;
                                    _restockFleeStuckCount = 0;
                                }

                                onMove = _restockFleeStuckCount < 5;
                            }

                            if (onMove)
                            {
                                LastDetail = string.Format("{0}途中遇袭 {1}，继续赶路", errand, MonsterName(attacker));
                                return false;
                            }
                        }

                        _interruptedState = State;
                    }

                    // 卖货/修理被反复打断的熔断（2026-11-18 现场：BOT#02 毒经生 被食人花围住，
                    // 每 10 秒一轮「Trade → 被打切 Fight → 没目标又接续回 Trade」，站着挨打不还手）。
                    //
                    // 判据：同一个假人 60 秒内因为挨打而切战斗 ≥4 次 → 认为「这趟事办不成」，
                    // 直接收尾（FinishTrade），并给卖货/修理 30 秒冷却，让它先把身边的怪清掉或走开。
                    if (State == BotState.Trade)
                    {
                        if (_tradeInterruptSince == default
                            || (SEnvir.Now - _tradeInterruptSince).TotalSeconds > 60)
                        {
                            _tradeInterruptSince = SEnvir.Now;
                            _tradeInterruptCount = 0;
                        }

                        _tradeInterruptCount++;

                        if (_tradeInterruptCount >= 4)
                        {
                            _tradeInterruptCount = 0;
                            _tradeInterruptSince = default;

                            BotLog.Warn(Profile.Seq, string.Format(
                                "卖货/修理被反复打断（60 秒内 4 次，最后是 {0}），先收尾脱身", MonsterName(attacker)));

                            FinishTrade("被围反复打断，先脱身", false);

                            // 30 秒内别再往 Trade 跑（否则下一拍又进去挨打）。
                            _sellCooldown = SEnvir.Now.AddSeconds(30);
                            _repairCooldown = SEnvir.Now.AddSeconds(30);
                        }
                    }

                    EnterState(BotState.Fight, string.Format("被 {0} 攻击，还击", MonsterName(attacker)), TimeSpan.FromSeconds(60));
                    SetTarget(attacker);

                    return true;
                }
            }

            // 0) 到「个人转生阈值」→ 转生。
            //
            // 原来是「到 Config.MaxLevel（75）就转」，于是所有假人在同一天集体转生，
            // 在线等级整齐地掉回 1 级 —— 比不转还假。现在每人一个 75~100 的固定阈值
            // （见 BotRebirthRule），散开转生时刻，练级曲线也不会同步。
            //
            // 仍然保留「到全服上限必转」：满级后打怪零收益，不转就是站着空砍。
            BotOptions rebirthOptions = Options;
            int rebirthThreshold = RebirthThreshold(rebirthOptions);

            if (_player.Level >= rebirthThreshold
                && State != BotState.Rebirth && State != BotState.Trade
                && SEnvir.Now >= _rebirthCooldown)
            {
                _rebirthBaseCount = _player.Character.Rebirth;
                _rebirthSnapshot = BotRebirthAudit.Capture(_player);

                BotLog.Info(Profile.Seq, string.Format(
                    "到达个人转生阈值（{0} 级，区间 {1}~{2}，已转生 {3} 次），去市政厅找管理员转生",
                    rebirthThreshold, rebirthOptions.RebirthMinLevel, rebirthOptions.RebirthMaxLevel,
                    _player.Character.Rebirth));

                EnterState(BotState.Rebirth, "到达转生阈值，去市政厅转生", TimeSpan.FromMinutes(10));

                return true;
            }

            // 1) 道士血低先自愈（7 级才有治愈术；比喝药省，还能顺带涨技能熟练度）
            // 阈值 2026-11-16 从 55% 提到 60%（方案阈值统一口径：自愈 60%、喝红 45%），
            // 更早开始自愈，减少落到喝红区间的血量。
            if (BotPerception.HealthPercent(_player) <= 60 && BotSkills.TrySelfHeal(_player))
                LastDetail = "给自己加血";

            // 2) 血量低于 45% 就喝红（走 ItemUse，冷却/负重与真人一致）
            if (BotPerception.HealthPercent(_player) <= 45 && BotActions.TryUsePotion(_player, Stat.Health))
                LastDetail = "喝红药";

            // 3) 蓝低于 30% 喝蓝：没蓝就放不出技能，只能贴脸，比没红药更致命。
            // 2026-11-16（D1）：战士 24 级起也补蓝（24 级前无耗蓝技能、省负重不补），
            // 法师/道士始终补。判据统一走 NeedsManaPotion()（喝蓝/买蓝/缺钱三处同口径）。
            if (NeedsManaPotion())
            {
                int maxMana = _player.Stats[Stat.Mana];

                if (maxMana > 0 && _player.CurrentMP * 100 / maxMana <= 30 &&
                    BotActions.TryUsePotion(_player, Stat.Mana))
                    LastDetail = "喝蓝药";
            }

            // 3.5) 传送中：不要切到卖货 / 补给。
            //
            // 为什么必须挡：传送是**跨图动作**，而卖货判定每帧都会问「背包满了吗」，
            // 一旦在传送途中被判成「要卖货」，就会重新进入 Trade → 又发起一次传送 ——
            // 状态来回弹跳、每帧发一次传送。实测 11 分钟刷出 5,585 次
            // 「卖货用六合石传送去 …」，传送其实一次都没成功。
            if (State == BotState.Teleport) return false;

            // 4) 后台点了「立即补给」：清冷却直接回城，不用等红药见底
            if (_restockRequested)
            {
                _restockRequested = false;
                _restockCooldown = DateTime.MinValue;
                _restockFailStreak = 0;

                EnterState(BotState.Restock, "后台要求立即补给", TimeSpan.FromMinutes(5));

                return true;
            }

            // 5) 药见底就回城补，别等喝完才想起（0 = 关闭自动补给）
            //
            // ⚠ 顺序：补给判定必须排在「背包满了就去卖」**之前**。
            // 实测（2026-11-12 12:17，30 假人 35 分钟）：11 倍爆率下背包长期接近满，
            // 原来那条 TryStartTrade 每帧抢先返回 true → 补给永远轮不到 ——
            // 35 分钟只补给 6 次，20 多个假人药 0/0 还在外面硬打，死亡 98 次。
            // 补给分支内部本来就「先卖货、再买药」，不需要外面的卖货抢在最前面。
            BotOptions options = Options;

            // 卖货 / 修理优先跑完，别被补给打断。
            //
            // 为什么必须加这条：Trade 状态下红药通常就是 0（正是没药才要补给），
            // 于是下一帧 ProcessSurvival 立刻 EnterState(Restock) 并 return true
            // → ProcessTrade **永远执行不到** → 卖货一次都做不成 → 负重降不下来
            // → 买药继续失败 → 再触发卖货…… 无限打转。
            //
            // 实测（2026-11-11 23:20）：12 个假人卡在「回城卖货」、12 个卡在「回城买药」，
            // 交易次数恒为 0，后台状态文字永远停在进入时那句「负重快满了，回城卖东西」。
            //
            // 注意：这里不能用 `return false` —— 后面的「日常卖货」还要跑。
            // 用条件包裹整段，任何一条不满足就往下落到日常卖货。
            bool tryRestock = options.RestockPotionLow > 0
                              && State != BotState.Restock
                              && State != BotState.Trade
                              && SEnvir.Now >= _restockCooldown;

            int red = tryRestock ? BotActions.CountPotion(_player, Stat.Health) : int.MaxValue;

            // 蓝药低位（2026-11-18 用户现场）：原来补给只看**红药**和弹药，
            // 道士「红药 27 瓶、蓝药 0 瓶」时根本不触发补给 → 放不出技能、
            // 只能贴脸平砍，还会被判成「远程可用」去放风筝，在怪面前来回跑。
            // 判据与喝蓝同口径（NeedsManaPotion：法师/道士始终要，战士 24 级起要）。
            int blue = tryRestock && NeedsManaPotion()
                ? BotActions.CountPotion(_player, Stat.Mana)
                : int.MaxValue;

            bool blueLow = tryRestock && NeedsManaPotion() && blue <= options.RestockPotionLow;

            // 弹药低位（步骤 3-3，2026-11-16）：断弹 = 道士技能全空放（有动画、扣蓝、零伤害），
            // 比红药见底更致命 —— 红药还够时弹药见底也要回城，不能等红药触发才顺带补。
            bool ammoLow = tryRestock && _player.Class == MirClass.Taoist
                           && BotSkills.HasAmmoStockBelow(_player, AmmoRestockLow)
                           && GoldAmount() >= AmmoRestockGoldFloor;

            if (tryRestock && (red <= options.RestockPotionLow || blueLow || ammoLow))
            {
                // 钱包连一瓶最便宜的药都买不起时，去药店也是空手而归。
                //
                // 实测（D5 主压测 126 分钟 + 千倍率探针 30 分钟）：金币 0 的假人每 60 秒
                // 空跑一趟补给，日志连续刷「补给结束：红 5 蓝 21，金币 0」，一趟一瓶都没买到。
                // 这种空转把「补给失败」抬到 785 次，而真实原因只是没钱。
                int floorPrice = BotActions.FloorPotionPrice(Stat.Health);

                // 资金门槛按触发原因分开算：红药触发要够买最便宜药，弹药触发要够买弹药
                // （护身符/毒粉几十金一份）。只触发一个维度时，另一个维度不拦 ——
                // 否则「红药够、弹药见底、钱够买弹药但不够买药」会被红药价误挡。
                bool potionGoldOk = red <= options.RestockPotionLow || blueLow
                    ? floorPrice <= 0 || GoldAmount() >= floorPrice
                    : true;
                bool ammoGoldOk = ammoLow ? GoldAmount() >= AmmoRestockGoldFloor : true;

                if (potionGoldOk && ammoGoldOk)
                {
                    // 补给前先清背包 —— 顺序定死「先卖、再买」，这是人补药的自然做法：
                    // 只留药和回城卷，装备/材料/矿石全卖掉，腾出负重和格子之后再买药。
                    //
                    // 为什么必须前置、而且不能只在「负重 ≥90%」时才做：
                    //   · 引擎的 CanGainItems 按 Weight × 瓶数 校验，余量为 0 就**静默 return**，
                    //     假人是无 socket 伪连接，收不到任何提示 → 走到药店也一瓶买不到；
                    //   · 11 倍爆率下负重几乎一直是满的（实测 66/66、110/84、117/84），
                    //     等它自己降到 90% 以下基本等不到。
                    // 阈值取 70%：留 30% 余量，够买一轮药（20 瓶 × 2 重量 = 40，上限 66 时约 60%）。
                    // 只要背包里有可卖物品（肉、装备、材料等杂物），补给前就先卖货腾地方。
                    // 不再要求「负重≥70%」——实测药见底回城时负重常没到 70%，旧逻辑只买药不卖货，
                    // 背包里的肉/装备一直堆着（用户 2026-11-18 反馈：回刷怪点时背包还有一堆没卖掉的装备）。
                    bool needClear = HasSellableItems();

                    if (needClear)
                    {
                        _tradeIsRepair = false;

                        // 卖货冷却中就先别跑 —— **不能**因为「药空了」就每帧重进 Trade：
                        // 实测那样会形成热循环（进门 → 卖不掉 → 收尾冷却 → 下一帧又进门），
                        // 30 个假人 30 分钟刷出 66,635 次空交易、18 个被判定 stuck。
                        // 冷却只有 20 秒，等得起。
                        if (SEnvir.Now >= _sellCooldown && TrySetTradeTargetForAnySellable(out _))
                        {
                            // 5 分钟：这一趟可能要跑好几类商人才卖得干净（装备/矿石/材料各一个 NPC）。
                            EnterState(BotState.Trade, "补给前先清背包（只留药和卷）", TimeSpan.FromMinutes(5));

                            return true;
                        }

                        LastDetail = "等卖货冷却，之后先清背包再补给";

                        // ⚠ 不能在这里「等」下去（2026-11-18 实测死锁）：
                        // 背包里剩一件「可卖但全图没人收」的东西（BOT#93 是 [部件]×1），
                        // HasSellableItems() 恒为 true、SetTradeTarget 恒失败 →
                        // 每 20 秒「补给前先清背包 → 卖不掉 → 再清背包」，
                        // 红药永远停在 4 瓶、回城卷永远 0，**补给一次都进不去**。
                        // 现在改为：卖不掉就先去补给，剩下的货以后再卖。
                        // 同时把「卖不掉的是哪些东西」打进日志（用户要按名字决定怎么改）。
                        if (SEnvir.Now >= _nextUnsoldLogTime)
                        {
                            _nextUnsoldLogTime = SEnvir.Now.AddSeconds(60);

                            BotLog.Info(Profile.Seq, "卖不掉的货（全图没有收购商，先补给）：" + DescribeUnsoldItems());
                        }
                    }

                    // 5 分钟而不是 3 分钟：一次补给要「AutoPath 跨图回城 → 走到药店买药 →
                    // 再走到杂货商买卷」三段，城外 60 格、两个 NPC 各 20~40 格。
                    // 3 分钟实测经常卡在最后一段（2026-11-12：6 次失败里 4 次是「补给超时」）。
                    string reason = ammoLow && red > options.RestockPotionLow && !blueLow
                        ? "弹药见底，回城补给"
                        : blueLow && red > options.RestockPotionLow
                            ? string.Format("蓝药只剩 {0} 瓶，回城补给", blue)
                            : string.Format("红药只剩 {0} 瓶，回城补给", red);

                    EnterState(BotState.Restock, reason, TimeSpan.FromMinutes(5));

                    return true;
                }
            }

            // 6) 日常：背包满了要回城卖货腾格子（否则会永久卡在拾取态）；
            //    装备耐久归零会被引擎整件跳过（攻击力变 0），也要修。
            if (State != BotState.Trade && TryStartTrade())
                return true;

            return false;
        }

        /// <summary>
        /// 回城买药。全程走真人路径：AutoPath 跨图 → 走到 NPC 旁 → NPCCall → NPCButton → NPCBuy。
        /// 任何一步失败都退回练级并给 60 秒冷却，避免「没钱 → 反复回城 → 永远不练级」的死循环。
        /// </summary>
        /// <summary>
        /// 这个假人自己的转生阈值（75~100 之间，按角色名+转生次数确定性散列，见 <see cref="BotRebirthRule"/>）。
        /// <para>
        /// **所有「该不该转生」的判定都必须走这里**：入口处判一次、进入转生状态后还要再判一次，
        /// 两处用不同基准（一处个人阈值、一处 <c>Config.MaxLevel</c>）就会出现
        /// 「刚到 88 级进状态 → 立刻被判『等级没到上限』踢回练级」的死循环。
        /// </para>
        /// </summary>
        private int RebirthThreshold(BotOptions options)
        {
            return BotRebirthRule.Threshold(_player.Character?.CharacterName, _player.Character?.Rebirth ?? 0,
                options.RebirthMinLevel, options.RebirthMaxLevel, Config.MaxLevel);
        }

        /// <summary>
        /// 转生：市政厅[2] 的「管理员」→ 按钮「转生主页」→ 按钮「执行转生」。
        /// <para>
        /// 为什么要有这条：到个人阈值（75~100）后再打怪没有任何收益，
        /// 而转生会给各项属性加成并回到 0 级重新练 —— 假人不转生就等于「到顶即报废」。
        /// 实测（mapq action:Rebirth）：全服只有 1 处转生动作，在市政厅。
        /// </para>
        /// </summary>
        private void ProcessRebirth()
        {
            // 判据必须是「个人阈值」而不是「全服上限」：阈值 88 的假人在 88 级进转生状态，
            // 拿 100（Config.MaxLevel）去比会立刻判成「等级没到上限」把它踢回练级，
            // 于是永远转不了 —— 而且日志显示「等级没到上限，回去练级」，看着像逻辑正常。
            if (_player.Level < RebirthThreshold(Options))
            {
                // 转生次数变多了 = 成功（等级已被引擎打回 1 级，不能再拿「等级是否满」判断）。
                if (_player.Character.Rebirth > _rebirthBaseCount)
                {
                    // 转生后立刻核对「不该丢的东西有没有丢」。
                    // 用 Warn：生产配置 BotLogLevel=2，Info 会被过滤，而这条是唯一的转生验收证据。
                    BotLog.Info(Profile.Seq, BotRebirthAudit.DescribeDiff(_rebirthSnapshot, BotRebirthAudit.Capture(_player)));

                    FinishRebirth(string.Format("转生完成：等级回到 {0}，累计转生 {1} 次",
                        _player.Level, _player.Character.Rebirth));

                    return;
                }

                FinishRebirth("等级没到上限，回去练级");

                return;
            }

            if (SEnvir.Now >= _stateDeadline)
            {
                FinishRebirth("转生超时（10 分钟没走完），先回去练级");

                return;
            }

            NPCInfo info = BotPerception.FindActionNpc(NPCActionType.Rebirth);

            if (info?.Region?.Map == null)
            {
                FinishRebirth("全服找不到转生 NPC（检查市政厅的管理员）");

                return;
            }

            // 对话已打开：一层层点到「执行转生」页；点进去的瞬间引擎就执行 Rebirth 动作。
            if (_player.NPCPage != null)
            {
                if (BotPerception.PageHasAction(_player.NPCPage, NPCActionType.Rebirth))
                {
                    // 已进入「执行转生」页：动作已经生效，下一拍按 Rebirth 计数判定成功。
                    LastDetail = "已进入「执行转生」页";

                    return;
                }

                int button = BotPerception.FindButtonToward(_player.NPCPage, NPCActionType.Rebirth);

                if (button <= 0)
                {
                    FinishRebirth("转生页里找不到通往「执行转生」的按钮");

                    return;
                }

                BotActions.TryPressButton(_player, button);

                LastDetail = "点「转生」按钮";

                return;
            }

            MapInfo dest = info.Region.Map;

            // 不在市政厅：交给 AutoPath 跨图走过去（它能识别 NPC 所在的区域）。
            if (_player.CurrentMap?.Info != dest)
            {
                if (_player.AutoPath != null)
                {
                    LastDetail = string.Format("前往 {0} 转生", dest.Description);

                    return;
                }

                if (!AutoPathService.Instance.TryStart(_player, info))
                {
                    FinishRebirth(string.Format("去 {0} 的路线规划失败", dest.Description));

                    return;
                }

                LastDetail = string.Format("前往 {0} 转生", dest.Description);

                return;
            }

            // 到了市政厅：找管理员本体（NPC 是阻挡物，走到它旁边再开口）
            NPCObject npc = null;

            foreach (NPCObject obj in _player.CurrentMap.NPCs)
            {
                if (obj?.NPCInfo != info) continue;

                npc = obj;
                break;
            }

            if (npc == null)
            {
                FinishRebirth("市政厅里找不到管理员");

                return;
            }

            int distance = BotNavigator.Distance(_player.CurrentLocation, npc.CurrentLocation);

            if (distance > AutoPathToNpcDistance)
            {
                if (_player.AutoPath != null)
                {
                    LastDetail = string.Format("自动寻路走向 {0}（{1} 格）", npc.Name, distance);

                    return;
                }

                if (AutoPathService.Instance.TryStart(_player, info))
                {
                    LastDetail = string.Format("自动寻路走向 {0}（{1} 格）", npc.Name, distance);

                    return;
                }
            }

            if (_player.AutoPath != null)
                AutoPathService.Instance.Cancel(_player);

            if (distance > 2)
            {
                BotMoveResult move = BotNavigator.TryStepTowardsNear(_player, npc.CurrentLocation, 1);

                if (move == BotMoveResult.Moved)
                {
                    LastDetail = string.Format("走向 {0}（{1} 格）", npc.Name, distance);

                    return;
                }

                if (move == BotMoveResult.Busy) return;

                if (BotNavigator.TryStepAdjacent(_player, npc.CurrentLocation))
                {
                    LastDetail = string.Format("贴近 {0}", npc.Name);

                    return;
                }
            }

            if (!BotActions.TryOpenNpc(_player, npc))
            {
                LastDetail = string.Format("还差 {1} 格才够到 {0}", npc.Name, distance);

                return;
            }

            LastDetail = "打开管理员，准备转生";
        }

        private void FinishRebirth(string detail)
        {
            BotActions.CloseNpc(_player);

            if (_player.AutoPath != null)
                AutoPathService.Instance.Cancel(_player);

            // 失败退避 5 分钟；成功时等级已经归零，下一帧条件自然不成立。
            _rebirthCooldown = SEnvir.Now.AddMinutes(5);

            _blockedCount = 0;

            BotLog.Warn(Profile.Seq, detail);

            EnterState(BotState.Idle, detail, TimeSpan.FromSeconds(5));
        }

        /// <summary>
        /// 挑补给的目的城：**走得到的、跨图次数最少的、有药店的城**。
        /// <para>
        /// <b>为什么不再无脑回绑定点</b>：绑定点是「死亡复活点」，由序号取模分配，
        /// 还会随「走进任意安全区」漂移（引擎 <c>UpdateBindPoint</c>）。拿它当补给目的地会出两种怪事：
        /// ① 在比奇城边打怪的假人绑在潘夜村，断药后横跨两张图去补给，规划失败就干等 60 秒；
        /// ② 人明明站在有药店的主城里，却要走去另一座城。
        /// </para>
        /// <para>
        /// 判据用 <see cref="AutoPathService.GetHopsFrom"/> 的跳数表（与真正规划同一套出口检查），
        /// 所以「挑中了」就等于「AutoPath 走得通」，不会再出现挑完才失败。
        /// 同跳数时绑定点优先（有回城卷 1 秒直达），其次按地图 Index 小的（结果稳定，重启不变）。
        /// </para>
        /// </summary>
        private MapInfo ResolveSupplyDestination(MapInfo bind)
        {
            Map current = _player.CurrentMap;

            if (current?.Info == null) return bind;

            // 同图复用上一次的结果：这个函数要跑一次地图级 BFS，别每帧算。
            if (_supplyDest != null && _supplyDestSourceMap == current.Info.Index) return _supplyDest;

            MapInfo currentInfo = current.Info;

            // D8 补充（2026-11-17，步骤 4-2）：当前图本身补给齐全（药店+杂货商+修理铺，满分 5）
            // → 就地补给，不回城，省卷轴省时间。只有满分才就地：缺杂货商/修理铺的图就地补给
            // 会「补一半再跑一趟」，正是要优先回主城避免的二次跨图（方案括号补充 3）。
            if (BotPerception.GetMapSupplyLevel(currentInfo) >= SupplyFullLevel)
            {
                _supplyDest = currentInfo;
                _supplyDestSourceMap = currentInfo.Index;

                BotLog.Info(Profile.Seq, string.Format(
                    "补给目的地：{0}（本图补给齐全：药店+杂货商+修理铺，就地补给）",
                    currentInfo.Description));

                return currentInfo;
            }

            Dictionary<MapInfo, int> hops = AutoPathService.Instance.GetHopsFrom(_player);

            MapInfo best = null;
            int bestHops = int.MaxValue;
            int bestSupply = -1;

            foreach (MapInfo town in BotPerception.PharmacyMaps())
            {
                if (!hops.TryGetValue(town, out int distance)) continue;   // 走不到的直接不考虑
                if (town != bind && IsTradeMapBlocked(town)) continue;      // 刚被判定去不了

                int supply = BotPerception.GetMapSupplyLevel(town);

                bool better;

                // D8（2026-11-17，步骤 4-1）：排序第一维是**补给齐全度**——
                // 主城（药店+杂货商+修理铺全，满分 5）优先，只有部分 NPC 的城次之。
                // 断药时去补给最全的城一站完成「出售+补给」，比去最近的小城「补一半再跑一趟」省一趟跨图。
                if (supply > bestSupply)
                {
                    better = true;
                }
                else if (supply < bestSupply)
                {
                    better = false;
                }
                else if (distance < bestHops)
                {
                    better = true;                                          // 同补给：跳数少优先
                }
                else if (distance > bestHops)
                {
                    better = false;
                }
                else if (town == bind)
                {
                    better = true;                                          // 同跳数：绑定点优先
                }
                else if (best == bind)
                {
                    better = false;
                }
                else
                {
                    better = best == null || town.Index < best.Index;       // 再按 Index，保证稳定
                }

                if (!better) continue;

                best = town;
                bestHops = distance;
                bestSupply = supply;
            }

            // 一座有药店的城都走不到（孤岛图）→ 退回绑定点，行为和改动前一致。
            if (best == null) best = bind;

            _supplyDest = best;
            _supplyDestSourceMap = current.Info.Index;

            // 把「为什么去这座城」写进日志：这正是用户会问的问题
            //（「在比奇城打怪，怎么规划到潘夜村去了？」），一行字省掉一次翻代码。
            if (best != null)
            {
                hops.TryGetValue(best, out int finalHops);

                BotLog.Info(Profile.Seq, string.Format(
                    "补给目的地：{0}（补给评分 {1}/5，{2} 跳，绑定点 {3}{4}）",
                    best.Description, bestSupply, finalHops, bind?.Description ?? "-",
                    best == current.Info ? "，本图有药店就地补给" : ""));
            }

            return best;
        }

        private void ProcessRestock()
        {
            // 打断待办保险清理（2026-11-17）：打断场景在 EnterState 接续时已清，
            // 这里再清一次防异常路径（catch 里跳 Idle 等）残留导致误接续。
            _interruptedState = default;

            if (SEnvir.Now >= _stateDeadline)
            {
                FinishRestock("补给超时（3 分钟没走完），先回去练级");
                return;
            }

            if (_player.Character == null)
            {
                FinishRestock("角色信息缺失，放弃补给");
                return;
            }

            // 进门先记下「进门前有多少药 / 多少卷」——
            // 收尾时用**增量**判定成功，而不是看存量。
            // 存量判定会误报：进门前本来就剩 2 瓶药，买不到新的也照样算「成功」。
            if (_restockBaselinePotions < 0)
            {
                _restockBaselinePotions = BotActions.CountPotion(_player, Stat.Health) + BotActions.CountPotion(_player, Stat.Mana);
                _restockBaselineScrolls = BotActions.CountTownScroll(_player);

                // 改动点 4：每轮补给进门时重置「试过买药」标记，
                // 本轮是否买药由 BuyPotions 里按 redNeed/blueNeed 重新记。
                _restockTriedPotion = false;
                _restockAmmoGain = 0;
            }

            // 对话已打开：按当前子阶段办事（买药 / 买回城卷 / 买弹药）
            if (_player.NPCPage != null)
            {
                if (_restockStage == RestockStage.TownScroll) BuyTownScrolls();
                else if (_restockStage == RestockStage.Ammo) BuyAmmo();
                else BuyPotions();

                return;
            }

            MapInfo bind = _player.Character?.BindPoint?.BindRegion?.Map;

            if (bind == null)
            {
                FinishRestock("角色没有绑定主城，无法补给");
                return;
            }

            // 目的地**不一定是绑定点**：绑定点由「序号取模」分配，还会随「走进任意安全区」漂移，
            // 于是出现「在比奇城边打怪、断药后却要横跨两张图回潘夜村补给」这种反直觉行为，
            // 规划失败就干等 60 秒（2026-11-13 用户实测报的）。
            // 现在改成「走得到的、补给最全的、有药店的城」，见 ResolveSupplyDestination（D8）。
            MapInfo home = ResolveSupplyDestination(bind);

            // 不在目的地：先瞬移，再走路。
            if (_player.CurrentMap?.Info != home)
            {
                // ① 目的地正好是绑定点 → 回城卷 1 秒直达，最省。
                if (home == bind && TryReturnHomeByScroll(home))
                {
                    LogRestockTravelOnce(string.Format("用回城卷回绑定点 {0}", home.Description));
                    return;
                }

                // ①-b 目的地不是绑定点，但**绑定点本身能补给** → 也别跑路（2026-11-18 用户要求：
                //     「补货优先用回城卷」）。回城卷永远回绑定点，所以先 1 秒回去、就地补给，
                //     比 AutoPath 跨图 3~5 分钟强得多（路上不练级、还挨打）。
                //     只有绑定点连药店都没有时，才继续按「补给最全的城」走过去。
                if (home != bind && BotPerception.HasPharmacy(bind))
                {
                    if (TryReturnHomeByScroll(bind))
                    {
                        _supplyDest = bind;
                        _supplyDestSourceMap = _player.CurrentMap?.Info?.Index ?? -1;

                        LogRestockTravelOnce(string.Format(
                            "用回城卷回绑定点 {0}（原计划去 {1}，省一趟跑路）",
                            bind.Description, home.Description));

                        return;
                    }

                    // 没卷 / 本图不能用卷 → 下面的 AutoPath 兜底，并留痕说明原因。
                    BotLog.Debug(Profile.Seq, string.Format(
                        "回城卷不可用（{0} 张，本图{1}），只能走回 {2} 补给",
                        BotActions.CountTownScroll(_player),
                        BotActions.CanUseTownScrollHere(_player) ? "允许用卷" : "禁止用卷",
                        home.Description));
                }

                if (_player.AutoPath != null)
                {
                    LastDetail = string.Format("正在走回 {0}", home.Description);
                    return;
                }

                if (!AutoPathService.Instance.TryStart(_player, home, TownPoint(home)))
                {
                    string reason = AutoPathService.LastFailureReason ?? "未知原因";

                    // ② 兜底：目的地不是绑定点、但绑定点有药店，且身上还有卷 ——
                    //    烧一张先回得去再说（哪怕远），总比在野外干等 60 秒强。
                    //    回城卷永远只回绑定点，所以这一步只在「绑定点本身能补给」时才做。
                    if (home != bind && BotPerception.HasPharmacy(bind) && TryReturnHomeByScroll(bind))
                    {
                        BotLog.Warn(Profile.Seq, string.Format(
                            "回 {0} 的路线规划失败（{1}），改烧回城卷回绑定点 {2} 补给",
                            home.Description, reason, bind.Description));

                        _supplyDest = bind;
                        _supplyDestSourceMap = _player.CurrentMap?.Info?.Index ?? -1;

                        return;
                    }

                    FinishRestock(string.Format("回 {0} 的路线规划失败（{1}），60 秒后再试", home.Description, reason));
                    return;
                }

                LastDetail = string.Format("走回 {0} 补给", home.Description);

                LogRestockTravelOnce(string.Format("跑路走回 {0}（原因：{1}）",
                    home.Description, RestockWalkReason(home, bind)));

                return;
            }

            // 在目的地：找当前阶段对应的 NPC
            LogRestockTravelOnce("就地补给（本图就是补给目的地，没走也没烧卷）");

            NPCObject npc = IsGrocerStage
                ? BotPerception.FindScrollShop(_player.CurrentMap)
                : BotPerception.FindPharmacy(_player.CurrentMap);

            if (npc == null)
            {
                if (IsGrocerStage)
                {
                    // 买不到卷/弹药不是致命错误 —— 药已经买到了，回城卷只是加速手段。
                    FinishRestock(string.Format("{0} 里没有卖回城卷的杂货商", home.Description));
                }
                else
                {
                    FinishRestock(string.Format("{0} 里找不到药店", home.Description));
                }

                return;
            }

            int distance = BotNavigator.Distance(_player.CurrentLocation, npc.CurrentLocation);

            string npcKind = IsGrocerStage ? "杂货商" : "药店";

            // 远距离先交给引擎 AutoPath：贪心走格在城内拐角/城墙会原地打转，
            // 实测有假人卡在「走不到药店 117 格外始终过不去」直到补给超时。
            if (distance > AutoPathToNpcDistance)
            {
                if (_player.AutoPath != null)
                {
                    LastDetail = string.Format("自动寻路走向{0} {1}（{2} 格）", npcKind, npc.Name, distance);
                    return;
                }

                if (AutoPathService.Instance.TryStart(_player, npc.NPCInfo))
                {
                    _blockedCount = 0;
                    LastDetail = string.Format("自动寻路走向{0} {1}（{2} 格）", npcKind, npc.Name, distance);
                    return;
                }
            }

            // 走到附近了：AutoPath 还挂着就撤掉，接管成逐格逼近（NPC 是阻挡物，
            // A* 的终点只能是它周围的可走格）。
            if (_player.AutoPath != null)
                AutoPathService.Instance.Cancel(_player);

            // NPC 本身是阻挡物：A* 的终点是它站的格子，贴身那一步永远迈不进去。
            // 所以走向 NPC 时，终点用「NPC 周围的可走格」（半径 1），
            // 而不是 NPC 自己那格 —— 否则永远算得出路径、却一步都迈不出去。
            // 好在 NPCCall 只要求 18 格视野内（Config.MaxViewRange），所以「走不动就直接开口」。
            //
            // 距离门槛取 2：NPC 周围 1 格的最远距离就是 2（斜向），
            // 门槛写成 3 的话会出现「已经走到 NPC 跟前了却还在尝试移动」的抖动。
            if (distance > 2)
            {
                BotMoveResult move = BotNavigator.TryStepTowardsNear(_player, npc.CurrentLocation, 1);

                if (move == BotMoveResult.Moved)
                {
                    _blockedCount = 0;
                    LastDetail = string.Format("走向{0} {1}（{2} 格）", npcKind, npc.Name, distance);
                    return;
                }

                if (move == BotMoveResult.Busy) return;

                // A* 也算不到（隔墙、隔河），改用「直线逼近」的兜底。
                if (BotNavigator.TryStepAdjacent(_player, npc.CurrentLocation))
                {
                    _blockedCount = 0;
                    LastDetail = string.Format("贴近{0} {1}（{2} 格）", npcKind, npc.Name, distance);
                    return;
                }
            }

            if (!BotActions.TryOpenNpc(_player, npc))
            {
                _blockedCount++;

                if (_blockedCount > 20)
                {
                    FinishRestock(string.Format("走不到{0} {1}（{2} 格外始终过不去）", npcKind, npc.Name, distance));
                    return;
                }

                LastDetail = string.Format("还差 {0} 格才够到{1} {2}", distance, npcKind, npc.Name);
                return;
            }

            _blockedCount = 0;

            LastDetail = string.Format("打开 {0}，准备{1}", npc.Name,
                _restockStage == RestockStage.TownScroll ? "买回城卷"
                : _restockStage == RestockStage.Ammo ? "买护身符 / 毒药粉"
                : "买药");
        }

        /// <summary>
        /// 用背包里的回城卷瞬移回绑定主城。
        /// <para>
        /// **为什么要这个**：断药时最费时间的不是买药，是「走回主城」——
        /// 实测从城外走回城要 60 秒以上（每格约 0.6 秒），AutoPath 跨图更久。
        /// 这一路假人不打怪、不捡东西，纯属浪费。有了回城卷，一秒回到城边。
        /// </para>
        /// <para>
        /// 返回 true = 本帧已经处理完（要么已回城，要么正在尝试），调用方应直接 return。
        /// </para>
        /// </summary>
        /// <summary>
        /// 卖货跨图：优先用**六合圣石**直达目的地（1 秒），而不是腿儿着走过去。
        /// <para>
        /// 卖货目的地常常是另一座城 —— 挖取物全是 Meat，而诺玛村落/沙漠土城没有肉类收购商，
        /// 只能去比奇城/失乐园/潘夜村/比奇城堡卖。走过去要 3~5 分钟，
        /// 实测 29 分钟内「交易超时」21 次，假人整段时间都在赶路而不是在练级。
        /// </para>
        /// </summary>
        private bool TryTradeTeleport(MapInfo dest)
        {
            if (dest == null || _player.CurrentMap == null) return false;

            NPCObject stone = BotPerception.FindTeleportStoneFor(_player.CurrentMap, dest.Index);

            if (stone == null) return false;

            int button = BotPerception.FindTeleportButton(stone.NPCInfo, dest.Index);

            if (button < 0) return false;

            int cost = BotPerception.CheapestTeleportCost(stone.NPCInfo, dest.Index);

            if (cost > 0 && GoldAmount() < cost) return false;

            _travelMap = dest;
            _travelPoint = default;
            _travelVia = null;
            _travelFailures = 0;
            _emptyHunts = 0;
            _lastHuntSuccessTime = SEnvir.Now;
            _teleportViaHome = false;

            BotLog.Info(Profile.Seq, string.Format("卖货用六合石传送去 {0}（花费 {1}）",
                dest.Description, cost > 0 ? cost.ToString() : "免费"));

            EnterState(BotState.Teleport, string.Format("传送去 {0} 卖货", dest.Description), TimeSpan.FromMinutes(5));

            return true;
        }

        /// <summary>
        /// 背包快照（2026-11-18 用户要求）。
        /// <para>
        /// 在「卖完身上的物品 → 去刷怪点」这一跳打一行，用来看货是不是卖干净了：
        /// 正常只该剩 回城卷 + 红药 + 蓝药（道士多符和毒药），
        /// 有「装备 / 材料」残留就说明还有收购商没跑到，或者判据把可卖品当成了必需品。
        /// </para>
        /// <para>
        /// 分类口径：红/蓝按 <c>Consumable</c> + 对应属性恢复量；回城卷走
        /// <see cref="BotActions.IsTownScroll(UserItem)"/>；符 = <c>Amulet</c>、毒药 = <c>Poison</c>；
        /// 装备按 <c>ItemType</c> 白名单；其余一律算「材料」并列出前几件名字。
        /// </para>
        /// </summary>
        private string DescribeBag()
        {
            UserItem[] bag = _player?.Inventory;

            if (bag == null) return "背包不可用";

            int red = 0, blue = 0, scrolls = 0, amulet = 0, poison = 0, equip = 0, other = 0, free = 0;
            int equipSellable = 0, otherSellable = 0, equipUnsellable = 0, otherUnsellable = 0;
            StringBuilder leftovers = new StringBuilder();
            StringBuilder blockedSample = new StringBuilder();

            foreach (UserItem item in bag)
            {
                if (item?.Info == null)
                {
                    free++;
                    continue;
                }

                int count = (int)Math.Max(1, item.Count);

                if (BotActions.IsTownScroll(item))
                {
                    scrolls += count;
                    continue;
                }

                switch (item.Info.ItemType)
                {
                    case ItemType.Consumable:
                        if (item.Info.Stats[Stat.Health] > 0) { red += count; continue; }
                        if (item.Info.Stats[Stat.Mana] > 0) { blue += count; continue; }
                        break;

                    case ItemType.Amulet:
                        amulet += count;
                        continue;

                    case ItemType.Poison:
                        poison += count;
                        continue;
                }

                // 卖不掉的三种原因分开记，日志里才能直接看出「是没去卖，还是根本卖不掉」：
                //   CanSell=false（商店不收） / Bound|Locked|Worthless（绑定/锁定/零价）。
                bool sellable = item.Info.CanSell && !BotItemRules.IsSellBlocked(item.Flags);

                bool isEquip = item.Info.ItemType == ItemType.Weapon
                    || item.Info.ItemType == ItemType.Armour
                    || item.Info.ItemType == ItemType.Helmet
                    || item.Info.ItemType == ItemType.Necklace
                    || item.Info.ItemType == ItemType.Bracelet
                    || item.Info.ItemType == ItemType.Ring
                    || item.Info.ItemType == ItemType.Shoes
                    || item.Info.ItemType == ItemType.Shield
                    || item.Info.ItemType == ItemType.Costume
                    || item.Info.ItemType == ItemType.Emblem
                    || item.Info.ItemType == ItemType.SocketGem;

                if (isEquip)
                {
                    equip += count;

                    if (sellable) equipSellable += count;
                    else equipUnsellable += count;
                }
                else
                {
                    other += count;

                    if (sellable) otherSellable += count;
                    else otherUnsellable += count;
                }

                if (leftovers.Length < 60)
                {
                    if (leftovers.Length > 0) leftovers.Append('、');
                    leftovers.Append(item.Info.ItemName).Append('×').Append(count);
                }

                if (!sellable && blockedSample.Length < 40)
                {
                    if (blockedSample.Length > 0) blockedSample.Append('、');
                    blockedSample.Append(item.Info.ItemName).Append('(')
                        .Append(item.Info.CanSell ? FlagText(item.Flags) : "商店不收")
                        .Append(')');
                }
            }

            return string.Format(
                "红 {0} 蓝 {1} 卷 {2} 符 {3} 毒 {4} | 装备 {5}(可卖{6}/卖不掉{7}) 材料 {8}(可卖{9}/卖不掉{10}) " +
                "| 空格 {11}/{12} 负重 {13}/{14}{15}{16}",
                red, blue, scrolls,
                // 弹药实际装在**装备槽**里（UseAmulet 读的是 Equipment），背包里通常是 0，
                // 所以按「装备槽 + 背包」合计，否则快照永远显示 符0/毒0（2026-11-18）。
                amulet + BotSkills.CountEquip(_player, EquipmentSlot.Amulet),
                poison + BotSkills.CountEquip(_player, EquipmentSlot.Poison),
                equip, equipSellable, equipUnsellable,
                other, otherSellable, otherUnsellable,
                free, bag.Length, _player.BagWeight, _player.Stats[Stat.BagWeight],
                other + equip > 0 ? " | 残留: " + leftovers : "",
                blockedSample.Length > 0 ? " | 卖不掉: " + blockedSample : "");
        }

        /// <summary>
        /// 本轮补给「怎么回城的」只记一次（2026-11-18 用户要求）。
        /// <para>
        /// 从日志一眼能看出这趟是 <b>用回城卷</b> / <b>跑路</b> / <b>就地补给</b>，
        /// 跑路时带上原因（没卷 / 本图禁用 / 绑定点没药店 / 卷在冷却），
        /// 不用再猜「为什么它没烧卷」。
        /// </para>
        /// </summary>
        private void LogRestockTravelOnce(string how)
        {
            if (_restockTravelLogged) return;

            _restockTravelLogged = true;

            BotLog.Info(Profile.Seq, string.Format("补给出行：{0}（背包卷 {1} 张，当前 {2}）",
                how, BotActions.CountTownScroll(_player),
                _player?.CurrentMap?.Info?.Description ?? "?"));
        }

        /// <summary>
        /// 列出「可卖但这次没卖掉」的物品明细（2026-11-18 用户要求：
        /// 要按物品名决定这些卖不掉的部件怎么处理）。
        /// <para>口径与 <see cref="HasSellableItems"/> 一致：
        /// <c>Info.CanSell</c> 且非 Bound/Locked/Worthless/Marriage、且不是要留的药和卷。</para>
        /// </summary>
        private string DescribeUnsoldItems()
        {
            BotPotionBudget budget = NewPotionBudget();

            StringBuilder text = new StringBuilder();
            int total = 0, listed = 0;

            foreach (UserItem item in _player.Inventory)
            {
                if (item?.Info == null) continue;
                if (!item.Info.CanSell) continue;
                if (BotItemRules.IsSellBlocked(item.Flags)) continue;
                if (IsKeepItem(item, budget)) continue;

                total++;

                if (listed >= 8) continue;

                if (text.Length > 0) text.Append('、');

                text.Append(item.Info.ItemName)
                    .Append('×').Append(Math.Max(1, item.Count))
                    .Append('[').Append(BotText.Item(item.Info.ItemType)).Append(']');

                listed++;
            }

            if (total == 0) return "无";

            return total > listed
                ? text + string.Format(" …共 {0} 格", total)
                : text.ToString();
        }

        /// <summary>标记位的中文描述（诊断日志用，2026-11-18）。</summary>
        private static string FlagText(UserItemFlags flags)
        {
            StringBuilder text = new StringBuilder();

            if ((flags & UserItemFlags.Bound) == UserItemFlags.Bound) text.Append("绑定");
            if ((flags & UserItemFlags.Locked) == UserItemFlags.Locked)
            {
                if (text.Length > 0) text.Append('+');
                text.Append("锁定");
            }
            if ((flags & UserItemFlags.Worthless) == UserItemFlags.Worthless)
            {
                if (text.Length > 0) text.Append('+');
                text.Append("零价");
            }
            if ((flags & UserItemFlags.Marriage) == UserItemFlags.Marriage)
            {
                if (text.Length > 0) text.Append('+');
                text.Append("婚戒");
            }

            return text.Length == 0 ? "无标记" : text.ToString();
        }

        /// <summary>跑路回城的原因（给「补给出行」日志用，2026-11-18）。</summary>
        private string RestockWalkReason(MapInfo home, MapInfo bind)
        {
            if (BotActions.CountTownScroll(_player) <= 0) return "背包没有回城卷";

            if (!BotActions.CanUseTownScrollHere(_player)) return "本图禁止使用回城卷";

            if (home != bind && !BotPerception.HasPharmacy(bind))
                return string.Format("绑定点 {0} 没有药店，只能去 {1}",
                    bind?.Description ?? "?", home.Description);

            return "回城卷正在冷却或使用失败";
        }

        private bool TryReturnHomeByScroll(MapInfo home)
        {
            if (BotActions.CountTownScroll(_player) <= 0) return false;

            // 地图不允许回城（幽灵船 4 层）/ 副本不许传送 → 只能老老实实走。
            if (!BotActions.CanUseTownScrollHere(_player)) return false;

            // 已经在主城就别浪费卷。
            if (_player.CurrentMap?.Info == home) return false;

            // 引擎的 UseItemTime 是**共用**冷却：刚喝过药（2000ms）就发包会被静默拒绝，
            // 看起来就像「卷坏了」。冷却没到就下一帧再试，别浪费一次机会。
            if (!BotActions.ItemUseCooldownReady(_player))
            {
                LastDetail = "等物品冷却，再用回城卷";
                return true;
            }

            if (!BotActions.TryUseTownScroll(_player))
            {
                // 卷在冷却期（引擎 UseItemTime）或背包已空 → 下一帧再试，退回 AutoPath。
                LastDetail = "回城卷用不了（冷却中），改走回主城";
                return false;
            }

            // 用卷次数单独记：它衡量的是「瞬移回城」这条捷径的利用量，
            // 与「补给成功次数」不是一回事（用了卷也可能因为没钱而买不到药）。
            TownScrollUseCount++;
            _player.Character.BotTownScrollUses++;

            // 传送后必须取消 AutoPath：引擎不会因为换图就自动清掉旧路线，
            // 留着的话假人会一边站在主城一边继续往城外走。
            if (_player.AutoPath != null)
                AutoPathService.Instance.Cancel(_player);

            BotLog.Info(Profile.Seq, string.Format("用回城卷回城（还剩 {0} 张）", BotActions.CountTownScroll(_player)));

            LastDetail = "用回城卷瞬移回主城";

            return true;
        }
        /// <summary>在已打开的 NPC 页里买药。买够、买不起、或没有买药入口时收尾。</summary>
        private void BuyPotions()
        {
            BotOptions options = Options;

            // 入口页一般只是"欢迎光临"，商品在按钮点进去的买卖页
            if (_player.NPCPage.Goods == null ||
                BotActions.FindPotionGood(_player, Stat.Health, int.MaxValue, int.MaxValue) == null)
            {
                int button = BotPerception.FindShopButton(_player.NPCPage);

                if (button <= 0)
                {
                    FinishRestock("这个 NPC 页里没有买药的入口");
                    return;
                }

                BotActions.TryPressButton(_player, button);
                LastDetail = "打开买卖页";

                return;
            }

            long gold = GoldAmount();

            int red = BotActions.CountPotion(_player, Stat.Health);
            int blue = BotActions.CountPotion(_player, Stat.Mana);

            int redNeed = Math.Max(0, options.RestockBuyRed - red);
            // 不需要蓝药的（战士 24 级前）蓝药需求视为 0，避免误置「试过买药」触发补给失败判定
            int blueNeed = NeedsManaPotion() ? Math.Max(0, options.RestockBuyBlue - blue) : 0;

            bool bought = false;

            // 改动点 4：记录本轮试过买药。收尾判「补给成功」时，
            // 试过买药就要求药真的变多（药 0→0 不算成功，见 FinishRestock）。
            if (redNeed > 0 || blueNeed > 0) _restockTriedPotion = true;

            if (redNeed > 0) bought |= TryBuyPotions(Stat.Health, redNeed, gold);
            if (blueNeed > 0) bought |= TryBuyPotions(Stat.Mana, blueNeed, gold);

            if (bought)
            {
                LastDetail = string.Format("买药中：红 {0}/{1}、蓝 {2}/{3}，金币 {4}",
                    BotActions.CountPotion(_player, Stat.Health), options.RestockBuyRed,
                    BotActions.CountPotion(_player, Stat.Mana), options.RestockBuyBlue, gold);

                return;
            }

            // 药买完了（或买不起）。接着去杂货商 —— 先买弹药（道士），再买回城卷。
            //
            // 弹药排在前面：没护身符/毒药粉的道士**技能全空放**（有动画、扣蓝、无伤害），
            // 会卡在「站在怪堆里一直放技能但怪不死」的状态，比少一张回城卷严重得多。
            if (ShouldBuyAmmo())
            {
                _restockStage = RestockStage.Ammo;

                // 换 NPC 必须先关掉当前对话，否则引擎会一直停在药店的页上。
                BotActions.CloseNpc(_player);

                LastDetail = "药买完了，去买护身符 / 毒药粉";

                return;
            }

            // 药买完了（或买不起）。接着去杂货商买回城卷 —— 下次断药就能秒回城。
            if (ShouldBuyTownScroll())
            {
                _restockStage = RestockStage.TownScroll;

                // 换 NPC 必须先关掉当前对话，否则引擎会一直停在药店的页上。
                BotActions.CloseNpc(_player);

                LastDetail = "药买完了，去买回城卷";

                return;
            }

            FinishRestock(string.Format("补给结束：红 {0} 蓝 {1}，金币 {2}",
                BotActions.CountPotion(_player, Stat.Health),
                BotActions.CountPotion(_player, Stat.Mana), gold));
        }
        /// <summary>
        /// 一次买多少弹药（护身符 / 毒药粉都按这个数）。
        /// <para>
        /// 2026-11-14 从 10 提到 15；<b>2026-11-18 按用户要求提到 10000</b>：
        /// 符/毒药是可堆叠、便宜、几乎不占重量的消耗品，一次技能就吃一份，
        /// 15 份打几只怪就见底 → 一天要回城补十几次。囤满 10000 份基本等于「永不断弹」。
        /// </para>
        /// <para>
        /// 实际能买多少仍受三道上限约束：金币 / 负重 / 单次下单不能超过堆叠上限
        /// （见 <see cref="BuyAmmo"/> 的分批下单）。
        /// </para>
        /// </summary>
        private const int AmmoTarget = 10000;

        /// <summary>
        /// 弹药低位触发补给的阈值（步骤 3-3，2026-11-16）：任一**已学会**弹药技能的
        /// 弹药**总库存**（装备槽 + 背包）低于这个数就触发回城补给。
        /// <para>
        /// 2026-11-18 随 <see cref="AmmoTarget"/> 一起放大到 1000（目标的 10%）：
        /// 目标 10000 时还按 3 触发，等于永远不触发，断弹后才发现。
        /// </para>
        /// </summary>
        private const int AmmoRestockLow = 1000;

        /// <summary>
        /// 弹药补给的资金门槛（与 <see cref="ShouldBuyAmmo"/> 一致，50 金）：
        /// 低于这个数不触发弹药补给，避免空跑一趟触发失败退避。
        /// </summary>
        private const long AmmoRestockGoldFloor = 50;

        /// <summary>
        /// 要不要顺路买弹药（护身符 / 毒药粉）？
        /// <para>
        /// 只有道士需要，而且<b>只买已学会技能要用的那种</b>：
        /// 施毒术 12 级、灵魂火符 13 级（本服 <c>MagicInfo.NeedLevel1</c>），
        /// 没学就买回来只是占负重。
        /// </para>
        /// <para>
        /// <b>为什么必须买</b>：这两种弹药缺了，道士的技能全部「空放」——
        /// <c>UseAmulet</c> / <c>UsePoison</c> 失败时 <c>MagicCast</c> 直接 break，
        /// 结果是有动画、扣蓝、一个目标都没有，假人站在怪堆里永远打不死怪。
        /// </para>
        /// </summary>
        private bool ShouldBuyAmmo()
        {
            if (_player.Class != MirClass.Taoist) return false;

            bool needAmulet = false;
            bool needPoison = false;

            foreach ((EquipmentSlot slot, MagicType _) in BotSkills.AmmoSkills(_player))
            {
                // 按**总库存**（装备槽 + 背包）算，不是只看槽：
                // 槽受 StackSize 限制装不下 10000，剩下的在背包里（2026-11-18）。
                int have = BotSkills.CountAmmoStock(_player, slot);

                if (have >= AmmoTarget) continue;

                if (slot == EquipmentSlot.Amulet) needAmulet = true;
                else needPoison = true;
            }

            if (!needAmulet && !needPoison) return false;

            // 至少买得起一份再跑这一趟（护身符 / 毒药粉都是几十金币一份）。
            return GoldAmount() >= 50;
        }

        /// <summary>
        /// 在杂货商处买护身符 / 毒药粉。买完（或买不起）就接着判断要不要买回城卷。
        /// <para>
        /// 三种东西在同一个 NPC 的同一个买卖页里，所以这一趟顺手全办掉，不再单独跑。
        /// </para>
        /// </summary>
        private void BuyAmmo()
        {
            // 当前页没有弹药 → 点按钮进买卖页（与买回城卷是同一个入口）
            if (BotActions.FindAmmoGood(_player, ItemType.Amulet, long.MaxValue) == null &&
                BotActions.FindAmmoGood(_player, ItemType.Poison, long.MaxValue) == null)
            {
                int button = BotPerception.FindGoodsButton(_player.NPC?.NPCInfo, ItemType.Amulet);

                if (button <= 0)
                    button = BotPerception.FindGoodsButton(_player.NPC?.NPCInfo, ItemType.Poison);

                if (button <= 0)
                {
                    FinishRestock("这个杂货商不卖护身符 / 毒药粉，先回去练级");
                    return;
                }

                BotActions.TryPressButton(_player, button);
                LastDetail = "打开卖护身符 / 毒药粉的页";

                return;
            }

            long gold = GoldAmount();

            int boughtAmulet = 0;
            int boughtPoison = 0;

            foreach ((EquipmentSlot slot, MagicType _) in BotSkills.AmmoSkills(_player))
            {
                ItemType itemType = slot == EquipmentSlot.Poison ? ItemType.Poison : ItemType.Amulet;

                // 先把背包里已有的装上（上一轮买了但没装、或者捡到过），再算还缺多少 ——
                // 否则会重复买，越攒越多纯压负重。
                BotActions.TryEquipAmmo(_player, slot);

                // 按**总库存**算缺口（2026-11-18）：装备槽受 StackSize 限制装不下 10000，
                // 只看槽会永远「缺」，每轮都再买一批。
                int have = BotSkills.CountAmmoStock(_player, slot);
                int need = Math.Max(0, AmmoTarget - have);

                if (need <= 0) continue;

                NPCGood good = BotActions.FindAmmoGood(_player, itemType, gold);

                if (good == null) continue;

                // 重量：可堆叠弹药常常 Weight=0，不能按 1 算 —— 否则「零重量」也会被负重卡死
                // （原来 `Math.Max(1, Weight)` 就是这个坑）。
                int per = Math.Max(0, good.Item.Weight);
                long freeWeight = Math.Max(0, _player.Stats[Stat.BagWeight] - _player.BagWeight);
                int byWeight = per <= 0 ? int.MaxValue : (int)Math.Min(int.MaxValue, freeWeight / per);

                // 单次下单量不能超过堆叠上限 —— NPCBuy 里
                // `if (amountToBuy > good.Item.StackSize) return;` 会**静默拒绝整个请求**。
                // 所以 10000 份必须按 StackSize 切块下单（2026-11-18）。
                int stack = good.Item.StackSize > 0 ? good.Item.StackSize : 1;

                int got = 0;

                // 每帧最多切 40 块，剩下的下一帧继续（BuyAmmo 在补给态每帧都会进来）。
                for (int chunk = 0; chunk < 40 && got < need; chunk++)
                {
                    int want = Math.Min(need - got, stack);
                    want = Math.Min(want, byWeight - got);

                    if (want > 0)
                    {
                        long affordable = gold / Math.Max(1, good.Cost);
                        want = (int)Math.Min(want, affordable);
                    }

                    if (want <= 0) break;

                    BotActions.TryBuy(_player, good.Index, want);

                    // 买回来的东西在**背包**里，必须装到装备槽才生效（UseAmulet 读的是 Equipment）。
                    BotActions.TryEquipAmmo(_player, slot);

                    // 用**实际到手数量**判定，不看金币差额 —— 背包满 / 负重超时引擎会静默拒绝。
                    int after = BotSkills.CountAmmoStock(_player, slot);
                    int gained = after - have - got;

                    if (gained <= 0) break;

                    got += gained;
                    gold -= (long)gained * good.Cost;

                    freeWeight = Math.Max(0, _player.Stats[Stat.BagWeight] - _player.BagWeight);
                    byWeight = per <= 0 ? int.MaxValue : (int)Math.Min(int.MaxValue, freeWeight / per);
                }

                if (got <= 0) continue;

                if (slot == EquipmentSlot.Poison) boughtPoison += got;
                else boughtAmulet += got;
            }

            if (boughtAmulet > 0 || boughtPoison > 0)
            {
                _restockAmmoGain = boughtAmulet + boughtPoison;

                LastDetail = string.Format("买弹药：护身符 {0}、毒药粉 {1}（金币 {2}）", boughtAmulet, boughtPoison, gold);

                bool stillShort = AmmoStockShort();

                string line = string.Format(
                    "补给：买护身符 {0} 份、毒药粉 {1} 份，金币剩 {2}（现有 符{3}/毒{4}，负重 {5}/{6}）",
                    boughtAmulet, boughtPoison, gold,
                    BotSkills.CountAmmoStock(_player, EquipmentSlot.Amulet),
                    BotSkills.CountAmmoStock(_player, EquipmentSlot.Poison),
                    _player.BagWeight, _player.Stats[Stat.BagWeight]);

                // 分批下单会连着跑几十帧，中间批次只按 2 秒节流记 Debug，最后一批才记 Info，
                // 否则一条补给能刷出几十行（2026-11-18）。
                if (stillShort)
                {
                    if (SEnvir.Now >= _nextAmmoLogTime)
                    {
                        _nextAmmoLogTime = SEnvir.Now.AddSeconds(2);
                        BotLog.Debug(Profile.Seq, line);
                    }

                    LastDetail = string.Format("继续买弹药（现有 符{0}/毒{1}）",
                        BotSkills.CountAmmoStock(_player, EquipmentSlot.Amulet),
                        BotSkills.CountAmmoStock(_player, EquipmentSlot.Poison));

                    return;
                }

                BotLog.Info(Profile.Seq, line);
            }
            else
            {
                BotLog.Info(Profile.Seq, string.Format(
                    "补给：护身符 / 毒药粉 一份都没买到（金币 {0}，负重 {1}/{2}）｜装备槽 符{3}/毒{4}，背包 符{5}/毒{6}｜{7}",
                    gold, _player.BagWeight, _player.Stats[Stat.BagWeight],
                    BotSkills.CountEquip(_player, EquipmentSlot.Amulet),
                    BotSkills.CountEquip(_player, EquipmentSlot.Poison),
                    BotActions.CountBag(_player, ItemType.Amulet),
                    BotActions.CountBag(_player, ItemType.Poison),
                    DumpAmmoGoods()));
            }

            // 同一个 NPC 顺手把回城卷也买了。
            if (ShouldBuyTownScroll())
            {
                _restockStage = RestockStage.TownScroll;

                LastDetail = "弹药买完了，接着买回城卷";

                return;
            }

            FinishRestock(string.Format("补给结束：护身符 {0}、毒药粉 {1}，金币 {2}",
                BotSkills.CountEquip(_player, EquipmentSlot.Amulet),
                BotSkills.CountEquip(_player, EquipmentSlot.Poison), gold));
        }

        /// <summary>弹药库存是否还没到目标（分批下单用，2026-11-18）。</summary>
        private bool AmmoStockShort()
        {
            if (_player.Class != MirClass.Taoist) return false;

            foreach ((EquipmentSlot slot, MagicType _) in BotSkills.AmmoSkills(_player))
            {
                if (BotSkills.CountAmmoStock(_player, slot) < AmmoTarget) return true;
            }

            return false;
        }

        /// <summary>
        /// 要不要顺路买回城卷？
        /// <para>
        /// 条件：配置里开了（有剩余额度）、背包里的卷不够买、而且**买得起**。
        /// 买不起就别跑这一趟 —— 药已经买到就够了，为一张 500 金币的卷再跑一趟不划算。
        /// </para>
        /// </summary>
        private bool ShouldBuyTownScroll()
        {
            BotOptions options = Options;

            if (options.RestockBuyTownScroll <= 0) return false;

            int have = BotActions.CountTownScroll(_player);

            if (have >= options.RestockBuyTownScroll) return false;

            // 留够买药的钱之后再买卷：卷比药贵（500 vs 80），不能让卷把买药的钱花光。
            // 这里用 500 作门槛（12 个杂货商里 11 个是这个价，炼狱岛是 1000）；
            // 真买贵了就买不到，会在 BuyTownScrolls 里优雅收尾。
            long gold = GoldAmount();

            return gold >= 500;
        }
        /// <summary>
        /// 在杂货商处买回城卷。
        /// <para>
        /// 杂货商的入口页 → 按钮 1 → 买卖页（回城卷在那页，价 500）。
        /// 实测 12 个主城的杂货商（伦纳德 / 科里 / 赛文…）**按钮 1 全都直达卖回城卷的页**，
        /// 但不写死按钮号 —— 按「目标页里有没有回城卷」去找。
        /// </para>
        /// </summary>
        private void BuyTownScrolls()
        {
            BotOptions options = Options;

            // 当前页不是卖回城卷的买卖页 → 点按钮进去
            if (BotActions.FindTownScrollGood(_player, int.MaxValue) == null)
            {
                int button = BotPerception.FindScrollButton(_player.NPC?.NPCInfo);

                if (button <= 0)
                {
                    FinishRestock("这个 NPC 页里没有买回城卷的入口");
                    return;
                }

                BotActions.TryPressButton(_player, button);
                LastDetail = "打开卖回城卷的页";

                return;
            }

            long gold = GoldAmount();

            int have = BotActions.CountTownScroll(_player);
            int need = Math.Max(0, options.RestockBuyTownScroll - have);

            if (need <= 0)
            {
                FinishRestock("回城卷够用了");
                return;
            }

            NPCGood good = BotActions.FindTownScrollGood(_player, (int)Math.Min(gold, int.MaxValue));

            // 预算不够一张卷时也放宽：只要买得起至少一张就别放弃 ——
            // 回城卷是「断药后秒回城」的工具，缺了它下次断药就得走 60 秒。
            if (good == null)
                good = BotActions.FindTownScrollGood(_player, int.MaxValue);

            if (good == null)
            {
                // 这个商店页里根本没有回城卷：药已经买到了，直接收尾。
                FinishRestock(string.Format("钱不够买回城卷（金币 {0}），先回去练级", gold));
                return;
            }

            // 每张 500 金币，能买几张买几张。
            int affordable = (int)Math.Min(need, gold / Math.Max(1, good.Cost));

            if (affordable <= 0)
            {
                FinishRestock(string.Format("金币不够买回城卷（{0}），先回去练级", gold));
                return;
            }

            int scrollBefore = BotActions.CountTownScroll(_player);

            BotActions.TryBuy(_player, good.Index, affordable);

            // 用**实际到手的张数**判定，不看金币差额：
            // 引擎在背包满 / 负重超时会静默拒绝购买，金币没扣但东西也没到，
            // 这种情况下报「买到了 N 张」是假的。
            int bought = BotActions.CountTownScroll(_player) - scrollBefore;

            if (bought > 0)
            {
                TownScrollBuyCount += bought;
                _player.Character.BotTownScrollBuys += bought;
            }

            FinishRestock(string.Format("补给结束：买药 + 回城卷 {0} 张（现有 {1}），金币 {2}",
                bought, BotActions.CountTownScroll(_player), gold));
        }

        /// <summary>
        /// 是否需要蓝药（喝蓝 / 买蓝 / 缺钱判断三处共用的统一判据）。
        /// <para>
        /// 2026-11-16（D1）：法师/道士始终需要；战士 24 级起才需要 —— 24 级前战士没有
        /// 耗蓝技能，买蓝药纯浪费（打怪掉的钱还不够买药），省负重不补；24 级起补蓝。
        /// 阈值统一走 <see cref="BotItemRules.WarriorManaPotionLevel"/>，与发药/捡药同口径，
        /// 否则会出现「会喝蓝但补给不买蓝」的脱节。
        /// </para>
        /// </summary>
        private bool NeedsManaPotion()
            => _player.Class != MirClass.Warrior
               || (_player.Class == MirClass.Warrior
                   && _player.Level >= BotItemRules.WarriorManaPotionLevel);

        /// <summary>
        /// 买一类药，返回是否**真的买到了**。三条约束缺一不可：
        /// 1. <b>单瓶恢复量 ≤ 自身血量/蓝量的合理上限</b> —— 1 级 52 血喝回 250 的药是纯浪费；
        /// 2. <b>优先买满</b>：按「剩余金币 ÷ 还缺的瓶数」当预算，能一次买满就一次买满；
        /// 3. <b>买不起也要买一点</b>：预算卡死时退一步，只买买得起的量。
        /// </summary>
        /// <para>
        /// 返回值是**引擎的真实结果**（背包里的药确实变多了），不是「发了请求」。
        /// 引擎在背包满 / 负重超时会静默拒绝购买 —— 那时返回 true 会让上层以为买到了，
        /// 结果药一直没补上，假人反复空跑，最后报「补给成功」却是假的。
        /// </para>
        private bool TryBuyPotions(Stat restoreStat, int need, long gold)
        {
            int pool = _player.Stats[restoreStat];

            if (pool <= 0) return false;

            // 不需要蓝药的（法师/道士除外的战士 24 级前）不买蓝药，纯浪费（打怪掉的钱还不够买药）。
            if (restoreStat == Stat.Mana && !NeedsManaPotion()) return false;

            int maxRestore = restoreStat == Stat.Health
                ? Math.Max(30, pool * 6 / 10)   // 红药按 60% 血量，至少允许回 30 的最低档
                : Math.Max(40, pool);           // 蓝药至少要能买最低档（回 40），否则法师永远买不到

            int perBottle = (int)Math.Min(int.MaxValue, gold / Math.Max(1, need));

            NPCGood good = BotActions.FindPotionGood(_player, restoreStat, perBottle, maxRestore);

            // 挑不到档位时的退路：**只放宽恢复量上限，预算保持 perBottle 不变**。
            //
            // 原来预算和恢复量上限一起放宽到 int.MaxValue → 退路会挑到「买得起但恢复量
            // 远超自身血量」的高档药：1 级号（血 52）可能直接买 5000 金币的 V 档，钱瞬间花光
            // 还只买到几瓶，断药风险反而更大。
            // 保持预算约束（perBottle = 剩余金币 ÷ 还缺瓶数）：买不起的档位仍然买不起，
            // 只放开「恢复量超过自身血量」这一维 —— 宁可喝溢出也不能断药，
            // 断药就打不了怪，整条经济闭环跟着断。
            if (good == null)
                good = BotActions.FindPotionGood(_player, restoreStat, perBottle, int.MaxValue);

            if (good == null)
            {
                BotLog.Warn(Profile.Seq, string.Format(
                    "买药诊断：{0} 挑不到任何档位（need={1} gold={2} pool={3} maxRestore={4} 页商品：{5}）",
                    restoreStat, need, gold, pool, maxRestore, DumpPotionGoods()));

                return false;
            }

            int affordable = (int)Math.Min(need, gold / Math.Max(1, good.Cost));

            // 单次下单量不能超过物品的堆叠上限 —— NPCBuy 里
            // `if (amountToBuy > good.Item.StackSize) return;` 会**静默拒绝整个请求**，
            // 假人没有客户端，收不到拒绝提示，表现就是「金币够、背包够，但一瓶都买不到」。
            if (good.Item != null && good.Item.StackSize > 0)
                affordable = Math.Min(affordable, good.Item.StackSize);

            // 负重：NPCBuy 里 `CanGainItems(checkWeight:true)` 会按 Weight × 瓶数 校验，
            // 超了就 return false → 同样静默 return。
            // **这是「金币 50 万、格子还剩 20 个、却一瓶药都买不到」的真凶**：
            // 假人背包长期塞满挖取物（打怪挖的肉），格子空着但负重早就满了。
            // 公式与 PlayerObject.CanCarryInventoryWeight 一致：BagWeight + 新增 <= 上限。
            if (good.Item != null && good.Item.Weight > 0)
            {
                long capacity = (long)_player.Stats[Stat.BagWeight] - _player.BagWeight;

                int byWeight = (int)Math.Max(0, capacity / good.Item.Weight);

                if (byWeight < affordable) affordable = byWeight;
            }

            if (affordable <= 0)
            {
                // 2026-11-15：三分支区分「买不起 / 负重满 / 装不下」。
                // 原文案一律报「装不下」，但实测大量场景是**金币不够一份**（金创药 IV 单价 1250，
                // 金币只剩几百），负重充足、背包空位 40+，文案与事实相反，排障时被误导。
                // 生产诊断（BOT#25）还出现过金币 64 万、剩余负重 0 —— 纯负重满，
                // 不标出来会把「该去卖货」误读成「该去赚钱」。
                // 剩余负重就地重算：上面的 capacity 是 if 块内局部变量，作用域到不了这里。
                long freeWeight = (long)_player.Stats[Stat.BagWeight] - _player.BagWeight;

                string reason = gold < good.Cost ? "买不起"
                              : freeWeight <= 0 ? "负重满"
                              : "装不下";

                BotLog.Warn(Profile.Seq, string.Format(
                    "买药诊断：{0} {1}（{2} 单价 {3} 金币 {4}，负重 {5}/{6}，背包空位 {7}）",
                    restoreStat, reason, good.Item?.ItemName, good.Cost, gold,
                    _player.BagWeight, _player.Stats[Stat.BagWeight],
                    BotPerception.FreeInventorySlots(_player)));

                return false;
            }

            int before = BotActions.CountPotion(_player, restoreStat);

            BotActions.TryBuy(_player, good.Index, affordable);

            int after = BotActions.CountPotion(_player, restoreStat);

            if (after <= before)
            {
                // 下单了但没进包：NPCBuy 的静默拒绝点全在这条日志里列出来。
                BotLog.Warn(Profile.Seq, string.Format(
                    "买药诊断：{0} 下单失败（{1} ×{2}，金币 {3}，负重 {4}/{5}，背包空位 {6}/{7}，StackSize={8}，goodGs={9}/npcGs={10}）",
                    restoreStat, good.Item?.ItemName, affordable, gold,
                    _player.BagWeight, _player.Stats[Stat.BagWeight],
                    BotPerception.FreeInventorySlots(_player), Globals.InventorySize,
                    good.Item?.StackSize, good.GoodsIndex, _player.NPC?.NPCInfo?.GoodsIndex));

                return false;
            }

            // 成功是常态，用 Debug 记（BotLogLevel=2 时不输出）；失败才升 Warn。
            BotLog.Debug(Profile.Seq, string.Format("买药诊断：{0} 买到 {1} ×{2}（{3} → {4}，金币 {5}）",
                restoreStat, good.Item?.ItemName, affordable, before, after, gold));

            // 刚买的药带着 Locked，不修的话下次负重超了也卖不掉（见 RepairPotionFlags）。
            RepairPotionFlags(log: false);

            return true;
        }

        /// <summary>
        /// 把当前 NPC 页的商品清单拼成一行，用于买药诊断。
        /// 只输出关键字段（恢复量 / 单价 / 等级门槛），否则一行太长塞不进日志。
        /// </summary>
        private string DumpPotionGoods()
        {
            NPCPage page = _player.NPCPage;

            if (page == null) return "无页";
            if (page.Goods == null) return string.Format("[{0}] 无商品", page.Description);

            var parts = new List<string>();

            foreach (NPCGood good in page.Goods)
            {
                if (good?.Item == null) continue;

                parts.Add(string.Format("{0}({1},HP={2},MP={3},价={4},需{5}级)",
                    good.Item.ItemName,
                    good.Item.ItemType,
                    good.Item.Stats[Stat.Health],
                    good.Item.Stats[Stat.Mana],
                    good.Cost,
                    good.Item.RequiredAmount));
            }

            return string.Format("[{0}] {1}", page.Description,
                parts.Count == 0 ? "无商品" : string.Join(" | ", parts));
        }

        /// <summary>
        /// 弹药诊断：把当前页里所有 Amulet / Poison 类商品连同 <c>GoodsIndex</c> 打出来。
        /// <para>
        /// 为什么要带 GoodsIndex：<c>NPCBuy</c> 里有
        /// <c>if (good.GoodsIndex != NPC.NPCInfo.GoodsIndex) continue;</c> ——
        /// 对不上号时整条购买请求被<b>静默跳过</b>（不报错、不扣钱），
        /// 表现就是「页面上明明有护身符，就是买不到」。
        /// </para>
        /// </summary>
        private string DumpAmmoGoods()
        {
            NPCPage page = _player.NPCPage;

            if (page == null) return "无页";
            if (page.Goods == null) return string.Format("[{0}] 无商品", page.Description);

            var parts = new List<string>();

            foreach (NPCGood good in page.Goods)
            {
                if (good?.Item == null) continue;

                if (good.Item.ItemType != ItemType.Amulet && good.Item.ItemType != ItemType.Poison) continue;

                parts.Add(string.Format("{0}({1},价={2},堆={3},GoodsIndex={4},NPC={5})",
                    good.Item.ItemName,
                    good.Item.ItemType,
                    good.Cost,
                    good.Item.StackSize,
                    good.GoodsIndex,
                    _player.NPC?.NPCInfo?.GoodsIndex ?? -1));
            }

            return string.Format("[{0}] {1}", page.Description,
                parts.Count == 0 ? "没有任何护身符/毒药粉" : string.Join(" | ", parts));
        }

        /// <summary>收尾：关对话、上冷却、回练级。</summary>
        private void FinishRestock(string detail)
        {
            BotActions.CloseNpc(_player);

            // 补给过程中可能用 AutoPath 走过 NPC，收尾必须撤掉，
            // 否则假人会一边「待机」一边沿着旧路线继续走。
            if (_player.AutoPath != null)
                AutoPathService.Instance.Cancel(_player);

            _blockedCount = 0;
            _target = null;

            // 补货完成：接续冷却窗口作废（无需再防「接续后立即切 Fight」的循环）。
            _restockResumeUntil = default;
            _restockStuckSince = default;

            // 下一轮补给从头开始（先买药，再考虑买卷）。
            _restockStage = RestockStage.Potion;

            // 失败判定：看**增量**（这次买到了多少），不看存量。
            // 存量判定会误报：进门前本来就剩 2 瓶药，买不到新的也照样算「成功」。
            int potions = BotActions.CountPotion(_player, Stat.Health) + BotActions.CountPotion(_player, Stat.Mana);
            int scrolls = BotActions.CountTownScroll(_player);

            int basePotions = _restockBaselinePotions;
            int baseScrolls = _restockBaselineScrolls;

            int potionGain = potions - basePotions;
            int scrollGain = scrolls - baseScrolls;

            // 改动点 4：本轮试过买药 → **必须药真的变多**才算补给成功。
            // 原来按「药+卷总量增量」判：药 0→0 但顺手买到回城卷也报「补给成功」，
            // 假人以为补给好了，实际药还是 0，继续空跑药店（死亡 55 次/时的推手之一）。
            // 没买药需求（药本来就够）的轮次，维持原来的总量增量判定。
            //
            // 2026-11-15 补充：弹药（护身符/毒药粉）买到也算实质进展 ——
            // 道士断弹 = 技能全空放（有动画、扣蓝、零伤害），比少买几瓶药严重得多。
            // 此前弹药买成功但药无增量时整轮判「失败」，日志上误读成「什么都没买到」。
            int ammoGain = _restockAmmoGain;

            bool success = _restockTriedPotion
                ? potionGain > 0 || ammoGain > 0
                : potionGain + scrollGain + ammoGain > 0;

            _restockBaselinePotions = -1;
            _restockTriedPotion = false;   // 本轮判定完重置，下轮重新记
            _restockAmmoGain = 0;

            if (success)
            {
                RestockCount++;
                _player.Character.BotRestocks++;
                _restockFailStreak = 0;
            }
            else
            {
                RestockFailCount++;
                _player.Character.BotRestockFails++;
                _restockFailStreak++;
            }

            // 冷却按连续失败次数翻倍：60 → 120 → 240 → 480 → 600（封顶）。
            //
            // 固定 60 秒的问题：钱不够的时候，假人会每 60 秒空跑一趟药店，
            // 一趟一瓶买不到，纯属浪费主循环。D5 实测这类空转占满了 785 次「补给失败」。
            // 退避之后，第一次失败 1 分钟重试（可能是临时走不到 NPC），
            // 连续失败就逐步放弃，把时间让给打怪和卖货。
            int cooldown = Math.Min(600, 60 << Math.Min(_restockFailStreak, 4));

            _restockCooldown = SEnvir.Now.AddSeconds(cooldown);

            // 成功走 Info、失败走 Warn：补给是**日常动作**，一天几十次，
            // 全按 Warn 记会把日志刷成一片 WARN，真异常反而淹了（2026-11-13 用户反馈）。
            // 失败仍要留痕，并且把「药/卷的前后数量」一起打出来 ——
            // 才能区分「根本没买到」和「买了但期间喝掉了」。
            string restockLine = string.Format("补给{0}：{1}（药 {2}→{3}，卷 {4}→{5}，弹药 +{6}）",
                success ? "成功" : "失败", detail,
                basePotions, potions, baseScrolls, scrolls, ammoGain);

            if (success) BotLog.Info(Profile.Seq, restockLine);
            else BotLog.Warn(Profile.Seq, restockLine);

            // 补给收尾 → 下一跳去刷怪点时打一行背包快照（2026-11-18 用户要求）。
            _logBagOnNextHunt = true;

            // 办完事不一定要立刻出城：按概率在城里待一会儿（P2-3）。
            LeaveTownOrStroll(detail);
        }

        #region 挖取（Harvest）

        /// <summary>
        /// 走到尸体旁挖取。
        /// <para>
        /// 鸡 / 猪 / 牛这类怪（<c>MonsterInfo.AI</c> = 1/2/3）的掉落**不落地**，
        /// 引擎把掉落留在尸体的 <c>Drops[账号]</c> 里，必须走过去挖。
        /// 不挖的话，假人的背包永远是空的 —— 卖货与换装两条链路全都无从谈起。
        /// </para>
        /// <para>
        /// 引擎的两条硬约束：
        /// 1. 挖取范围是以**朝向正前方一格**为圆心、半径 <c>PickUpRadius</c> 的区域 ——
        ///    所以必须站到尸体的正前方（隔一格）并把朝向对过去；
        /// 2. 尸体的 <c>HarvestCount</c> 每挖一次减一（鸡 2 次、猪牛 3 次），
        ///    减到 0 之前只会空挖。所以要**反复挖同一个尸体**，直到掉落被拿完。
        /// </para>
        /// </summary>
        private void ProcessHarvest()
        {
            if (SEnvir.Now >= _stateDeadline)
            {
                FinishHarvest("挖取超时，先回去练级");
                return;
            }

            // 尸体没了 / 掉落被别人拿完了 → 结束
            MonsterObject corpse = _harvestTarget;

            if (corpse == null || corpse.Node == null || corpse.Drops == null ||
                !corpse.Drops.TryGetValue(_player.Character?.Account, out List<UserItem> items) ||
                items == null || items.Count == 0)
            {
                FinishHarvest("尸体上的东西挖完了");
                return;
            }

            int distance = BotNavigator.Distance(_player.CurrentLocation, corpse.CurrentLocation);

            // 还没走到尸体附近：先过去。
            if (distance > 1)
            {
                BotMoveResult move = BotNavigator.TryStepTowards(_player, corpse.CurrentLocation);

                if (move == BotMoveResult.Moved)
                {
                    _blockedCount = 0;
                    LastDetail = string.Format("走向尸体（{0} 格）", distance);
                    return;
                }

                if (move == BotMoveResult.Busy) return;

                // A* 走不到尸体脚下（尸体占着格子），贴到旁边一格。
                if (BotNavigator.TryStepAdjacent(_player, corpse.CurrentLocation))
                {
                    _blockedCount = 0;
                    LastDetail = string.Format("贴近尸体（{0} 格）", distance);
                    return;
                }

                _blockedCount++;

                if (_harvestBlockedSince == default) _harvestBlockedSince = SEnvir.Now;

                // 时间判据（3 秒）而不是按帧计数：主循环 80 帧/秒，20 帧只有 0.25 秒，
                // 人群里稍一拥挤就误判「走不到」，然后立刻重进、再误判（2026-11-18）。
                if (SEnvir.Now - _harvestBlockedSince >= TimeSpan.FromSeconds(3))
                {
                    // 记入排除表：60 秒内不再选这具，去挖别的或直接打怪。
                    _harvestExclude = corpse;
                    _harvestExcludeExpire = SEnvir.Now.AddSeconds(60);
                    _harvestBlockedSince = default;

                    bool log = SEnvir.Now >= _nextHarvestBlockedLogTime;

                    if (log) _nextHarvestBlockedLogTime = SEnvir.Now.AddSeconds(10);

                    FinishHarvest(log
                        ? string.Format("走不到尸体（{0} 格），60 秒内不再试这具", distance)
                        : null);

                    return;
                }

                return;
            }

            // 站到尸体正前方一格，并把朝向对过去 —— 引擎只扫「朝向正前方」的范围。
            MirDirection direction = Functions.DirectionFromPoint(_player.CurrentLocation, corpse.CurrentLocation);

            _player.Direction = direction;

            // ⚠ 必须等 CanMove 为真（= ActionTime / MoveTime 都过了）才能发 Harvest。
            // 引擎的 Harvest 开头是 `if (SEnvir.Now < ActionTime || SEnvir.Now < MoveTime)`
            // → 把动作塞进 ActionList 延迟队列 —— 而 **ActionList 只被 Add、从不消费**
            // （真人的客户端会重发包，假人没有客户端）→ 那一次挖取就永远丢了。
            // 刚打完怪时 ActionTime 还在未来（攻击冷却 600ms），所以这一步是必须的。
            if (!_player.CanMove)
            {
                LastDetail = "等动作冷却";
                return;
            }

            // 引擎的 Harvest 有冷却（ActionTime），这里靠状态期限节流，
            // 一次挖完等一小会儿再挖下一次（HarvestCount 要减到 0 才能拿全）。
            if (SEnvir.Now < _nextHarvestTime)
            {
                LastDetail = "等待挖取" + HarvestRemain(corpse.HarvestCount);
                return;
            }

            _player.Harvest(direction);

            // 引擎的 HarvestTime 是 600ms，这里留 100ms 余量
            // （ActionTime 会被其它动作刷新，早一点发会被引擎排队而不是立即执行）。
            _nextHarvestTime = SEnvir.Now.AddMilliseconds(700);

            LastDetail = string.Format("挖 {0}", corpse.MonsterInfo?.MonsterName ?? "尸体") + HarvestRemain(corpse.HarvestCount);

            // ⚠ 结束条件**不能**用 HarvestCount：
            // 引擎里 `if (ob.HarvestCount > 0) { ob.HarvestCount--; continue; }` ——
            // 也就是说前 N 次挖全是**空挖**，第 N+1 次才真正把东西拿走。
            // 鸡的 HarvestCount=2 → 要挖 3 次才有收获。
            // 所以只认「这个账号的掉落被拿完」这一个结束条件，
            // 引擎会在拿到东西后把条目从 Drops 里移除，下一轮开头就能判定出来。
        }

        /// <summary>挖取收尾。<paramref name="detail"/> 为 null 时不写日志（高频路径节流用）。</summary>
        private void FinishHarvest(string detail)
        {
            _harvestTarget = null;
            _target = null;
            _harvestBlockedSince = default;

            HarvestCount++;
            _player.Character.BotHarvests++;

            if (detail != null) BotLog.Debug(Profile.Seq, detail);

            // 用户 2026-11-15 观察：一具尸体挖完就进 Idle，附近其它可挖尸体（鸡/猪/牛）
            // 与遗留掉落被留在原地。清完怪应把可挖的全挖、可捡的全捡。
            // 顺序：先查附近其它可挖尸体（继续挖），再查遗留掉落（继续捡），都没有才走。
            MonsterObject moreCorpse = FindHarvestableCorpse();

            if (moreCorpse != null)
            {
                _harvestTarget = moreCorpse;
                _nextHarvestTime = DateTime.MinValue;

                EnterState(BotState.Harvest, "附近还有尸体，继续挖", TimeSpan.FromSeconds(30));
                return;
            }

            if (BotPerception.FindNearestLoot(_player, LootRadius) != null)
            {
                EnterState(BotState.Loot, "尸体挖完了，捡遗留物品", TimeSpan.FromSeconds(30));
                return;
            }

            EnterState(BotState.Idle, detail ?? "尸体挖完了", TimeSpan.FromSeconds(2));
        }

        /// <summary>
        /// 找一个可挖的尸体，跳过「近期走不到」的那具（2026-11-18）。
        /// 排除项过期后自动失效，不会永久拉黑一具其实只是暂时被堵住的尸体。
        /// </summary>
        private MonsterObject FindHarvestableCorpse()
        {
            if (_harvestExclude != null && SEnvir.Now >= _harvestExcludeExpire)
                _harvestExclude = null;

            return BotPerception.FindHarvestableCorpse(_player, LootRadius, _harvestExclude);
        }

        /// <summary>
        /// 挖取剩余次数的说明文字。
        /// <para>
        /// <c>HarvestCount</c> 是**空挖次数**：前 N 次挖都是空的，第 N+1 次才真正拿到东西。
        /// 所以它减到 0 时不能说「还要再挖 0 次」—— 那一次正是有收获的那一下。
        /// </para>
        /// </summary>
        private static string HarvestRemain(int count)
        {
            return count > 0 ? string.Format("（还要再挖 {0} 次）", count) : "（这一下就有收获）";
        }

        #endregion

        #region 装备评分与自动换装

        /// <summary>
        /// 给一件装备打分（越高越好）。
        /// <para>
        /// 用于 P1-4「捡到更好的装备就换上」。真人的判断依据是属性面板，
        /// 这里用同一套数据源（<c>Info.Stats</c> + 强化值 <c>Stats</c>）做加权求和，
        /// 让假人的换装倾向接近真人 —— 不会为 +1 防御就换掉全身装备。
        /// </para>
        /// </summary>
        public static int EquipmentScore(UserItem item)
        {
            if (item?.Info == null) return 0;

            int score = 0;

            // 基础属性：攻防血蓝、准确敏捷等（用引擎真实��名）。
            foreach (Stat stat in new[]
            {
                Stat.MinAC, Stat.MaxAC, Stat.MinMR, Stat.MaxMR,
                Stat.MinDC, Stat.MaxDC, Stat.MinMC, Stat.MaxMC,
                Stat.MinSC, Stat.MaxSC,
                Stat.Health, Stat.Mana,
                Stat.Accuracy, Stat.Agility, Stat.AttackSpeed,
                Stat.Strength, Stat.Luck,
            })
            {
                score += item.Info.Stats[stat] * 10;
                score += item.Stats[stat] * 10;
            }

            // 等级门槛也计入：高级装备天然更强。
            score += item.Info.RequiredAmount;

            return score;
        }

        /// <summary>
        /// 找一件「比现在穿的更好、而且能穿」的背包装备。
        /// 返回要换的背包格号，没有就返回 -1。
        /// </summary>
        public static int FindBetterEquipment(PlayerObject player)
        {
            if (player?.Inventory == null) return -1;

            int bestSlot = -1;
            int bestScore = 0;

            for (int i = 0; i < player.Inventory.Length; i++)
            {
                UserItem item = player.Inventory[i];

                if (item?.Info == null) continue;
                if (item.Count <= 0) continue;

                // 耐久归零的装备不能穿（引擎也不计它的属性）。
                if (item.Info.Durability > 0 && item.CurrentDurability <= 0) continue;

                EquipmentSlot slot;

                if (!TryFindEquipSlot(player, item, out slot)) continue;
                if (!player.CanWearItem(item, slot)) continue;

                UserItem current = player.Equipment[(int)slot];

                int score = EquipmentScore(item);
                int currentScore = current == null ? 0 : EquipmentScore(current);

                // 评分必须更高才换，但「更高」得按比例算，不能写死 +50。
                // 原来写死 +50：低等级装备整把武器的分才 20~40（木剑 ≈21、学徒手刃 ≈43），
                // 差值永远够不到 50 → 假人从 1 级到 12 级一直穿木剑，捡到更好的也只是塞在背包里。
                // 2026-11-12 botpeek 实测：转生 3 次、击杀 512 的号，装备仍是木剑 + 布衣，
                // 背包里躺着两把「学徒手刃」—— 这就是「假人一直很弱、死得多」的一条暗线。
                // 改成「至少高 5%，且至少高 5 分」：既修掉低等级死区，又保留「差不多的别来回折腾」。
                int threshold = Math.Max(5, currentScore / 20);

                if (score <= currentScore + threshold) continue;

                // 换装要留负重余量：穿装备不受 CanGainItems 把关，可以一路顶到 130%。
                //
                // 实测（2026-11-12，生产配置：11 倍爆率）：掉落装备暴增 → 假人专挑属性最高
                // （也是最重）的装备穿 → 负重 110/84 → 买药时 CanGainItems 返回 false
                // → 一瓶药都买不到（NPCBuy 静默 return）→ 补给成功率掉到 40%。
                // 这里留 15% 余量给药品：留着穿不了，就等于把「补给闭环」焊死了。
                int delta = item.Info.Weight - (current?.Info?.Weight ?? 0);
                long capacity = player.Stats[Stat.BagWeight];

                if (capacity > 0 && player.BagWeight + delta > capacity * 85 / 100) continue;

                if (score <= bestScore) continue;

                bestScore = score;
                bestSlot = i;
            }

            return bestSlot;
        }

        /// <summary>
        /// 找这件装备该穿到哪个槽位。
        /// <para>
        /// 规则跟真人一样：
        /// 1. 优先穿到**空槽**（双槽装备如戒指 / 手镯，空的那个先）；
        /// 2. 空槽没有合适的，才去**替换同类型的已有装备** —— 这才是「换装」，
        ///    只找空槽的话永远换不了任何东西（旧装备一直占着槽）。
        /// </para>
        /// </summary>
        private static bool TryFindEquipSlot(PlayerObject player, UserItem item, out EquipmentSlot slot)
        {
            EquipmentSlot occupied = EquipmentSlot.Weapon;
            bool hasOccupied = false;

            for (int i = 0; i < player.Equipment.Length; i++)
            {
                EquipmentSlot candidate = (EquipmentSlot)i;

                if (!Functions.CorrectSlot(item.Info.ItemType, candidate)) continue;

                // 空槽优先。
                if (player.Equipment[i] == null)
                {
                    slot = candidate;
                    return true;
                }

                // 记下第一个同类型的已有槽，等下用它替换。
                if (!hasOccupied)
                {
                    occupied = candidate;
                    hasOccupied = true;
                }
            }

            if (hasOccupied)
            {
                slot = occupied;
                return true;
            }

            slot = EquipmentSlot.Weapon;

            return false;
        }

        /// <summary>
        /// 测试辅助：强制切到某个状态。
        /// <para>
        /// 单独暴露是为了让 <c>Tests/BotLifeChecks</c> 能验证「状态超时后会切走」——
        /// 这是 P1-1 修的硬伤（原来 ProcessLoot 永久卡死），不暴露就没法自动化覆盖。
        /// </para>
        /// </summary>
        public void EnterStateForTest(BotState state, string detail, TimeSpan duration)
        {
            EnterState(state, detail, duration);
        }

        /// <summary>测试辅助：跑一帧决策。</summary>
        public void ProcessForTest()
        {
            Process();
        }

        /// <summary>检查背包里有没有更好的装备，有就换上（每次最多换一件，避免频繁操作）。</summary>
        public void TryEquipBetter()
        {
            int slot = FindBetterEquipment(_player);

            if (slot < 0) return;

            UserItem item = _player.Inventory[slot];

            if (TryEquip(_player, slot))
                BotLog.Debug(Profile.Seq, string.Format("换上 {0}", item?.Info?.ItemName ?? "装备"));
        }

        /// <summary>把背包里某件装备穿上（走真人同一条 <c>C.ItemMove</c> 路径）。</summary>
        public static bool TryEquip(PlayerObject player, int inventorySlot)
        {
            if (player?.Inventory == null) return false;

            if (inventorySlot < 0 || inventorySlot >= player.Inventory.Length) return false;

            UserItem item = player.Inventory[inventorySlot];

            if (item?.Info == null) return false;

            if (!TryFindEquipSlot(player, item, out EquipmentSlot slot)) return false;

            if (!player.CanWearItem(item, slot)) return false;

            player.ItemMove(new C.ItemMove
            {
                FromGrid = GridType.Inventory,
                FromSlot = inventorySlot,
                ToGrid = GridType.Equipment,
                ToSlot = (int)slot,
            });

            return player.Equipment[(int)slot] == item;
        }

        #endregion

        #region 交易（卖货 / 修理）

        /// <summary>
        /// 判断要不要去回城做一笔交易（卖货腾格子 / 修理装备）。
        /// 返回 true = 已经切到 <see cref="BotState.Trade"/>，调用方应直接 return。
        /// <para>
        /// 触发条件有两个（P1 修的三个硬伤里的两个）：
        /// 1. <b>背包满</b> —— 不卖货腾格子，假人会永久卡在拾取态（P1-1 修了一半，这边补上另一半）；
        /// 2. <b>有装备耐久归零</b> —— 引擎会把耐久归零的装备整件跳过、不计入任何属性，
        ///    等于攻击力变 0，假人打不动怪（P1-3）。
        /// </para>
        /// </summary>
        private bool TryStartTrade()
        {
            if (_player?.Character == null) return false;
            if (_player.Dead) return false;

            // 背包余量 / 负重这两项下面三个分支都要用，先算一次。
            //    格子阈值留 8 格余量：不然走到一半捡两件又满了，会反复半途折返。
            int free = BotPerception.FreeInventorySlots(_player);

            long capacity = _player.Stats[Stat.BagWeight];
            bool overweight = capacity > 0 && _player.BagWeight >= capacity * 9 / 10;

            // 真要卖货之前，先把「卖不掉的药」修成能卖的（幂等，只扫一遍 48 格背包）。
            //
            // 为什么不能只在上线时修：药是**边打边买**的，上线那一刻还没买，
            // 等负重爆了才买的那批照样带 Locked；启动资源发的则带 Bound|Worthless。
            // 实测 三更雪 负重 153/84 里 60 点是绑定药，上线修一次也救不了它 ——
            // 它是在线期间被补给的 Locked 药顶上去的。
            if (overweight || free <= 8) RepairPotionFlags(log: false);

            // 1) 有装备耐久见底 → 去修理（修理优先于卖货：属性全失效是致命的）
            if (SEnvir.Now >= _repairCooldown && HasBrokenEquipment())
            {
                _tradeIsRepair = true;

                // 必须看返回值：全服都找不到「收这一类」的修理铺时别再每拍重试。
                if (SetTradeTarget(BrokenEquipmentType()))
                {
                    EnterState(BotState.Trade, "装备耐久见底了，回城修理", TimeSpan.FromMinutes(3));

                    return true;
                }

                _repairCooldown = SEnvir.Now.AddMinutes(10);
            }

            // 2) 背包快满了 → 去卖货腾格子
            //
            // ⚠ 卖货冷却闸放在修理分支**之后**：修理有自己的 10 分钟冷却，不该被卖货的 20 秒挡。
            // 这道闸也不能只写在「补给前先清背包」那一段 —— 下面 2/3/4 三个分支
            // （格子满了 / 没钱了 / 负重爆了）都是**每拍都成立**的条件，
            // 少了它就是「收尾 → 下一拍又进 Trade」的热循环：实测（2026-11-12 21:27）
            // 夜行人 因为目的地图规划失败，每 0.5 秒刷一条「回 比奇城堡 的路线规划失败」。
            if (SEnvir.Now < _sellCooldown) return false;

            // 2.5) 上一趟卖货没收干净（当前 NPC 不收剩下的类型）→ 换一家收购商继续卖。
            //
            // 2026-11-18 背包快照实测：卖完肉就收尾出城，背包里还躺着 8~10 件**可卖**装备
            // 和几十个可卖材料 —— 而下面三条触发条件（空格≤8 / 没钱 / 负重满）都不成立，
            // 于是这些货一直跟着假人练级，直到背包被塞满才想起卖。
            // 用户要求「卖完身上的物品再去刷怪点」，所以这里补一条「收尾后继续卖」。
            if (_sellPending && HasSellableItems())
            {
                _tradeIsRepair = false;

                if (_sellPendingAttempts >= 3)
                {
                    // 连换三家都卖不掉（收购页对不上 / 走不到），先放着，别无限跑。
                    _sellPending = false;
                    _sellPendingAttempts = 0;
                    _sellCooldown = SEnvir.Now.AddSeconds(60);

                    BotLog.Debug(Profile.Seq, "剩下的可卖物品连续 3 趟没卖掉，先放着");
                }
                else if (TrySetTradeTargetForAnySellable(out _))
                {
                    _sellPending = false;      // 已消费，收尾时按当时背包重新置位
                    _sellPendingAttempts++;

                    EnterState(BotState.Trade, "还有货没卖完，换一家收购商", TimeSpan.FromMinutes(5));

                    return true;
                }
                else
                {
                    // 全图没有收这些货的 NPC：放弃这一轮，20 秒内不再重试。
                    _sellPending = false;
                    _sellPendingAttempts = 0;
                    _sellCooldown = SEnvir.Now.AddSeconds(20);

                    BotLog.Debug(Profile.Seq, "剩下的可卖物品找不到收购商，先放着");
                }
            }

            if (free <= 8 && HasSellableItems())
            {
                _tradeIsRepair = false;

                if (TrySetTradeTargetForAnySellable(out _))
                {
                    EnterState(BotState.Trade, "背包快满了，回城卖东西", TimeSpan.FromMinutes(3));

                    return true;
                }
            }

            // 3) 钱包买不起下一轮药 → 去卖货赚钱
            //
            // 为什么必须加这条：背包有 Globals.InventorySize = 48 格，而假人挖取物只占 20 来格，
            // 「剩余 ≤ 8 格」实际上**永远不会成立** → 从不卖货 → 金币只出不进 →
            // 买药失败率 90%+，假人卡在「回城买药 → 买不起 → 回去挨打」的死循环里。
            // D5 压测实测：补给成功/失败 = 1/105，交易 0 次。
            //
            // 卖货目的地是「肉类买卖」这类收购页（挖取物是 Meat），正好能把挖来的肉换成钱。
            if (NeedsMoneyForRestock() && HasSellableItems())
            {
                _tradeIsRepair = false;

                if (TrySetTradeTargetForAnySellable(out _))
                {
                    EnterState(BotState.Trade, "没钱买药了，回城卖东西", TimeSpan.FromMinutes(3));

                    return true;
                }
            }

            // 4) 负重快满 → 去卖货减重
            //
            // 为什么必须加这条：格子还剩 20 个、金币 50 万，但**负重早就爆了**。
            // NPCBuy 的 CanGainItems(checkWeight:true) 按 Weight × 瓶数 校验，
            // 超重直接 return → 一瓶药都买不到，且假人收不到任何提示。
            // 而上面两条都不成立（有格子、有钱），卖货永不触发 → 负重永远降不下来
            // → 断药 → 打不了怪 → 捡不到东西卖。
            //
            // 实测（2026-11-11）：金币 500,000、背包空位 18/48，日志
            // 「买药诊断：Health 下单失败（金创药 ×20 …）」刷了几十条，红药永远是 0。
            if (overweight && HasSellableItems())
            {
                _tradeIsRepair = false;

                if (TrySetTradeTargetForAnySellable(out _))
                {
                    EnterState(BotState.Trade, "负重快满了，回城卖东西", TimeSpan.FromMinutes(3));

                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 钱包够不够买满下一轮药？不够就该去卖货赚钱。
        /// <para>
        /// 估算口径：还缺的瓶数 × 背包里同类药的最低价（<see cref="BotActions.CheapestPotionPrice"/>）。
        /// 背包里没有同类药（取不到价）时**保守返回 true**（当成缺钱）——
        /// 宁可多跑一趟卖货，也别让假人一直空跑回城。
        /// </para>
        /// </summary>
        private bool NeedsMoneyForRestock()
        {
            BotOptions options = Options;

            if (options.RestockPotionLow <= 0) return false;   // 关了自动补给就不需要钱

            int red = BotActions.CountPotion(_player, Stat.Health);
            int blue = BotActions.CountPotion(_player, Stat.Mana);

            int redNeed = Math.Max(0, options.RestockBuyRed - red);
            int blueNeed = NeedsManaPotion() ? Math.Max(0, options.RestockBuyBlue - blue) : 0;

            if (redNeed == 0 && blueNeed == 0) return false;

            int redPrice = BotActions.CheapestPotionPrice(_player, Stat.Health);
            int bluePrice = BotActions.CheapestPotionPrice(_player, Stat.Mana);

            // 不需要蓝药的（战士 24 级前）蓝价缺失不算缺钱
            bool needBlue = blueNeed > 0 && NeedsManaPotion();

            if (redNeed > 0 && redPrice <= 0) return true;
            if (needBlue && bluePrice <= 0) return true;

            long need = (long)redNeed * Math.Max(0, redPrice)
                      + (needBlue ? (long)blueNeed * Math.Max(0, bluePrice) : 0);

            return GoldAmount() < need;
        }

        /// <summary>有没有耐久归零的装备（这类装备会被引擎整件跳过、不计属性）。</summary>
        private bool HasBrokenEquipment()
        {
            foreach (UserItem item in _player.Equipment)
            {
                if (item?.Info == null) continue;
                if (item.Info.Durability <= 0) continue;
                if (!item.Info.CanRepair) continue;
                if (item.MaxDurability <= 0) continue;

                // 原来只认「正好 0」，可界面按整数百分比显示 —— 剩 0.4% 也显示 0%，
                // 而 HasBrokenEquipment 判不出来，于是那件装备一直挂在 0% 没人修。
                // 改成「剩余 <= 15% 就去修」，配合 10 分钟冷却，既不会刷屏也不会穿到烂。
                if (!BotItemRules.NeedsRepair(item.CurrentDurability, item.MaxDurability, RepairDurabilityPercent))
                    continue;

                return true;
            }

            return false;
        }

        /// <summary>
        /// 「最该修」的那件装备的类型 —— 按**剩余比例**挑最差的，不是按背包顺序。
        /// <para>
        /// 为什么按比例：不同装备的 <c>MaxDurability</c> 差好几倍（武器 2 万、戒指 5 千），
        /// 用绝对值比会把「还剩 3000 的武器」当成比「还剩 800 的戒指」更该修。
        /// </para>
        /// <para>
        /// 这个类型直接决定去**哪一家**修理铺：武器店只收 Weapon/Shield、
        /// 首饰店只收 Ring/Bracelet/Necklace、服装店只收 Armour/Helmet/Shoes/Shield。
        /// </para>
        /// </summary>
        private ItemType BrokenEquipmentType()
        {
            ItemType worstType = ItemType.Weapon;
            long worstRatio = long.MaxValue;

            foreach (UserItem item in _player.Equipment)
            {
                if (item?.Info == null) continue;
                if (item.Info.Durability <= 0) continue;
                if (!item.Info.CanRepair) continue;
                if (item.MaxDurability <= 0) continue;
                if (item.CurrentDurability >= item.MaxDurability) continue;

                long ratio = BotItemRules.DurabilityPermille(item.CurrentDurability, item.MaxDurability);

                if (ratio >= worstRatio) continue;

                worstRatio = ratio;
                worstType = item.Info.ItemType;
            }

            return worstType;
        }

        /// <summary>
        /// 背包里有没有能卖的东西。
        /// <para>
        /// **药和回城卷不算**：药是自己要用的，回城卷是回城的工具 ——
        /// 卖了等于自断退路。药店页的收货类型恰好含 <c>Consumable</c>，
        /// 引擎不会拦，所以必须在这里自己挡掉。
        /// </para>
        /// </summary>
        private bool HasSellableItems()
        {
            BotPotionBudget budget = NewPotionBudget();

            foreach (UserItem item in _player.Inventory)
            {
                if (item?.Info == null) continue;
                if (!item.Info.CanSell) continue;
                if (BotItemRules.IsSellBlocked(item.Flags)) continue;
                if (IsKeepItem(item, budget)) continue;

                return true;
            }

            return false;
        }

        /// <summary>
        /// 这件东西是「自己要用 / 要留着」的，不卖。
        /// <para>
        /// 目前两类：<b>药</b>（回血回蓝的消耗品）与<b>回城卷</b>（回城工具）。
        /// </para>
        /// <para>
        /// 为什么必须挡：
        /// 1. <b>药</b> —— 药店页的收货类型含 <c>Consumable</c>，而药的 <c>ItemType</c> 就是
        ///    <c>Consumable</c>。商店买来的药带 <c>Locked</c>（引擎 <c>NPCBuy</c> 给所有商品加的），
        ///    引擎会拒绝卖；但**从地上捡的药没有这个标记**，会被「背包快满了 → 回城卖货」贱卖掉。
        ///    卖掉自己的药 → 立刻又断药 → 又得回城买，白跑一趟，「断药能回城买」的闭环就断了。
        /// 2. <b>回城卷</b> —— 同理，而且它更贵（500 金币）。它是回城的工具，卖了等于自断退路。
        /// </para>
        /// <para>
        /// 判定药的标准与 <see cref="BotActions.CountPotion"/> 一致（<c>Stats[Health]</c> /
        /// <c>Stats[Mana]</c> 之一大于 0）—— 否则会出现「数药时算它、卖货时又卖它」的自相矛盾。
        /// </para>
        /// </summary>
        private static bool IsKeepItem(UserItem item, BotPotionBudget budget)
        {
            if (item?.Info == null) return false;

            // 回城卷：回城的工具，绝不能卖。
            if (BotActions.IsTownScroll(item)) return true;

            // 弹药（护身符 / 毒药粉）：道士的技能消耗品，卖了自己就断弹（2026-11-18）。
            // 补货目标提到 10000 份后，大部分弹药存在背包里，不挡住就会被当杂货卖掉。
            if (item.Info.ItemType == ItemType.Amulet || item.Info.ItemType == ItemType.Poison) return true;

            if (item.Info.ItemType != ItemType.Consumable) return false;

            bool heals = item.Info.Stats[Stat.Health] > 0;
            bool restoresMana = item.Info.Stats[Stat.Mana] > 0;

            // 卷轴 / 食物之类不算药，按普通物品处理（能卖就卖）。
            if (!heals && !restoresMana) return false;

            Stat stat = heals ? Stat.Health : Stat.Mana;
            long weight = (long)item.Info.Weight * Math.Max(1, item.Count);

            // 额度内保留，超出额度的可以卖（见 BotPotionBudget 的说明）。
            return budget.Allow(stat, weight);
        }

        /// <summary>
        /// 本轮遍历要用的药水保留额度。
        /// <para>
        /// 未到「该卖货」的程度（负重 &lt;90%）→ <see cref="BotPotionBudget.Unlimited"/>，一瓶不卖，
        /// 保持原来的保守行为。超重了才限量，把囤积的超出部分吐出来解开死锁。
        /// </para>
        /// </summary>
        private BotPotionBudget NewPotionBudget()
        {
            BotOptions options = Options;

            return BotPotionBudget.For(
                _player.BagWeight,
                _player.Stats[Stat.BagWeight],
                _player.Character?.Class == MirClass.Warrior,
                options.RestockBuyRed,
                options.RestockBuyBlue);
        }

        /// <summary>
        /// 背包里第一件能卖的东西的类型。
        /// <para>
        /// 引擎的卖货校验是 <c>NPCPage.Types.Any(x => x.ItemType == item.Info.ItemType)</c>，
        /// 每个商店只收自己那几类（武器店收 Weapon/Ore、首饰店收
        /// Necklace/Bracelet/Ring、服装店收 Armour/Helmet/Shoes…），
        /// 所以必须**按类型分别跑一趟**，一次只能处理一个类型。
        /// </para>
        /// <para>
        /// 药和回城卷不算「能卖的东西」—— 见 <see cref="IsKeepItem"/>。
        /// </para>
        /// </summary>
        private ItemType SellableItemType()
        {
            Dictionary<ItemType, SellableGroup> groups = SellableTypeCounts();

            if (groups.Count == 0) return ItemType.Nothing;

            // 排序维度按「这次是为了解决什么」来选：
            //   · 负重已经爆了（≥90%）→ 按**总重**挑，而且**不迁就当前地图**；
            //   · 只是格子满了 → 按**件数**挑，优先就地卖（腾格子更快）。
            long capacity = _player.Stats[Stat.BagWeight];
            bool byWeight = capacity > 0 && _player.BagWeight >= capacity * 9 / 10;

            Map current = _player.CurrentMap;

            if (byWeight)
            {
                // 减重优先。原来这里先看「当前地图能不能卖」，实测后果是：
                // 三更雪背包里 140 重药水 + 8 重肉，练级图有肉商、没药店 →
                // 每次都挑肉，卖了半小时负重一点没降（143/116 纹丝不动）。
                // 减重是硬需求，值得为它跑一趟能收的城：
                // 当前地图收不了也没关系，SetTradeTarget → ResolveTradeDestination 会挑一张能收的图。
                return groups.OrderByDescending(x => x.Value.Weight).First().Key;
            }

            // 挑「**当前地图**就能卖」的类型：卖货最怕跑一趟还是卖不掉，
            // 沙漠土城/诺玛村落 都没有肉类收购商，而挖取物恰恰全是 Meat。
            ItemType pick = PickByMap(groups, current, byWeight);

            if (pick != ItemType.Nothing) return pick;

            // 退一步：绑定主城能卖的
            MapInfo home = _player.Character?.BindPoint?.BindRegion?.Map;
            Map homeMap = home == null ? null : SEnvir.GetMap(home);

            pick = PickByMap(groups, homeMap, byWeight);

            if (pick != ItemType.Nothing) return pick;

            // 都不行就交给目的地解析去挑一张能收的图（宁可跑一趟，也别抱着背包不动）
            return groups.OrderByDescending(x => x.Value.Count).First().Key;
        }

        /// <summary>一类可卖物品的统计：件数与总重。</summary>
        private sealed class SellableGroup
        {
            public int Count;
            public long Weight;
        }

        /// <summary>在 <paramref name="map"/> 收得下的类型里，挑排序值最高的那类。</summary>
        private static ItemType PickByMap(Dictionary<ItemType, SellableGroup> groups, Map map, bool byWeight)
        {
            if (map == null) return ItemType.Nothing;

            ItemType best = ItemType.Nothing;
            long bestRank = 0;

            foreach (KeyValuePair<ItemType, SellableGroup> pair in groups)
            {
                long rank = byWeight ? pair.Value.Weight : pair.Value.Count;

                if (rank <= bestRank) continue;
                if (BotPerception.FindTrader(map, pair.Key) == null) continue;

                best = pair.Key;
                bestRank = rank;
            }

            return best;
        }

        /// <summary>背包里每种「可卖」物品各有多少件、多少重。</summary>
        private Dictionary<ItemType, SellableGroup> SellableTypeCounts()
        {
            Dictionary<ItemType, SellableGroup> result = new Dictionary<ItemType, SellableGroup>();

            BotPotionBudget budget = NewPotionBudget();

            foreach (UserItem item in _player.Inventory)
            {
                if (item?.Info == null) continue;
                if (!item.Info.CanSell) continue;
                if (BotItemRules.IsSellBlocked(item.Flags)) continue;
                if (IsKeepItem(item, budget)) continue;

                ItemType type = item.Info.ItemType;

                if (!result.TryGetValue(type, out SellableGroup group))
                {
                    group = new SellableGroup();
                    result[type] = group;
                }

                group.Count++;
                group.Weight += (long)item.Info.Weight * Math.Max(1, item.Count);
            }

            return result;
        }

        /// <summary>设置本轮交易目标（类型 + 目的地地图）。返回 false = 没地方卖，别白跑。</summary>
        /// <summary>
        /// 依次试背包里**存在的**可卖类型，返回第一个「真的有人收」的类型（2026-11-18）。
        /// <para>
        /// 原来只挑一个类型（<see cref="SellableItemType"/>）就去 <c>SetTradeTarget</c>：
        /// 那一类没人收（材料 <c>Nothing</c> 尤其明显，<see cref="ResolveTradeDestination"/>
        /// 对它是直接返回绑定点、不校验有没有收购商）→ 整趟不卖，
        /// 而背包里其实还有武器/护甲/肉类能卖。实测日志「卖不掉的货」里
        /// 布衣、铁剑、食人花果实一起挂着，就是这个原因。
        /// </para>
        /// </summary>
        private bool TrySetTradeTargetForAnySellable(out ItemType picked)
        {
            picked = ItemType.Nothing;

            Dictionary<ItemType, SellableGroup> groups = SellableTypeCounts();

            if (groups.Count == 0) return false;

            ItemType first = SellableItemType();

            List<ItemType> order = new List<ItemType>(groups.Count);

            if (first != ItemType.Nothing && groups.ContainsKey(first)) order.Add(first);

            foreach (ItemType type in groups.Keys)
            {
                if (type != first) order.Add(type);
            }

            foreach (ItemType type in order)
            {
                if (!SetTradeTarget(type)) continue;

                // 目的地要有**真的收这种货**的 NPC，否则跑过去也是空手而归。
                Map dest = _tradeDest == null ? null : SEnvir.GetMap(_tradeDest);

                if (dest != null && BotPerception.FindTrader(dest, type) == null) continue;

                picked = type;
                return true;
            }

            _tradeDest = null;

            return false;
        }

        private bool SetTradeTarget(ItemType type)
        {
            _tradeItemType = type;
            _tradeDest = ResolveTradeDestination(type);

            // 新的一趟，靠近进度清零（见 _tradeApproachBest）。
            _tradeApproachBest = int.MaxValue;
            _tradeApproachSince = SEnvir.Now;

            return _tradeDest != null;
        }

        /// <summary>
        /// 挑一张**真正收这种货**的地图：当前图 → 绑定主城 → 白名单里最近的图。
        /// 全都收不了就返回 null（调用方放弃卖货，避免每帧重试烧 CPU）。
        /// </summary>
        private MapInfo ResolveTradeDestination(ItemType type)
        {
            Map current = _player.CurrentMap;
            MapInfo home = _player.Character?.BindPoint?.BindRegion?.Map;

            if (_tradeIsRepair) return ResolveRepairDestination(type, current, home);

            if (type == ItemType.Nothing) return home;

            // ① 就地能卖 → 一步都不用跑
            if (current != null && BotPerception.FindTrader(current, type) != null && !IsTradeMapBlocked(current.Info))
                return current.Info;

            // ② 绑定主城（回城卷直达，1 秒）
            Map homeMap = home == null ? null : SEnvir.GetMap(home);

            if (homeMap != null && BotPerception.FindTrader(homeMap, type) != null && !IsTradeMapBlocked(home))
                return home;

            // ③ 白名单里（练级图，有六合石直达）按索引距离挑最近的一张
            MapInfo best = null;
            int bestDist = int.MaxValue;

            foreach (int index in Options.HuntMaps)
            {
                MapInfo info = SEnvir.MapInfoList.Binding.FirstOrDefault(x => x.Index == index);

                if (info == null || info == home) continue;
                if (IsTradeMapBlocked(info)) continue;

                Map map = SEnvir.GetMap(info);

                if (map == null || BotPerception.FindTrader(map, type) == null) continue;

                int dist = Math.Abs(info.Index - (current?.Info?.Index ?? home?.Index ?? 0));

                if (dist >= bestDist) continue;

                bestDist = dist;
                best = info;
            }

            return best;
        }

        /// <summary>这张图上有没有**收这一类**的修理铺（武器/首饰/服装各一家，不能混）。</summary>
        private static bool HasRepairShopFor(Map map, ItemType type)
        {
            if (map == null) return false;

            NPCObject npc = BotPerception.FindRepairShop(map, type);

            return npc != null && BotPerception.RepairAcceptsType(npc.NPCInfo, type);
        }

        /// <summary>
        /// 挑一家**收这一类装备**的修理铺所在地图：当前图 → 绑定主城 → 全服可达图。
        /// <para>
        /// <b>为什么必须走到第三级</b>：修理铺按店铺分家，小村子常常缺一家 ——
        /// 潘夜村(2) 只有武器店和首饰店、**没有服装店**，而假人老家就是潘夜村。
        /// 只看「当前图 + 主城」的话，衣服耐久见底永远找不到铺子，
        /// 只会反复「找不到 → 10 分钟冷却」（实测 <c>老酒坛</c> 衣服 0%、<c>半盏茶</c> 10%）。
        /// </para>
        /// <para>
        /// 第三级优先**六合石直达**（1 秒、3000 金），其次两跳，最后才交给 AutoPath 走图，
        /// 免得为了一件装备横穿半个世界。
        /// </para>
        /// </summary>
        private MapInfo ResolveRepairDestination(ItemType type, Map current, MapInfo home)
        {
            // ① 就地能修 → 一步都不用跑
            if (current != null && HasRepairShopFor(current, type)) return current.Info;

            // ② 绑定主城（回城卷直达，1 秒）
            Map homeMap = home == null ? null : SEnvir.GetMap(home);

            if (home != null && HasRepairShopFor(homeMap, type) && !IsTradeMapBlocked(home)) return home;

            // ③ 全服找：按「六合石直达 → 两跳 → 其他」排序，同级挑索引最近的
            HashSet<int> direct = BotPerception.TeleportTargetsFrom(current?.Info ?? home);

            MapInfo best = null;
            int bestRank = int.MaxValue;
            int bestDist = int.MaxValue;

            foreach (MapInfo info in BotPerception.RepairMapsFor(type))
            {
                if (info == null) continue;
                if (info == home) continue;
                if (current?.Info == info) continue;
                if (IsTradeMapBlocked(info)) continue;

                int rank;

                if (direct.Contains(info.Index)) rank = 0;
                else if (BotPerception.TryFindTwoHopRoute(current?.Info ?? home, info.Index, out _)) rank = 1;
                else rank = 2;

                int dist = Math.Abs(info.Index - (current?.Info?.Index ?? home?.Index ?? 0));

                if (rank > bestRank) continue;
                if (rank == bestRank && dist >= bestDist) continue;

                bestRank = rank;
                bestDist = dist;
                best = info;
            }

            return best;
        }

        /// <summary>这张图是不是刚被判定「走不到商人 / 去不了」，还在退避期内。</summary>
        private bool IsTradeMapBlocked(MapInfo map)
        {
            if (map == null) return false;
            if (!_tradeMapBlockedUntil.TryGetValue(map.Index, out DateTime until)) return false;

            return SEnvir.Now < until;
        }

        /// <summary>把「走不到商人 / 去不了」的图拉黑 5 分钟，让下一次卖货换一张图。</summary>
        private void BlacklistTradeMap(MapInfo map)
        {
            if (map == null) return;

            _tradeMapBlockedUntil[map.Index] = SEnvir.Now.AddMinutes(5);

            BotLog.Warn(Profile.Seq, string.Format("{0} 的商人去不了，5 分钟内不再选它当卖货目的地",
                map.Description));
        }

        /// <summary>回城交易的主流程：走回主城 → 找到对应 NPC → 卖货或修理。</summary>
        private void ProcessTrade()
        {
            if (SEnvir.Now >= _stateDeadline)
            {
                FinishTrade("交易超时，先回去练级");

                // 走不回城（没卷 + 路线又远）时，20 秒冷却会让它立刻再跑一趟，
                // 结果整段时间都在路上。实测 BOT#11 连续 12 次采样「正在走回 潘夜村 交易」。
                // 超时属于「这趟不划算」，给 5 分钟冷却，先回去练级攒卷。
                _sellCooldown = SEnvir.Now.AddMinutes(5);
                _repairCooldown = SEnvir.Now.AddMinutes(5);

                return;
            }

            if (_player.Character == null)
            {
                FinishTrade("角色信息缺失，放弃交易");
                return;
            }

            // 对话已打开：直接卖 / 修
            if (_player.NPCPage != null)
            {
                if (_tradeIsRepair) RepairEquipment();
                else SellItems();

                return;
            }

            // 目的地不一定是绑定主城：沙漠土城/诺玛村落没有肉类收购商，
            // 硬回主城只会「里没有收 Meat 的商人」然后每帧重试（实测 66,635 次空跑）。
            MapInfo home = _tradeDest;

            if (home == null)
            {
                FinishTrade(string.Format("没有任何地图收 {0}，放弃这趟卖货", BotText.Item(_tradeItemType)));
                return;
            }

            // 不在目的地：**优先用回城卷瞬移**（和补给流程同一套逻辑）。
            // 不用卷的话只能跨图走回去 —— 低级假人在路上会被打死，
            // 30 个假人里有 12 个同时卡在「回城卖货」走不回来就是这么来的。
            if (_player.CurrentMap?.Info != home)
            {
                // ① 先试**六合石直达**（1 秒）。
                //    为什么必须先于走路：卖货目的地常常是另一座城（诺玛村落没有收 Meat 的商人），
                //    走过去要好几分钟，实测 29 分钟里「交易超时」21 次 —— 假人整段时间都在赶路。
                if (TryTradeTeleport(home)) return;

                // ② 回城卷只会把人送回**绑定主城**。目的地不是主城时用卷 =
                // 「传过去 → 还是不在目的地 → 再传」，几分钟就能把卷烧光，
                // 而且位置原地踏步。这种情形只能走 AutoPath。
                MapInfo bind = _player.Character?.BindPoint?.BindRegion?.Map;

                if (bind == home && TryReturnHomeByScroll(home)) return;

                if (_player.AutoPath != null)
                {
                    LastDetail = string.Format("正在走回 {0} 交易", home.Description);
                    return;
                }

                if (!AutoPathService.Instance.TryStart(_player, home, HomePoint(home)))
                {
                    // 死亡期间规划被拒（见 AutoPathService.Start）不代表这张图去不了，
                    // 拉黑会把它永久排除在卖货目的地之外。
                    if (_player.Dead) return;

                    // 规划不出来 = 这张图根本去不了（等级/道具限制）。把它也拉黑，
                    // 否则下一次 ResolveTradeDestination 还会挑中它，变成「规划失败 → 20 秒后又规划失败」。
                    BlacklistTradeMap(home);

                    FinishTrade(string.Format("回 {0} 的路线规划失败，换张图卖", home.Description));
                    return;
                }

                LastDetail = string.Format("走回 {0} 交易", home.Description);

                return;
            }

            // 在目的地：找对应的 NPC（修理要按类型挑铺子，见 FindRepairShop）
            NPCObject npc = _tradeIsRepair
                ? BotPerception.FindRepairShop(_player.CurrentMap, _tradeItemType)
                : BotPerception.FindTrader(_player.CurrentMap, _tradeItemType);

            if (npc == null)
            {
                FinishTrade(_tradeIsRepair
                    ? string.Format("{0} 里找不到修理铺", home.Description)
                    : string.Format("{0} 里没有收 {1} 的商人", home.Description, BotText.Item(_tradeItemType)));

                return;
            }

            int distance = BotNavigator.Distance(_player.CurrentLocation, npc.CurrentLocation);

            // 走不到就换图（见 _tradeApproachBest）：30 秒没能靠近一格 = 这条路堵死了，
            // 别在这儿耗到 3 分钟交易超时、再重来一遍（实测那样能耗 14 分钟以上）。
            if (distance < _tradeApproachBest)
            {
                _tradeApproachBest = distance;
                _tradeApproachSince = SEnvir.Now;
            }
            else if (distance > 2 && SEnvir.Now > _tradeApproachSince.AddSeconds(TradeApproachStallSeconds))
            {
                BlacklistTradeMap(home);

                // 把「卡在哪」一起打出来：只看「30 秒没能靠近一格」分不清是被人群挤在
                // 街口、还是最后一步的相邻格永远被别人占着 —— 两者的修法完全不同。
                FinishTrade(string.Format("走不到 {0}（{1} 秒没能靠近一格，卡在 ({2},{3})，还差 {4} 格，最好成绩 {5} 格，{6}）",
                    npc.Name, TradeApproachStallSeconds,
                    _player.CurrentLocation.X, _player.CurrentLocation.Y,
                    distance, _tradeApproachBest,
                    _player.AutoPath != null ? "自动寻路中" : "自己走"));

                return;
            }

            // 和补给同一套：远距离交给 AutoPath，贪心走格在城内会原地打转。
            if (distance > AutoPathToNpcDistance)
            {
                if (_player.AutoPath != null)
                {
                    LastDetail = string.Format("自动寻路走向 {0}（{1} 格）", npc.Name, distance);
                    return;
                }

                if (AutoPathService.Instance.TryStart(_player, npc.NPCInfo))
                {
                    _blockedCount = 0;
                    LastDetail = string.Format("自动寻路走向 {0}（{1} 格）", npc.Name, distance);
                    return;
                }
            }

            if (_player.AutoPath != null)
                AutoPathService.Instance.Cancel(_player);

            // NPC 本身是阻挡物：贴身那一步永远迈不进去，
            // 所以走不动就直接开口（NPCCall 只要 18 格视野内）。
            // 终点用「NPC 周围的可走格」（半径 1），否则 A* 只会把最后一步指向 NPC 那格。
            if (distance > 2)
            {
                BotMoveResult move = BotNavigator.TryStepTowardsNear(_player, npc.CurrentLocation, 1);

                if (move == BotMoveResult.Moved)
                {
                    _blockedCount = 0;
                    LastDetail = string.Format("走向 {0}（{1} 格）", npc.Name, distance);
                    return;
                }

                if (move == BotMoveResult.Busy) return;

                if (BotNavigator.TryStepAdjacent(_player, npc.CurrentLocation))
                {
                    _blockedCount = 0;
                    LastDetail = string.Format("贴近 {0}（{1} 格）", npc.Name, distance);
                    return;
                }
            }

            if (!BotActions.TryOpenNpc(_player, npc))
            {
                _blockedCount++;

                if (_blockedCount > 20)
                {
                    FinishTrade(string.Format("走不到 {0}（{1} 格外始终过不去）", npc.Name, distance));
                    return;
                }

                LastDetail = string.Format("还差 {1} 格才够到 {0}", npc.Name, distance);

                return;
            }

            _blockedCount = 0;

            LastDetail = string.Format("打开 {0}，准备{1}", npc.Name, _tradeIsRepair ? "修理" : "卖货");
        }

        /// <summary>在已打开的 NPC 页里卖背包里该类型的货。</summary>
        private void SellItems()
        {
            // 当前页不一定是收这类货的页（入口页 / 别的按钮页），要点对按钮。
            if (_player.NPCPage.DialogType != NPCDialogType.BuySell)
            {
                int button = BotPerception.FindTradeButton(_player.NPC?.NPCInfo, _tradeItemType);

                if (button <= 0)
                {
                    FinishTrade(string.Format("这个 NPC 页里没有卖 {0} 的入口", BotText.Item(_tradeItemType)));
                    return;
                }

                BotActions.TryPressButton(_player, button);

                LastDetail = string.Format("打开卖 {0} 的页面", BotText.Item(_tradeItemType));

                return;
            }

            List<CellLinkInfo> links = BuildSellLinks(_tradeItemType);

            if (links.Count == 0)
            {
                FinishTrade(string.Format("没有可卖的 {0}", BotText.Item(_tradeItemType)));
                return;
            }

            long goldBefore = GoldAmount();

            BotActions.TrySell(_player, links);

            long goldAfter = GoldAmount();

            LastDetail = string.Format("卖了 {0}，金币 {1} → {2}", links.Count, goldBefore, goldAfter);

            // 循环卖货（2026-11-15，生产诊断）：原来卖完一类就 FinishTrade 关对话，
            // 背包里其它类的可卖物品没被卖 → 负重降不下来 → 买药买不下弹药。
            // 现在卖完一类后，如果背包仍需要清理且当前 NPC 还收其它可卖类型，
            // 切换下一类继续卖，不关对话、不回城重跑。
            if (NeedsMoreSelling() && TrySwitchToNextSellableType())
            {
                return;  // 已切换类型并点好按钮，下一帧继续卖新类型
            }

            FinishTrade(string.Format("卖了 {0} 件 {1}，赚了 {2} 金币",
                links.Count, BotText.Item(_tradeItemType), goldAfter - goldBefore), true);
        }

        /// <summary>
        /// 卖完一类后，背包是否仍需继续清理（2026-11-15，循环卖货用）。
        /// <para>
        /// 判据：负重 ≥ 90% 容量（减重需求），或空闲格 ≤ 5（腾格子需求）。
        /// 两者都不满足说明背包已清到可接受程度，可以收尾。
        /// </para>
        /// </summary>
        private bool NeedsMoreSelling()
        {
            // 只要背包还有可卖物品（肉/装备/材料），就继续卖下一类 —— 不再要求负重≥90%或空格≤5。
            // 用户 2026-11-18 反馈：补给后回刷怪点背包还有一堆装备/肉没卖掉，就是旧判据太保守，
            // 卖完一类（比如肉）就收尾，装备/材料留在背包里带回练级点。
            return HasSellableItems();
        }

        /// <summary>
        /// 在当前已打开的 NPC 卖货页里，切换到下一个「背包里仍有、且该 NPC 收」的可卖类型
        /// （2026-11-15，循环卖货用）。
        /// <para>
        /// 返回 true = 已切换 <c>_tradeItemType</c> 并点好对应按钮，下一帧 <see cref="SellItems"/>
        /// 继续卖新类型；返回 false = 没有可切换的（该 NPC 只收这一类，或背包已无可卖），
        /// 调用方应 <see cref="FinishTrade"/> 收尾。
        /// </para>
        /// <para>
        /// 按总重降序挑（减重优先），跳过当前正在卖的类型，只考虑当前 NPC 页收的类型
        /// （<c>NPCPage.Types</c>）—— 否则点了按钮也是白点（引擎静默拒绝）。
        /// </para>
        /// </summary>
        private bool TrySwitchToNextSellableType()
        {
            Dictionary<ItemType, SellableGroup> groups = SellableTypeCounts();

            if (groups.Count == 0) return false;

            // 当前 NPC 页收哪些类型。
            HashSet<ItemType> accepted = new HashSet<ItemType>();

            if (_player.NPCPage?.Types != null)
            {
                foreach (NPCType type in _player.NPCPage.Types)
                {
                    if (type?.ItemType != ItemType.Nothing) accepted.Add(type.ItemType);
                }
            }

            ItemType pick = ItemType.Nothing;

            foreach (KeyValuePair<ItemType, SellableGroup> pair in groups.OrderByDescending(x => x.Value.Weight))
            {
                if (pair.Key == _tradeItemType) continue;
                if (!accepted.Contains(pair.Key)) continue;

                pick = pair.Key;
                break;
            }

            if (pick == ItemType.Nothing) return false;

            int button = BotPerception.FindTradeButton(_player.NPC?.NPCInfo, pick);

            if (button <= 0) return false;

            _tradeItemType = pick;

            BotActions.TryPressButton(_player, button);

            LastDetail = string.Format("继续卖 {0}", BotText.Item(pick));

            return true;
        }

        /// <summary>构造要卖出的物品列表（只挑当前商店收、且引擎允许卖的那些）。</summary>
        private List<CellLinkInfo> BuildSellLinks(ItemType itemType)
        {
            List<CellLinkInfo> links = new List<CellLinkInfo>();

            if (_player.NPCPage?.Types == null) return links;

            bool accepted = false;

            foreach (NPCType type in _player.NPCPage.Types)
            {
                if (type?.ItemType == itemType) { accepted = true; break; }
            }

            // 这个商店不收这类东西 —— 引擎会静默拒绝，别白跑一趟。
            if (!accepted) return links;

            BotPotionBudget budget = NewPotionBudget();

            for (int i = 0; i < _player.Inventory.Length; i++)
            {
                UserItem item = _player.Inventory[i];

                if (item?.Info == null) continue;
                if (item.Info.ItemType != itemType) continue;
                if (!item.Info.CanSell) continue;
                if (item.Count <= 0) continue;

                // 锁定 = 防手滑，真人中键就能解（2026-11-18）。
                // 卖货前先解锁，否则这些货永远卖不掉 —— 引擎 NPCSell 直接拒收 Locked。
                // 零价（Worthless）/ 婚戒 / 要留的药不解，交给下面的过滤挡掉。
                if ((item.Flags & UserItemFlags.Locked) == UserItemFlags.Locked)
                {
                    bool worthUnlocking = (item.Flags & (UserItemFlags.Worthless | UserItemFlags.Marriage)) == UserItemFlags.None
                        && !IsKeepItem(item, budget);

                    if (!worthUnlocking || !BotActions.TryUnlockItem(_player, i)) continue;
                }

                if (BotItemRules.IsSellBlocked(item.Flags)) continue;

                // 药和回城卷不卖（药店收货类型含 Consumable，引擎不会拦，得自己挡）。
                // 但超重时额度外的药水可以卖 —— 否则囤了几十瓶药就再也降不下负重（见 BotPotionBudget）。
                if (IsKeepItem(item, budget)) continue;

                links.Add(new CellLinkInfo
                {
                    GridType = GridType.Inventory,
                    Slot = i,
                    Count = item.Count,
                });
            }

            return links;
        }

        /// <summary>在已打开的修理页里，把所有耐久不足的装备修一遍。</summary>
        private void RepairEquipment()
        {
            if (_player.NPCPage.DialogType != NPCDialogType.Repair)
            {
                int button = BotPerception.FindRepairButton(_player.NPC?.NPCInfo);

                if (button <= 0)
                {
                    FinishTrade("这个 NPC 页里没有修理入口");
                    return;
                }

                BotActions.TryPressButton(_player, button);

                LastDetail = "打开修理页";

                return;
            }

            List<CellLinkInfo> links = BuildRepairLinks();

            if (links.Count == 0)
            {
                FinishTrade("没有要修的装备");
                return;
            }

            long goldBefore = GoldAmount();

            // 修理前记录各件装备的持久：服务端在金币不足 / DialogType 不对 / ParseLinks 失败时
            // **静默 return** —— 金币不变、持久不变。只发包不验结果的话，
            // 日志会误报「修了 X 件、花了 0 金币」，实际一件都没修上
            //（2026-11-14 现场：同一假人 88 秒修两次同一套、每次 0 金币）。
            int durabilityBefore = 0;

            foreach (CellLinkInfo link in links)
            {
                UserItem item = _player.Equipment[link.Slot];

                if (item != null) durabilityBefore += item.CurrentDurability;
            }

            BotActions.TryRepair(_player, links);

            long goldAfter = GoldAmount();

            int durabilityAfter = 0;

            foreach (CellLinkInfo link in links)
            {
                UserItem item = _player.Equipment[link.Slot];

                if (item != null) durabilityAfter += item.CurrentDurability;
            }

            // 验结果：金币减少（扣了钱）**或**持久增加（修上了），才算真修了；
            // 两者都不变 = 服务端静默拒绝，按失败记（Warn 级别，日志直接能看出没生效）。
            bool repaired = goldAfter < goldBefore || durabilityAfter > durabilityBefore;

            // D10（2026-11-17，步骤 4-3）：修完这一类后，当前图（主城）还有别类装备见底
            // → 同一趟一并修掉。原来每类回一趟（间隔 10 分钟冷却），武器+衣服见底要跑两趟城；
            // 方案括号补充 7 要求「主城修理铺齐全，回城时一并修掉」。
            if (repaired && TryContinueRepairSameTrip())
            {
                BotLog.Debug(Profile.Seq, string.Format(
                    "修了 {0}，花了 {1} 金币；当前图还有别类装备见底，同趟继续修",
                    RepairNames(links), goldBefore - goldAfter));

                return;
            }

            // 日志要写清**修的是哪几件**：只写「修了 1 件装备」分不清修的是武器还是衣服，
            // 而「衣服到底修没修上」正是这一轮要验证的东西（三家铺子各收一类，很容易修错家）。
            FinishTrade(repaired
                ? string.Format("修了 {0}，花了 {1} 金币", RepairNames(links), goldBefore - goldAfter)
                : string.Format("修理未生效（{0}，金币 {1}→{2}，持久 {3}→{4}，服务端静默拒绝）",
                    RepairNames(links), goldBefore, goldAfter, durabilityBefore, durabilityAfter),
                repaired);
        }

        /// <summary>
        /// 修完一类后，同一趟接着修下一类（D10，2026-11-17）。
        /// <para>
        /// 只在**当前图就能找到下一类修理铺**时继续：当前图修不了下一类就收工回练级，
        /// 不跨图去修——跨图修会变成「修一趟 + 补给一趟」的两趟，违背一站完成。
        /// </para>
        /// <para>
        /// 返回 true = 已切换修理目标并关掉当前对话，下一帧 <see cref="RepairEquipment"/>
        /// 会在当前图找下一家铺子；<c>_tradeDest</c> 不变（当前图就是这趟的目的地）。
        /// </para>
        /// </summary>
        private bool TryContinueRepairSameTrip()
        {
            ItemType next = NextRepairType();

            if (next == ItemType.Nothing) return false;

            Map current = _player.CurrentMap;

            if (current == null || !HasRepairShopFor(current, next)) return false;

            _tradeItemType = next;

            // 换了修理铺，靠近进度清零：新 NPC 的距离重新计，别拿上一家的「最好成绩」卡新铺子。
            _tradeApproachBest = int.MaxValue;
            _tradeApproachSince = SEnvir.Now;

            // 换修理铺必须先关掉当前对话，否则引擎会一直停在这家铺子的页上。
            BotActions.CloseNpc(_player);

            LastDetail = string.Format("同趟继续修{0}", BotText.Item(next));

            return true;
        }

        /// <summary>当前身上还需要修的下一类装备（耐久≤15% 的类任取一类；修过的耐久已恢复，自然不再命中）。</summary>
        private ItemType NextRepairType()
        {
            foreach (UserItem item in _player.Equipment)
            {
                if (item?.Info == null) continue;
                if (item.Info.Durability <= 0) continue;
                if (!item.Info.CanRepair) continue;
                if (item.MaxDurability <= 0) continue;

                if (!BotItemRules.NeedsRepair(item.CurrentDurability, item.MaxDurability, RepairDurabilityPercent)) continue;

                return item.Info.ItemType;
            }

            return ItemType.Nothing;
        }

        /// <summary>把要修的槽位翻成「布衣（男）、铁剑」这样的名字串，给日志用。</summary>
        private string RepairNames(List<CellLinkInfo> links)
        {
            List<string> names = new List<string>();

            foreach (CellLinkInfo link in links)
            {
                UserItem item = _player.Equipment[link.Slot];

                names.Add(item?.Info?.ItemName ?? "?");
            }

            if (names.Count == 0) return "0 件装备";

            // 一身 14 件同时见底的情况很少，真遇上了也别把一行日志写成一篇小作文。
            if (names.Count > 4) return string.Format("{0} 等 {1} 件", string.Join("、", names.GetRange(0, 4)), names.Count);

            return string.Join("、", names);
        }

        /// <summary>构造要修理的装备列表（只挑这个修理铺收、且耐久不足的）。</summary>
        private List<CellLinkInfo> BuildRepairLinks()
        {
            List<CellLinkInfo> links = new List<CellLinkInfo>();

            if (_player.NPCPage?.Types == null) return links;

            for (int i = 0; i < _player.Equipment.Length; i++)
            {
                UserItem item = _player.Equipment[i];

                if (item?.Info == null) continue;
                if (!item.Info.CanRepair) continue;
                if (item.Info.Durability <= 0) continue;
                if (item.CurrentDurability >= item.MaxDurability) continue;

                if ((item.Flags & UserItemFlags.Marriage) == UserItemFlags.Marriage) continue;

                // 修理页按类型收（武器修理只收 Weapon、防具修理只收 Armour/Helmet/Shoes…）。
                bool accepted = false;

                foreach (NPCType type in _player.NPCPage.Types)
                {
                    if (type?.ItemType == item.Info.ItemType) { accepted = true; break; }
                }

                if (!accepted) continue;

                links.Add(new CellLinkInfo
                {
                    GridType = GridType.Equipment,
                    Slot = i,
                    Count = item.Count,
                });
            }

            return links;
        }

        /// <summary>
        /// 交易收尾：关对话、上冷却、回练级。
        /// <paramref name="ok"/> = 这趟真的卖出/修好了（走 Info）；否则（超时、走不到、没商人）走 Warn。
        /// </summary>
        private void FinishTrade(string detail, bool ok = false)
        {
            BotActions.CloseNpc(_player);

            // 同补给：可能用 AutoPath 走过 NPC，收尾必须撤掉。
            if (_player.AutoPath != null)
                AutoPathService.Instance.Cancel(_player);

            // 冷却分开设：卖货 20 秒（背包还满的话下一拍就要再卖）、修理 10 分钟（耐久掉得慢）。
            // 卖货 20 秒而不是 1 分钟：原来「药空了 → 等冷却」这一分钟正好是被打死的时候。
            //
            // 修理要看「修完还有没有别的部位见底」：武器 / 首饰 / 防具分属三家店，
            // 一趟只能修一类。还有别的就只给 60 秒，让它接着跑下一家；都修好了才给 10 分钟。
            if (_tradeIsRepair) _repairCooldown = SEnvir.Now.AddSeconds(HasBrokenEquipment() ? 60 : 600);
            // 还有可卖物品（这个 NPC 收不了、要换下一家）就只等 2 秒，尽快去下一个收购商；
            // 全卖完了才按常规 20 秒冷却。否则「卖完肉收尾 → 20 秒后才去卖装备」太慢。
            else _sellCooldown = SEnvir.Now.AddSeconds(HasSellableItems() ? 2 : 20);

            // 收尾时还有可卖物品 → 冷却结束后换一家收购商继续卖（2026-11-18 用户要求：
            // 「卖完身上的物品再去刷怪点」）。卖成功了就把失败计数清零。
            if (ok) _sellPendingAttempts = 0;

            _sellPending = HasSellableItems();

            // 正常收尾也清掉「被反复打断」的计数（2026-11-18）。
            _tradeInterruptCount = 0;
            _tradeInterruptSince = default;

            _blockedCount = 0;
            _target = null;
            _tradeDest = null;

            TradeCount++;
            _player.Character.BotTrades++;

            // 成功 Info / 失败 Warn：卖货是日常动作，全按 Warn 记会把日志刷成一片 WARN。
            // 但失败必须留痕 —— 「没有收 Meat 的商人」曾经一个字都没有，只能靠面板猜。
            if (ok) BotLog.Info(Profile.Seq, detail);
            else BotLog.Warn(Profile.Seq, detail);

            // 卖货收尾 → 下一跳去刷怪点时打一行背包快照（2026-11-18 用户要求）。
            _logBagOnNextHunt = true;

            // 卖完 / 修完同样按概率在城里待一会儿（P2-3）。
            LeaveTownOrStroll(detail);
        }

        #endregion

        /// <summary>回城落点：优先绑定点安全区的区域点，取不到就退地图中心。</summary>
        private Point HomePoint(MapInfo home)
        {
            MapRegion region = _player.Character?.BindPoint?.BindRegion;

            if (region?.PointList != null && region.PointList.Count > 0)
                return region.PointList[SEnvir.Random.Next(region.PointList.Count)];

            Map map = SEnvir.GetMap(home);

            if (map != null) return new Point(map.Width / 2, map.Height / 2);

            return _player.CurrentLocation;
        }

        /// <summary>
        /// 任意一座城的落点。**不能复用 <see cref="HomePoint"/>** —— 那个取的是绑定点安全区，
        /// 目的地不是绑定点时会返回另一张图上的坐标，AutoPath 会直接判「目标不在本图」。
        /// </summary>
        private Point TownPoint(MapInfo town)
        {
            if (town == null) return _player.CurrentLocation;

            foreach (SafeZoneInfo zone in SEnvir.SafeZoneInfoList.Binding)
            {
                if (zone?.BindRegion?.Map != town) continue;

                // ValidBindPoints 是引擎挑过的「能站的格子」，比 PointList 更保险。
                if (zone.ValidBindPoints != null && zone.ValidBindPoints.Count > 0)
                    return zone.ValidBindPoints[SEnvir.Random.Next(zone.ValidBindPoints.Count)];

                if (zone.BindRegion.PointList != null && zone.BindRegion.PointList.Count > 0)
                    return zone.BindRegion.PointList[SEnvir.Random.Next(zone.BindRegion.PointList.Count)];
            }

            Map map = SEnvir.GetMap(town);

            if (map != null) return new Point(map.Width / 2, map.Height / 2);

            return _player.CurrentLocation;
        }

        private long GoldAmount()
        {
            UserCurrency gold = _player.Character?.Gold;

            return gold?.Amount ?? 0;
        }

        #endregion

        #region 选点

        /// <summary>
        /// 在当前地图的刷怪区里挑一个「走得到」的点。
        /// P0 只做同图：跨图要靠 AutoPathService，而它被 MapInfo.CanAutoPath 卡着（本库几乎全关）。
        /// </summary>
        private bool TryPickHuntPoint(bool ignoreCapacity = false)
        {
            Map map = _player.CurrentMap;

            if (map?.Info == null) return false;

            // 这张图上的人已经超过容量 → 交给 ProcessIdle 去跨图分流。
            // ignoreCapacity = true 是兜底：跨图也去不了的时候，宁可挤一点也别站着不动。
            if (!ignoreCapacity && CountBotsOnMap(map.Info) > MapCapacity) return false;

            // 用本实例的缓存快照（5 秒刷一次），别在这里现建 ——
            // 选练级点是每个假人每轮都会走到的路径，FromConfig 里有一次字符串 Split。
            BotOptions options = Options;

            // 白名单地图（Config.BotHuntMaps）：不在名单里的图不练级，免得假人跑去高级图抢资源。
            if (options.HuntMaps.Count > 0 && !options.HuntMaps.Contains(map.Info.Index))
            {
                BotLog.Debug(Profile.Seq, string.Format("{0}(Index {1}) 不在练级白名单 [{2}] 里",
                    map.Info.Description, map.Info.Index, string.Join(",", options.HuntMaps)));

                return false;
            }

            // 负载负权重（D5）：收集本图其他假人的位置（不含自己），供选点负载打分。
            // 选点是低频路径（每次选点跑一次），遍历一次玩家列表的开销可接受。
            List<Point> botLocations = null;

            foreach (PlayerObject player in SEnvir.Players)
            {
                if (player?.Bot == null) continue;
                if (ReferenceEquals(player, _player)) continue;
                if (player.CurrentMap?.Info != map.Info) continue;

                (botLocations ??= new List<Point>()).Add(player.CurrentLocation);
            }

            // 按刷怪区预计算包围盒（区域级负载用），同 region 只算一次。
            Dictionary<MapRegion, BotPerception.HuntRegionBox> regionBoxes = new Dictionary<MapRegion, BotPerception.HuntRegionBox>();

            // 把本图所有「怪等级打得过」的刷怪区摊平，按「怪等级合适 → 离自己近」排序。
            // 之前不过滤等级：最近的刷怪区常常是 5 级以上的怪，1 级号走到那里一只都打不了，
            // 表现是「到达 → 附近全是高级怪 → 空等」的循环。
            List<(Point Point, int Level, MapRegion Region)> candidates = new List<(Point, int, MapRegion)>();

            foreach (RespawnInfo region in SEnvir.RespawnInfoList.Binding)
            {
                if (region.Region?.Map != map.Info) continue;

                MonsterInfo resident = region.Monster;

                if (resident == null) continue;

                if (resident.Level > _player.Level + 4) continue;
                if (resident.Level < _player.Level - 8) continue;

                List<Point> points = region.Region.PointList;

                if (points == null || points.Count == 0) continue;

                // 预计算刷怪区包围盒（区域级负载用，同 region 只算一次）。
                if (!regionBoxes.TryGetValue(region.Region, out BotPerception.HuntRegionBox box))
                {
                    int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;

                    foreach (Point p in points)
                    {
                        if (p.X < minX) minX = p.X;
                        if (p.X > maxX) maxX = p.X;
                        if (p.Y < minY) minY = p.Y;
                        if (p.Y > maxY) maxY = p.Y;
                    }

                    box = new BotPerception.HuntRegionBox(minX, maxX, minY, maxY);
                    regionBoxes[region.Region] = box;
                }

                foreach (Point p in points)
                    candidates.Add((p, resident.Level, region.Region));
            }

            if (candidates.Count == 0)
            {
                BotLog.Debug(Profile.Seq, string.Format("{0} 上没有等级合适的刷怪区（当前 {1} 级，可打上限 {2}）",
                    map.Info.Description, _player.Level, _player.Level + 4));

                return false;
            }

            // 先只留「有经验价值」的怪（≥ 自己 -3），不够再退回全部。
            //
            // 为什么必须加：原来只按距离取最近 40 个点，10 级的假人天天在城门口砍
            // 羊和鸡（1~2 级怪，经验约等于 0），实测 30 个假人跑了一个多小时
            // 平均等级一直卡在 9.37 —— 不是打不动，是在刷没经验的东西。
            List<(Point Point, int Level, MapRegion Region)> worthwhile = candidates
                .Where(x => x.Level >= _player.Level - 3)
                .ToList();

            if (worthwhile.Count == 0) worthwhile = candidates;

            Point origin = _player.CurrentLocation;

            // 排除脚边 8 格以内的点：那样的「到达刷怪点」意味着原地转一圈又回来，
            // 表现就是「到达 → 没怪 → 再到达同一个点」死循环，人也不动。
            List<(Point Point, int Level, MapRegion Region)> pool = worthwhile
                .Where(x => BotNavigator.Distance(origin, x.Point) >= 8)
                .ToList();

            if (pool.Count == 0) pool = worthwhile;

            // 负载负权重（D5）：先给每个候选点算好负载分（点级 + 区域级），
            // 排序时按「等级贴近 → 距离 → 负载轻 → 随机打散」挑综合最优。
            // 负载分预乘进结构只算一次，避免 OrderBy 比较时重复遍历假人列表。
            List<(Point Point, int Level, MapRegion Region, int Load)> scored = pool
                .Select(x => (Point: x.Point, Level: x.Level, Region: x.Region,
                    Load: BotPerception.HuntLoadWeight(x.Point, botLocations, LoadPointRadius,
                        regionBoxes.TryGetValue(x.Region, out var box) ? box : null)))
                .ToList();

            // 排序：先靠近自己等级的怪（同等级段里怪越高级经验越多），再按距离，
            // 再负载负权重（负载轻的优先，群体不挤同一点/区域），最后随机打散（同负载内打散）。
            List<Point> nearest = scored
                .OrderBy(x => Math.Abs(x.Level - _player.Level - 1))
                .Take(60)
                .OrderBy(x => BotNavigator.Distance(origin, x.Point))
                .Take(40)
                .OrderBy(x => x.Load)
                .ThenBy(x => SEnvir.Random.Next())
                .Select(x => x.Point)
                .ToList();

            // 最近的一批点里也有走不通的（隔河、被墙围住）。原来逐个算路：40 个候选点
            // 最坏跑 40 次整图 A*（单次 20~30ms，最坏 1 秒、制造 135ms 尖峰）。
            // 改为多终点一次 A*：打乱后的候选点分批进终点集合，A* 一次算完，
            // 返回最先到达的点（最近的可达点）。一批 10 个，常见情况一批命中（~25ms），
            // 走不通再换下一批，最坏 4 批（~100ms），远好于原来 40 次。
            // 分批 + 沿用已有的打乱，保留了「多个刷怪点分散」的语义（原来打乱后逐个试、
            // 第一个走通的用）。
            const int huntBatchSize = 10;

            for (int offset = 0; offset < nearest.Count; offset += huntBatchSize)
            {
                HashSet<Point> batch = new HashSet<Point>();

                foreach (Point candidate in nearest.GetRange(offset, Math.Min(huntBatchSize, nearest.Count - offset)))
                {
                    // 刚因为「绕圈走不到」被拉黑的点，3 分钟内不再选 ——
                    // 否则按距离排序会把它原样选回来，形成「弃点 → 重选同一点 → 再绕圈」的死循环。
                    if (IsBadHuntPoint(candidate)) continue;

                    batch.Add(candidate);
                }

                if (batch.Count == 0) continue;

                if (!BotNavigator.TryGetNextStepToAny(_player, batch, out _, out Point chosen)) continue;

                _huntPoint = chosen;
                // D7：新刷怪点落定，以当前到点距离作为「最小距离」基线并刷新进展计时，
                // 否则基线默认 0，第一帧量距永远 ≥ 0 会被误判「无进展」立刻触发脱困。
                _huntPointBestDistance = BotNavigator.Distance(_player.CurrentLocation, chosen);
                MarkHuntProgress();
                // 赶路总时限（2026-11-17）：选点落定 = 出发时刻，到达/击杀/换点时清零。
                _huntStartTime = SEnvir.Now;
                _stuckCount = 0;
                _blockedCount = 0;
                _trail.Clear();          // 换了刷怪点 = 重新起算「绕圈」窗口
                _nextAvoidStepTime = default;
                _nextPoisonCheckTime = default;
                _nextSummonCheckTime = default;   // 3-4 召唤：新战斗立即可补召，不等节流
                _nextKiteTime = default;
                _kiteStartTime = default;

                return true;
            }

            return false;
        }

        /// <summary>环形卡死的观察窗口（秒）与容忍半径（格）。</summary>
        private const double StuckWindowSeconds = 8;
        private const int StuckRadius = 4;

        /// <summary>
        /// 换目标 —— 同时把「这只怪归我」的认领登记上，并释放上一只。
        /// <para>
        /// 认领的必要性见 <see cref="BotClaims"/>：不加的话 30 个假人各自独立打分，
        /// 算出来必然是同一只怪（最近的 + 血最少的），于是刚刷的怪被一群人同时扑上去。
        /// </para>
        /// </summary>
        private void SetTarget(MonsterObject monster)
        {
            if (_target != null && !ReferenceEquals(_target, monster))
                BotClaims.Release(_target, Profile.Seq);

            _target = monster;

            if (monster != null)
                BotClaims.Claim(monster, Profile.Seq);
        }

        /// <summary>
        /// 「在原地绕圈」判定 —— 不是「站着不动」，是「一直在走但没走出去」。
        /// <para>
        /// 旧实现只比「当前格 vs 上一格」：只要迈出去一步就把计数清零。
        /// 而卡在遮挡物 / 人群前的典型表现恰恰是<b>左右横跳</b>：
        /// A* 指向被占住的那格 → 迈不动 → 侧移一步 → 下一帧 A* 又指回原格 → 再侧移回来。
        /// 每步都在动，10 秒后还在原地 3 格内，于是<b>永远判不出来</b>
        /// （用户 2026-11-13 录屏复现：假人围着遮挡物转圈转到天荒地老）。
        /// </para>
        /// <para>
        /// 现在的判据：把最近 <see cref="StuckWindowSeconds"/> 秒的位置每 500ms 采一个点，
        /// 算这些点的<b>包围盒</b>；长和宽都 ≤ <see cref="StuckRadius"/> 格就认定「绕圈」。
        /// </para>
        /// <para>
        /// 脱困顺序（照客户端自动挂机的思路：先绕、再换怪、最后换地方）：
        /// ① 用「避让活物」的 A* 重算一次（能绕开人群就直接走）；
        /// ② 还不行 → 弃掉当前怪；
        /// ③ 再不行 → 回待机，由 <see cref="ProcessIdle"/> 重新挑刷怪点或跨图。
        /// </para>
        /// </summary>
        private void TrackStuck()
        {
            DateTime now = SEnvir.Now;
            Point here = _player.CurrentLocation;

            // 「围着目标打」在有些状态下是**正确行为**，绝不能算绕圈。
            //
            // 实测（2026-11-13 本地）：第一版判据在这里误报了 —— BOT#27 在潘夜村砍鸡，
            // 8 秒打一条「原地绕圈…目标=鸡」，而它其实一直跟着鸡在小范围里转。
            // 用「可移动=False」当判据也不行：走路/挥刀动作期间 ActionTime 在将来，
            // 采样随时可能撞上 false，会把真卡死全滤掉。
            if (IsEngagedWithNearbyTarget())
            {
                _trail.Clear();
                return;
            }

            // 每 500ms 采一个点：每帧采太密（8 秒 480 个点），对判定精度也没帮助。
            if (_trail.Count == 0 || now - _trail[_trail.Count - 1].Time >= TimeSpan.FromMilliseconds(500))
                _trail.Add((now, here));

            // 关于「能不能动」：这里**没有**用 `_player.CanMove` 做判据，是被实测否掉的。
            //
            // 2026-11-13 第四轮试过「CanMove 连续 10 秒 false 就判被定住」，
            // 结果 5 分钟刷了 161 条「不能移动 274 秒」，全是假的。两个原因：
            // ① TrackStuck 是**刚迈完一步**才调用的，而 `player.Move` 会把 MoveTime
            //    推到 Now+0.6s —— 也就是说「正在正常走路」的假人在这里采样永远是 false；
            // ② `_cannotMoveSince` 只在「非战斗且能移动」时才会被清，正常打怪的假人
            //    一天走不到那个分支，起点就一直是几分钟前。
            // 结论：这个采样点上 `CanMove` 没有判别力。真正要看的（动作/移动/硬直/中毒）
            // 已经打进下面的绕圈日志里，需要时看那一行就够。

            int drop = 0;

            while (drop < _trail.Count && now - _trail[drop].Time > TimeSpan.FromSeconds(StuckWindowSeconds))
                drop++;

            if (drop > 0) _trail.RemoveRange(0, drop);

            // 窗口没铺满不判：刚出发那几秒本来就在原地附近。
            if (_trail.Count < 4) return;

            if (now - _trail[0].Time < TimeSpan.FromSeconds(StuckWindowSeconds - 1.5)) return;

            int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;

            foreach ((DateTime _, Point point) in _trail)
            {
                if (point.X < minX) minX = point.X;
                if (point.X > maxX) maxX = point.X;
                if (point.Y < minY) minY = point.Y;
                if (point.Y > maxY) maxY = point.Y;
            }

            if (maxX - minX > StuckRadius || maxY - minY > StuckRadius) return;

            // 真的在原地绕圈了。
            _trail.Clear();

            _circleStrikes++;

            // 日志带全现场：只写「绕圈 8 秒」的话，事后完全看不出它到底想去哪
            // （2026-11-13 本地实测就是靠这行才发现「绕圈的是想去刷怪点、而不是追怪」）。
            // 节流 30 秒：一个卡死的假人 8 秒一条会把 Logs.txt 刷满。
            if (SEnvir.Now >= _nextStuckLogTime)
            {
                _nextStuckLogTime = SEnvir.Now.AddSeconds(30);

                BotLog.Info(Profile.Seq, string.Format(
                    "原地绕圈 {0:0.#} 秒（活动范围 {1}×{2} 格，位置 {3},{4}）｜状态={5} 地图={6} 刷怪点={7} 目标={8} 目标距离={9} 距上次攻击={10:0.#}s 可移动={11}（动作+{12:0.#}s 移动+{13:0.#}s 硬直+{14:0.#}s 中毒={15}）｜第 {16} 次脱困",
                    StuckWindowSeconds, maxX - minX, maxY - minY, here.X, here.Y,
                    State,
                    _player.CurrentMap?.Info?.PlayerDescription ?? "?",
                    _huntPoint == default ? "无" : string.Format("{0},{1}", _huntPoint.X, _huntPoint.Y),
                    _target == null ? "无" : MonsterName(_target),
                    _target == null ? -1 : BotNavigator.Distance(here, _target.CurrentLocation),
                    (SEnvir.Now - _lastAttackTime).TotalSeconds,
                    _player.CanMove,
                    (_player.ActionTime - SEnvir.Now).TotalSeconds,
                    (_player.MoveTime - SEnvir.Now).TotalSeconds,
                    (_player.ShockTime - SEnvir.Now).TotalSeconds,
                    _player.Poison,
                    _circleStrikes));
            }

            // ① 第一次：先试「避让活物」重算一步，绕开堵路的假人/宝宝。
            //
            // 只挪一步就完事是**不够**的 —— 若目标本身走不到（隔墙 / 隔河 / 在另一张图），
            // 挪一步之后下一帧 A* 照样指回原方向，8 秒后又绕一圈。所以后面还要升级。
            if (_circleStrikes == 1)
            {
                if (_huntPoint != default && BotNavigator.TryStepAvoidingLive(_player, _huntPoint)) return;
                if (_target != null && BotNavigator.TryStepAdjacent(_player, _target.CurrentLocation)) return;

                TryShuffleStep();
                return;
            }

            // ② 第二次：这个目标/刷怪点确实走不到 —— 弃掉，并且**拉黑**，
            // 否则 TryPickHuntPoint 会按「最近」把同一个点又选回来，形成死循环。
            if (_circleStrikes == 2)
            {
                if (_huntPoint != default)
                {
                    // D6：写进跨假人共享黑名单 —— 同图其它假人选点时也避开这个走不到的点。
                    int mapIndex = _player.CurrentMap?.Info?.Index ?? 0;
                    BotBadHuntPoints.Mark(mapIndex, _huntPoint);
                    _huntPoint = default;
                }

                _target = null;
                TryShuffleStep();

                EnterState(BotState.Idle, "绕圈脱困：弃掉走不到的刷怪点", TimeSpan.FromSeconds(2));
                return;
            }

            // ③ 第三次以上：本图这一片整体走不通（河岸 / 城墙死角 / 人堆），直接跨图。
            // 跨不过去（没钱 / 没石头）就烧一张回城卷瞬移脱身 ——
            // 这是**唯一不依赖走路**的逃生手段，专门对付「A* 根本算不出路」的死角。
            // 连卷都没有就退回待机，把等待时间拉长，别再空转。
            // 停在 3 而不是清零：清零的话日志会一直写「第 1 次脱困」，
            // 看不出「这具假人已经反复升级到最重一档了」。真正归零只在有进展时做。
            _circleStrikes = 3;

            _huntPoint = default;
            _target = null;
            TryShuffleStep();

            if (TryTravelToHuntMap()) return;

            if (TryEscapeByScroll()) return;

            EnterState(BotState.Idle, "绕圈脱困：本图走不通，歇一会儿", TimeSpan.FromSeconds(15));
        }

        /// <summary>
        /// 兜底脱困：烧一张回城卷瞬移回绑定点。
        /// <para>
        /// 走不动的时候（A* 从当前格算不出任何路、或者被围死）只有瞬移能自救。
        /// 和补给路径上的 <see cref="TryReturnHomeByScroll"/> 不同，这里**不检查「是不是已经在主城」** ——
        /// 卡在同一张图的死角里同样需要脱身。
        /// </para>
        /// </summary>
        private bool TryEscapeByScroll()
        {
            if (BotActions.CountTownScroll(_player) <= 0) return false;

            if (!BotActions.CanUseTownScrollHere(_player)) return false;

            // 引擎的 UseItemTime 是共用冷却，冷却没到发包会被静默拒绝。
            if (!BotActions.ItemUseCooldownReady(_player)) return false;

            if (!BotActions.TryUseTownScroll(_player)) return false;

            TownScrollUseCount++;

            if (_player.Character != null) _player.Character.BotTownScrollUses++;

            if (_player.AutoPath != null) AutoPathService.Instance.Cancel(_player);

            BotLog.Info(Profile.Seq, string.Format("绕圈脱困：烧回城卷瞬移（还剩 {0} 张）",
                BotActions.CountTownScroll(_player)));

            EnterState(BotState.Idle, "绕圈脱困：回城卷脱身", TimeSpan.FromSeconds(3));

            return true;
        }

        /// <summary>
        /// 是否正在「围着目标打」：Fight 态、目标活着同图、距离 ≤ 3 格。
        /// 这种位置不变是正常行为，不能判成绕圈。
        /// <para>
        /// 为什么不能用「距离 ≤ <c>Globals.MagicRange</c>」：实测 BOT#27/BOT#21 打鸡时
        /// 距离在 2~3 格来回（鸡自己会跑），8 秒内活动范围只有 2×2 格 ——
        /// **位置没扩大是因为目标在原地绕，不是因为路走不通**。
        /// 判据要问「有没有在往目标靠近」，而不是「离得够不够近」。
        /// </para>
        /// <para>
        /// 距离 &gt; 3 还缩在小范围里，才是真的「追不上 / 走不过去」，那时才计数。
        /// 另外「贴脸但隔着墙砍不到」这种，<see cref="ProcessFight"/> 末尾的
        /// 「这只真的够不到」分支会自己弃目标，不依赖这里。
        /// </para>
        /// </summary>
        private bool IsEngagedWithNearbyTarget()
        {
            if (State != BotState.Fight) return false;

            MonsterObject monster = _target;

            if (monster == null || monster.Dead || monster.CurrentMap != _player.CurrentMap) return false;

            return BotNavigator.Distance(_player.CurrentLocation, monster.CurrentLocation) <= 3;
        }

        /// <summary>
        /// 刷怪点是否在「近期走不到」的**跨假人共享**黑名单里（<see cref="TryPickHuntPoint"/> 选点时调用）。
        /// 标记见 <see cref="BotBadHuntPoints"/>：同图任何假人确认过的不可达点，本假人也不再选。
        /// </summary>
        private bool IsBadHuntPoint(Point point)
        {
            int mapIndex = _player.CurrentMap?.Info?.Index ?? 0;

            return BotBadHuntPoints.IsBad(mapIndex, point);
        }

        /// <summary>
        /// 刷新「赶路有进展」计时（D7）：进展 = 距刷怪点更近 / 到达 / 击杀 / 换点换图。
        /// 无进展判据的计时基准，超过 <see cref="NoProgressSeconds"/> 未刷新才判停滞。
        /// </summary>
        private void MarkHuntProgress()
        {
            _lastProgressTime = SEnvir.Now;
            _noProgressStrikes = 0;
        }

        /// <summary>
        /// 赶路「无进展」检测（D7，与 <see cref="TrackStuck"/> 互补）。
        /// <para>
        /// TrackStuck 用「8 秒包围盒 ≤ 4 格」只抓<b>小范围原地绕圈</b>；但障碍前的典型表现是
        /// <b>大范围反复横跳</b> —— 活动范围远超 4 格，包围盒判据永不触发，假人却始终没朝刷怪点靠近
        ///（用户 2026-11-13 观察：障碍前 10 分钟反复移动）。本判据抓的是「有没有在朝目标靠近」，
        /// 不关心活动范围大小，两者互补。
        /// </para>
        /// <para>
        /// 进展 = 距刷怪点更近（刷新最小距离）/ 到达 / 击杀 / 换点换图。
        /// 30 秒无任何进展 → 分级脱困（1 避让重算 → 2 换点 → 3 跨图），与绕圈脱困同构。
        /// </para>
        /// <para>
        /// 返回 <c>true</c> 表示本帧已被脱困动作接管（换点 / 跨图 / 转 Idle，目标点或状态已变），
        /// 调用方应提前 <c>return</c>，等下一帧按新目标重算；返回 <c>false</c> 表示未接管，
        /// 调用方继续正常流程。
        /// </para>
        /// </summary>
        private bool TrackNoProgress()
        {
            Point hunt = _huntPoint;

            // 赶路总时限（2026-11-17 用户决策）：出发超过 HuntTravelLimitSeconds，
            // 即使期间有微小进展（距刷怪点在缓慢接近）也强制换点/跨图脱困，
            // 封顶极端长途多跳赶路（5-3 观察：青灯客 500.1s 穿越石刻墓穴 1→4 层等）。
            // 必须在量距进展判据**之前**检查 —— 放在后面的话每帧更近都提前 return，
            // 总时限永不触发。
            if (_huntStartTime != default)
            {
                if (hunt == default)
                {
                    // 无目标点的过渡状态（换点/跨图中），无赶路可言，直接归零。
                    _huntStartTime = default;
                }
                else if (SEnvir.Now - _huntStartTime >= TimeSpan.FromSeconds(HuntTravelLimitSeconds))
                {
                    if (SEnvir.Now >= _nextNoProgressLogTime)
                    {
                        _nextNoProgressLogTime = SEnvir.Now.AddSeconds(30);

                        BotLog.Info(Profile.Seq, string.Format(
                            "赶路超时 {0:0.#} 秒（距刷怪点 {1} 格，最小 {2} 格，位置 {3},{4}）｜状态={5}，强制脱困",
                            (SEnvir.Now - _huntStartTime).TotalSeconds,
                            BotNavigator.Distance(_player.CurrentLocation, hunt),
                            _huntPointBestDistance,
                            _player.CurrentLocation.X, _player.CurrentLocation.Y,
                            State));
                    }

                    // 与无进展脱困同构：换点 → 跨图 → 兜底歇一会儿。
                    // 换点成功会重新记录出发时刻（选点落定处），总时限重新起算。
                    _huntStartTime = default;
                    _huntPoint = default;
                    _target = null;

                    if (TryPickHuntPoint()) return true;

                    if (TryTravelToHuntMap()) return true;

                    if (TryEscapeByScroll()) return true;

                    EnterState(BotState.Idle, "赶路超时，换地方", TimeSpan.FromSeconds(3));
                    return true;
                }
            }

            // 有刷怪点：量距，刷新最小距离即进展。
            if (hunt != default)
            {
                int dist = BotNavigator.Distance(_player.CurrentLocation, hunt);

                if (dist < _huntPointBestDistance)
                {
                    _huntPointBestDistance = dist;
                    MarkHuntProgress();
                    return false;
                }
            }
            else
            {
                // 没有目标点（换点/跨图过渡中）—— 没有可停滞的目标，重置计数。
                MarkHuntProgress();
                return false;
            }

            // 无进展超时检查。
            if (SEnvir.Now - _lastProgressTime < TimeSpan.FromSeconds(NoProgressSeconds))
                return false;

            _noProgressStrikes++;

            if (SEnvir.Now >= _nextNoProgressLogTime)
            {
                _nextNoProgressLogTime = SEnvir.Now.AddSeconds(30);

                BotLog.Info(Profile.Seq, string.Format(
                    "赶路无进展 {0:0.#} 秒（距刷怪点 {1} 格，最小 {2} 格，位置 {3},{4}）｜状态={5} 第 {6} 次脱困",
                    NoProgressSeconds,
                    BotNavigator.Distance(_player.CurrentLocation, hunt),
                    _huntPointBestDistance,
                    _player.CurrentLocation.X, _player.CurrentLocation.Y,
                    State,
                    _noProgressStrikes));
            }

            // ① 第一次：避让活物重算一步，绕开障碍/人群。仍在原刷怪点上，未接管本帧。
            if (_noProgressStrikes == 1)
            {
                if (hunt != default && BotNavigator.TryStepAvoidingLive(_player, hunt))
                {
                    TrackStuck();
                    return false;
                }

                TryShuffleStep();
                return false;
            }

            // ② 第二次：这个刷怪点确实到不了 —— 换点（重置目标点，本帧不再按旧点走）。
            if (_noProgressStrikes == 2)
            {
                _huntPoint = default;
                _target = null;

                if (TryPickHuntPoint()) return true;

                EnterState(BotState.Idle, "赶路无进展，换个地方", TimeSpan.FromSeconds(3));
                return true;
            }

            // ③ 第三次以上：本图这一片整体走不通，跨图（目标点与状态均已变更，接管本帧）。
            _noProgressStrikes = 3;

            _huntPoint = default;
            _target = null;
            TryShuffleStep();

            if (TryTravelToHuntMap()) return true;

            if (TryEscapeByScroll()) return true;

            EnterState(BotState.Idle, "赶路无进展，本图走不通，歇一会儿", TimeSpan.FromSeconds(15));
            return true;
        }

        #endregion

        private void EnterState(BotState state, string detail, TimeSpan duration)
        {
            if (State != state)
            {
                // 战斗结束接续被打断的补货待办（2026-11-17 用户决策，修 Fight/Restock 震荡）：
                // 补给回城途中被攻击切 Fight 还击，战斗结束后不管流转到哪个状态
                // （击杀/脱离/超时/换点），都先回到 Restock 把补货做完（战利品稍后再捡），
                // 避免「回城 ↔ 还击」反复横跳。接续后重置遇袭位置判据，重给补货时限。
                if ((_interruptedState == BotState.Restock || _interruptedState == BotState.Trade)
                    && State == BotState.Fight)
                {
                    BotState resume = _interruptedState;

                    _interruptedState = default;
                    _restockFleeCheckPos = _player?.CurrentLocation ?? default;
                    _restockFleeStuckCount = 0;

                    // 冷却窗口（2026-11-17 修野外被围震荡）：接续后 20 秒内不因威胁立即切 Fight，
                    // 给假人时间逃离/回城；被围死（位置持续 10 秒没动）才允许还击，见威胁检测。
                    _restockResumeUntil = SEnvir.Now.AddSeconds(RestockResumeCooldownSeconds);
                    _restockStuckSince = SEnvir.Now;

                    state = resume;
                    detail = resume == BotState.Trade ? "战斗结束，继续去卖货" : "战斗结束，继续回城补给";
                    duration = TimeSpan.FromMinutes(5);
                }

                BotLog.Debug(Profile.Seq, string.Format("{0} → {1}：{2}", State, state, detail));

                // 换了目标状态，上一段的轨迹和避让节流都作废 —— 否则新目标一开始
                // 就会被旧轨迹判成「还在绕圈」。
                _trail.Clear();
                _nextAvoidStepTime = default;
                _nextPoisonCheckTime = default;
                _nextSummonCheckTime = default;   // 3-4 召唤：换目标立即可补召，不等节流
                _nextKiteTime = default;
                _kiteStartTime = default;
            }

            State = state;
            LastDetail = detail;
            StateSince = SEnvir.Now;
            _stateDeadline = SEnvir.Now + duration;

            // 每轮补给重新记「怎么回城的」（2026-11-18）。
            if (state == BotState.Restock) _restockTravelLogged = false;

            // 背包快照钩子（2026-11-18 用户要求）：卖完货 / 补完给 → 去刷怪点这一跳，
            // 打一行背包内容，用来判断货是不是卖干净了（只该剩药和卷）。
            if (state == BotState.GoHunt && _logBagOnNextHunt)
            {
                _logBagOnNextHunt = false;
                BotLog.Info(Profile.Seq, "补给后背包：" + DescribeBag());
            }
        }

        private static string MonsterName(MonsterObject monster)
        {
            // MonsterObject.Name 恒为空，真正的名字在 MonsterInfo 上（D1 实测）。
            return monster?.MonsterInfo?.MonsterName ?? "怪物";
        }

        /// <summary>诊断用：把半径内的怪按「被哪条规则过滤掉」分类统计。</summary>
        private string DescribeNearbyMonsters(int radius)
        {
            Map map = _player?.CurrentMap;

            if (map == null) return "地图为空";

            int total = 0, guards = 0, tooHigh = 0, noInfo = 0, valid = 0;

            foreach (MapObject ob in map.Objects)
            {
                if (!(ob is MonsterObject monster)) continue;
                if (monster.Dead || monster.Node == null) continue;

                if (BotNavigator.Distance(_player.CurrentLocation, monster.CurrentLocation) > radius) continue;

                total++;

                if (monster is Guard)
                {
                    guards++;
                    continue;
                }

                MonsterInfo info = monster.MonsterInfo;

                if (info == null)
                {
                    noInfo++;
                    continue;
                }

                if (info.Level > _player.Level + 4)
                {
                    tooHigh++;
                    continue;
                }

                valid++;
            }

            return string.Format("半径 {0} 内 共 {1}（守卫 {2} / 无信息 {3} / 等级过高 {4} / 可打 {5}）",
                radius, total, guards, noInfo, tooHigh, valid);
        }

        /// <summary>当前地图上活着的怪总数（诊断用：判断是「没刷怪」还是「刷了但够不到」）。</summary>
        private int CountMapMonsters()
        {
            Map map = _player?.CurrentMap;

            if (map == null) return 0;

            int count = 0;

            foreach (MapObject ob in map.Objects)
            {
                if (!(ob is MonsterObject monster)) continue;
                if (monster.Dead || monster.Node == null) continue;

                count++;
            }

            return count;
        }

        #region 拟人化（P2）

        /// <summary>
        /// 数走了几步（P2-2）。
        /// <para>
        /// 用「坐标变了」来判断，而不是在 10 个走格调用点上各加一行 —— 走格入口有
        /// <c>TryStepTowards</c> / <c>TryStepTowardsNear</c> / 传送 / 自动寻路好几个，
        /// 逐个埋点必然漏。坐标对比只有一个地方，且天然不会重复计数。
        /// </para>
        /// </summary>
        private void TrackSteps()
        {
            Point here = _player.CurrentLocation;

            if (here != _lastPosition)
            {
                _lastPosition = here;

                if (Options.WalkPauseEnabled)
                {
                    _stepsSincePause++;

                    if (_stepsSincePause >= _nextPauseAt)
                    {
                        _stepsSincePause = 0;
                        _nextPauseAt = SEnvir.Random.Next(5, 16);

                        _walkPauseUntil = SEnvir.Now.AddMilliseconds(SEnvir.Random.Next(400, 1201));
                        _pauseHealthPercent = BotPerception.HealthPercent(_player);
                    }
                }
            }

            // 换图流动（P2-4）：发现换了图就重新计时，随机 25~45 分钟后再考虑走。
            MapInfo info = _player.CurrentMap?.Info;

            if (info != null && info != _lastMapInfo)
            {
                _lastMapInfo = info;
                _mapEnteredAt = SEnvir.Now;
                _mapRotateAfter = TimeSpan.FromMinutes(SEnvir.Random.Next(25, 46));
            }
        }

        /// <summary>
        /// 在同一张图待太久了，换一张（P2-4）。
        /// <para>
        /// 只负责「提出换图请求」，真正的选图 / 防折返 / 冷却全在
        /// <see cref="TryTravelToHuntMap"/> 里，不重复实现一遍。
        /// 走不了就把计时重置 —— 否则会每帧都试一次，日志刷屏还白烧 CPU。
        /// </para>
        /// </summary>
        private void MaybeRotateMap()
        {
            if (!Options.MapRotateEnabled) return;
            if (_mapEnteredAt == DateTime.MinValue) return;
            if (SEnvir.Now - _mapEnteredAt < _mapRotateAfter) return;

            // 安全区里不换图（刚回城办事，让它先办完）。
            if (BotPerception.InSafeZone(_player)) return;

            _mapEnteredAt = SEnvir.Now;
            _mapRotateAfter = TimeSpan.FromMinutes(SEnvir.Random.Next(25, 46));

            if (TryTravelToHuntMap())
                BotLog.Debug(Profile.Seq, "在这张图待久了，换个地方");
        }

        /// <summary>
        /// 这一帧要不要「站着不动」（P2-2）。
        /// <para>
        /// 停顿必须能被打断：打架、掉血、目标贴脸时立刻恢复。否则会出现
        /// 「怪在脸上砍它，它站着不动」—— 比匀速巡逻更像机器人。
        /// </para>
        /// </summary>
        private bool ShouldHoldForPause()
        {
            if (_walkPauseUntil == DateTime.MinValue) return false;

            if (SEnvir.Now >= _walkPauseUntil)
            {
                _walkPauseUntil = DateTime.MinValue;
                return false;
            }

            // 打架 / 死亡时不停顿：站着挨打比「像人」重要得多。
            if (State == BotState.Fight || State == BotState.Dead || _player.Dead)
            {
                _walkPauseUntil = DateTime.MinValue;
                return false;
            }

            // 被打断：血掉得快。
            if (BotPerception.HealthPercent(_player) <= _pauseHealthPercent - 15)
            {
                _walkPauseUntil = DateTime.MinValue;
                return false;
            }

            // 被打断：目标已经贴到 3 格以内。
            if (_target != null && !_target.Dead && _target.Node != null
                && BotNavigator.Distance(_player.CurrentLocation, _target.CurrentLocation) <= 3)
            {
                _walkPauseUntil = DateTime.MinValue;
                return false;
            }

            return true;
        }

        /// <summary>
        /// 极小概率走岔一步（P2-5）。
        /// <para>
        /// <b>按时间排期，不按「每次决策掷骰子」</b>。2026-11-12 压测踩到：
        /// 原来写的是「每次决策 0.5%」，而假人每 200ms 决策一次 ——
        /// 24 个假人在登录后 2.5 分钟里就把「每小时 2 次」的配额全部用光（实测 17 次），
        /// 之后一小时一个都不犯。观感上是「刚上线一群人手忙脚乱，然后集体变成完美脚本」。
        /// </para>
        /// <para>
        /// 现在是「隔 18~42 分钟走岔一步」（由 <see cref="BotOptions.MistakePerHour"/> 推导），
        /// 天然满足每小时上限，也不会扎堆。
        /// </para>
        /// </summary>
        private bool TryMistake()
        {
            if (!Options.MistakeEnabled) return false;

            if (_nextMistakeTime == DateTime.MinValue)
            {
                ScheduleNextMistake();
                return false;
            }

            if (SEnvir.Now < _nextMistakeTime) return false;

            // 状态不合适就一分钟后再试（不重置整段间隔，否则会永远排不上）。
            if (State != BotState.GoHunt && State != BotState.Travel && State != BotState.Loot && State != BotState.Harvest
                || _player.Dead || _player.CurrentMap == null
                || BotPerception.HealthPercent(_player) < 30
                || BotPerception.InSafeZone(_player))
            {
                _nextMistakeTime = SEnvir.Now.AddMinutes(1);
                return false;
            }

            ScheduleNextMistake();

            _player.Move((MirDirection)SEnvir.Random.Next(8), 1);

            BotLog.Debug(Profile.Seq, "走岔了一步（拟人化误操作）");

            return true;
        }

        /// <summary>按「每小时 N 次」推导下一次误操作时间，带 ±40% 抖动（避免 30 个假人同时犯傻）。</summary>
        private void ScheduleNextMistake()
        {
            int perHour = Math.Max(1, Options.MistakePerHour);

            double minutes = 60.0 / perHour;

            // 0.6 ~ 1.4 倍：perHour=2 → 18~42 分钟一次。
            double factor = 0.6 + SEnvir.Random.NextDouble() * 0.8;

            _nextMistakeTime = SEnvir.Now.AddMinutes(minutes * factor);
        }

        /// <summary>
        /// 补给 / 卖货办完之后的选择（P2-3）。
        /// <para>
        /// 按概率在城里再待 1~3 分钟：目的不是效率，是「城里有人」。
        /// 一队假人办完事立刻消失，比根本不来还假。
        /// </para>
        /// </summary>
        private void LeaveTownOrStroll(string detail)
        {
            if (!Options.TownStrollEnabled || SEnvir.Random.Next(100) >= Options.TownStrollPercent)
            {
                EnterState(BotState.Idle, detail, TimeSpan.FromSeconds(3));
                return;
            }

            _strollUntil = SEnvir.Now.AddSeconds(SEnvir.Random.Next(60, 181));
            _nextStrollStep = DateTime.MinValue;

            // 期限给 4 秒而不是整段闲逛时间：靠 _strollUntil 控制结束，
            // 这样 ProcessIdle 会每 4 秒进来一次，才能真的「走两步」而不是站着。
            EnterState(BotState.Idle, detail + "，在城里转转", TimeSpan.FromSeconds(4));
        }

        /// <summary>城里闲逛：在安全区内随机挪步，绝不往城外跑（出了安全区就是靶子）。</summary>
        private void ProcessTownStroll()
        {
            if (SEnvir.Now < _nextStrollStep) return;

            _nextStrollStep = SEnvir.Now.AddSeconds(SEnvir.Random.Next(4, 12));

            if (!BotPerception.InSafeZone(_player))
            {
                // 晃出安全区了（被怪推 / 走偏）→ 立刻结束闲逛回城流程。
                _strollUntil = DateTime.MinValue;
                EnterState(BotState.Idle, "走出安全区了，不逛了", TimeSpan.FromSeconds(1));
                return;
            }

            Point here = _player.CurrentLocation;

            Point target = new Point(here.X + SEnvir.Random.Next(-6, 7), here.Y + SEnvir.Random.Next(-6, 7));

            BotNavigator.TryStepTowards(_player, target);
        }

        /// <summary>
        /// 榜首降速（P4-4）：假人连续 <see cref="BotOptions.TopHoldMinutes"/> 分钟待在排行榜前
        /// <see cref="BotOptions.TopRankLimit"/> 名，就暂停练级 <see cref="BotOptions.TopSlowMinutes"/> 分钟。
        /// <para>
        /// 为什么不做「等级封顶」：封顶会让等级停在某个整数上一动不动，比占榜还假。
        /// 降速是「先跑一阵、再歇两小时」，看起来就像玩家自己下线/挂机去了。
        /// </para>
        /// <para>
        /// 返回 true = 本帧由降速逻辑接管，状态机不要再跑。
        /// </para>
        /// </summary>
        private bool ProcessTopSlowdown()
        {
            if (!Options.TopSlowEnabled) return false;
            if (_player?.Character == null) return false;

            // ⓪ 等级门（2026-11-13）：低于 BotTopSlowMinLevel 的人不参与降速，连计时都不开始。
            //
            // 为什么必须加：降速原来只看「在不在前 N 名」。真人普遍还没练上来时，
            // 30 个假人会把前 3 名长期占死，然后集体被降速 —— 假人既不长个也不干活，
            // 白占在线水位。75 级以下让它们正常练，75 之后才开始「跑一阵、歇两小时」。
            //
            // 已经进入降速期又掉到门槛以下（比如刚转生回 1 级）：走下面 ① 的结束分支取消，
            // 所以这里只挡「新开始计时」，不挡「结束」。
            if (_player.Level < Options.TopSlowMinLevel && _slowUntil == DateTime.MinValue)
            {
                if (_topSince != DateTime.MinValue)
                {
                    _topSince = DateTime.MinValue;

                    BotLog.Info(Profile.Seq, string.Format("等级 {0} 低于降速门槛 {1}，取消占榜计时",
                        _player.Level, Options.TopSlowMinLevel));
                }

                return false;
            }

            // ① 降速期内：不练级、不换图，只在城里待着。
            if (_slowUntil != DateTime.MinValue)
            {
                if (SEnvir.Now < _slowUntil)
                {
                    ProcessSlowIdle();
                    return true;
                }

                _slowUntil = DateTime.MinValue;
                _topSince = DateTime.MinValue;

                BotLog.Info(Profile.Seq, "榜首降速结束，恢复练级");

                EnterState(BotState.Idle, "降速结束，准备继续练级", TimeSpan.FromSeconds(3));
                return false;
            }

            // ② 不在前 N 名：计时清零。
            if (!BotPerception.IsTopRanked(_player.Character, Options.TopRankLimit))
            {
                if (_topSince != DateTime.MinValue)
                {
                    _topSince = DateTime.MinValue;
                    BotLog.Info(Profile.Seq, string.Format("已掉出排行榜前 {0}，取消降速计时", Options.TopRankLimit));
                }

                return false;
            }

            // ③ 刚进前 N：开始计时。
            if (_topSince == DateTime.MinValue)
            {
                _topSince = SEnvir.Now;

                BotLog.Info(Profile.Seq, string.Format("进入排行榜前 {0}（{1} 级），开始计时",
                    Options.TopRankLimit, _player.Level));

                return false;
            }

            // ④ 连续占榜够久 → 开始降速。
            if (SEnvir.Now - _topSince < TimeSpan.FromMinutes(Options.TopHoldMinutes)) return false;

            _slowUntil = SEnvir.Now.AddMinutes(Options.TopSlowMinutes);
            _topSince = DateTime.MinValue;

            BotLog.Info(Profile.Seq, string.Format(
                "连续 {0} 分钟占据排行榜前 {1}，暂停练级 {2} 分钟（把名次让给真人）",
                Options.TopHoldMinutes, Options.TopRankLimit, Options.TopSlowMinutes));

            EnterState(BotState.Idle, "榜首降速中", TimeSpan.FromSeconds(10));

            return true;
        }

        /// <summary>
        /// 降速期间干什么：回城、在安全区待着。
        /// <para>
        /// 不能在野外干站着 —— 那是活靶子，两小时下来会死成一条流水线，比占榜还难看。
        /// 所以先想办法回主城（有卷用卷，没卷就走），到了城里就正常发呆/闲逛。
        /// </para>
        /// </summary>
        private void ProcessSlowIdle()
        {
            MapInfo home = _player.Character?.BindPoint?.BindRegion?.Map;

            if (BotPerception.InSafeZone(_player))
            {
                // 已经在安全区：站着不动（细节见 ProcessSlowRest 的注释 —— 走动会晃出安全区）。
                LogSlowBranch("安全区待着");

                ProcessSlowRest();
                return;
            }

            if (home == null)
            {
                // 没有绑定点（理论上不会发生）：至少别站着挨打，靠生存逻辑喝药。
                LogSlowBranch("没有绑定主城");

                if (SEnvir.Now >= _stateDeadline)
                    EnterState(BotState.Idle, "榜首降速中（没有绑定主城）", TimeSpan.FromSeconds(15));

                return;
            }

            // ① 在别的图：先试回城卷（瞬移，最省事），再退回 AutoPath 跨图走回去。
            //    这段与 ProcessRestock 的回城分支完全同构 —— 跨图 AutoPath 是引擎支持的，
            //    假人走这条路已经跑了几百次补给。
            if (_player.CurrentMap?.Info != home)
            {
                if (TryReturnHomeByScroll(home))
                {
                    LogSlowBranch("用回城卷回 " + home.Description);

                    LastDetail = string.Format("榜首降速，用回城卷回 {0}", home.Description);
                    return;
                }

                if (_player.AutoPath != null)
                {
                    LogSlowBranch("AutoPath 走回 " + home.Description);

                    LastDetail = string.Format("榜首降速，正在走回 {0}", home.Description);
                    return;
                }

                if (AutoPathService.Instance.TryStart(_player, home, HomePoint(home)))
                {
                    LogSlowBranch("AutoPath 走回 " + home.Description);

                    LastDetail = string.Format("榜首降速，走回 {0}", home.Description);
                    return;
                }

                // 路线规划失败（等级/道具限制）：原地喝药撑着，稍后重试。
                LogSlowBranch("回城路线规划失败");

                if (SEnvir.Now >= _stateDeadline)
                    EnterState(BotState.Idle, "榜首降速中（回城路线规划失败）", TimeSpan.FromSeconds(15));

                return;
            }

            // ② 已经在本图，但人不在安全区（刷怪点边上）：自己一步步走回绑定点。
            //    这条分支不能交给 AutoPathService —— 它只做跨图路线，同图目标点
            //    TryStart 会直接失败，假人就会一声不响地站在原地。
            //    实测踩过：BOT#05 在诺玛村落刷怪点触发降速，回城卷分支因为
            //    「当前图 == 绑定图」被跳过，结果在怪物堆里站满 3 分钟（那次刷的是 1 级羊才没事）。
            LogSlowBranch("同图走回 " + home.Description + " 安全区");

            if (SEnvir.Now >= _nextSlowStep)
            {
                _nextSlowStep = SEnvir.Now.AddMilliseconds(SEnvir.Random.Next(400, 900));

                Point target = HomePoint(home);

                BotNavigator.TryStepTowards(_player, target);

                LastDetail = string.Format("榜首降速，走回 {0} 安全区", home.Description);
            }
        }

        /// <summary>
        /// 降速期在安全区里做什么：<b>什么都不做，站着</b>。
        /// <para>
        /// 试过两种「看起来更活」的做法，都被实测否掉：
        /// ① 复用 <c>ProcessTownStroll</c>（±6 格随机点）—— 会晃出安全区，被守卫踢回走回分支，30~100 秒拉锯一次；
        /// ② 朝绑定点 <c>HomePoint</c> 小步走 —— <c>BindRegion.PointList</c> 是**出生区域**，
        ///    不等于 <c>Cell.SafeZone</c>，走到区域边缘那几格就已经不是安全区了，照样拉锯（实测 9 次/3 分钟）。
        /// </para>
        /// <para>
        /// 降速本来就是要「安静下来把名次让给真人」，真人挂机也是站着不动 —— 站着最像，也最安全。
        /// </para>
        /// </summary>
        private void ProcessSlowRest()
        {
            if (SEnvir.Now >= _stateDeadline)
                EnterState(BotState.Idle, "榜首降速中（在安全区待着）", TimeSpan.FromSeconds(10));
        }

        /// <summary>降速期分支变化时打一条 Debug 日志（每帧都打会刷屏，所以只在变化时打）。</summary>
        private void LogSlowBranch(string branch)
        {
            if (_slowBranch == branch) return;

            _slowBranch = branch;

            BotLog.Debug(Profile.Seq, "降速期：" + branch);
        }


        /// <summary>
        /// 红名 PK（P3-4）。
        /// <para>
        /// 决策：<b>红名就打，含真人</b>。所以这条路径是唯一会主动攻击真人的地方，
        /// 守卫写得很死：只打红名（<c>Stats[Stat.PKPoint] &gt;= Config.RedPoint</c>）、
        /// 只在自己血 &gt; 50%、只在非安全区、每次间隔 ≥ <see cref="BotOptions.PkIntervalMinutes"/> 分钟。
        /// </para>
        /// <para>
        /// 而且<b>只吓唬不击杀</b>：目标血低于 30% 就收手。理由很实际 ——
        /// 真打死了要爆装备、要结仇，两个假人互爆装备还会破坏养成感。
        /// </para>
        /// </summary>
        private bool TryPk()
        {
            if (!Options.PkEnabled) return false;
            if (SEnvir.Now < _nextPkTime) return false;
            if (_player.Dead || _player.CurrentMap == null) return false;

            // 打怪 / 传送 / 转生这些状态不掺 PK：正在做的事优先。
            if (State == BotState.Fight || State == BotState.Teleport || State == BotState.Rebirth) return false;

            if (BotPerception.HealthPercent(_player) < 50) return false;
            if (BotPerception.InSafeZone(_player)) return false;

            PlayerObject target = BotPerception.FindRedNameTarget(_player, Options.PkRadius, Config.RedPoint);

            if (target == null) return false;

            // 找到红名就排下一次（不论这一刀砍不砍得到），否则会每帧扫全图玩家。
            _nextPkTime = SEnvir.Now.AddMinutes(Math.Max(1, Options.PkIntervalMinutes));

            // 只吓唬：血已经很低了就别再补刀。
            if (BotPerception.HealthPercent(target) <= 30) return false;

            if (BotNavigator.Distance(_player.CurrentLocation, target.CurrentLocation) > 1)
            {
                BotNavigator.TryStepTowards(_player, target.CurrentLocation);
                return true;
            }

            if (!BotActions.TryAttackPlayer(_player, target)) return false;

            _pkCount++;

            BotLog.Info(Profile.Seq, string.Format("红名 PK：{0}（对方血量 {1}%）",
                target.Name, BotPerception.HealthPercent(target)));

            return true;
        }

        /// <summary>
        /// 说话（P2-1）。
        /// <para>
        /// 频道不自己拼包：普通频道直接 <c>Chat(text)</c>，世界频道拼 <c>!@</c> 前缀交给引擎，
        /// 由引擎做等级（33 级）、账号级 30 秒冷却、屏蔽名单的全部校验 ——
        /// 自己发包绕过校验，迟早会出现「30 级假人上世界频道」这种一眼假。
        /// </para>
        /// <para>
        /// 排期是「说之前先排下一次」：语料为空、选句失败都不会退化成每帧重试。
        /// </para>
        /// </summary>
        private void ProcessChat()
        {
            if (!Options.ChatEnabled)
            {
                // 关掉开关就把排期清空：重新打开后重新随机等 15~40 分钟，
                // 而不是「一开就立刻集体开口」。
                _nextChatTime = DateTime.MinValue;
                return;
            }

            if (_nextChatTime == DateTime.MinValue)
            {
                _nextChatTime = SEnvir.Now.Add(Options.NextChatDelay());
                return;
            }

            if (SEnvir.Now < _nextChatTime) return;

            _nextChatTime = SEnvir.Now.Add(Options.NextChatDelay());

            // 死亡 / 传送读条 / 转生流程里角色是"冻结"的，这时候开口很假。
            if (_player.Dead || State == BotState.Teleport || State == BotState.Rebirth || State == BotState.Dead) return;

            if (!BotChat.TryPick(_player, ChatKindForState(), out string text, out bool world)) return;

            _player.Chat(world ? "!@" + text : text);

            // 发言计数（P4-5 性能卡片）。只在真的发出去之后 +1 ——
            // TryPick 只是「挑一句」，挑完因为别的原因没发出去的不该算。
            BotMetrics.Chat();
        }

        /// <summary>按当前状态挑说话场景（语料里没有这个场景时 BotChat 会自己放宽到任意类别）。</summary>
        private BotChatKind ChatKindForState()
        {
            switch (State)
            {
                case BotState.Trade:
                case BotState.Restock:
                    return BotChatKind.Trade;
                case BotState.Dead:
                    return BotChatKind.Death;
                case BotState.Idle:
                    return SEnvir.Random.Next(2) == 0 ? BotChatKind.Greeting : BotChatKind.Feeling;
                default:
                    // 打怪途中偶尔"惊讶"（掉了东西、血掉太快），其余是感慨。
                    return SEnvir.Random.Next(4) == 0 ? BotChatKind.Surprise : BotChatKind.Feeling;
            }
        }

        #endregion

        /// <summary>后台展示用的一行快照。</summary>
        public string Snapshot()
        {
            if (_player == null) return State.ToString();

            return string.Format("状态 {0}　等级 {1}　{2}（{3},{4}）　HP {5}%　击杀 {6}",
                State,
                _player.Level,
                _player.CurrentMap?.Info?.Description ?? "-",
                _player.CurrentLocation.X,
                _player.CurrentLocation.Y,
                BotPerception.HealthPercent(_player),
                Kills);
        }
    }
}
