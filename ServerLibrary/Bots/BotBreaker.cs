using Library;
using Server.Envir;
using System;
using System.Threading;

namespace Server.Bots
{
    /// <summary>
    /// 假人全局熔断（P4-5）。
    /// <para>
    /// 与「单假人熔断」（<c>Config.BotMaxFailures</c>，某个假人连续失败 3 次 → 它自己 <c>Broken</c> 下线）
    /// 解决的是完全不同的问题：<b>单假人熔断看不见「每个假人都在正常工作，只是 30 个一起正常工作」这种过载。</b>
    /// 而假人不会因为服务器卡就少干点活，它们只会因为卡而超时、失败、重试，然后更卡 —— 这是个正反馈。
    /// 全局熔断就是给这个循环准备的软刹车。
    /// </para>
    /// <para>
    /// <b>动作只做一件事</b>：把水位上限临时压到当前在线数，<b>只减不增</b>。
    /// 不踢已经在线的假人 —— 玩家看到假人成批消失，比看到卡顿更假；
    /// 而水位是自然衰减的（假人下线后不再补），半小时内自己就降下来了。
    /// </para>
    /// <para>
    /// <b>这个上限只在内存里，绝不写回 <c>Server.ini</c>。</b>
    /// 走 <c>ConfigurationService</c> 会把运维配的 <c>BotTargetOnline</c> 永久改坏，
    /// 重启后还原不回来 —— 那是事故，不是熔断。
    /// </para>
    /// <para>
    /// <b>判据是「假人段占单核 CPU 的百分比」（绝对量口径），不是毫秒。</b>
    /// 2026-11-14 改动点 5：原口径「占主循环百分比」会被自旋帧率稀释（帧率一变，
    /// 同一真实开销算出的占比就变，阈值失真）；现口径 <c>BotMsPerSecond / 10.0</c>，
    /// 1000ms/秒 = 100% = 占满一个核，与帧率无关，直接回答「假人段吃掉了几个核」。
    /// </para>
    /// <para>
    /// 默认关闭，先在后台「假人性能」卡片看几天真实基线再打开。
    /// </para>
    /// </summary>
    public static class BotBreaker
    {
        /// <summary>水位上限覆盖值。<c>-1</c> = 未熔断（不限制）。用 <see cref="Volatile"/> 读写，跨线程安全。</summary>
        private static int _ceiling = -1;

        private static int _overloadSeconds;
        private static int _healthySeconds;

        private static DateTime _openedAt = DateTime.MinValue;
        private static int _tripCount;

        /// <summary>当前是否处于熔断状态（水位上限被压住）。</summary>
        public static bool Open => Volatile.Read(ref _ceiling) >= 0;

        /// <summary>当前水位上限覆盖值；<c>-1</c> = 不限制。</summary>
        public static int Ceiling => Volatile.Read(ref _ceiling);

        /// <summary>连续超阈值的秒数（未熔断时才有意义，给后台显示「还要多久触发」）。</summary>
        public static int OverloadSeconds => Volatile.Read(ref _overloadSeconds);

        /// <summary>熔断触发时刻（<see cref="DateTime.MinValue"/> = 没触发过）。</summary>
        public static DateTime OpenedAt => _openedAt;

        /// <summary>本次进程内累计触发过几次（用来判断「是不是反复在抖」）。</summary>
        public static int TripCount => Volatile.Read(ref _tripCount);

        /// <summary>
        /// 每秒评估一次（由 <see cref="BotRoster.Process"/> 的 1 秒分支调用，必须在游戏线程内）。
        /// </summary>
        /// <param name="online">当前在线假人数（熔断时压到的目标值）。</param>
        public static void Evaluate(int online)
        {
            if (!Config.BotBreakerEnabled)
            {
                // 运行中把开关关掉 → 立刻解除，别让旧的上限一直压着。
                if (Open) Close("总开关已关闭");

                Volatile.Write(ref _overloadSeconds, 0);
                Volatile.Write(ref _healthySeconds, 0);

                return;
            }

            double share = ServerPerf.BotSharePercent;
            double tripShare = Math.Max(0.1, Config.BotBreakerSharePercent);

            if (share >= tripShare)
            {
                Volatile.Write(ref _healthySeconds, 0);

                if (!Open)
                {
                    int seconds = OverloadSeconds + 1;

                    Volatile.Write(ref _overloadSeconds, seconds);

                    if (seconds >= Math.Max(1, Config.BotBreakerTripSeconds))
                        Trip(online, share);
                }
                else if (online < Ceiling)
                {
                    // 熔断期间继续往下压：只减不增。假人自然下线后，上限跟着降，
                    // 否则「上限 = 触发时的在线数」会让掉线的那几个立刻被补回来。
                    Volatile.Write(ref _ceiling, Math.Max(0, online));
                }

                return;
            }

            // 只要低于阈值就清空「连续超时」计数 —— 要求的是**连续** 60 秒，不是累计 60 秒。
            Volatile.Write(ref _overloadSeconds, 0);

            if (!Open) return;

            // 滞回：必须回落到阈值的一半以下才算真的好了，否则会在阈值附近反复开合。
            if (share > tripShare / 2.0)
            {
                Volatile.Write(ref _healthySeconds, 0);
                return;
            }

            int healthy = Volatile.Read(ref _healthySeconds) + 1;

            Volatile.Write(ref _healthySeconds, healthy);

            if (healthy >= Math.Max(1, Config.BotBreakerRecoverSeconds))
                Close(string.Format("假人段占比已回落到 {0:0.#}%", share));
        }

        /// <summary>
        /// 把水位目标夹到上限内。未熔断时原样返回。
        /// <para>
        /// 注意这里**同时**把上限往下带（<c>ceiling = min(ceiling, online)</c>），
        /// 这样 <c>target &lt;= online</c> 恒成立 → <see cref="BotRoster"/> 永远不会走「下线」分支，
        /// 真正做到「只停止新增，不踢人」。
        /// </para>
        /// </summary>
        public static int ClampTarget(int target, int online)
        {
            int ceiling = Ceiling;

            if (ceiling < 0) return target;

            if (online < ceiling)
            {
                ceiling = Math.Max(0, online);
                Volatile.Write(ref _ceiling, ceiling);
            }

            return Math.Min(target, ceiling);
        }

        /// <summary>后台手动解除（熔断误触发时的逃生口）。必须在游戏线程内调用。</summary>
        public static void Reset(string reason)
        {
            if (!Open) return;

            Close(reason);
        }

        /// <summary>调度器停止时清零，避免重启后还带着上一次的触发记录。</summary>
        public static void Clear()
        {
            Volatile.Write(ref _ceiling, -1);
            Volatile.Write(ref _overloadSeconds, 0);
            Volatile.Write(ref _healthySeconds, 0);
            Volatile.Write(ref _tripCount, 0);

            _openedAt = DateTime.MinValue;
        }

        private static void Trip(int online, double share)
        {
            int ceiling = Math.Max(0, online);

            Volatile.Write(ref _ceiling, ceiling);
            Volatile.Write(ref _overloadSeconds, 0);
            Volatile.Write(ref _healthySeconds, 0);
            Volatile.Write(ref _tripCount, TripCount + 1);

            // 用 Library.Time.LocalNow 而不是 SEnvir.Now：后者只在主循环里被赋值，
            // 单元测试和「服务端还没起来」的情况下是 DateTime.MinValue，时间戳会变成空值。
            // 这里只是一个给人看的显示时刻，跟游戏内时钟没有关系（BotTotals.SnapshotAt 也是这么取的）。
            _openedAt = Library.Time.LocalNow;

            BotLog.SystemWarn(string.Format(
                "全局熔断触发：假人段占单核 {0:0.#}% ≥ {1}%（连续 {2} 秒，累计 {3:0} ms/秒），水位上限临时压到 {4}（只停止新增，不踢在线；占比回落到 {5}% 以下并持续 {6} 秒后自动解除）",
                share, Config.BotBreakerSharePercent, Config.BotBreakerTripSeconds,
                ServerPerf.BotMsPerSecond, ceiling,
                Config.BotBreakerSharePercent / 2.0, Config.BotBreakerRecoverSeconds));
        }

        private static void Close(string reason)
        {
            int was = Ceiling;

            Volatile.Write(ref _ceiling, -1);
            Volatile.Write(ref _overloadSeconds, 0);
            Volatile.Write(ref _healthySeconds, 0);

            _openedAt = DateTime.MinValue;

            BotLog.SystemWarn(string.Format("全局熔断解除（{0}）：水位上限恢复，之前压在 {1}", reason, was));
        }
    }
}
