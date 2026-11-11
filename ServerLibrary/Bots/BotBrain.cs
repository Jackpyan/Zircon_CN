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
        public decimal ExperienceStart { get; private set; }

        private DateTime _nextActionTime;
        private DateTime _stateDeadline;
        private DateTime _restockCooldown;
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

        /// <summary>上次同步技能时的等级。升级时补技能（法师 7 级火球、道士 13 级火符）。</summary>
        private int _syncedLevel = -1;
        /// <summary>后台「立即补给」投递的一次性请求。</summary>
        private bool _restockRequested;

        /// <summary>本轮交易要处理的物品类型（P1-2：按类型找对应商店）。</summary>
        private ItemType _tradeItemType;

        /// <summary>本轮交易是修理模式（P1-3）还是卖货模式（P1-2）。</summary>
        private bool _tradeIsRepair;

        /// <summary>回城交易的冷却：卖完 / 修完别马上又跑一趟。</summary>
        private DateTime _tradeCooldown;

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
                    case BotState.Trade:
                        ProcessTrade();
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

            // 这只真的够不到，换一只。
            _target = null;
            EnterState(BotState.Fight, "目标够不到，换怪", TimeSpan.FromSeconds(10));
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

            // 白名单（Config.BotHuntMaps）同样约束跨图目标：不填 = 不限制，填了就只在这些图之间流动。
            BotOptions options = Options;

            // 等级窗口：能打得动（≤ 自己+4）且不至于太低（≥ 自己-8，否则经验少得没意义）。
            List<RespawnInfo> candidates = SEnvir.RespawnInfoList.Binding
                .Where(x => x.Region?.Map != null && x.Region.Map != current.Info && x.Monster != null)
                .Where(x => options.HuntMaps.Count == 0 || options.HuntMaps.Contains(x.Region.Map.Index))
                .Where(x => x.Monster.Level <= _player.Level + 4 && x.Monster.Level >= _player.Level - 8)
                .Where(x => x.Region.Map.MinimumLevel <= _player.Level)
                .OrderBy(x => Math.Abs(x.Region.Map.Index - current.Info.Index))
                .Take(12)
                .ToList();

            // 第一轮只挑没满的图；第二轮放开容量（宁可超员也别让人卡在城里不动）。
            for (int pass = 0; pass < 2; pass++)
            foreach (RespawnInfo candidate in candidates)
            {
                MapInfo map = candidate.Region.Map;

                if (pass == 0 && CountBotsOnMap(map) >= MapCapacity) continue;

                // 懒加载：地图没加载时区域点列表是空的，必须先 GetMap 触发一次。
                if (SEnvir.GetMap(map) == null) continue;

                List<Point> points = candidate.Region.PointList;

                if (points == null || points.Count == 0) continue;

                Point point = points[SEnvir.Random.Next(points.Count)];

                if (!AutoPathService.Instance.TryStart(_player, map, point)) continue;

                _travelMap = map;
                _travelPoint = point;
                _travelFailures = 0;
                _emptyHunts = 0;

                BotLog.Info(Profile.Seq, string.Format("{0} 已不适合继续练级（{1} 级），跨图去 {2}（{3},{4}）",
                    current.Info.Description, _player.Level, map.Description, point.X, point.Y));

                EnterState(BotState.Travel, string.Format("跨图前往 {0}", map.Description), TimeSpan.FromMinutes(5));

                return true;
            }

            return false;
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

            BotLog.Info(Profile.Seq, detail);

            EnterState(BotState.Idle, detail, TimeSpan.FromSeconds(30));
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

            // 4) 后台点了「立即补给」：清冷却直接回城，不用等红药见底
            if (_restockRequested)
            {
                _restockRequested = false;
                _restockCooldown = DateTime.MinValue;

                EnterState(BotState.Restock, "后台要求立即补给", TimeSpan.FromMinutes(3));

                return true;
            }

            // 5) P1：背包满了要回城卖货腾格子 —— 否则假人会永久卡在拾取态。
            //    修理同理：耐久归零的装备会被引擎整件跳过（攻击力变 0），假人就打不动怪了。
            if (State != BotState.Trade && SEnvir.Now >= _tradeCooldown && TryStartTrade())
                return true;

            // 6) 药见底就回城补，别等喝完才想起（0 = 关闭自动补给）
            BotOptions options = Options;

            if (options.RestockPotionLow <= 0) return false;
            if (State == BotState.Restock) return false;
            if (SEnvir.Now < _restockCooldown) return false;

            int red = BotActions.CountPotion(_player, Stat.Health);

            if (red > options.RestockPotionLow) return false;

            EnterState(BotState.Restock, string.Format("红药只剩 {0} 瓶，回城补给", red), TimeSpan.FromMinutes(3));

            return true;
        }

        /// <summary>
        /// 回城买药。全程走真人路径：AutoPath 跨图 → 走到 NPC 旁 → NPCCall → NPCButton → NPCBuy。
        /// 任何一步失败都退回练级并给 60 秒冷却，避免「没钱 → 反复回城 → 永远不练级」的死循环。
        /// </summary>
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

            // 对话已打开：直接买
            if (_player.NPCPage != null)
            {
                BuyPotions();
                return;
            }

            MapInfo home = _player.Character.BindPoint?.BindRegion?.Map;

            if (home == null)
            {
                FinishRestock("角色没有绑定主城，无法补给");
                return;
            }

            // 不在主城：跨图走回去（CanAutoPath 已全开，上一轮实测可用）
            if (_player.CurrentMap?.Info != home)
            {
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

            // 在主城：找药店
            NPCObject pharmacy = BotPerception.FindPharmacy(_player.CurrentMap);

            if (pharmacy == null)
            {
                FinishRestock(string.Format("{0} 里找不到药店", home.Description));
                return;
            }

            int distance = BotNavigator.Distance(_player.CurrentLocation, pharmacy.CurrentLocation);

            // NPC 本身是阻挡物：A* 的终点是它站的格子，贴身那一步永远迈不进去。
            // 好在 NPCCall 只要求 18 格视野内（Config.MaxViewRange），所以「走不动就直接开口」。
            if (distance > 3)
            {
                BotMoveResult move = BotNavigator.TryStepTowards(_player, pharmacy.CurrentLocation);

                if (move == BotMoveResult.Moved)
                {
                    _blockedCount = 0;
                    LastDetail = string.Format("走向药店 {0}（{1} 格）", pharmacy.Name, distance);
                    return;
                }

                if (move == BotMoveResult.Busy) return;

                // A* 算不到 NPC 脚下（那格被 NPC 自己占着），改用「贴近一格」的兜底。
                if (BotNavigator.TryStepAdjacent(_player, pharmacy.CurrentLocation))
                {
                    _blockedCount = 0;
                    LastDetail = string.Format("贴近药店 {0}（{1} 格）", pharmacy.Name, distance);
                    return;
                }
            }

            if (!BotActions.TryOpenNpc(_player, pharmacy))
            {
                _blockedCount++;

                if (_blockedCount > 20)
                {
                    FinishRestock(string.Format("走不到药店 {0}（{1} 格外始终过不去）", pharmacy.Name, distance));
                    return;
                }

                LastDetail = string.Format("还差 {0} 格才够到药店 {1}", distance, pharmacy.Name);
                return;
            }

            _blockedCount = 0;

            LastDetail = string.Format("打开 {0}，准备买药", pharmacy.Name);
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

            FinishRestock(string.Format("补给结束：红 {0} 蓝 {1}，金币 {2}",
                BotActions.CountPotion(_player, Stat.Health),
                BotActions.CountPotion(_player, Stat.Mana), gold));
        }

        /// <summary>
        /// 买一类药，返回是否买到了。两条约束缺一不可：
        /// 1. <b>每瓶预算 = 剩余金币 / 还缺的瓶数</b> —— 保证这次能买满，而不是把钱砸在几瓶贵药上；
        /// 2. <b>单瓶恢复量 ≤ 自身血量/蓝量的合理上限</b> —— 1 级 52 血喝回 250 的药是纯浪费。
        /// </summary>
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

            if (good == null) return false;

            int affordable = (int)Math.Min(need, gold / Math.Max(1, good.Cost));

            if (affordable <= 0) return false;

            return BotActions.TryBuy(_player, good.Index, affordable);
        }

        /// <summary>收尾：关对话、上冷却、回练级。</summary>
        private void FinishRestock(string detail)
        {
            BotActions.CloseNpc(_player);

            // 60 秒内不再触发补给：没钱的时候反复回城会彻底不练级。
            _restockCooldown = SEnvir.Now.AddSeconds(60);
            _blockedCount = 0;
            _target = null;

            BotLog.Info(Profile.Seq, detail);

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
                _tradeItemType = BrokenEquipmentType();

                EnterState(BotState.Trade, "装备耐久没了，回城修理", TimeSpan.FromMinutes(3));

                return true;
            }

            // 2) 背包快满了 → 去卖货腾格子
            //    阈值留 8 格余量：不然走到一半捡两件又满了，会反复半途折返。
            int free = BotPerception.FreeInventorySlots(_player);

            if (free <= 8 && HasSellableItems())
            {
                _tradeIsRepair = false;
                _tradeItemType = SellableItemType();

                EnterState(BotState.Trade, "背包快满了，回城卖东西", TimeSpan.FromMinutes(3));

                return true;
            }

            return false;
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

        /// <summary>背包里有没有能卖的东西。</summary>
        private bool HasSellableItems()
        {
            foreach (UserItem item in _player.Inventory)
            {
                if (item?.Info == null) continue;
                if (!item.Info.CanSell) continue;
                if ((item.Flags & UserItemFlags.Bound) == UserItemFlags.Bound) continue;
                if ((item.Flags & UserItemFlags.Locked) == UserItemFlags.Locked) continue;
                if ((item.Flags & UserItemFlags.Worthless) == UserItemFlags.Worthless) continue;

                return true;
            }

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
        /// </summary>
        private ItemType SellableItemType()
        {
            foreach (UserItem item in _player.Inventory)
            {
                if (item?.Info == null) continue;
                if (!item.Info.CanSell) continue;
                if ((item.Flags & UserItemFlags.Bound) == UserItemFlags.Bound) continue;
                if ((item.Flags & UserItemFlags.Locked) == UserItemFlags.Locked) continue;
                if ((item.Flags & UserItemFlags.Worthless) == UserItemFlags.Worthless) continue;
                if ((item.Flags & UserItemFlags.Marriage) == UserItemFlags.Marriage) continue;

                return item.Info.ItemType;
            }

            return ItemType.Nothing;
        }

        /// <summary>回城交易的主流程：走回主城 → 找到对应 NPC → 卖货或修理。</summary>
        private void ProcessTrade()
        {
            if (SEnvir.Now >= _stateDeadline)
            {
                FinishTrade("交易超时，先回去练级");
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

            MapInfo home = _player.Character.BindPoint?.BindRegion?.Map;

            if (home == null)
            {
                FinishTrade("角色没有绑定主城，无法交易");
                return;
            }

            // 不在主城：跨图走回去
            if (_player.CurrentMap?.Info != home)
            {
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

            // 在主城：找对应的 NPC
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

            // NPC 本身是阻挡物：贴身那一步永远迈不进去，
            // 所以走不动就直接开口（NPCCall 只要 18 格视野内）。
            if (distance > 3)
            {
                BotMoveResult move = BotNavigator.TryStepTowards(_player, npc.CurrentLocation);

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

            // 冷却：卖完别马上又跑一趟（但背包还满的话下一拍就要再卖，得留短冷却）；
            // 修完给长一点 —— 耐久掉得慢，不用频繁跑。
            _tradeCooldown = SEnvir.Now.AddMinutes(_tradeIsRepair ? 10 : 1);
            _blockedCount = 0;
            _target = null;

            BotLog.Info(Profile.Seq, detail);

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

            // 把本图所有「怪等级打得过」的刷怪点摊平，按「离自己近」排序。
            // 之前不过滤等级：最近的刷怪区常常是 5 级以上的怪，1 级号走到那里一只都打不了，
            // 表现是「到达 → 附近全是高级怪 → 空等」的循环。
            List<Point> candidates = new List<Point>();

            foreach (RespawnInfo region in SEnvir.RespawnInfoList.Binding)
            {
                if (region.Region?.Map != map.Info) continue;

                MonsterInfo resident = region.Monster;

                if (resident == null) continue;

                if (resident.Level > _player.Level + 4) continue;
                if (resident.Level < _player.Level - 8) continue;

                List<Point> points = region.Region.PointList;

                if (points == null || points.Count == 0) continue;

                candidates.AddRange(points);
            }

            if (candidates.Count == 0)
            {
                BotLog.Debug(Profile.Seq, string.Format("{0} 上没有等级合适的刷怪区（当前 {1} 级，可打上限 {2}）",
                    map.Info.Description, _player.Level, _player.Level + 4));

                return false;
            }

            Point origin = _player.CurrentLocation;

            // 排除脚边 8 格以内的点：那样的「到达刷怪点」意味着原地转一圈又回来，
            // 表现就是「到达 → 没怪 → 再到达同一个点」死循环，人也不动。
            List<Point> pool = candidates
                .Where(x => BotNavigator.Distance(origin, x) >= 8)
                .ToList();

            if (pool.Count == 0) pool = candidates;

            List<Point> nearest = pool
                .OrderBy(x => BotNavigator.Distance(origin, x))
                .Take(40)
                .OrderBy(x => SEnvir.Random.Next())
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
