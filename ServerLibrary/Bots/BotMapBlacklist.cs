using Server.Envir;
using System;
using System.Collections.Generic;

namespace Server.Bots
{
    /// <summary>
    /// 假人高危地图黑名单（2026-11-20，用户要求）。
    /// <para>
    /// 背景：幽灵船全系（入口 68 / 4层 69 / 3层 70 / 2层 71 / 1层 72 / 飞行甲板 73）
    /// 怪等级 75 且带 250 级 BOSS，单人进去只有被怪打死。生产实测 22 级法师被 A* 跨图
    /// 路径带进「幽灵船入口」，卡在入口点被 30 只 35 级怪围死；另有 26 级道士在潘夜岛
    /// 被 250 级 BOSS 打死。用户要求：**连路过、中转都不能经过**。
    /// </para>
    /// <para>
    /// 只作用于假人（<c>player.Bot != null</c>），不影响真人玩家与真人自动寻路。
    /// 配置项：<see cref="Config.BotAvoidMaps"/>（逗号分隔的地图 Index）。
    /// </para>
    /// </summary>
    public static class BotMapBlacklist
    {
        private static string _cachedRaw;
        private static HashSet<int> _cached = new HashSet<int>();

        /// <summary>这张图是不是假人绝对不去的图。</summary>
        public static bool Contains(int mapIndex)
        {
            if (mapIndex <= 0) return false;

            string raw = Config.BotAvoidMaps ?? string.Empty;

            // 配置热更新：原串变了就重算（后台保存配置后无需重启）。
            if (!string.Equals(raw, _cachedRaw, StringComparison.Ordinal))
            {
                _cachedRaw = raw;
                _cached = BotOptions.ParseIndexes(raw);
            }

            return _cached.Contains(mapIndex);
        }

        /// <summary>当前黑名单张数（后台展示用）。</summary>
        public static int Count => _cached.Count;
    }
}
