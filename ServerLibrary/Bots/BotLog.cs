using Server.Envir;
using System;

namespace Server.Bots
{
    /// <summary>假人日志级别，与 <see cref="Config.BotLogLevel"/> 比较，低于配置值的不输出。</summary>
    public enum BotLogLevel
    {
        Debug = 0,
        Info = 1,
        Warn = 2,
        Error = 3,
    }

    /// <summary>
    /// 假人专用日志。统一走 <see cref="SEnvir.Log(string)"/> 落到 Logs.txt，
    /// 格式：<c>[HH:mm:ss.fff] BOT#07 [WARN ] 内容</c>，方便 grep 出单个假人的整条流水。
    /// 默认只记 Warn 以上（Config.BotLogLevel = 2），调成 0 可看每帧细节。
    /// </summary>
    public static class BotLog
    {
        public static void Debug(int seq, string message) => Write(BotLogLevel.Debug, seq, message);
        public static void Info(int seq, string message) => Write(BotLogLevel.Info, seq, message);
        public static void Warn(int seq, string message) => Write(BotLogLevel.Warn, seq, message);
        public static void Error(int seq, string message) => Write(BotLogLevel.Error, seq, message);

        /// <summary>不针对单个假人的调度日志（水位变化、开关切换等）。</summary>
        public static void System(string message)
        {
            if (BotLogLevel.Info < (BotLogLevel)Config.BotLogLevel) return;

            SEnvir.Log(string.Format("[{0:HH:mm:ss.fff}] BOT     [INFO ] {1}", DateTime.Now, message));
        }

        /// <summary>
        /// 不针对单个假人、但<b>必须留痕</b>的系统级告警（熔断触发/解除）。
        /// <para>
        /// 生产档 <see cref="Config.BotLogLevel"/> = 2 会把 <see cref="System"/> 的 Info 全部滤掉，
        /// 而「假人把服务器拖到降载」这种事绝不能只存在于后台页面里 —— 事后排查要能 grep 到。
        /// 所以走 Warn 档，默认配置下一定输出。
        /// </para>
        /// </summary>
        public static void SystemWarn(string message)
        {
            if (BotLogLevel.Warn < (BotLogLevel)Config.BotLogLevel) return;

            SEnvir.Log(string.Format("[{0:HH:mm:ss.fff}] BOT     [WARN ] {1}", DateTime.Now, message));
        }

        /// <summary>
        /// 系统级错误（后台读库失败之类）。
        /// <para>
        /// 一律按 Error 档输出，<b>不受 <see cref="Config.BotLogLevel"/> 过滤</b> ——
        /// 后台某个面板拉不到数据，最怕的就是「页面上只是空着，日志里什么都没有」，
        /// 这种「静默降级」排查起来比直接报错贵十倍（2026-11-13 假人档案表空白就是这么来的）。
        /// </para>
        /// </summary>
        public static void SystemError(string message)
        {
            SEnvir.Log(string.Format("[{0:HH:mm:ss.fff}] BOT     [ERROR] {1}", DateTime.Now, message));
        }

        private static void Write(BotLogLevel level, int seq, string message)
        {
            if ((int)level < Config.BotLogLevel) return;

            string tag;

            switch (level)
            {
                case BotLogLevel.Debug:
                    tag = "DEBUG";
                    break;
                case BotLogLevel.Info:
                    tag = "INFO ";
                    break;
                case BotLogLevel.Warn:
                    tag = "WARN ";
                    break;
                default:
                    tag = "ERROR";
                    break;
            }

            SEnvir.Log(string.Format("[{0:HH:mm:ss.fff}] BOT#{1:00} [{2}] {3}", DateTime.Now, seq, tag, message));
        }
    }
}
