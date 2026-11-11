using Server.Envir;
using System;
using System.Collections.Generic;

namespace Server.Bots
{
    /// <summary>
    /// 一轮调度用到的配置快照。每轮从 <see cref="Config"/> 现读，支持后台改完即时生效，不用重启。
    /// 之所以拷一份而不是到处读 Config：一轮里读到的值必须一致，否则中途改配置会导致水位判断错乱。
    /// </summary>
    public sealed class BotOptions
    {
        public bool Enabled { get; private init; }

        public int TargetOnline { get; private init; }
        public int MinOnline { get; private init; }
        public int MaxOnline { get; private init; }
        public int MaxChangePerTick { get; private init; }
        public int RosterIntervalSeconds { get; private init; }
        public int StartupRampSeconds { get; private init; }

        public int StepDelayMinMs { get; private init; }
        public int StepDelayMaxMs { get; private init; }

        public int NewbieGold { get; private init; }
        public int NewbiePotionCount { get; private init; }
        public bool AutoSkill { get; private init; }

        public int MapCapacity { get; private init; }

        public int RestockPotionLow { get; private init; }
        public int RestockBuyRed { get; private init; }
        public int RestockBuyBlue { get; private init; }

        /// <summary>每次补给顺路买多少张回城卷（0 = 不买）。</summary>
        public int RestockBuyTownScroll { get; private init; }

        public int LevelCap { get; private init; }
        public int LogLevel { get; private init; }
        public int MaxFailures { get; private init; }

        /// <summary>允许练级的地图 Index 集合，空集合 = 不限制。</summary>
        public HashSet<int> HuntMaps { get; private init; } = new HashSet<int>();

        public static BotOptions FromConfig()
        {
            return new BotOptions
            {
                Enabled = Config.BotEnabled,
                TargetOnline = Config.BotTargetOnline,
                MinOnline = Config.BotMinOnline,
                MaxOnline = Config.BotMaxOnline,
                MaxChangePerTick = Math.Max(1, Config.BotMaxChangePerTick),
                RosterIntervalSeconds = Math.Max(1, Config.BotRosterIntervalSeconds),
                StartupRampSeconds = Math.Max(1, Config.BotStartupRampSeconds),
                StepDelayMinMs = Math.Max(0, Config.BotStepDelayMinMs),
                StepDelayMaxMs = Math.Max(Config.BotStepDelayMinMs, Config.BotStepDelayMaxMs),
                NewbieGold = Math.Max(0, Config.BotNewbieGold),
                NewbiePotionCount = Math.Max(0, Config.BotNewbiePotionCount),
                AutoSkill = Config.BotAutoSkill,
                MapCapacity = Math.Max(1, Config.BotMapCapacity),
                RestockPotionLow = Math.Max(0, Config.BotRestockPotionLow),
                RestockBuyRed = Math.Max(0, Config.BotRestockBuyRed),
                RestockBuyBlue = Math.Max(0, Config.BotRestockBuyBlue),
                RestockBuyTownScroll = Math.Max(0, Config.BotRestockBuyTownScroll),
                LevelCap = Math.Max(0, Config.BotLevelCap),
                LogLevel = Config.BotLogLevel,
                MaxFailures = Math.Max(1, Config.BotMaxFailures),
                HuntMaps = ParseIndexes(Config.BotHuntMaps),
            };
        }

        /// <summary>两步之间的随机等待：300~1200ms，避免 30 个假人同一帧登录。</summary>
        public TimeSpan NextStepDelay()
        {
            if (StepDelayMaxMs <= StepDelayMinMs) return TimeSpan.FromMilliseconds(StepDelayMinMs);

            return TimeSpan.FromMilliseconds(SEnvir.Random.Next(StepDelayMinMs, StepDelayMaxMs + 1));
        }

        /// <summary>解析 "1,3,5" 形式的地图索引列表。空串或全非法 → 空集合（视为不限制）。</summary>
        public static HashSet<int> ParseIndexes(string text)
        {
            HashSet<int> result = new HashSet<int>();

            if (string.IsNullOrWhiteSpace(text)) return result;

            foreach (string part in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                // 必须是 >= 0：**比奇城的地图索引就是 0**，
                // 原来写成 `index > 0` 会把比奇城永久排除在白名单外，
                // 配 BotHuntMaps=0,1 也永远选不到比奇城。
                if (int.TryParse(part.Trim(), out int index) && index >= 0)
                    result.Add(index);
            }

            return result;
        }
    }
}
