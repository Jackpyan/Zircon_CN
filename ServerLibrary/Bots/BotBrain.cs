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
        private const int AttackRange = 1;
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
        /// </summary>
        private const int AutoPathToNpcDistance = 20;
        /// <summary>同一张图上最多同时有几个假人在练级，超了就分流（后台可调）。</summary>
        private int MapCapacity => Math.Max(1, Options.MapCapacity);
        /// <summary>本图连续几次找不到怪才考虑跨图（避免刷怪间隙就乱跑）。</summary>
        private const int EmptyHuntsBeforeTravel = 3;

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

        public decimal ExperienceStart { get; private set; }

        private DateTime _nextActionTime;
        private DateTime _stateDeadline;
        private DateTime _restockCooldown;

        /// <summary>连续补给失败次数。用来做退避：失败越多，下次重试间隔越长。</summary>
        private int _restockFailStreak;
        private DateTime _nextOptionsRefresh;
        private BotOptions _options;
        private Point _huntPoint;
        private int _stuckCount;
        private int _blockedCount;

        /// <summary>
        /// 连续捡不到东西的次数（P1）。
        /// 背包满 / 负重超 / 被别人抢先，都会导致引擎拒绝拾取；
        /// 连着失败 3 次就别再死磕这一件，换目标或回城卖货。
        /// </summary>
        private int _lootFailCount;
        private Point _lastLocation;
        private MonsterObject _target;

        private MapInfo _travelMap;
        private Point _travelPoint;
        private int _travelFailures;
        private DateTime _travelDebugTime;
        private int _emptyHunts;

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
        /// 回城中转的退避冷却。
        /// 没有它会出现「回城 → 主城石头传不了 → 跑远 → 又回城」的烧卷空转。
        /// </summary>
        private DateTime _returnHomeCooldown;

        /// <summary>累计回城中转次数（D5 量化：长途跨图有没有真的走上传送而不是腿儿着）。</summary>
        public int ReturnHomeForTeleportCount { get; private set; }

        /// <summary>上次同步技能时的等级。升级时补技能（法师 7 级火球、道士 13 级火符）。</summary>
        private int _syncedLevel = -1;
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

        /// <summary>回城交易的冷却：卖完 / 修完别马上又跑一趟。</summary>
        private DateTime _tradeCooldown;

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

        /// <summary>补给流程的子阶段。</summary>
        private enum RestockStage
        {
            /// <summary>去药店买药。</summary>
            Potion = 0,
            /// <summary>去杂货商买回城卷（下次断药就能秒回城）。</summary>
            TownScroll = 1,
        }

        /// <summary>当前要挖的尸体。</summary>
        private MonsterObject _harvestTarget;

        /// <summary>下次挖取的最早时间（引擎的 Harvest 有冷却动作）。</summary>
        private DateTime _nextHarvestTime;

        public BotBrain(PlayerObject player, BotProfile profile)
        {
            _player = player;
            Profile = profile;
            StateSince = SEnvir.Now;
            ExperienceStart = player?.Experience ?? 0;
            _lastLocation = player?.CurrentLocation ?? Point.Empty;

            EnterState(BotState.Idle, "刚上线，先站一会儿", TimeSpan.FromSeconds(SEnvir.Random.Next(2, 6)));
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

            // 路上看到怪就先打，不必走到点上（1 级号 52 血，闷头走容易被围死）。
            MonsterObject monster = BotPerception.FindHuntTarget(_player, HuntRadius);

            if (monster != null)
            {
                EnterState(BotState.Fight, string.Format("路上遇到 {0}", MonsterName(monster)), TimeSpan.FromSeconds(60));
                _target = monster;
                return;
            }

            BotMoveResult result = BotNavigator.TryStepTowards(_player, _huntPoint);

            switch (result)
            {
                case BotMoveResult.Moved:
                    TrackStuck();
                    return;

                case BotMoveResult.Arrived:
                    _target = null;
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
            MonsterObject monster = _target;

            // 上一刀把它砍死了：记一次击杀，再去找下一只。
            if (monster != null && monster.Dead)
            {
                Kills++;

                // 累计击杀落库（2026-11-11）：进程内的 Kills 重启就归零，
                // 落库后后台「假人档案」才能显示跨重启的历史战绩。
                if (_player?.Character != null)
                    _player.Character.BotKills++;

                BotLog.Debug(Profile.Seq, string.Format("击杀 {0}（累计 {1}），经验 {2}/{3}，等级 {4}",
                    MonsterName(monster), Kills, _player.Experience, _player.MaxExperience, _player.Level));

                // P1：击杀后**顺手捡战利品**（真人打完怪就会捡，不会等到周围没怪）。
                // 原来只在「附近没怪了」才去捡 —— 怪刷得勤时假人永远不会进 Loot 态，
                // 于是背包一直不涨、卖货与换装两条链路全是死的。
                if (BotPerception.FindNearestLoot(_player, LootRadius) != null)
                {
                    _target = null;

                    EnterState(BotState.Loot, "打完了，捡战利品", TimeSpan.FromSeconds(30));

                    return;
                }

                // P1：鸡 / 猪 / 牛的掉落不落地，留在尸体里 —— 必须走过去挖。
                // 没有这一步，假人的背包永远是空的（实测 27 次击杀 0 拾取）。
                MonsterObject corpse = BotPerception.FindHarvestableCorpse(_player, LootRadius);

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

            if (monster == null || monster.Node == null)
            {
                monster = BotPerception.FindHuntTarget(_player, HuntRadius);
                _target = monster;
            }

            if (monster == null)
            {
                // 附近没怪了，先看有没有东西可捡，再原地等一会儿 —— 刷怪有间隔，
                // 立刻换点会导致「到达 → 没怪 → 再到达」的来回空转。
                if (BotPerception.FindNearestLoot(_player, LootRadius) != null)
                {
                    EnterState(BotState.Loot, "附近没怪，先捡东西", TimeSpan.FromSeconds(30));
                    return;
                }

                // P1：附近还有可挖的尸体（鸡 / 猪 / 牛的掉落不落地）→ 去挖。
                MonsterObject corpse = BotPerception.FindHarvestableCorpse(_player, LootRadius);

                if (corpse != null)
                {
                    _harvestTarget = corpse;
                    _nextHarvestTime = DateTime.MinValue;

                    EnterState(BotState.Harvest, "附近没怪，去挖尸体", TimeSpan.FromSeconds(30));
                    return;
                }

                // 连续几次都空手，说明这张图确实没货（或被别人清光了）→ 换个图。
                _emptyHunts++;

                if (_emptyHunts >= EmptyHuntsBeforeTravel && TryTravelToHuntMap()) return;

                EnterState(BotState.Idle, "附近没怪，原地等刷新", TimeSpan.FromSeconds(SEnvir.Random.Next(8, 16)));
                BotLog.Debug(Profile.Seq, "找怪失败：" + DescribeNearbyMonsters(HuntRadius));
                return;
            }

            int distance = BotNavigator.Distance(_player.CurrentLocation, monster.CurrentLocation);

            // 法师/道士先用魔法：射程 10 格，不用贴脸挨打。
            // 技能不可用（蓝不够/冷却中）时**原地等**，别往前凑 —— 法师近战等于送死。
            if (_player.Class != MirClass.Warrior && distance <= Globals.MagicRange && distance > AttackRange)
            {
                MagicType attack = BotSkills.PickAttack(_player);

                if (attack != MagicType.None)
                {
                    if (!_player.CanCast)
                    {
                        LastDetail = string.Format("等 {0} 冷却（蓝 {1}/{2}）", attack, _player.CurrentMP, _player.Stats[Stat.Mana]);
                        return;
                    }

                    if (BotSkills.TryCastAttack(_player, monster))
                    {
                        LastDetail = string.Format("{0} 打 {1}（距离 {2}，蓝 {3}/{4}）",
                            attack, MonsterName(monster), distance, _player.CurrentMP, _player.Stats[Stat.Mana]);

                        BotLog.Debug(Profile.Seq, LastDetail);

                        return;
                    }
                }
            }

            // 近身（或没蓝/没技能）：贴脸砍。
            if (distance <= AttackRange)
            {
                if (BotActions.TryAttack(_player, monster))
                    LastDetail = string.Format("砍 {0}（HP {1}/{2}）", MonsterName(monster), monster.CurrentHP, monster.Stats[Stat.Health]);

                return;
            }

            BotMoveResult result = BotNavigator.TryStepTowards(_player, monster.CurrentLocation);

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

        private void ProcessLoot()
        {
            // P1 修的硬伤：原���这个状态没有超时出口。
            // 背包满时引擎捡不动，假人却以为捡到了（TryPickUp 原来无条件返回 true），
            // 于是每一拍都重新找到同一个东西再捡一次 → 永久卡在拾取态、再也不练级。
            // 现在有三道保险：状态期限、背包容量检查、拾取结果的真实判定。
            if (SEnvir.Now < _stateDeadline) return;

            // 背包满了就先别捡了 —— 去回城卖货腾格子（P1-2）。
            // 货币不进背包、直接进角色货币表，所以背包满时也允许继续捡货币。
            if (BotPerception.FreeInventorySlots(_player) <= 0)
            {
                EnterState(BotState.Idle, "背包满了，回城卖东西腾格子", TimeSpan.FromSeconds(3));
                return;
            }

            ItemObject item = BotPerception.FindNearestLoot(_player, LootRadius);

            if (item == null)
            {
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
                EnterState(BotState.Idle, "掉落物够不到", TimeSpan.FromSeconds(2));
        }

        private void ProcessDead()
        {
            if (State != BotState.Dead)
            {
                Deaths++;
                EnterState(BotState.Dead, "死了，准备回城复活", TimeSpan.FromSeconds(2));
                BotLog.Warn(Profile.Seq, string.Format("死亡（累计 {0} 次），2 秒后回城复活", Deaths));
                return;
            }

            if (SEnvir.Now < _stateDeadline) return;

            // 死亡惩罚不豁免（已决策）：经验/物品按引擎规则照掉，这里只负责复活。
            if (BotActions.TryTownRevive(_player))
                BotLog.Info(Profile.Seq, "已回城复活");

            // 复活即回主城，中转的意义已经由「人回到主城」实现了 —— 清位，重新选图。
            _teleportViaHome = false;

            EnterState(BotState.Idle, "复活后待机", TimeSpan.FromSeconds(SEnvir.Random.Next(3, 8)));
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

            // ① 本图没有六合圣石 → 先回主城中转。
            //
            // 为什么必须放在挑目标图之前：假人一旦离开主城，就再也传不了 ——
            // 「南方通道 / 沙漠土城 / 诺玛村落」这些中途图**根本没有六合石**，
            // 于是在那里只能 A* 逐格走，一路被怪打死 → 复活 → 再走，形成活锁。
            // D5 实测：11 个假人困在 225/9/8 → 潘夜村[6] 的长途上，传送 0 次、死 441 / 杀 599。
            //
            // 回城后 _teleportViaHome 立即清位，下一次决策就在主城用石头直达目标图。
            if (BotPerception.FindTeleportStone(current) == null && TryStartReturnHomeForTeleport())
                return true;

            // 白名单（Config.BotHuntMaps）同样约束跨图目标：不填 = 不限制，填了就只在这些图之间流动。
            BotOptions options = Options;

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
                // 排序三要素：① 怪等级最贴近自己（经验效率）② 有六合石（能传送，不用腿儿着）
                // ③ 地图索引距离（近似「离得近」）。
                // 全服练级图有 60 多张，只按索引距离排会挑到八竿子打不着的地图。
                .OrderBy(x => Math.Abs(x.Monster.Level - _player.Level))
                .ThenByDescending(x => BotPerception.HasTeleportStone(x.Region.Map))
                .ThenBy(x => Math.Abs(x.Region.Map.Index - current.Info.Index))
                .Take(12)
                .ToList();

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

                    if (TryStartTeleport(map, points[SEnvir.Random.Next(points.Count)])) return true;
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
                        return true;
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
                    _travelFailures = 0;
                    _emptyHunts = 0;

                    BotLog.Warn(Profile.Seq, string.Format(
                        "跨图(A*)：{0}[{1}] → {2}[{3}]，白名单 {{{4}}}，当前图六合石={5}",
                        current.Info.Description, current.Info.Index,
                        map.Description, map.Index,
                        string.Join(",", options.HuntMaps),
                        BotPerception.FindTeleportStone(current) != null ? "有" : "无"));

                    EnterState(BotState.Travel, string.Format("跨图前往 {0}", map.Description), TimeSpan.FromMinutes(5));

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

            BotLog.Warn(Profile.Seq, string.Format(
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

                _travelMap = null;
                _emptyHunts = 0;

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

        private void FinishTravel(string detail)
        {
            AutoPathService.Instance.Cancel(_player);

            _travelMap = null;
            _travelFailures = 0;
            _emptyHunts = 0;
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
                _travelMap = null;
                _emptyHunts = 0;
                _blockedCount = 0;
                _teleportViaHome = false;

                EnterState(BotState.GoHunt, "传送到达，开始找怪", TimeSpan.FromSeconds(HuntTimeoutSeconds));
                return;
            }

            if (SEnvir.Now >= _stateDeadline)
            {
                FinishTravel(string.Format("传送超时（2 分钟没送到 {0}），先在本图练级", _travelMap.Description));
                return;
            }

            NPCObject stone = BotPerception.FindTeleportStoneFor(_player.CurrentMap, _travelMap.Index);

            if (stone == null)
            {
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

            int button = BotPerception.FindTeleportButton(stone.NPCInfo, _travelMap.Index);

            if (button < 0)
            {
                BotActions.CloseNpc(_player);
                FallbackToTravel(string.Format("传送石没有直达 {0} 的按钮", _travelMap.Description));
                return;
            }

            // 按下按钮后引擎会在 DoActions 里同步执行 Teleport，玩家当帧就换图了；
            // 此时 NPCPage 已被清空，所以**不能用返回值判断成败**。
            BotActions.TryPressButton(_player, button);

            LastDetail = string.Format("传送至 {0}", _travelMap.Description);
        }

        /// <summary>传送走不通时退回 A* 跨图（保留目标，只换交通方式）。</summary>
        private void FallbackToTravel(string reason)
        {
            BotActions.CloseNpc(_player);

            _blockedCount = 0;
            _travelFailures = 0;
            _teleportViaHome = false;

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
            _travelMap = null;
            _travelPoint = default;
            _travelFailures = 0;
            _emptyHunts = 0;
            _blockedCount = 0;

            ReturnHomeForTeleportCount++;

            int scrolls = BotActions.CountTownScroll(_player);

            BotLog.Warn(Profile.Seq, string.Format(
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

                BotLog.Warn(Profile.Seq, string.Format("已回到 {0}，改用六合石传送", home.Description));

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

            _travelMap = null;
            _travelFailures = 0;

            BotLog.Warn(Profile.Seq, string.Format(
                "回城中转放弃：{0}（{1} 秒内不再尝试，累计 {2} 次）", reason, seconds, _returnHomeFailStreak));

            EnterState(BotState.Idle, reason + "，先在本图练级", TimeSpan.FromSeconds(30));
        }

        /// <summary>能不能用传送石去目标图（有石头 + 有直达按钮 + 钱够）。</summary>
        private bool TryStartTeleport(MapInfo map, Point point)
        {
            // 必须找「能直达这张图」的那颗石头：一个城里常常有好几颗，
            // 内传送员的按钮只通向城内，外传送员才有跨城按钮（沙漠土城实测）。
            NPCObject stone = BotPerception.FindTeleportStoneFor(_player.CurrentMap, map.Index);

            if (stone == null) return false;

            int button = BotPerception.FindTeleportButton(stone.NPCInfo, map.Index);

            if (button < 0) return false;

            int cost = BotPerception.CheapestTeleportCost(stone.NPCInfo, map.Index);

            if (cost > 0 && GoldAmount() < cost) return false;

            _travelMap = map;
            _travelPoint = point;
            _travelFailures = 0;
            _emptyHunts = 0;
            _blockedCount = 0;

            // 能直接传就别再走中转分支（中转标记可能从上一次补给打断里残留）。
            _teleportViaHome = false;

            // 用 Warn 而不是 Info：生产配置 BotLogLevel=2（Warn），Info 会被整体过滤，
            // 而「这次跨图到底走的是传送还是腿儿着」是判断跨图机制有没有生效的唯一线索。
            BotLog.Warn(Profile.Seq, string.Format("{0} 已不适合继续练级（{1} 级），传送去 {2}（花费 {3}）",
                _player.CurrentMap.Info.Description, _player.Level, map.Description,
                cost > 0 ? cost.ToString() : "免费"));

            EnterState(BotState.Teleport, string.Format("传送前往 {0}", map.Description), TimeSpan.FromMinutes(2));

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

            // 0) 满级 → 转生。
            // 全服等级上限 Config.MaxLevel（生产 75），满级后打怪一点收益都没有；
            // 转生会加各项属性并回到 0 级重新练，是设计上的「循环」。
            // 不加这一条，假人 75 级后就只会站在那儿空砍，永远不成长。
            if (Config.MaxLevel > 0 && _player.Level >= Config.MaxLevel
                && State != BotState.Rebirth && State != BotState.Trade
                && SEnvir.Now >= _rebirthCooldown)
            {
                _rebirthBaseCount = _player.Character.Rebirth;

                BotLog.Warn(Profile.Seq, string.Format("满级（{0} 级），去市政厅找管理员转生（已转生 {1} 次）",
                    _player.Level, _player.Character.Rebirth));

                EnterState(BotState.Rebirth, "满级了，去市政厅转生", TimeSpan.FromMinutes(10));

                return true;
            }

            // 1) 道士血低先自愈（7 级才有治愈术；比喝药省，还能顺带涨技能熟练度）
            if (BotPerception.HealthPercent(_player) <= 55 && BotSkills.TrySelfHeal(_player))
                LastDetail = "给自己加血";

            // 2) 血量低于 45% 就喝红（走 ItemUse，冷却/负重与真人一致）
            if (BotPerception.HealthPercent(_player) <= 45 && BotActions.TryUsePotion(_player, Stat.Health))
                LastDetail = "喝红药";

            // 3) 法师/道士蓝低于 30% 喝蓝：没蓝就放不出技能，只能贴脸，比没红药更致命
            if (_player.Class != MirClass.Warrior)
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

            if (tryRestock && red <= options.RestockPotionLow)
            {
                // 钱包连一瓶最便宜的药都买不起时，去药店也是空手而归。
                //
                // 实测（D5 主压测 126 分钟 + 千倍率探针 30 分钟）：金币 0 的假人每 60 秒
                // 空跑一趟补给，日志连续刷「补给结束：红 5 蓝 21，金币 0」，一趟一瓶都没买到。
                // 这种空转把「补给失败」抬到 785 次，而真实原因只是没钱。
                int floorPrice = BotActions.FloorPotionPrice(Stat.Health);

                if (floorPrice <= 0 || GoldAmount() >= floorPrice)
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
                    long capacity = _player.Stats[Stat.BagWeight];
                    int freeSlots = BotPerception.FreeInventorySlots(_player);

                    bool needClear = capacity > 0 && (_player.BagWeight >= capacity * 7 / 10 || freeSlots < 10);

                    if (needClear && HasSellableItems())
                    {
                        _tradeIsRepair = false;

                        // 卖货冷却中就先别跑 —— **不能**因为「药空了」就每帧重进 Trade：
                        // 实测那样会形成热循环（进门 → 卖不掉 → 收尾冷却 → 下一帧又进门），
                        // 30 个假人 30 分钟刷出 66,635 次空交易、18 个被判定 stuck。
                        // 冷却只有 20 秒，等得起。
                        if (SEnvir.Now >= _tradeCooldown && SetTradeTarget(SellableItemType()))
                        {
                            // 5 分钟：这一趟可能要跑好几类商人才卖得干净（装备/矿石/材料各一个 NPC）。
                            EnterState(BotState.Trade, "补给前先清背包（只留药和卷）", TimeSpan.FromMinutes(5));

                            return true;
                        }

                        LastDetail = "等卖货冷却，之后先清背包再补给";
                    }
                    else
                    {
                        // 5 分钟而不是 3 分钟：一次补给要「AutoPath 跨图回城 → 走到药店买药 →
                        // 再走到杂货商买卷」三段，城外 60 格、两个 NPC 各 20~40 格。
                        // 3 分钟实测经常卡在最后一段（2026-11-12：6 次失败里 4 次是「补给超时」）。
                        EnterState(BotState.Restock, string.Format("红药只剩 {0} 瓶，回城补给", red), TimeSpan.FromMinutes(5));

                        return true;
                    }
                }
            }

            // 6) 日常：背包满了要回城卖货腾格子（否则会永久卡在拾取态）；
            //    装备耐久归零会被引擎整件跳过（攻击力变 0），也要修。
            if (State != BotState.Trade && SEnvir.Now >= _tradeCooldown && TryStartTrade())
                return true;

            return false;
        }

        /// <summary>
        /// 回城买药。全程走真人路径：AutoPath 跨图 → 走到 NPC 旁 → NPCCall → NPCButton → NPCBuy。
        /// 任何一步失败都退回练级并给 60 秒冷却，避免「没钱 → 反复回城 → 永远不练级」的死循环。
        /// </summary>
        /// <summary>
        /// 满级转生：市政厅[2] 的「管理员」→ 按钮「转生主页」→ 按钮「执行转生」。
        /// <para>
        /// 为什么要有这条：全服等级上限 75（<c>[System] MaxLevel</c>），满级后再打怪没有任何收益，
        /// 而转生会给各项属性加成并回到 0 级重新练 —— 假人不转生就等于「到顶即报废」。
        /// 实测（mapq action:Rebirth）：全服只有 1 处转生动作，在市政厅。
        /// </para>
        /// </summary>
        private void ProcessRebirth()
        {
            if (Config.MaxLevel <= 0 || _player.Level < Config.MaxLevel)
            {
                // 转生次数变多了 = 成功（等级已被引擎打回 1 级，不能再拿「等级是否满」判断）。
                if (_player.Character.Rebirth > _rebirthBaseCount)
                {
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

        private void ProcessRestock()
        {
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
            }

            // 对话已打开：按当前子阶段办事（买药 / 买回城卷）
            if (_player.NPCPage != null)
            {
                if (_restockStage == RestockStage.TownScroll) BuyTownScrolls();
                else BuyPotions();

                return;
            }

            MapInfo home = _player.Character.BindPoint?.BindRegion?.Map;

            if (home == null)
            {
                FinishRestock("角色没有绑定主城，无法补给");
                return;
            }

            // 不在主城：优先用**回城卷**瞬移（省掉 AutoPath 走 60 秒+），
            // 没有卷才退回 AutoPath 跨图走回去。
            if (_player.CurrentMap?.Info != home)
            {
                if (TryReturnHomeByScroll(home)) return;

                if (_player.AutoPath != null)
                {
                    LastDetail = string.Format("正在走回 {0}", home.Description);
                    return;
                }

                if (!AutoPathService.Instance.TryStart(_player, home, HomePoint(home)))
                {
                    FinishRestock(string.Format("回 {0} 的路线规划失败（等级/道具限制），60 秒后再试", home.Description));
                    return;
                }

                LastDetail = string.Format("走回 {0} 补给", home.Description);
                return;
            }

            // 在主城：找当前阶段对应的 NPC
            NPCObject npc = _restockStage == RestockStage.TownScroll
                ? BotPerception.FindScrollShop(_player.CurrentMap)
                : BotPerception.FindPharmacy(_player.CurrentMap);

            if (npc == null)
            {
                if (_restockStage == RestockStage.TownScroll)
                {
                    // 买不到卷不是致命错误 —— 药已经买到了，回城卷只是加速手段。
                    FinishRestock(string.Format("{0} 里没有卖回城卷的杂货商", home.Description));
                }
                else
                {
                    FinishRestock(string.Format("{0} 里找不到药店", home.Description));
                }

                return;
            }

            int distance = BotNavigator.Distance(_player.CurrentLocation, npc.CurrentLocation);

            string npcKind = _restockStage == RestockStage.TownScroll ? "杂货商" : "药店";

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

            LastDetail = string.Format("打开 {0}，准备{1}", npc.Name, _restockStage == RestockStage.TownScroll ? "买回城卷" : "买药");
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
            _travelFailures = 0;
            _emptyHunts = 0;
            _teleportViaHome = false;

            BotLog.Warn(Profile.Seq, string.Format("卖货用六合石传送去 {0}（花费 {1}）",
                dest.Description, cost > 0 ? cost.ToString() : "免费"));

            EnterState(BotState.Teleport, string.Format("传送去 {0} 卖货", dest.Description), TimeSpan.FromMinutes(5));

            return true;
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
            int blueNeed = Math.Max(0, options.RestockBuyBlue - blue);

            bool bought = false;

            if (redNeed > 0) bought |= TryBuyPotions(Stat.Health, redNeed, gold);
            if (blueNeed > 0) bought |= TryBuyPotions(Stat.Mana, blueNeed, gold);

            if (bought)
            {
                LastDetail = string.Format("买药中：红 {0}/{1}、蓝 {2}/{3}，金币 {4}",
                    BotActions.CountPotion(_player, Stat.Health), options.RestockBuyRed,
                    BotActions.CountPotion(_player, Stat.Mana), options.RestockBuyBlue, gold);

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

            // 战士没有耗蓝技能，买蓝药纯浪费（打怪掉的钱还不够买药）。
            if (restoreStat == Stat.Mana && _player.Character?.Class == MirClass.Warrior) return false;

            int maxRestore = restoreStat == Stat.Health
                ? Math.Max(30, pool * 6 / 10)   // 红药按 60% 血量，至少允许回 30 的最低档
                : Math.Max(40, pool);           // 蓝药至少要能买最低档（回 40），否则法师永远买不到

            int perBottle = (int)Math.Min(int.MaxValue, gold / Math.Max(1, need));

            NPCGood good = BotActions.FindPotionGood(_player, restoreStat, perBottle, maxRestore);

            // 挑不到档位时的退路：**预算和恢复量上限一起放宽**。
            //
            // 原来只放宽预算、maxRestore 保持不变 → 退路形同虚设：
            // 药店里所有红药的恢复量都超过 maxRestore 时，两次查询都返回 null，一瓶也买不到。
            // 实测（2026-11-11，金币 50 万）：2 级假人血量上限约 50 → maxRestore = 30，
            // 而药店最低档红药回 40+ → 一直「补给结束：红 0 蓝 20，金币 499000」，红药永远是 0。
            // 宁可喝溢出也不能断药 —— 断药就打不了怪，整条经济闭环跟着断。
            if (good == null)
                good = BotActions.FindPotionGood(_player, restoreStat, int.MaxValue, int.MaxValue);

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
                BotLog.Warn(Profile.Seq, string.Format(
                    "买药诊断：{0} 装不下（{1} 单价 {2} 金币 {3}，负重 {4}/{5}，背包空位 {6}）",
                    restoreStat, good.Item?.ItemName, good.Cost, gold,
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

            // 下一轮补给从头开始（先买药，再考虑买卷）。
            _restockStage = RestockStage.Potion;

            // 失败判定：看**增量**（这次买到了多少），不看存量。
            // 存量判定会误报：进门前本来就剩 2 瓶药，买不到新的也照样算「成功」。
            int potions = BotActions.CountPotion(_player, Stat.Health) + BotActions.CountPotion(_player, Stat.Mana);
            int scrolls = BotActions.CountTownScroll(_player);

            int basePotions = _restockBaselinePotions;
            int baseScrolls = _restockBaselineScrolls;

            int gained = potions - basePotions + (scrolls - baseScrolls);

            bool success = gained > 0;

            _restockBaselinePotions = -1;

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

            // 必须是 Warn：补给成败是 D5 验收的核心指标，而 Info 会被 BotLogLevel=2 整体过滤。
            // 2026-11-12 又踩了一次：补给成功率掉到 38%，日志里一个字都看不到，只能靠猜。
            // 把「药/卷的前后数量」一起打出来，才能区分「根本没买到」和「买了但期间喝掉了」。
            BotLog.Warn(Profile.Seq, string.Format("补给{0}：{1}（药 {2}→{3}，卷 {4}→{5}）",
                success ? "成功" : "失败", detail,
                basePotions, potions, baseScrolls, scrolls));

            EnterState(BotState.Idle, detail, TimeSpan.FromSeconds(3));
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

                if (_blockedCount > 20)
                {
                    FinishHarvest(string.Format("走不到尸体（{0} 格）", distance));
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
                LastDetail = string.Format("等待挖取（还要再挖 {0} 次）", corpse.HarvestCount);
                return;
            }

            _player.Harvest(direction);

            // 引擎的 HarvestTime 是 600ms，这里留 100ms 余量
            // （ActionTime 会被其它动作刷新，早一点发会被引擎排队而不是立即执行）。
            _nextHarvestTime = SEnvir.Now.AddMilliseconds(700);

            LastDetail = string.Format("挖 {0}（还要再挖 {1} 次）",
                corpse.MonsterInfo?.MonsterName ?? "尸体", Math.Max(0, corpse.HarvestCount));

            // ⚠ 结束条件**不能**用 HarvestCount：
            // 引擎里 `if (ob.HarvestCount > 0) { ob.HarvestCount--; continue; }` ——
            // 也就是说前 N 次挖全是**空挖**，第 N+1 次才真正把东西拿走。
            // 鸡的 HarvestCount=2 → 要挖 3 次才有收获。
            // 所以只认「这个账号的掉落被拿完」这一个结束条件，
            // 引擎会在拿到东西后把条目从 Drops 里移除，下一轮开头就能判定出来。
        }

        /// <summary>挖取收尾。</summary>
        private void FinishHarvest(string detail)
        {
            _harvestTarget = null;
            _target = null;

            HarvestCount++;
            _player.Character.BotHarvests++;

            BotLog.Debug(Profile.Seq, detail);

            EnterState(BotState.Idle, detail, TimeSpan.FromSeconds(2));
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

                // 评分必须**明显更高**才换：差个位数的属性不值得来回折腾。
                int score = EquipmentScore(item);
                int currentScore = current == null ? 0 : EquipmentScore(current);

                if (score <= currentScore + 50) continue;

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

            // 1) 有装备坏了 → 去修理（修理优先于卖货：攻击力为 0 是致命的）
            if (HasBrokenEquipment())
            {
                _tradeIsRepair = true;
                SetTradeTarget(BrokenEquipmentType());

                EnterState(BotState.Trade, "装备耐久没了，回城修理", TimeSpan.FromMinutes(3));

                return true;
            }

            // 2) 背包快满了 → 去卖货腾格子
            //    阈值留 8 格余量：不然走到一半捡两件又满了，会反复半途折返。
            int free = BotPerception.FreeInventorySlots(_player);

            if (free <= 8 && HasSellableItems())
            {
                _tradeIsRepair = false;

                if (SetTradeTarget(SellableItemType()))
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

                if (SetTradeTarget(SellableItemType()))
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
            long capacity = _player.Stats[Stat.BagWeight];

            if (capacity > 0 && _player.BagWeight >= capacity * 9 / 10 && HasSellableItems())
            {
                _tradeIsRepair = false;

                if (SetTradeTarget(SellableItemType()))
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
            int blueNeed = Math.Max(0, options.RestockBuyBlue - blue);

            if (redNeed == 0 && blueNeed == 0) return false;

            int redPrice = BotActions.CheapestPotionPrice(_player, Stat.Health);
            int bluePrice = BotActions.CheapestPotionPrice(_player, Stat.Mana);

            // 战士不买蓝药，蓝价缺失不算缺钱
            bool needBlue = blueNeed > 0 && _player.Character?.Class != MirClass.Warrior;

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
                if (item.CurrentDurability > 0) continue;

                return true;
            }

            return false;
        }

        /// <summary>第一件坏掉的装备的类型（用来找对应的修理铺）。</summary>
        private ItemType BrokenEquipmentType()
        {
            foreach (UserItem item in _player.Equipment)
            {
                if (item?.Info == null) continue;
                if (item.Info.Durability <= 0) continue;
                if (item.CurrentDurability > 0) continue;

                return item.Info.ItemType;
            }

            return ItemType.Weapon;
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
            foreach (UserItem item in _player.Inventory)
            {
                if (item?.Info == null) continue;
                if (!item.Info.CanSell) continue;
                if ((item.Flags & UserItemFlags.Bound) == UserItemFlags.Bound) continue;
                if ((item.Flags & UserItemFlags.Locked) == UserItemFlags.Locked) continue;
                if ((item.Flags & UserItemFlags.Worthless) == UserItemFlags.Worthless) continue;
                if (IsKeepItem(item)) continue;

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
        private static bool IsKeepItem(UserItem item)
        {
            if (item?.Info == null) return false;

            // 回城卷：回城的工具，绝不能卖。
            if (BotActions.IsTownScroll(item)) return true;

            // 药：回血 / 回蓝的消耗品，自己要用。
            if (item.Info.ItemType == ItemType.Consumable &&
                (item.Info.Stats[Stat.Health] > 0 || item.Info.Stats[Stat.Mana] > 0))
                return true;

            return false;
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
            // 按**件数**排序：优先卖掉占位最多的那一类（挖取物 Meat 常常是几十件，
            // 随便挑一件装备卖掉毫无意义，背包还是满的，会变成「卖一件→又满→再卖」的循环）。
            Dictionary<ItemType, int> counts = SellableTypeCounts();

            if (counts.Count == 0) return ItemType.Nothing;

            // 优先挑「**当前地图**就能卖」的类型：卖货最怕跑一趟还是卖不掉，
            // 沙漠土城/诺玛村落 都没有肉类收购商，而挖取物恰恰全是 Meat。
            Map current = _player.CurrentMap;

            ItemType pick = PickByMap(counts, current);

            if (pick != ItemType.Nothing) return pick;

            // 退一步：绑定主城能卖的
            MapInfo home = _player.Character?.BindPoint?.BindRegion?.Map;
            Map homeMap = home == null ? null : SEnvir.GetMap(home);

            pick = PickByMap(counts, homeMap);

            if (pick != ItemType.Nothing) return pick;

            // 都不行就交给目的地解析去挑一张能收的图（宁可跑一趟，也别抱着背包不动）
            return counts.OrderByDescending(x => x.Value).First().Key;
        }

        /// <summary>在 <paramref name="map"/> 收得下的类型里，挑件数最多的那类。</summary>
        private static ItemType PickByMap(Dictionary<ItemType, int> counts, Map map)
        {
            if (map == null) return ItemType.Nothing;

            ItemType best = ItemType.Nothing;
            int bestCount = 0;

            foreach (KeyValuePair<ItemType, int> pair in counts)
            {
                if (pair.Value <= bestCount) continue;
                if (BotPerception.FindTrader(map, pair.Key) == null) continue;

                best = pair.Key;
                bestCount = pair.Value;
            }

            return best;
        }

        /// <summary>背包里每种「可卖」物品各有多少件。</summary>
        private Dictionary<ItemType, int> SellableTypeCounts()
        {
            Dictionary<ItemType, int> result = new Dictionary<ItemType, int>();

            foreach (UserItem item in _player.Inventory)
            {
                if (item?.Info == null) continue;
                if (!item.Info.CanSell) continue;
                if ((item.Flags & UserItemFlags.Bound) == UserItemFlags.Bound) continue;
                if ((item.Flags & UserItemFlags.Locked) == UserItemFlags.Locked) continue;
                if ((item.Flags & UserItemFlags.Worthless) == UserItemFlags.Worthless) continue;
                if ((item.Flags & UserItemFlags.Marriage) == UserItemFlags.Marriage) continue;
                if (IsKeepItem(item)) continue;

                ItemType type = item.Info.ItemType;

                result.TryGetValue(type, out int count);
                result[type] = count + 1;
            }

            return result;
        }

        /// <summary>设置本轮交易目标（类型 + 目的地地图）。返回 false = 没地方卖，别白跑。</summary>
        private bool SetTradeTarget(ItemType type)
        {
            _tradeItemType = type;
            _tradeDest = ResolveTradeDestination(type);

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

            if (_tradeIsRepair)
            {
                if (current != null && BotPerception.FindRepairShop(current) != null) return current.Info;
                return home;
            }

            if (type == ItemType.Nothing) return home;

            // ① 就地能卖 → 一步都不用跑
            if (current != null && BotPerception.FindTrader(current, type) != null)
                return current.Info;

            // ② 绑定主城（回城卷直达，1 秒）
            Map homeMap = home == null ? null : SEnvir.GetMap(home);

            if (homeMap != null && BotPerception.FindTrader(homeMap, type) != null)
                return home;

            // ③ 白名单里（练级图，有六合石直达）按索引距离挑最近的一张
            MapInfo best = null;
            int bestDist = int.MaxValue;

            foreach (int index in Options.HuntMaps)
            {
                MapInfo info = SEnvir.MapInfoList.Binding.FirstOrDefault(x => x.Index == index);

                if (info == null || info == home) continue;

                Map map = SEnvir.GetMap(info);

                if (map == null || BotPerception.FindTrader(map, type) == null) continue;

                int dist = Math.Abs(info.Index - (current?.Info?.Index ?? home?.Index ?? 0));

                if (dist >= bestDist) continue;

                bestDist = dist;
                best = info;
            }

            return best;
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
                _tradeCooldown = SEnvir.Now.AddMinutes(5);

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
                FinishTrade(string.Format("没有任何地图收 {0}，放弃这趟卖货", _tradeItemType));
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
                    FinishTrade(string.Format("回 {0} 的路线规划失败，60 秒后再试", home.Description));
                    return;
                }

                LastDetail = string.Format("走回 {0} 交易", home.Description);

                return;
            }

            // 在目的地：找对应的 NPC
            NPCObject npc = _tradeIsRepair
                ? BotPerception.FindRepairShop(_player.CurrentMap)
                : BotPerception.FindTrader(_player.CurrentMap, _tradeItemType);

            if (npc == null)
            {
                FinishTrade(_tradeIsRepair
                    ? string.Format("{0} 里找不到修理铺", home.Description)
                    : string.Format("{0} 里没有收 {1} 的商人", home.Description, _tradeItemType));

                return;
            }

            int distance = BotNavigator.Distance(_player.CurrentLocation, npc.CurrentLocation);

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
                    FinishTrade(string.Format("这个 NPC 页里没有卖 {0} 的入口", _tradeItemType));
                    return;
                }

                BotActions.TryPressButton(_player, button);

                LastDetail = string.Format("打开卖 {0} 的页面", _tradeItemType);

                return;
            }

            List<CellLinkInfo> links = BuildSellLinks(_tradeItemType);

            if (links.Count == 0)
            {
                FinishTrade(string.Format("没有可卖的 {0}", _tradeItemType));
                return;
            }

            long goldBefore = GoldAmount();

            BotActions.TrySell(_player, links);

            long goldAfter = GoldAmount();

            LastDetail = string.Format("卖了 {0}，金币 {1} → {2}", links.Count, goldBefore, goldAfter);

            FinishTrade(string.Format("卖了 {0} 件 {1}，赚了 {2} 金币",
                links.Count, _tradeItemType, goldAfter - goldBefore));
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

            for (int i = 0; i < _player.Inventory.Length; i++)
            {
                UserItem item = _player.Inventory[i];

                if (item?.Info == null) continue;
                if (item.Info.ItemType != itemType) continue;
                if (!item.Info.CanSell) continue;
                if (item.Count <= 0) continue;

                if ((item.Flags & UserItemFlags.Bound) == UserItemFlags.Bound) continue;
                if ((item.Flags & UserItemFlags.Locked) == UserItemFlags.Locked) continue;
                if ((item.Flags & UserItemFlags.Worthless) == UserItemFlags.Worthless) continue;
                if ((item.Flags & UserItemFlags.Marriage) == UserItemFlags.Marriage) continue;

                // 药和回城卷不卖（药店收货类型含 Consumable，引擎不会拦，得自己挡）。
                if (IsKeepItem(item)) continue;

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

            BotActions.TryRepair(_player, links);

            long goldAfter = GoldAmount();

            FinishTrade(string.Format("修了 {0} 件装备，花了 {1} 金币", links.Count, goldBefore - goldAfter));
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

        /// <summary>交易收尾：关对话、上冷却、回练级。</summary>
        private void FinishTrade(string detail)
        {
            BotActions.CloseNpc(_player);

            // 同补给：可能用 AutoPath 走过 NPC，收尾必须撤掉。
            if (_player.AutoPath != null)
                AutoPathService.Instance.Cancel(_player);

            // 冷却：卖完别马上又跑一趟（但背包还满的话下一拍就要再卖，得留短冷却）；
            // 修完给长一点 —— 耐久掉得慢，不用频繁跑。
            // 卖货 20 秒：原来 1 分钟太长，而「药空了 → 等冷却」这一分钟正好是被打死的时候。
            _tradeCooldown = SEnvir.Now.AddMinutes(_tradeIsRepair ? 10 : 1.0 / 3.0);
            _blockedCount = 0;
            _target = null;
            _tradeDest = null;

            TradeCount++;
            _player.Character.BotTrades++;

            // Warn 而不是 Info：BotLogLevel=2（生产默认）会把 Info 全部过滤掉。
            // 本次排查就吃过这个亏 —— 「没有收 Meat 的商人」在日志里一个字都没有，
            // 只能从后台面板的 detail 里看到。
            BotLog.Warn(Profile.Seq, detail);

            EnterState(BotState.Idle, detail, TimeSpan.FromSeconds(3));
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

            BotOptions options = BotOptions.FromConfig();

            // 白名单地图（Config.BotHuntMaps）：不在名单里的图不练级，免得假人跑去高级图抢资源。
            if (options.HuntMaps.Count > 0 && !options.HuntMaps.Contains(map.Info.Index))
            {
                BotLog.Debug(Profile.Seq, string.Format("{0}(Index {1}) 不在练级白名单 [{2}] 里",
                    map.Info.Description, map.Info.Index, string.Join(",", options.HuntMaps)));

                return false;
            }

            // 把本图所有「怪等级打得过」的刷怪区摊平，按「怪等级合适 → 离自己近」排序。
            // 之前不过滤等级：最近的刷怪区常常是 5 级以上的怪，1 级号走到那里一只都打不了，
            // 表现是「到达 → 附近全是高级怪 → 空等」的循环。
            List<(Point Point, int Level)> candidates = new List<(Point, int)>();

            foreach (RespawnInfo region in SEnvir.RespawnInfoList.Binding)
            {
                if (region.Region?.Map != map.Info) continue;

                MonsterInfo resident = region.Monster;

                if (resident == null) continue;

                if (resident.Level > _player.Level + 4) continue;
                if (resident.Level < _player.Level - 8) continue;

                List<Point> points = region.Region.PointList;

                if (points == null || points.Count == 0) continue;

                foreach (Point p in points)
                    candidates.Add((p, resident.Level));
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
            List<(Point Point, int Level)> worthwhile = candidates
                .Where(x => x.Level >= _player.Level - 3)
                .ToList();

            if (worthwhile.Count == 0) worthwhile = candidates;

            Point origin = _player.CurrentLocation;

            // 排除脚边 8 格以内的点：那样的「到达刷怪点」意味着原地转一圈又回来，
            // 表现就是「到达 → 没怪 → 再到达同一个点」死循环，人也不动。
            List<(Point Point, int Level)> pool = worthwhile
                .Where(x => BotNavigator.Distance(origin, x.Point) >= 8)
                .ToList();

            if (pool.Count == 0) pool = worthwhile;

            // 排序：先靠近自己等级的怪（同等级段里怪越高级经验越多），再按距离。
            List<Point> nearest = pool
                .OrderBy(x => Math.Abs(x.Level - _player.Level - 1))
                .Take(60)
                .OrderBy(x => BotNavigator.Distance(origin, x.Point))
                .Take(40)
                .OrderBy(x => SEnvir.Random.Next())
                .Select(x => x.Point)
                .ToList();

            // 最近的一批点里也有走不通的（隔河、被墙围住），逐个算一次路，通了才用。
            foreach (Point candidate in nearest)
            {
                if (!BotNavigator.TryGetNextStep(_player, candidate, out _)) continue;

                _huntPoint = candidate;
                _stuckCount = 0;
                _blockedCount = 0;
                _lastLocation = _player.CurrentLocation;

                return true;
            }

            return false;
        }

        /// <summary>
        /// 连续 40 次「发了走路指令但坐标没变」视为卡住，强制回待机换目标。
        /// 40 × 250ms = 10 秒：追会跑的怪时坐标短暂不变是常态，阈值太小学生会一直放弃。
        /// </summary>
        private void TrackStuck()
        {
            if (_player.CurrentLocation != _lastLocation)
            {
                _lastLocation = _player.CurrentLocation;
                _stuckCount = 0;
                return;
            }

            _stuckCount++;

            if (_stuckCount < 40) return;

            _stuckCount = 0;
            EnterState(BotState.Idle, "卡住了，换个目标", TimeSpan.FromSeconds(2));
        }

        #endregion

        private void EnterState(BotState state, string detail, TimeSpan duration)
        {
            if (State != state)
                BotLog.Debug(Profile.Seq, string.Format("{0} → {1}：{2}", State, state, detail));

            State = state;
            LastDetail = detail;
            StateSince = SEnvir.Now;
            _stateDeadline = SEnvir.Now + duration;
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
