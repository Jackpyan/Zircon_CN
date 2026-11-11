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
