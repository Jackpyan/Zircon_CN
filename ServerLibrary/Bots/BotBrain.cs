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
            ItemObject item = BotPerception.FindNearestLoot(_player, LootRadius);

            if (item == null)
            {
                EnterState(BotState.Idle, "没东西可捡了", TimeSpan.FromSeconds(2));
                return;
            }

            int distance = BotNavigator.Distance(_player.CurrentLocation, item.CurrentLocation);

            if (distance <= 1)
            {
                BotActions.TryPickUp(_player, item);
                LastDetail = string.Format("捡 {0}", item.Item?.Info?.ItemName ?? "物品");
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

            // 5) 药见底就回城补，别等喝完才想起（0 = 关闭自动补给）
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
