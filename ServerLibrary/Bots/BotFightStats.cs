using Server.Envir;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Server.Bots
{
    /// <summary>
    /// 战斗效率统计（W12，2026-11-22）。
    /// <para>
    /// <b>为什么需要</b>：复核发现「Fight 占比 40.8% 正常，但每战斗分钟的击杀从 1.38 掉到 0.81」——
    /// 打的时间没少，杀得少了。原来的指标只有「击杀/小时」和「状态占比」，
    /// 分不清是「打不动（怪太硬）」还是「空转（打不着/走位/施法失败）」。
    /// </para>
    /// <para>
    /// 这里按 10 分钟窗口统计：**出手次数 / 击杀数 / 战斗帧数**，
    /// 输出「每次出手的击杀率」和「每帧出手率」——前者低=打不动，后者低=空转。
    /// </para>
    /// </summary>
    public static class BotFightStats
    {
        private static readonly object Sync = new object();

        private static DateTime _windowStart = DateTime.MinValue;

        private static long _attacks;

        private static long _kills;

        private static long _ticks;

        private static long _gapFast;

        private static long _gapMid;

        private static long _gapSlow;

        private static long _gapVerySlow;

        private static long _gapStuck;

        private static double _gapSum;

        private static long _gapCount;

        /// <summary>窗口长度（分钟）。</summary>
        public const int WindowMinutes = 10;

        /// <summary>
        /// 一次成功出手（技能命中 / 平砍发出）。
        /// <para>
        /// W14（2026-11-22）：带上**距上一次出手的秒数**。只看「每帧出手率」分不清
        /// 是「冷却在拖」还是「走位打断」，攻击间隔分布能直接看出来：
        /// 集中在 1.5 秒 = 冷却正常、瓶颈在走位；散在 3~8 秒 = 冷却/处理频率在拖。
        /// </para>
        /// </summary>
        public static void OnAttack(double gapSeconds)
        {
            lock (Sync)
            {
                _attacks++;

                if (gapSeconds <= 0) return;

                _gapSum += gapSeconds;
                _gapCount++;

                if (gapSeconds < 1.5) _gapFast++;
                else if (gapSeconds < 3) _gapMid++;
                else if (gapSeconds < 6) _gapSlow++;
                else if (gapSeconds < 12) _gapVerySlow++;
                else _gapStuck++;
            }
        }

        /// <summary>一次击杀。</summary>
        public static void OnKill()
        {
            lock (Sync) _kills++;
        }

        /// <summary>一帧战斗处理（Fight 态每轮调用一次）。</summary>
        public static void OnFightTick()
        {
            lock (Sync) _ticks++;
        }

        /// <summary>
        /// 由 <see cref="BotRoster"/> 每轮调用：到点就把上一窗口汇总成一行系统日志。
        /// <para>
        /// W13（2026-11-22）：额外采样「Fight 态但没出手」的假人在干什么，
        /// 按 <c>LastDetail</c> 归类输出占比 —— 区分「等冷却」和「一直在走」，
        /// 这两种情况的修法完全不同。
        /// </para>
        /// </summary>
        public static void Process(IReadOnlyList<BotSession> sessions)
        {
            lock (Sync)
            {
                if (_windowStart == DateTime.MinValue)
                {
                    _windowStart = SEnvir.Now;
                    return;
                }

                if (SEnvir.Now - _windowStart < TimeSpan.FromMinutes(WindowMinutes)) return;

                long attacks = _attacks;
                long kills = _kills;
                long ticks = _ticks;

                long gapFast = _gapFast;
                long gapMid = _gapMid;
                long gapSlow = _gapSlow;
                long gapVerySlow = _gapVerySlow;
                long gapStuck = _gapStuck;
                double gapSum = _gapSum;
                long gapCount = _gapCount;

                _attacks = 0;
                _kills = 0;
                _ticks = 0;
                _gapFast = 0;
                _gapMid = 0;
                _gapSlow = 0;
                _gapVerySlow = 0;
                _gapStuck = 0;
                _gapSum = 0;
                _gapCount = 0;
                _windowStart = SEnvir.Now;

                // 没有战斗数据就闭嘴（夜里 0 假人时不刷空行）。
                if (ticks == 0) return;

                double killsPerAttack = attacks > 0 ? (double)kills / attacks : 0;
                double attackPerTick = ticks > 0 ? (double)attacks / ticks : 0;

                BotLog.System(string.Format(
                    "战斗效率[{0}分钟]：出手 {1} 次、击杀 {2} 只、战斗帧 {3}｜每出手击杀 {4:0.00}、每帧出手率 {5:0.00}%",
                    WindowMinutes, attacks, kills, ticks, killsPerAttack, attackPerTick * 100));

                if (gapCount > 0)
                {
                    BotLog.System(string.Format(
                        "攻击间隔：<1.5s {0:0}% / 1.5~3s {1:0}% / 3~6s {2:0}% / 6~12s {3:0}% / >12s {4:0}%｜均值 {5:0.0}s（{6} 次）",
                        gapFast * 100.0 / gapCount, gapMid * 100.0 / gapCount, gapSlow * 100.0 / gapCount,
                        gapVerySlow * 100.0 / gapCount, gapStuck * 100.0 / gapCount,
                        gapSum / gapCount, gapCount));
                }

                string reason = DescribeIdleReasons(sessions);

                if (reason.Length > 0)
                    BotLog.System("战斗效率｜未出手原因：" + reason);
            }
        }

        /// <summary>
        /// 采样当前在 Fight 态的假人，按 LastDetail 归类（W13）。
        /// <para>纯采样：只在窗口收尾时跑一次，没有每帧开销。</para>
        /// </summary>
        private static string DescribeIdleReasons(IReadOnlyList<BotSession> sessions)
        {
            if (sessions == null || sessions.Count == 0) return string.Empty;

            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (BotSession session in sessions)
            {
                if (session?.Brain == null) continue;
                if (session.Brain.State != BotState.Fight) continue;

                string category = Categorize(session.Brain.LastDetail);

                counts[category] = counts.TryGetValue(category, out int current) ? current + 1 : 1;
            }

            if (counts.Count == 0) return string.Empty;

            int total = counts.Values.Sum();

            IEnumerable<string> parts = counts
                .OrderByDescending(x => x.Value)
                .Take(5)
                .Select(x => string.Format("{0} {1:0}%", x.Key, x.Value * 100.0 / total));

            return string.Join(" / ", parts) + string.Format("（样本 {0} 个战斗中的假人）", total);
        }

        /// <summary>把 LastDetail 归到 5 类里；关键词按「越具体越靠前」匹配。</summary>
        private static string Categorize(string detail)
        {
            if (string.IsNullOrEmpty(detail)) return "无动作";

            if (detail.Contains("冷却")) return "等冷却";
            if (detail.Contains("施法失败")) return "施法失败";
            if (detail.Contains("放风筝") || detail.Contains("后退")) return "放风筝后退";
            if (detail.Contains("脱离")) return "脱离/逃生";
            if (detail.Contains("砍 ") || detail.Contains("打 ")) return "已出手";

            return "走位/接近";
        }
    }
}
