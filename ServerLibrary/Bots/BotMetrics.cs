using System.Threading;

namespace Server.Bots
{
    /// <summary>
    /// 假人运行期的累计计数（P4-5 后台性能卡片用）。
    /// <para>
    /// 全部是<b>进程内</b>计数，重启归零 —— 与 <c>Users.db</c> 里的跨重启档案是两回事：
    /// 这些数只用来回答「这段时间假人在干什么、干得多不多」，不需要持久化。
    /// </para>
    /// <para>
    /// 用 <see cref="Interlocked"/> 是因为写入点在游戏线程、读取点在 Blazor 线程。
    /// </para>
    /// </summary>
    public static class BotMetrics
    {
        private static long _mapChanges;
        private static long _chats;
        private static long _travelStarted;
        private static long _travelInFlight;
        private static long _travelInterrupted;
        private static long _travelArrived;
        private static long _travelAborted;

        /// <summary>累计换图次数（真正换了 <c>CurrentMap</c> 才算，不是「决定要走」的次数）。</summary>
        public static long MapChanges => Interlocked.Read(ref _mapChanges);

        /// <summary>
        /// 累计「跨图到达目标图」次数（2026-11-20）。
        /// <para>
        /// 与 <see cref="MapChanges"/> 的区别：换图次数包含中转跳板（A→B→C 记 2 次），
        /// 这个只在**抵达最终目标图**时 +1。两个数一起看才能判断跨图是在推进还是在空转 ——
        /// 生产实测曾出现「换图 4499 次、到达仅 7 次、放弃 690 次」的折返死循环。
        /// </para>
        /// </summary>
        public static long TravelArrived => Interlocked.Read(ref _travelArrived);

        /// <summary>累计「放弃跨图」次数（血线告急/超时等）。</summary>
        public static long TravelAborted => Interlocked.Read(ref _travelAborted);

        /// <summary>
        /// 累计「跨图启动」次数（2026-11-20）。
        /// <para>
        /// 恒等式：**启动 = 到达 + 放弃 + 进行中**。用户看后台时可以用它校验数字对不对 ——
        /// 只显示「换图次数 / 到达 / 放弃」时三者对不上，是因为换图次数含中转跳板（A→B→C 记 2 次）。
        /// </para>
        /// </summary>
        public static long TravelStarted => Interlocked.Read(ref _travelStarted);

        /// <summary>
        /// 当前**真正在赶路**的假人数（G4，2026-11-20）。
        /// <para>
        /// 由 <c>EnterState</c> 在进出 <c>Travel</c> 时增减维护。
        /// 原来后台用「启动 - 到达 - 放弃」当进行中，但「启动」含重新规划次数
        /// （实测同一时刻在途 63 个，卡片显示 127），数字对不上直觉。
        /// </para>
        /// </summary>
        public static long TravelInFlight => Interlocked.Read(ref _travelInFlight);

        /// <summary>
        /// 累计「赶路被怪打断」次数（2026-11-20 用户要求）。
        /// <para>
        /// 每次 `Travel → Fight` 记一次。它与「放弃」不同：打断后假人还会接续赶路
        /// （见 `_interruptedState`），不结算这一趟。看这个数能判断「跨图路上有多不太平」。
        /// </para>
        /// </summary>
        public static long TravelInterrupted => Interlocked.Read(ref _travelInterrupted);

        /// <summary>累计发言条数（真的发出去了才算）。</summary>
        public static long Chats => Interlocked.Read(ref _chats);

        public static void MapChange() => Interlocked.Increment(ref _mapChanges);

        public static void TravelStart() => Interlocked.Increment(ref _travelStarted);

        public static void TravelInterrupt() => Interlocked.Increment(ref _travelInterrupted);

        public static void TravelEnter() => Interlocked.Increment(ref _travelInFlight);

        public static void TravelExit()
        {
            // 防负数（异常路径可能多退一次）。
            if (Interlocked.Read(ref _travelInFlight) > 0) Interlocked.Decrement(ref _travelInFlight);
        }

        public static void TravelArrive() => Interlocked.Increment(ref _travelArrived);

        public static void TravelAbort() => Interlocked.Increment(ref _travelAborted);

        public static void Chat() => Interlocked.Increment(ref _chats);

        /// <summary>调度器停止时清零。</summary>
        public static void Reset()
        {
            Interlocked.Exchange(ref _mapChanges, 0);
            Interlocked.Exchange(ref _chats, 0);
            Interlocked.Exchange(ref _travelStarted, 0);
            Interlocked.Exchange(ref _travelInFlight, 0);
            Interlocked.Exchange(ref _travelInterrupted, 0);
            Interlocked.Exchange(ref _travelArrived, 0);
            Interlocked.Exchange(ref _travelAborted, 0);
        }
    }
}
