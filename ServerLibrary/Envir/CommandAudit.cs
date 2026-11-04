using System;
using System.Linq;
using Server.DBModels;
using Server.Models;

namespace Server.Envir
{
    /// <summary>
    /// 一条 GM 命令审计记录。字段刻意用简单类型，方便 Web 侧直接映射到 AdminAuditEntry，
    /// 避免 ServerLibrary 反向依赖 Server.Web（那是循环依赖）。
    /// </summary>
    public sealed class CommandAuditRecord
    {
        public DateTime Time { get; set; }
        /// <summary>账号（邮箱）</summary>
        public string Account { get; set; }
        /// <summary>角色名</summary>
        public string Character { get; set; }
        /// <summary>身份中文（监督者 / 运营者 / 管理员）</summary>
        public string Identity { get; set; }
        /// <summary>英文命令名</summary>
        public string Command { get; set; }
        /// <summary>玩家实际输入的写法（可能是中文别名）</summary>
        public string Input { get; set; }
        /// <summary>命令参数（已拼成一串，超长截断）</summary>
        public string Arguments { get; set; }
        /// <summary>Success / Denied / Failed</summary>
        public string Result { get; set; }
        public string Reason { get; set; }
        /// <summary>执行时所在地图文件名</summary>
        public string Map { get; set; }
        public string Address { get; set; }
    }

    /// <summary>
    /// 游戏内 GM 命令审计：**双写**
    ///   1) 永远写 Logs.txt（进程内兜底，Server.exe 单独起服时也有记录）
    ///   2) 若 Web 进程注册了 <see cref="Sink"/>，再推一份到后台审计流水（异步落盘，不阻塞游戏线程）
    ///
    /// 为什么不直接写库：MirDB 的 Session.Save 是整库重写，每条命令落一次盘会拖慢主循环，
    /// 且审计数据混进 Users.db 会拖慢账号库的加载与备份。
    /// </summary>
    public static class CommandAudit
    {
        public const string Success = "Success";
        public const string Denied = "Denied";
        public const string Failed = "Failed";

        private const int MaxArgumentsLength = 200;

        /// <summary>Web 启动时注册；未注册（单独跑 Server.exe）时只写 Logs.txt。</summary>
        public static Action<CommandAuditRecord> Sink { get; set; }

        public static void Record(PlayerObject player, string commandValue, string[] input, string result, string reason)
        {
            // 审计自身绝不能影响命令执行：任何异常都吞掉
            try
            {
                if (player?.Character?.Account == null) return;

                string account = player.Character.Account.EMailAddress ?? "unknown";
                string character = player.Name ?? "unknown";
                string identity = player.Character.Account.GetEffectiveIdentity(includeTemp: true).ToCN();
                string arguments = BuildArguments(input);

                SEnvir.Log(string.Format("[GM命令] {0} {1}({2}) @{3} 身份={4} 地图={5} 参数={6}{7}",
                    result, account, character, commandValue, identity,
                    player.CurrentMap?.Info?.FileName ?? "-",
                    string.IsNullOrEmpty(arguments) ? "-" : arguments,
                    string.IsNullOrEmpty(reason) ? string.Empty : " 原因=" + reason));

                Action<CommandAuditRecord> sink = Sink;
                if (sink == null) return;

                sink(new CommandAuditRecord
                {
                    Time = DateTime.Now,
                    Account = account,
                    Character = character,
                    Identity = identity,
                    Command = commandValue,
                    Input = input != null && input.Length > 0 ? input[0] : commandValue,
                    Arguments = arguments,
                    Result = result,
                    Reason = reason,
                    Map = player.CurrentMap?.Info?.FileName,
                    Address = player.Connection?.IPAddress
                });
            }
            catch
            {
                // ignore
            }
        }

        private static string BuildArguments(string[] input)
        {
            if (input == null || input.Length <= 1) return string.Empty;

            string text = string.Join(" ", input.Skip(1));

            if (text.Length > MaxArgumentsLength)
                text = text.Substring(0, MaxArgumentsLength) + "…";

            return text;
        }
    }
}
