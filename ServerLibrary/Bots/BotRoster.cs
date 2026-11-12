using Library;
using Library.SystemModels;
using Server.DBModels;
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

        /// <summary>是否在线（<see cref="Phase"/> 是给人看的文字，判断逻辑一律用这个布尔值）。</summary>
        public bool IsOnline { get; init; }

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

        #region P5-1 数据补齐（照片里要显示、原来拿不到的字段）

        /// <summary>当前 HP 绝对值（照片里的 <c>619/650</c> 前半段）。</summary>
        public int CurrentHealth { get; init; }

        /// <summary>最大 HP（<c>Stats[Stat.Health]</c>，随装备/等级变化，不是常量）。</summary>
        public int MaxHealth { get; init; }

        /// <summary>当前 MP 绝对值。</summary>
        public int CurrentMana { get; init; }

        /// <summary>最大 MP。</summary>
        public int MaxMana { get; init; }

        /// <summary>已背负重（<c>PlayerObject.BagWeight</c>）。</summary>
        public int BagWeight { get; init; }

        /// <summary>负重上限（<c>Stats[Stat.BagWeight]</c>）。</summary>
        public int MaxBagWeight { get; init; }

        /// <summary>背包已用格数。</summary>
        public int UsedSlots { get; init; }

        /// <summary>背包总格数（<c>Inventory.Length</c>）。</summary>
        public int SlotCount { get; init; }

        /// <summary>负重百分比（0~100+，超重会 &gt;100）。</summary>
        public int BagWeightPercent => MaxBagWeight > 0 ? (int)Math.Min(int.MaxValue, BagWeight * 100L / MaxBagWeight) : 0;

        /// <summary>全身装备里最差的那件还剩多少持久（%）。<c>-1</c> = 身上没装备。</summary>
        public int WorstDurabilityPercent { get; init; } = -1;

        /// <summary>最差持久那件装备的槽位名（如 <c>Helmet</c>），配合上面那个百分比显示。</summary>
        public string WorstDurabilitySlot { get; init; } = string.Empty;

        /// <summary>最近一次成功放出的攻击魔法（战士为空 —— 战士没有攻击魔法，靠平砍）。</summary>
        public string LastMagic { get; init; } = string.Empty;

        #endregion

        #region P4-1 经济观测

        /// <summary>本次在线期间赚到的金币（只累加正向差额，买药/修理不算负产出）。</summary>
        public long GoldEarned { get; init; }

        /// <summary>金币/小时（按本次在线时长折算；在线不足 60 秒时按 60 秒算，避免开头几分钟数字乱跳）。</summary>
        public long GoldPerHour { get; init; }

        #endregion
    }

    /// <summary>
    /// 假人整体总览（P5-1，给后台 6 张 KPI 卡用）。
    /// <para>
    /// 全部从 <see cref="BotSnapshot"/> 数组算出来，**不额外读 <c>PlayerObject</c>** ——
    /// 页面每 2 秒刷新一次，聚合逻辑放在游戏线程的快照重建里做一次，
    /// Blazor 线程只读这份现成的值（跨线程只读，安全）。
    /// </para>
    /// </summary>
    public sealed class BotTotals
    {
        /// <summary>这份数据是什么时候采的（页面右上角「更新 12:00:00」）。</summary>
        public DateTime SnapshotAt { get; init; }

        /// <summary>在线假人数。</summary>
        public int Online { get; init; }

        /// <summary>调度器里的假人总数（含离线）。</summary>
        public int Total { get; init; }

        /// <summary>真人在线数（水位公式的另一半，页面上要能解释「为什么只上了 12 个」）。</summary>
        public int RealPlayers { get; init; }

        /// <summary>平均等级（按**在线**假人算，离线的不参与，否则刚撤下来几个就会拉低均值）。</summary>
        public double AverageLevel { get; init; }

        public int TotalKills { get; init; }

        public int TotalDeaths { get; init; }

        /// <summary>存活率（%）= <c>Kills / (Kills + Deaths)</c>；<c>-1</c> = 还没样本，页面显示 <c>—</c>。</summary>
        public int SurvivalRate { get; init; } = -1;

        /// <summary>负重 ≥100% 的人数（超重，捡不了东西）。</summary>
        public int Overweight { get; init; }

        /// <summary>负重 85%~100% 的人数（临界，快要捡不了）。</summary>
        public int NearOverweight { get; init; }

        /// <summary>全体金币存量。</summary>
        public long TotalGold { get; init; }

        /// <summary>本次在线期间赚到的金币合计（P4-1）。</summary>
        public long GoldEarned { get; init; }

        /// <summary>金币产出速度合计（金币/小时，P4-1 用来判断经济有没有被灌水）。</summary>
        public long GoldPerHour { get; init; }
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
        private static BotTotals _totals = new BotTotals();
        private static DateTime _nextOverviewLogTime = DateTime.MinValue;

        private static DateTime _nextEvaluateTime = DateTime.MinValue;

        /// <summary>冷启动暖机中：本次启动还没铺满过，放人按 <see cref="BotOptions.StartupRampSeconds"/> 间隔。</summary>
        private static bool _warmup;
        private static DateTime _nextSnapshotTime = DateTime.MinValue;
        private static bool _started;

        public static bool Started => _started;
        public static int SessionCount => Sessions.Count;
        public static int OnlineCount => Sessions.Count(x => x.IsOnline);

        /// <summary>全部 Session 的只读视图（社交 / 后台聚合用；不要在外部改它）。</summary>
        public static IReadOnlyList<BotSession> AllSessions => Sessions;

        /// <summary>
        /// 最近一次快照时刻的真人在线数。后台页面直接读这个值，
        /// 免得每次刷新都要往游戏线程排一次任务（页面首屏也拿不到异步结果）。
        /// </summary>
        public static int RealPlayerCount { get; private set; }

        /// <summary>
        /// 现在是否处于「夜间时段」（P2-6 作息）。
        /// <para>
        /// 支持跨零点：<c>Min=22, Max=7</c> 时 23 点与 3 点都算夜间。
        /// 两个值相等视为「不启用夜间」，返回 false。
        /// </para>
        /// </summary>
        public static bool IsNightHour(DateTime now)
        {
            int min = Config.BotNightMinHour;
            int max = Config.BotNightMaxHour;

            if (min == max) return false;

            int hour = now.Hour;

            return min < max ? hour >= min && hour < max : hour >= min || hour < max;
        }

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

            // 社交（行会 / 组队 / 交易 / PK）：内部有 20 秒节流，每帧调也只是一次时间比较。
            BotSocial.Process();

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

        /// <summary>
        /// 按当前配置补齐 Session 池（只在需要扩容时动，缩容不动 —— 腾出来的序号留着复用）。
        /// <para>
        /// 为什么要按配置而不是按在线数：<see cref="Evaluate"/> 用 <c>Sessions.Count</c> 当可用上限，
        /// 池子建小了，配置写 30 也只会到 10。
        /// </para>
        /// </summary>
        private static void EnsureCapacity(BotOptions options)
        {
            int capacity = Math.Max(options.MaxOnline, options.TargetOnline);
            capacity = Math.Max(0, Math.Min(capacity, 200));

            while (Sessions.Count < capacity)
                Sessions.Add(new BotSession(BotProfile.Create(Sessions.Count + 1)));
        }

        /// <summary>按水位决定这一轮拉起几个、撤下几个（每轮最多变 MaxChangePerTick 个）。返回实际变化数量。</summary>
        private static int Evaluate(BotOptions options)
        {
            int realPlayers = SEnvir.Connections.Count(x => !x.IsBot && x.Stage == GameStage.Game);

            // 热扩容：后台把 BotMaxOnline / BotTargetOnline 改大之后，下一轮就把缺的 Session 补出来。
            //
            // 原先只在 Start() 里按当时的 MaxOnline 建一次 —— 改了配置也上不去，
            // 现象是「配置明明写的 30，实际永远只有 10 个在线」，看起来像水位调度坏了。
            // D5 的阶梯压测（10 → 20 → 30）正是靠热改规模来做，每次都要重启游戏服就废了。
            EnsureCapacity(options);

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

                // 作息（P2-6）：夜里目标人数按比例缩水，白天恢复。
                // 缩水不是"整点集体消失"—— 下线走 MaxChangePerTick（默认 1 个/轮），
                // 30 人减到 15 人是十五分钟里一个一个走的，跟真人下线一个节奏。
                if (options.NightEnabled && IsNightHour(SEnvir.Now))
                    target = Math.Max(options.MinOnline, target * options.NightScalePercent / 100);

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
            BotTotals totals = BuildTotals(items);

            lock (SnapshotSync)
            {
                _snapshot = items;
                _totals = totals;
            }

            LogTotals(totals);
        }

        /// <summary>
        /// 每 5 分钟往 Logs.txt 打一行总览（Info 级，生产档 <c>BotLogLevel=2</c> 看不到，
        /// 调成 0/1 才输出）。用途：后台页面打不开时，靠日志也能判断「经济有没有被灌水」。
        /// </summary>
        private static void LogTotals(BotTotals overview)
        {
            if (Config.BotLogLevel > 1) return;
            if (SEnvir.Now < _nextOverviewLogTime) return;

            _nextOverviewLogTime = SEnvir.Now.AddMinutes(5);

            BotLog.System(string.Format(
                "总览：在线 {0}/{1}（真人 {2}），平均 {3:0.#} 级，击杀 {4} / 复活 {5}，存活 {6}，超重 {7}/{8}，金币 {9:N0}（本次赚 {10:N0}，{11:N0}/小时）",
                overview.Online, overview.Total, overview.RealPlayers, overview.AverageLevel,
                overview.TotalKills, overview.TotalDeaths,
                overview.SurvivalRate >= 0 ? overview.SurvivalRate + "%" : "—",
                overview.Overweight, overview.NearOverweight,
                overview.TotalGold, overview.GoldEarned, overview.GoldPerHour));
        }

        /// <summary>
        /// 从快照数组算总览。纯函数式，不碰 <c>PlayerObject</c> —— 所以可以单独测。
        /// </summary>
        public static BotTotals BuildTotals(IReadOnlyList<BotSnapshot> items)
        {
            if (items == null || items.Count == 0)
            {
                return new BotTotals
                {
                    SnapshotAt = Library.Time.LocalNow,
                    RealPlayers = RealPlayerCount,
                };
            }

            int online = 0, kills = 0, deaths = 0, overweight = 0, nearOverweight = 0;
            long totalGold = 0, goldEarned = 0, goldPerHour = 0;
            double levelSum = 0;

            foreach (BotSnapshot bot in items)
            {
                // 离线的不参与等级/存活率统计：它们的数据是「上次在线时的残影」。
                if (!bot.IsOnline) continue;

                online++;
                kills += bot.Kills;
                deaths += bot.Deaths;
                totalGold += bot.Gold;
                goldEarned += bot.GoldEarned;
                goldPerHour += bot.GoldPerHour;
                levelSum += bot.Level;

                if (bot.MaxBagWeight > 0)
                {
                    if (bot.BagWeightPercent >= 100) overweight++;
                    else if (bot.BagWeightPercent >= 85) nearOverweight++;
                }
            }

            int samples = kills + deaths;

            return new BotTotals
            {
                SnapshotAt = Library.Time.LocalNow,
                Online = online,
                Total = items.Count,
                RealPlayers = RealPlayerCount,
                AverageLevel = online > 0 ? Math.Round(levelSum / online, 1) : 0,
                TotalKills = kills,
                TotalDeaths = deaths,
                SurvivalRate = samples > 0 ? kills * 100 / samples : -1,
                Overweight = overweight,
                NearOverweight = nearOverweight,
                TotalGold = totalGold,
                GoldEarned = goldEarned,
                GoldPerHour = goldPerHour,
            };
        }

        private static BotSnapshot ToSnapshot(BotSession session)
        {
            PlayerObjectHolder holder = PlayerObjectHolder.From(session);

            // P4-1：每轮快照采一次金币（约 2 秒一次），只累加正向差额。
            // **只在真的拿到 PlayerObject 时采** —— 离线/登录中的 session 取不到角色，
            // Gold 会退化成 0；如果照样采，基线就被写成 0，等它上线时整笔余额会被当成「赚到的」
            // （实测 8 个假人 5 分钟「赚」了 306 万，比全服金币存量还多）。
            if (holder.HasPlayer)
                session.SampleGold(holder.Gold);

            long onlineSeconds = session.IsOnline && session.OnlineSince != DateTime.MinValue
                ? (long)Math.Max(0, (SEnvir.Now - session.OnlineSince).TotalSeconds)
                : 0;

            long goldPerHour = onlineSeconds > 0
                ? session.GoldEarned * 3600L / Math.Max(60L, onlineSeconds)
                : 0;

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
                CurrentHealth = holder.CurrentHealth,
                MaxHealth = holder.MaxHealth,
                CurrentMana = holder.CurrentMana,
                MaxMana = holder.MaxMana,
                BagWeight = holder.BagWeight,
                MaxBagWeight = holder.MaxBagWeight,
                UsedSlots = holder.UsedSlots,
                SlotCount = holder.SlotCount,
                WorstDurabilityPercent = holder.WorstDurabilityPercent,
                WorstDurabilitySlot = holder.WorstDurabilitySlot,
                LastMagic = session.Brain?.LastMagic ?? string.Empty,
                GoldEarned = session.GoldEarned,
                GoldPerHour = goldPerHour,
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
                IsOnline = session.IsOnline,
                Enabled = session.Enabled,
            };
        }

        /// <summary>后台读取快照（任意线程安全）。</summary>
        public static BotSnapshot[] Snapshot()
        {
            lock (SnapshotSync)
                return _snapshot;
        }

        /// <summary>后台读取总览（任意线程安全，和 <see cref="Snapshot"/> 是同一批数据算出来的）。</summary>
        public static BotTotals Totals()
        {
            lock (SnapshotSync)
                return _totals;
        }

        /// <summary>
        /// 读某个假人的角色详情（P5-3）。
        /// <para>
        /// <b>必须在游戏线程调用</b>：它直接读 <c>PlayerObject.Equipment</c>，
        /// 跨线程读会和主循环抢同一批对象（<c>BotAdminService.GetDetailAsync</c> 已经用
        /// <c>SEnvir.InvokeOnGameThreadAsync</c> 包好了，页面别直接调这个）。
        /// </para>
        /// <para>找不到这个 Seq、或它当前不在线（还没拿到 <c>PlayerObject</c>）时返回 <c>null</c>。</para>
        /// </summary>
        public static BotDetail GetDetail(int seq)
        {
            BotSession session = Sessions.FirstOrDefault(x => x.Profile.Seq == seq);

            if (session == null) return null;

            BotSnapshot row = Snapshot().FirstOrDefault(x => x.Seq == seq);

            return BotDetail.Build(session, row);
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
            /// <summary>是否真的拿到了 <see cref="PlayerObject"/>（false = 离线/还在登录，其它字段都是退化值）。</summary>
            public bool HasPlayer { get; init; }

            public int Level { get; init; }
            public int ExpPercent { get; init; }
            public string MapName { get; init; }
            public int MapIndex { get; init; }
            public int X { get; init; }
            public int Y { get; init; }
            public int HealthPercent { get; init; }
            public int ManaPercent { get; init; }
            public int CurrentHealth { get; init; }
            public int MaxHealth { get; init; }
            public int CurrentMana { get; init; }
            public int MaxMana { get; init; }
            public int BagWeight { get; init; }
            public int MaxBagWeight { get; init; }
            public int UsedSlots { get; init; }
            public int SlotCount { get; init; }
            public int WorstDurabilityPercent { get; init; }
            public string WorstDurabilitySlot { get; init; }
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
                        WorstDurabilityPercent = -1,
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
                    HasPlayer = true,
                    Level = player.Level,
                    ExpPercent = expPercent,
                    MapName = player.CurrentMap?.Info?.Description ?? "-",
                    MapIndex = player.CurrentMap?.Info?.Index ?? 0,
                    X = player.CurrentLocation.X,
                    Y = player.CurrentLocation.Y,
                    HealthPercent = BotPerception.HealthPercent(player),
                    ManaPercent = manaPercent,
                    CurrentHealth = player.CurrentHP,
                    MaxHealth = player.Stats[Stat.Health],
                    CurrentMana = player.CurrentMP,
                    MaxMana = maxMana,
                    BagWeight = player.BagWeight,
                    MaxBagWeight = player.Stats[Stat.BagWeight],
                    UsedSlots = BotPerception.UsedInventorySlots(player),
                    SlotCount = player.Inventory?.Length ?? 0,
                    WorstDurabilityPercent = WorstDurability(player, out string worstSlot),
                    WorstDurabilitySlot = worstSlot,
                    Gold = gold,
                    MagicCount = BotSkills.KnownCount(player),
                    TownScrollCount = BotActions.CountTownScroll(player),
                    RedPotions = BotActions.CountPotion(player, Stat.Health),
                    BluePotions = BotActions.CountPotion(player, Stat.Mana),
                };
            }

            /// <summary>
            /// 全身装备里最差的那件还剩百分之多少持久，顺带带出槽位名。
            /// <para>
            /// 只统计**有耐久概念**的装备（<c>Info.Durability &gt; 0</c>）：戒指/项链这类
            /// 本来就不掉持久的，按 100% 算会把「武器快碎了」这种信息淹掉。
            /// 身上一件装备都没有时返回 <c>-1</c>，让页面显示 <c>—</c> 而不是 0%（0% 看起来像全碎了）。
            /// </para>
            /// </summary>
            private static int WorstDurability(PlayerObject player, out string slot)
            {
                slot = string.Empty;

                UserItem[] equipment = player.Equipment;

                if (equipment == null) return -1;

                int worst = -1;

                for (int i = 0; i < equipment.Length; i++)
                {
                    UserItem item = equipment[i];

                    if (item?.Info == null) continue;
                    if (item.Info.Durability <= 0) continue;

                    int max = item.MaxDurability > 0 ? item.MaxDurability : item.Info.Durability;

                    if (max <= 0) continue;

                    int percent = (int)Math.Min(100L, item.CurrentDurability * 100L / max);

                    if (worst >= 0 && percent >= worst) continue;

                    worst = percent;
                    slot = ((EquipmentSlot)i).ToString();
                }

                return worst;
            }
        }
    }
}
