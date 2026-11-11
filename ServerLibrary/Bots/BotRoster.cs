using Library;
using Library.SystemModels;
using Server.Envir;
using Server.Models;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace Server.Bots
{
    /// <summary>后台展示用的假人快照（跨线程只读拷贝）。</summary>
    public sealed class BotSnapshot
    {
        public int Seq { get; init; }
        public string CharacterName { get; init; } = string.Empty;
        public string ClassName { get; init; } = string.Empty;
        public int Level { get; init; }
        public int ExpPercent { get; init; }
        public string Phase { get; init; } = string.Empty;
        public string State { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;
        public string MapName { get; init; } = string.Empty;
        public int MapIndex { get; init; }
        public int X { get; init; }
        public int Y { get; init; }
        public int HealthPercent { get; init; }
        public int ManaPercent { get; init; }
        public int Kills { get; init; }
        public int Deaths { get; init; }
        public long Gold { get; init; }
        public int MagicCount { get; init; }
        public string OnlineFor { get; init; } = string.Empty;
        public string LastError { get; init; } = string.Empty;
        public bool Broken { get; init; }

        /// <summary>
        /// 本次在线期间成功补给的次数（D5 量化「断药能回城买」）。
        /// <para>只在真的买到东西时 +1，与跨重启的累计值分开看。</para>
        /// </summary>
        public int Restocks { get; init; }

        /// <summary>本次在线期间补给失败的次数。</summary>
        public int RestockFails { get; init; }

        /// <summary>本次在线期间的交易（卖货 / 修理）次数。</summary>
        public int Trades { get; init; }

        /// <summary>本次在线期间挖取的次数。</summary>
        public int Harvests { get; init; }

        /// <summary>本次在线期间使用回城卷的次数。</summary>
        public int TownScrollUses { get; init; }

        /// <summary>本次在线期间买到的回城卷张数。</summary>
        public int TownScrollBuys { get; init; }

        /// <summary>背包里的回城卷张数（存量）。</summary>
        public int TownScrollCount { get; init; }

        /// <summary>背包里的红药瓶数（存量）。</summary>
        public int RedPotions { get; init; }

        /// <summary>背包里的蓝药瓶数（存量）。</summary>
        public int BluePotions { get; init; }

        /// <summary>后台单人开关状态（false = 被运营关掉，调度器不再拉起）。</summary>
        public bool Enabled { get; init; } = true;
    }

    /// <summary>技能自检结果（D4 验收用，见 <see cref="BotRoster.SkillCheck"/>）。</summary>
    public sealed record BotSkillCheckResult(
        bool Ok,
        int LevelBefore,
        int LevelAfter,
        int MagicBefore,
        int MagicAfter,
        int KeyMagicFound,
        string Detail);

    /// <summary>
    /// 假人调度器：按"水位"决定上多少个，并推进每个假人的生命周期。
    /// <para>
    /// 水位公式：<c>目标假人数 = Clamp(BotTargetOnline - 真人在线数, BotMinOnline, BotMaxOnline)</c>。
    /// 真人多了就撤假人，真人少了就补 —— 目的是让在线人数看着稳定，而不是固定挂 30 个。
    /// </para>
    /// <para>所有方法都必须在游戏线程内调用（<see cref="Process"/> 由主循环每帧调用）。</para>
    /// </summary>
    public static class BotRoster
    {
        private static readonly List<BotSession> Sessions = new List<BotSession>();

        private static readonly object SnapshotSync = new object();
        private static BotSnapshot[] _snapshot = Array.Empty<BotSnapshot>();

        private static DateTime _nextEvaluateTime = DateTime.MinValue;

        /// <summary>冷启动暖机中：本次启动还没铺满过，放人按 <see cref="BotOptions.StartupRampSeconds"/> 间隔。</summary>
        private static bool _warmup;
        private static DateTime _nextSnapshotTime = DateTime.MinValue;
        private static bool _started;

        public static bool Started => _started;
        public static int SessionCount => Sessions.Count;
        public static int OnlineCount => Sessions.Count(x => x.IsOnline);

        /// <summary>
        /// 最近一次快照时刻的真人在线数。后台页面直接读这个值，
        /// 免得每次刷新都要往游戏线程排一次任务（页面首屏也拿不到异步结果）。
        /// </summary>
        public static int RealPlayerCount { get; private set; }

        /// <summary>启动调度。按配置建满序号 1..BotMaxOnline 的 Session，是否上线由水位决定。</summary>
        public static void Start()
        {
            if (_started) return;

            _started = true;

            // 数据目录（Bots/）：没有就建一个，后台能直接看到路径；文件本身缺失会回落内置池。
            BotData.EnsureDirectory();

            Sessions.Clear();

            BotOptions options = BotOptions.FromConfig();
            int capacity = Math.Max(options.MaxOnline, options.TargetOnline);
            capacity = Math.Max(0, Math.Min(capacity, 200));

            for (int seq = 1; seq <= capacity; seq++)
                Sessions.Add(new BotSession(BotProfile.Create(seq)));

            _nextEvaluateTime = SEnvir.Now;
            _nextSnapshotTime = SEnvir.Now;
            _warmup = true;

            BotLog.System(string.Format("调度器已启动：容量 {0} 个，BotEnabled={1}，目标在线 {2}，冷启动间隔 {3} 秒",
                capacity, options.Enabled, options.TargetOnline, options.StartupRampSeconds));
        }

        /// <summary>停服前调用：把所有在线假人正常下线（走 StopGame 存档）。</summary>
        public static void Stop()
        {
            if (!_started) return;

            int count = OnlineCount;

            foreach (BotSession session in Sessions)
            {
                session.RequestStop();

                try
                {
                    session.Connection?.Disconnect();
                }
                catch (Exception ex)
                {
                    BotLog.Warn(session.Profile.Seq, string.Format("停服下线异常：{0}", ex.Message));
                }
            }

            Sessions.Clear();
            _started = false;
            _warmup = false;

            lock (SnapshotSync)
                _snapshot = Array.Empty<BotSnapshot>();

            BotLog.System(string.Format("调度器已停止，下线 {0} 个假人", count));
        }

        /// <summary>主循环每帧调用。</summary>
        public static void Process()
        {
            if (!_started) return;

            BotOptions options = BotOptions.FromConfig();

            foreach (BotSession session in Sessions)
                session.Process(options);

            if (SEnvir.Now >= _nextEvaluateTime)
            {
                int changed = Evaluate(options);

                // 暖机阶段每放一个等 StartupRampSeconds（默认 45 秒），错开出生点抢怪；
                // 铺满之后立刻转回稳态间隔，掉线补水不用再等 45 秒。
                bool ramping = changed > 0 && _warmup;

                _nextEvaluateTime = SEnvir.Now.AddSeconds(ramping ? options.StartupRampSeconds : options.RosterIntervalSeconds);
            }

            if (SEnvir.Now >= _nextSnapshotTime)
            {
                _nextSnapshotTime = SEnvir.Now.AddSeconds(1);
                RebuildSnapshot();
            }

        }

        /// <summary>按水位决定这一轮拉起几个、撤下几个（每轮最多变 MaxChangePerTick 个）。返回实际变化数量。</summary>
        private static int Evaluate(BotOptions options)
        {
            int realPlayers = SEnvir.Connections.Count(x => !x.IsBot && x.Stage == GameStage.Game);

            int target;

            if (!options.Enabled)
            {
                // 总开关关闭 → 目标 0，在线假人逐个优雅下线（不是硬踢），
                // 每轮撤 MaxChangePerTick 个，避免一次性 30 个 StopGame 把主循环顶住。
                target = 0;
            }
            else
            {
                target = Math.Clamp(options.TargetOnline - realPlayers, options.MinOnline, options.MaxOnline);

                // 被后台单独关掉的序号不参与水位：可用 20 个就不能把目标定成 30，
                // 否则永远 online < target，冷启动暖机标志永远清不掉，掉线补水要白等 45 秒。
                int available = Sessions.Count(x => x.Enabled);

                if (target > available) target = available;
            }

            int online = OnlineCount;

            if (online < target)
            {
                int spawnCount = Math.Min(options.MaxChangePerTick, target - online);
                int spawned = TrySpawn(spawnCount, options);

                if (spawned > 0)
                    BotLog.System(string.Format("水位上调：真人 {0}，目标 {1}，当前 {2}，本轮拉起 {3}{4}",
                        realPlayers, target, online, spawned, _warmup ? "（冷启动暖机）" : ""));

                return spawned;
            }

            // 已经铺满（或真人上线把人挤下去），暖机结束：之后掉线补水按稳态间隔，不再等 45 秒。
            _warmup = false;

            if (online > target)
            {
                int stopCount = Math.Min(options.MaxChangePerTick, online - target);
                int stopped = TryDespawn(stopCount);

                if (stopped > 0)
                    BotLog.System(string.Format("水位下调：真人 {0}，目标 {1}，当前 {2}，本轮下线 {3}",
                        realPlayers, target, online, stopped));

                return stopped;
            }

            return 0;
        }

        private static int TrySpawn(int count, BotOptions options)
        {
            int spawned = 0;

            foreach (BotSession session in Sessions)
            {
                if (spawned >= count) break;

                // Offline 才拉；Broken 不自动重试（后台手动重置）；被运营关掉的也不拉。
                if (session.Phase != BotPhase.Offline) continue;
                if (!session.Enabled) continue;

                session.Begin(options);
                spawned++;
            }

            return spawned;
        }

        private static int TryDespawn(int count)
        {
            int stopped = 0;

            // 优先撤「最晚上线」的，让早期假人活得久一点，看起来更真。
            foreach (BotSession session in Sessions.OrderByDescending(x => x.OnlineSince))
            {
                if (stopped >= count) break;

                if (!session.IsOnline && session.Phase != BotPhase.Entering && session.Phase != BotPhase.Logging) continue;

                session.RequestStop();
                stopped++;
            }

            return stopped;
        }

        private static void RebuildSnapshot()
        {
            RealPlayerCount = SEnvir.Connections.Count(x => !x.IsBot && x.Stage == GameStage.Game);

            BotSnapshot[] items = Sessions.Select(ToSnapshot).ToArray();

            lock (SnapshotSync)
                _snapshot = items;
        }

        private static BotSnapshot ToSnapshot(BotSession session)
        {
            PlayerObjectHolder holder = PlayerObjectHolder.From(session);

            return new BotSnapshot
            {
                Seq = session.Profile.Seq,
                CharacterName = session.Character?.CharacterName ?? session.Profile.CharacterName,
                ClassName = session.Character?.Class.ToString() ?? string.Empty,
                Level = holder.Level,
                ExpPercent = holder.ExpPercent,
                Phase = PhaseName(session.Phase),
                State = session.Brain?.State.ToString() ?? "-",
                Detail = session.Brain?.LastDetail ?? string.Empty,
                MapName = holder.MapName,
                MapIndex = holder.MapIndex,
                X = holder.X,
                Y = holder.Y,
                HealthPercent = holder.HealthPercent,
                ManaPercent = holder.ManaPercent,
                Kills = session.Brain?.Kills ?? 0,
                Deaths = session.Brain?.Deaths ?? 0,
                Gold = holder.Gold,
                MagicCount = holder.MagicCount,
                Restocks = session.Brain?.RestockCount ?? 0,
                RestockFails = session.Brain?.RestockFailCount ?? 0,
                Trades = session.Brain?.TradeCount ?? 0,
                Harvests = session.Brain?.HarvestCount ?? 0,
                TownScrollUses = session.Brain?.TownScrollUseCount ?? 0,
                TownScrollBuys = session.Brain?.TownScrollBuyCount ?? 0,
                TownScrollCount = holder.TownScrollCount,
                RedPotions = holder.RedPotions,
                BluePotions = holder.BluePotions,
                OnlineFor = session.IsOnline ? FormatDuration(SEnvir.Now - session.OnlineSince) : string.Empty,
                LastError = session.LastError,
                Broken = session.Phase == BotPhase.Broken,
                Enabled = session.Enabled,
            };
        }

        /// <summary>后台读取快照（任意线程安全）。</summary>
        public static BotSnapshot[] Snapshot()
        {
            lock (SnapshotSync)
                return _snapshot;
        }

        /// <summary>后台「全部重置」：把 Broken / 卡住的都拉回可调度状态。</summary>
        public static void ResetAll()
        {
            foreach (BotSession session in Sessions)
                session.Reset();

            _nextEvaluateTime = SEnvir.Now;
        }

        /// <summary>后台「立即上线 N 个」（不受水位限制，便于测试）。</summary>
        public static int ForceSpawn(int count)
        {
            BotOptions options = BotOptions.FromConfig();

            return TrySpawn(Math.Max(0, count), options);
        }

        /// <summary>后台「立即下线 N 个」。</summary>
        public static int ForceDespawn(int count)
        {
            return TryDespawn(Math.Max(0, count));
        }

        #region 单人操作（后台调用，必须已投递到游戏线程）

        /// <summary>按序号找 Session。找不到返回 null（序号超出容量）。</summary>
        public static BotSession Find(int seq)
        {
            return Sessions.FirstOrDefault(x => x.Profile.Seq == seq);
        }

        /// <summary>单人开关。关掉即刻优雅下线，且之后不再被水位拉起。</summary>
        public static bool SetEnabled(int seq, bool enabled)
        {
            BotSession session = Find(seq);

            if (session == null) return false;

            session.SetEnabled(enabled);

            // 打开时要让调度器下一轮立刻看到，不然等水位周期（最长 45 秒冷却）才补。
            if (enabled) _nextEvaluateTime = SEnvir.Now;

            return true;
        }

        /// <summary>踢下线（不改变开关，调度器后续还会按水位把它拉回来）。</summary>
        public static bool Kick(int seq)
        {
            BotSession session = Find(seq);

            if (session == null || session.Phase == BotPhase.Offline) return false;

            session.RequestStop();

            return true;
        }

        /// <summary>传送到指定地图坐标。假人必须在线。</summary>
        public static bool Teleport(int seq, int mapIndex, int x, int y)
        {
            BotSession session = Find(seq);

            if (session == null) return false;

            return session.Teleport(mapIndex, new System.Drawing.Point(x, y));
        }

        /// <summary>立即补给（清掉 60 秒冷却，下一次决策就回城）。</summary>
        public static bool RequestRestock(int seq)
        {
            BotSession session = Find(seq);

            return session != null && session.RequestRestock();
        }

        /// <summary>把某个序号重置成可调度状态（含 Broken）。</summary>
        public static bool Reset(int seq)
        {
            BotSession session = Find(seq);

            if (session == null) return false;

            session.Reset();

            _nextEvaluateTime = SEnvir.Now;

            return true;
        }

        #endregion

        #region 自检（D4 技能补齐的验收手段）

        /// <summary>
        /// 调试用：把在线假人直接加到指定等级，跑一次 <see cref="BotSkills.Sync"/>，回报前后差异。
        /// <para>
        /// 为什么要作弊加经验：真实练到 7 级（法师火球）/13 级（道士火符）要几十分钟，
        /// 而「升级时是否补到技能、补到的技能能不能放出来」必须当轮验证，不能靠等。
        /// 只对假人生效，且不写库外的任何东西；D5 验收后可以连按钮一起删。
        /// </para>
        /// </summary>
        public static BotSkillCheckResult SkillCheck(int seq, int targetLevel = 0)
        {
            BotSession session = Find(seq);
            PlayerObject player = session?.Connection?.Player;

            if (player == null) return new BotSkillCheckResult(false, 0, 0, 0, 0, 0, "假人不在线");

            // 服务器等级上限（Config.MaxLevel，本服 = 10）之外不可能升级，目标等级要夹住。
            if (targetLevel <= 0 || targetLevel > Config.MaxLevel) targetLevel = Config.MaxLevel;

            int levelBefore = player.Level;
            int magicBefore = BotSkills.KnownCount(player);

            int guard = 0;

            while (player.Level < targetLevel && guard++ < 100)
            {
                decimal need = player.MaxExperience - player.Experience;

                if (need <= 0) need = 1;

                player.GainExperience(need, false, int.MaxValue, rateEffected: false);
            }

            BotSkills.Sync(player);

            int magicAfter = BotSkills.KnownCount(player);

            // 关键技能：法师看火球术（7 级），道士看灵魂火符（13 级）。没有就说明补技能没生效。
            MagicType key = player.Class == MirClass.Taoist ? MagicType.ExplosiveTalisman : MagicType.FireBall;

            bool hasKey = player.Class != MirClass.Warrior && player.GetMagic(key, out MagicObject _);

            string detail = string.Format("等级 {0}→{1}，技能 {2}→{3}，{4} {5}",
                levelBefore, player.Level, magicBefore, magicAfter, key, hasKey ? "已学" : "未学");

            BotLog.Info(seq, "技能自检：" + detail);

            return new BotSkillCheckResult(true, levelBefore, player.Level, magicBefore, magicAfter, hasKey ? 1 : 0, detail);
        }

        #endregion

        private static string PhaseName(BotPhase phase)
        {
            switch (phase)
            {
                case BotPhase.Offline: return "离线";
                case BotPhase.Connecting: return "建连接";
                case BotPhase.WaitJoin: return "等待入列";
                case BotPhase.Logging: return "登录中";
                case BotPhase.Entering: return "进图中";
                case BotPhase.Online: return "在线";
                case BotPhase.Stopping: return "下线中";
                case BotPhase.Broken: return "故障";
                default: return phase.ToString();
            }
        }

        private static string FormatDuration(TimeSpan span)
        {
            if (span.TotalSeconds < 0) return "-";

            if (span.TotalHours >= 1) return string.Format("{0:0}h{1:00}m", span.TotalHours, span.Minutes);
            if (span.TotalMinutes >= 1) return string.Format("{0:0}m{1:00}s", span.TotalMinutes, span.Seconds);

            return string.Format("{0:0}s", span.TotalSeconds);
        }

        /// <summary>
        /// 快照要跨线程读 <see cref="PlayerObject"/> 的字段，直接读会有撕裂风险。
        /// 这里在游戏线程内先把要用的值拷出来（快照本身就是游戏线程重建的，所以这层只是可读性包装）。
        /// </summary>
        private readonly struct PlayerObjectHolder
        {
            public int Level { get; init; }
            public int ExpPercent { get; init; }
            public string MapName { get; init; }
            public int MapIndex { get; init; }
            public int X { get; init; }
            public int Y { get; init; }
            public int HealthPercent { get; init; }
            public int ManaPercent { get; init; }
            public long Gold { get; init; }
            public int MagicCount { get; init; }
            public int TownScrollCount { get; init; }
            public int RedPotions { get; init; }
            public int BluePotions { get; init; }

            public static PlayerObjectHolder From(BotSession session)
            {
                PlayerObject player = session.Connection?.Player;

                if (player == null)
                {
                    return new PlayerObjectHolder
                    {
                        Level = session.Character?.Level ?? 0,
                        MapName = "-",
                        HealthPercent = 0,
                    };
                }

                decimal maxExp = player.MaxExperience;
                int expPercent = maxExp > 0
                    ? (int)Math.Min(100m, Math.Floor(player.Experience * 100m / maxExp))
                    : 0;

                int maxMana = player.Stats[Stat.Mana];
                int manaPercent = maxMana > 0 ? player.CurrentMP * 100 / maxMana : 0;

                long gold = 0;

                var currency = player.Character?.Gold;

                if (currency != null) gold = currency.Amount;

                return new PlayerObjectHolder
                {
                    Level = player.Level,
                    ExpPercent = expPercent,
                    MapName = player.CurrentMap?.Info?.Description ?? "-",
                    MapIndex = player.CurrentMap?.Info?.Index ?? 0,
                    X = player.CurrentLocation.X,
                    Y = player.CurrentLocation.Y,
                    HealthPercent = BotPerception.HealthPercent(player),
                    ManaPercent = manaPercent,
                    Gold = gold,
                    MagicCount = BotSkills.KnownCount(player),
                    TownScrollCount = BotActions.CountTownScroll(player),
                    RedPotions = BotActions.CountPotion(player, Stat.Health),
                    BluePotions = BotActions.CountPotion(player, Stat.Mana),
                };
            }
        }
    }
}
