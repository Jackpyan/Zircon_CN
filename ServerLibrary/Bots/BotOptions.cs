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

        /// <summary>真人优先：不抢真人已经打过的怪（<c>EXPOwner</c> 是真人）。</summary>
        public bool RespectRealPlayerTags { get; private init; }

        /// <summary>BOSS 留给真人：跳过 <c>MonsterInfo.IsBoss</c>。</summary>
        public bool SkipBoss { get; private init; }

        /// <summary>说话总开关（默认关）。</summary>
        public bool ChatEnabled { get; private init; }

        public int ChatIntervalMinSeconds { get; private init; }
        public int ChatIntervalMaxSeconds { get; private init; }

        public bool WalkPauseEnabled { get; private init; }
        public bool TownStrollEnabled { get; private init; }
        public int TownStrollPercent { get; private init; }
        public bool MistakeEnabled { get; private init; }
        public int MistakePerHour { get; private init; }

        public bool NightEnabled { get; private init; }
        public int NightScalePercent { get; private init; }
        public bool MapRotateEnabled { get; private init; }

        public bool PkEnabled { get; private init; }
        public int PkIntervalMinutes { get; private init; }
        public int PkRadius { get; private init; }

        public bool TopSlowEnabled { get; private init; }
        public int TopRankLimit { get; private init; }
        public int TopHoldMinutes { get; private init; }
        public int TopSlowMinutes { get; private init; }

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
                RespectRealPlayerTags = Config.BotRespectRealPlayerTags,
                SkipBoss = Config.BotSkipBoss,
                ChatEnabled = Config.BotChatEnabled,
                ChatIntervalMinSeconds = Math.Max(60, Config.BotChatIntervalMinSeconds),
                ChatIntervalMaxSeconds = Math.Max(Math.Max(60, Config.BotChatIntervalMinSeconds), Config.BotChatIntervalMaxSeconds),
                WalkPauseEnabled = Config.BotWalkPauseEnabled,
                TownStrollEnabled = Config.BotTownStrollEnabled,
                TownStrollPercent = Math.Clamp(Config.BotTownStrollPercent, 0, 100),
                MistakeEnabled = Config.BotMistakeEnabled,
                MistakePerHour = Math.Max(0, Config.BotMistakePerHour),
                NightEnabled = Config.BotNightEnabled,
                NightScalePercent = Math.Clamp(Config.BotNightScalePercent, 0, 100),
                MapRotateEnabled = Config.BotMapRotateEnabled,
                PkEnabled = Config.BotSocialEnabled && Config.BotPkEnabled,
                PkIntervalMinutes = Math.Max(1, Config.BotPkIntervalMinutes),
                PkRadius = Math.Max(1, Config.BotPkRadius),
                TopSlowEnabled = Config.BotTopSlowEnabled,
                TopRankLimit = Math.Max(1, Config.BotTopRankLimit),
                TopHoldMinutes = Math.Max(1, Config.BotTopHoldMinutes),
                TopSlowMinutes = Math.Max(1, Config.BotTopSlowMinutes),
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

        /// <summary>
        /// 下一次说话要等多久（15~40 分钟，随机）。
        /// <para>
        /// 必须随机：固定周期会让 30 个假人慢慢同步成「整点合唱」，比不说话还假。
        /// </para>
        /// </summary>
        public TimeSpan NextChatDelay()
        {
            if (ChatIntervalMaxSeconds <= ChatIntervalMinSeconds)
                return TimeSpan.FromSeconds(ChatIntervalMinSeconds);

            return TimeSpan.FromSeconds(SEnvir.Random.Next(ChatIntervalMinSeconds, ChatIntervalMaxSeconds + 1));
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
