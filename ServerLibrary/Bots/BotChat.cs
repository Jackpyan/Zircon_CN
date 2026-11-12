using Library;
using Server.Envir;
using Server.Models;
using System;
using System.Collections.Generic;
using System.IO;

namespace Server.Bots
{
    /// <summary>说话场景。决定「这一刻说什么」，也决定语料能不能用。</summary>
    public enum BotChatKind
    {
        /// <summary>打招呼 / 没话找话。</summary>
        Greeting = 0,
        /// <summary>感慨（怪硬、药贵、地图远）。</summary>
        Feeling = 1,
        /// <summary>买卖相关（喊收、喊卖、抱怨价格）。</summary>
        Trade = 2,
        /// <summary>组队相关。</summary>
        Group = 3,
        /// <summary>惊讶（掉装备、遇到 boss）。</summary>
        Surprise = 4,
        /// <summary>死亡。</summary>
        Death = 5,
    }

    /// <summary><c>chat.txt</c> 里的一行语料。</summary>
    public sealed class BotChatLine
    {
        public BotChatKind Kind { get; init; }

        /// <summary>限定地图名（<see cref="Library.SystemModels.MapInfo.Description"/>）；空 = 不限。</summary>
        public string Map { get; init; } = string.Empty;

        /// <summary>等级下限；低于这个等级不会说这句（低级号谈屠龙一眼假）。</summary>
        public int MinLevel { get; init; }

        public string Text { get; init; } = string.Empty;

        public int Line { get; init; }
    }

    /// <summary>
    /// 假人聊天（P2-1）。
    /// <para>
    /// 三件事：<b>选句子</b>（按场景 + 地图 + 等级过滤）、<b>限频</b>（每人 15~40 分钟一句）、
    /// <b>选频道</b>（本图普通频道；33 级后小概率上世界频道）。
    /// </para>
    /// <para>
    /// 为什么限频这么狠：方案原值「每人 3~10 分钟一句」，30 个假人就是每分钟 3~10 句，
    /// 真人一眼就能看出是广告机。默认改成 15~40 分钟（全服约 0.75~2 句/分钟）。
    /// </para>
    /// <para>
    /// 频道判定<b>不自己发包</b>：直接调 <see cref="PlayerObject.Chat"/>，让引擎走完整的
    /// 等级校验（<c>!@</c> 世界频道要求 33 级）、冷却（账号级 30 秒）与屏蔽名单。
    /// </para>
    /// </summary>
    public static class BotChat
    {
        private static readonly object Sync = new object();

        private static List<BotChatLine> _lines = new List<BotChatLine>();
        private static DateTime _stamp = DateTime.MinValue;
        private static int _reportedErrors;

        /// <summary>世界频道下一次允许说话的时间（全服共享，防止几个假人同时刷世界）。</summary>
        private static DateTime _nextWorldTime = DateTime.MinValue;

        /// <summary>最近说过的几句，避免同一句话在短时间内被两个假人复读。</summary>
        private static readonly Queue<string> Recent = new Queue<string>();

        /// <summary>当前加载到的语料条数（后台显示「已就位 N 条」用）。</summary>
        public static int Count
        {
            get
            {
                lock (Sync)
                    return LinesLocked().Count;
            }
        }

        /// <summary>读语料（按文件 mtime 热加载，改完存盘 5 秒内生效）。</summary>
        public static List<BotChatLine> Lines()
        {
            lock (Sync)
                return LinesLocked();
        }

        private static List<BotChatLine> LinesLocked()
        {
            string path = BotData.ChatPath;

            if (!File.Exists(path))
            {
                if (_lines.Count > 0)
                {
                    _lines = new List<BotChatLine>();
                    _stamp = DateTime.MinValue;
                }

                return _lines;
            }

            DateTime stamp = File.GetLastWriteTimeUtc(path);

            if (stamp == _stamp) return _lines;

            _stamp = stamp;
            _lines = Parse(path);
            _reportedErrors = 0;

            BotLog.System(string.Format("聊天语料已加载：{0} 条（{1}）", _lines.Count, path));

            return _lines;
        }

        private static List<BotChatLine> Parse(string path)
        {
            List<BotChatLine> result = new List<BotChatLine>();

            string[] lines;

            try
            {
                lines = File.ReadAllLines(path);
            }
            catch (Exception ex)
            {
                BotLog.Warn(0, string.Format("读 chat.txt 失败，假人不会说话：{0}", ex.Message));
                return result;
            }

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();

                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//")) continue;

                // 格式：类别|地图|等级下限|文本
                string[] parts = line.Split('|');

                if (parts.Length < 4)
                {
                    ReportBadLine(i + 1, "字段不足 4 段（类别|地图|等级下限|文本）");
                    continue;
                }

                BotChatKind? kind = ParseKind(parts[0].Trim());

                if (kind == null)
                {
                    ReportBadLine(i + 1, "类别不认识：" + parts[0].Trim());
                    continue;
                }

                string text = parts[3].Trim();

                if (text.Length == 0 || text.Length > 60)
                {
                    // 超过 60 字不像玩家聊天，像客服公告；直接丢弃。
                    ReportBadLine(i + 1, "文本为空或超过 60 字");
                    continue;
                }

                int minLevel = 1;

                if (!string.IsNullOrWhiteSpace(parts[2]) && !int.TryParse(parts[2].Trim(), out minLevel))
                {
                    ReportBadLine(i + 1, "等级下限不是数字：" + parts[2].Trim());
                    continue;
                }

                result.Add(new BotChatLine
                {
                    Kind = kind.Value,
                    Map = parts[1].Trim(),
                    MinLevel = Math.Max(1, minLevel),
                    Text = text,
                    Line = i + 1,
                });
            }

            return result;
        }

        private static void ReportBadLine(int line, string reason)
        {
            if (_reportedErrors >= 5) return;

            _reportedErrors++;

            BotLog.Warn(0, string.Format("chat.txt 第 {0} 行被跳过：{1}", line, reason));
        }

        private static BotChatKind? ParseKind(string text)
        {
            switch (text)
            {
                case "问候": case "greeting": return BotChatKind.Greeting;
                case "感慨": case "feeling": return BotChatKind.Feeling;
                case "交易": case "trade": return BotChatKind.Trade;
                case "组队": case "group": return BotChatKind.Group;
                case "惊讶": case "surprise": return BotChatKind.Surprise;
                case "死亡": case "death": return BotChatKind.Death;
                default: return null;
            }
        }

        /// <summary>
        /// 挑一句话。
        /// <para>
        /// 过滤顺序：场景 + 地图 + 等级 → 去掉场景（同地图同等级）→ 只保等级 → 任意。
        /// 一路放宽是为了「语料写少了也还能说话」，而不是死板地一句不说。
        /// </para>
        /// </summary>
        /// <param name="world">true = 走世界频道（<c>!@</c> 前缀），调用方自己拼前缀。</param>
        public static bool TryPick(PlayerObject player, BotChatKind preferred, out string text, out bool world)
        {
            text = string.Empty;
            world = false;

            if (player?.CurrentMap?.Info == null) return false;

            List<BotChatLine> all;

            lock (Sync)
                all = LinesLocked();

            if (all.Count == 0) return false;

            string mapName = player.CurrentMap.Info.Description ?? string.Empty;
            int level = player.Level;

            List<BotChatLine> pool = Filter(all, preferred, mapName, level);

            if (pool.Count == 0) pool = Filter(all, null, mapName, level);
            if (pool.Count == 0) pool = Filter(all, null, null, level);
            if (pool.Count == 0) return false;

            BotChatLine picked = pool[SEnvir.Random.Next(pool.Count)];

            // 最近说过的话换一句（最多重抽 3 次，抽不到就用原来那句）。
            for (int i = 0; i < 3 && IsRecent(picked.Text); i++)
                picked = pool[SEnvir.Random.Next(pool.Count)];

            Remember(picked.Text);

            text = picked.Text;
            world = ShouldUseWorld(player);

            return true;
        }

        private static List<BotChatLine> Filter(List<BotChatLine> all, BotChatKind? kind, string mapName, int level)
        {
            List<BotChatLine> result = new List<BotChatLine>();

            foreach (BotChatLine line in all)
            {
                if (kind != null && line.Kind != kind.Value) continue;
                if (line.MinLevel > level) continue;

                // 地图名留空 = 任何图都能说；填了就只在那张图说。
                if (!string.IsNullOrEmpty(line.Map) && mapName != null && line.Map != mapName) continue;

                result.Add(line);
            }

            return result;
        }

        /// <summary>
        /// 这一句要不要发到世界频道。
        /// <para>
        /// 三个条件缺一不可：等级够（引擎硬要求 33 级）、全服冷却到了（40 秒）、概率命中（默认 35%）。
        /// 不够就退回本图普通频道 —— 宁可少一点人气，也不要刷屏。
        /// </para>
        /// </summary>
        private static bool ShouldUseWorld(PlayerObject player)
        {
            if (!Config.BotChatWorldEnabled) return false;
            if (player.Level < Config.BotChatWorldMinLevel) return false;

            lock (Sync)
            {
                if (SEnvir.Now < _nextWorldTime) return false;

                if (SEnvir.Random.Next(100) >= Math.Clamp(Config.BotChatWorldPercent, 0, 100)) return false;

                _nextWorldTime = SEnvir.Now.AddSeconds(Math.Max(10, Config.BotChatWorldCooldownSeconds));

                return true;
            }
        }

        private static bool IsRecent(string text)
        {
            lock (Sync)
                return Recent.Contains(text);
        }

        private static void Remember(string text)
        {
            lock (Sync)
            {
                Recent.Enqueue(text);

                while (Recent.Count > 8)
                    Recent.Dequeue();
            }
        }
    }
}
