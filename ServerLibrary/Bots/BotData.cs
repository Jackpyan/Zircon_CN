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
