using Library;
using Server.Envir;
using System;
using System.Collections.Generic;
using System.IO;

namespace Server.Bots
{
    /// <summary>
    /// 假人的外部数据文件（运行目录 <c>Bots/</c>）：<c>names.txt</c> 名单、<c>chat.txt</c> 占位。
    /// <para>
    /// 约定与 <c>Config</c> 一致：<b>文件不存在或解析为空就用内置兜底</b>，绝不因为少一个文件起不来服。
    /// 三个文件都按「最后写入时间」热加载，改完存盘 5 秒内生效，不用重启。
    /// </para>
    /// <para>
    /// 为什么名单要放外部文件：角色名正则是 <c>^[A-Za-z0-9]{3,15}$</c>（<b>不支持中文</b>），
    /// 运营商想起一批符合自家服调性的拼音名，不该来改代码重新编译。
    /// </para>
    /// </summary>
    public static class BotData
    {
        /// <summary>运行目录下的数据文件夹（与 <c>Database</c> / <c>Config</c> / <c>Logs</c> 同级）。</summary>
        public static string Root => Path.Combine(AppContext.BaseDirectory, "Bots");

        public static string NamesPath => Path.Combine(Root, "names.txt");
        public static string ChatPath => Path.Combine(Root, "chat.txt");

        /// <summary>假人行会名清单（2026-11-22）：一行一个，`#` 开头是注释。</summary>
        public static string GuildsPath => Path.Combine(Root, "guilds.txt");

        /// <summary>名单里的一行。<see cref="ClassName"/> / <see cref="GenderName"/> 为空表示「按序号轮转」。</summary>
        public sealed class NameEntry
        {
            public string Name { get; init; } = string.Empty;
            public MirClass? Class { get; init; }
            public MirGender? Gender { get; init; }
            public int Line { get; init; }
        }

        private static readonly object Sync = new object();

        private static List<NameEntry> _names = new List<NameEntry>();
        private static DateTime _namesStamp = DateTime.MinValue;
        private static int _namesReportedErrors;

        /// <summary>
        /// 读名单（带 mtime 热加载）。返回的条目可能为空 —— 调用方必须准备好内置兜底。
        /// </summary>
        public static List<NameEntry> Names()
        {
            lock (Sync)
            {
                string path = NamesPath;

                if (!File.Exists(path))
                {
                    // 文件被删掉时要清缓存，回到内置池，而不是继续用内存里的旧名单。
                    if (_names.Count > 0)
                    {
                        _names = new List<NameEntry>();
                        _namesStamp = DateTime.MinValue;
                    }

                    return _names;
                }

                DateTime stamp = File.GetLastWriteTimeUtc(path);

                if (stamp == _namesStamp) return _names;

                _namesStamp = stamp;
                _names = ParseNames(path);
                _namesReportedErrors = 0;

                BotLog.System(string.Format("名单已加载：{0} 条（{1}）", _names.Count, path));

                return _names;
            }
        }

        private static List<NameEntry> ParseNames(string path)
        {
            List<NameEntry> result = new List<NameEntry>();

            string[] lines;

            try
            {
                lines = File.ReadAllLines(path);
            }
            catch (Exception ex)
            {
                BotLog.Warn(0, string.Format("读 names.txt 失败，改用内置名池：{0}", ex.Message));
                return result;
            }

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();

                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//")) continue;

                string[] parts = line.Split('|');

                string name = parts[0].Trim();

                // 名字不合规就跳过并记一次日志：宁可少一个假人，也不要建出一个非法角色名。
                if (!IsValidName(name))
                {
                    if (_namesReportedErrors < 5)
                    {
                        _namesReportedErrors++;
                        BotLog.Warn(0, string.Format("names.txt 第 {0} 行名字不合法（含汉字 2~8 字，纯字母数字 3~15 位）：{1}", i + 1, name));
                    }

                    continue;
                }

                result.Add(new NameEntry
                {
                    Name = name,
                    Class = ParseClass(parts.Length > 1 ? parts[1].Trim() : null),
                    Gender = ParseGender(parts.Length > 2 ? parts[2].Trim() : null),
                    Line = i + 1,
                });
            }

            return result;
        }

        /// <summary>
        /// 与 <see cref="Globals.CharacterReg"/> 完全一致（含汉字 2~8 / 纯英文数字 3~15）。
        /// 这里只是把判定挪过来，好给出「第几行错了」的日志。
        /// </summary>
        public static bool IsValidName(string name) => CharacterNameRules.IsValid(name);

        private static MirClass? ParseClass(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            switch (text.ToLowerInvariant())
            {
                case "战士": case "warrior": case "w": return MirClass.Warrior;
                case "法师": case "wizard": case "m": return MirClass.Wizard;
                case "道士": case "taoist": case "t": return MirClass.Taoist;
                case "刺客": case "assassin": case "a": return MirClass.Assassin;
                default: return null;
            }
        }

        private static MirGender? ParseGender(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            switch (text.ToLowerInvariant())
            {
                case "男": case "male": case "m": return MirGender.Male;
                case "女": case "female": case "f": return MirGender.Female;
                default: return null;
            }
        }

        /// <summary>行会名清单是否存在（后台「初始化」按钮要用它判重）。</summary>
        public static bool GuildsFileExists => File.Exists(GuildsPath);

        /// <summary>「行会名单缺失」日志的节流（10 分钟一条，避免每 20 秒刷屏）。</summary>
        private static DateTime _nextGuildsMissingLog = DateTime.MinValue;

        /// <summary>
        /// 行会名单文件缺失时的告警（2026-11-22）。
        /// <para>
        /// <b>缺文件必须停掉整个行会流程</b>：建会 / 入会 / 换会 / 退会全部不执行。
        /// 否则运维误删 <c>guilds.txt</c> 会被当成「清单为空」→ 所有假人所在会都算「不在清单」→
        /// 全员退会，一次误操作就能把行会系统清空。
        /// </para>
        /// </summary>
        public static void WarnGuildsMissing()
        {
            if (SEnvir.Now < _nextGuildsMissingLog) return;

            _nextGuildsMissingLog = SEnvir.Now.AddMinutes(10);

            BotLog.Warn(0, string.Format(
                "未找到行会名单 {0} —— 建会/入会/换会/退会全部暂停。"
                + "请到后台「服务器配置」点「初始化行会名单」创建。", GuildsPath));
        }

        /// <summary>
        /// 后台按钮：创建行会名单文件（2026-11-22）。**文件已存在时直接失败**，不覆盖。
        /// </summary>
        public static bool TryCreateGuildsFile(out string message)
        {
            try
            {
                if (File.Exists(GuildsPath))
                {
                    message = string.Format("初始化失败：文件已存在（{0}）", GuildsPath);
                    return false;
                }

                if (!Directory.Exists(Root)) Directory.CreateDirectory(Root);

                File.WriteAllText(GuildsPath, DefaultGuildsTemplate(), new System.Text.UTF8Encoding(false));

                // 立刻热加载一次，后台点完按钮就能生效，不用等下一轮。
                _guildsStamp = DateTime.MinValue;

                message = string.Format("已创建行会名单：{0}（{1} 个行会名）", GuildsPath, Guilds().Count);
                return true;
            }
            catch (Exception ex)
            {
                message = string.Format("初始化失败：{0}", ex.Message);
                return false;
            }
        }

        /// <summary>初始化用的模板内容（5 个中文行会名 + 注释头）。</summary>
        private static string DefaultGuildsTemplate()
        {
            return "# 假人行会名清单（一行一个，# 开头是注释）\n"
                 + "# 规则：2~15 个字符，字母/数字/汉字；重名会被引擎拒绝\n"
                 + "# 最多取前 5 个建会；少于 5 个就按实际数量建\n"
                 + "# 删掉本文件 = 建会/入会/换会/退会全部暂停（防止误删导致全员退会）\n"
                 + "明月楼\n旧时光\n铁血同盟\n听雨轩\n长安城\n";
        }

        private static List<string> _guilds = new List<string>();
        private static DateTime _guildsStamp = DateTime.MinValue;
        private static int _guildsReportedErrors;

        /// <summary>
        /// 读假人行会名清单（带 mtime 热加载，2026-11-22）。
        /// <para>
        /// 文件：<c>Bots/guilds.txt</c>，一行一个名字，`#` / `//` 开头是注释。
        /// 名字规则与建会正则一致：2~15 个字符，字母/数字/汉字。
        /// </para>
        /// <para>
        /// 返回空列表表示「文件不存在或全不合法」，调用方要用 <c>Config.BotGuildNames</c> 兜底 ——
        /// 和 <c>names.txt</c> 一样的约定：**少一个文件不能让服起不来**。
        /// </para>
        /// </summary>
        public static List<string> Guilds()
        {
            lock (Sync)
            {
                string path = GuildsPath;

                if (!File.Exists(path))
                {
                    if (_guilds.Count > 0)
                    {
                        _guilds = new List<string>();
                        _guildsStamp = DateTime.MinValue;
                    }

                    return _guilds;
                }

                DateTime stamp = File.GetLastWriteTimeUtc(path);

                if (stamp == _guildsStamp) return _guilds;

                _guildsStamp = stamp;
                _guilds = ParseGuilds(path);
                _guildsReportedErrors = 0;

                BotLog.System(string.Format("行会名清单已加载：{0} 条（{1}）", _guilds.Count, path));

                return _guilds;
            }
        }

        private static List<string> ParseGuilds(string path)
        {
            List<string> result = new List<string>();

            string[] lines;

            try
            {
                lines = File.ReadAllLines(path);
            }
            catch (Exception ex)
            {
                BotLog.Warn(0, string.Format("读 guilds.txt 失败，改用配置里的行会名：{0}", ex.Message));
                return result;
            }

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();

                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//")) continue;

                // 已经加过的跳过：重名会让建会直接被引擎拒（GuildNameTaken）。
                if (result.Exists(x => string.Compare(x, line, StringComparison.OrdinalIgnoreCase) == 0))
                    continue;

                if (!Globals.GuildNameRegex.IsMatch(line))
                {
                    if (_guildsReportedErrors < 5)
                    {
                        _guildsReportedErrors++;
                        BotLog.Warn(0, string.Format(
                            "guilds.txt 第 {0} 行行会名不合法（2~15 个字符，字母/数字/汉字）：{1}", i + 1, line));
                    }

                    continue;
                }

                result.Add(line);
            }

            return result;
        }

        /// <summary>
        /// 聊天语料（P2 才用）。现在只负责告诉后台「这个文件在不在」，不做解析。
        /// </summary>
        public static bool ChatFileExists => File.Exists(ChatPath);

        /// <summary>确保 <c>Bots/</c> 目录存在（首次部署时后台能直接看到路径）。</summary>
        public static void EnsureDirectory()
        {
            try
            {
                if (!Directory.Exists(Root)) Directory.CreateDirectory(Root);
            }
            catch
            {
                // 目录建不出来不阻断启动，Names() 会走兜底。
            }
        }
    }
}
