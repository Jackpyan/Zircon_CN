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

        /// <summary>累计换图次数（真正换了 <c>CurrentMap</c> 才算，不是「决定要走」的次数）。</summary>
        public static long MapChanges => Interlocked.Read(ref _mapChanges);

        /// <summary>累计发言条数（真的发出去了才算）。</summary>
        public static long Chats => Interlocked.Read(ref _chats);

        public static void MapChange() => Interlocked.Increment(ref _mapChanges);

        public static void Chat() => Interlocked.Increment(ref _chats);

        /// <summary>调度器停止时清零。</summary>
        public static void Reset()
        {
            Interlocked.Exchange(ref _mapChanges, 0);
            Interlocked.Exchange(ref _chats, 0);
        }
    }
}
