using System;
using System.Diagnostics;

namespace Library
{
    public static class Time
    {
        private static readonly DateTime StartTime = DateTime.UtcNow;
        private static readonly Stopwatch Stopwatch = Stopwatch.StartNew();

        public static DateTime Now => StartTime + Stopwatch.Elapsed;

        /// <summary>
        /// 展示用的本地时间：<see cref="Now"/> 与游戏逻辑一致是 UTC，写日志/界面显示时必须转成本机时区（北京时间）。
        /// </summary>
        public static DateTime LocalNow => DateTime.SpecifyKind(Now, DateTimeKind.Utc).ToLocalTime();
    }
}
