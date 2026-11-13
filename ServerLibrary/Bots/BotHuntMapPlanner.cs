using Library;
using Library.SystemModels;
using Server.Envir;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Server.Bots
{
    /// <summary>某个等级段里有哪些图能打（后台展示用）。</summary>
    public sealed record HuntMapBand(int FromLevel, int ToLevel, IReadOnlyList<string> Maps, IReadOnlyList<int> Indices);

    /// <summary>白名单重算结果：分段明细 + 去重后的索引表 + 可直接写进配置的 CSV。</summary>
    public sealed record HuntMapPlan(IReadOnlyList<HuntMapBand> Bands, IReadOnlyList<int> Indices, string Csv);

    /// <summary>
    /// 练级地图白名单的「按等级段生成」（2026-11-13）。
    /// <para>
    /// 为什么要有：<c>Config.BotHuntMaps</c> 原来是手工维护的 84 个地图 Index，
    /// 加图 / 开新等级段时必然漏 —— 漏了的现象是「假人等级上去了还在出生地打怪」，
    /// 因为跨图候选被白名单过滤成 0，代码只会打一行 Debug 日志「跨图放弃」，
    /// 从表现上看完全不像配置问题。
    /// </para>
    /// <para>
    /// 判据与 <c>BotBrain</c> 的选图窗口**必须一致**：怪等级落在
    /// <c>[自身等级 - 8, 自身等级 + 4]</c> 且 <c>MapInfo.MinimumLevel &lt;= 自身等级</c>。
    /// 两边一旦不一致，就会出现「白名单里有这张图，但去了也打不着」。
    /// </para>
    /// <para>
    /// BOSS 图不纳入：<c>BotSkipBoss</c> 会把 BOSS 当空气，只有 BOSS 的图进去等于空跑。
    /// </para>
    /// </summary>
    public static class BotHuntMapPlanner
    {
        /// <summary>选图窗口：怪等级上限（自身 +4）。改这里必须同步 <c>BotBrain.TryPickHuntPoint</c>。</summary>
        private const int LevelAbove = 4;

        /// <summary>选图窗口：怪等级下限（自身 -8）。</summary>
        private const int LevelBelow = 8;

        /// <summary>每段多少级（后台展示用，不影响生成的索引集合）。</summary>
        private const int BandSize = 10;

        public static HuntMapPlan Build(int maxLevel)
        {
            maxLevel = Math.Clamp(maxLevel, 1, Globals.MaxExperienceLevel);

            // 等级 → 这一级能打的图
            Dictionary<int, HashSet<MapInfo>> perLevel = new();

            for (int level = 1; level <= maxLevel; level++) perLevel[level] = new HashSet<MapInfo>();

            foreach (RespawnInfo respawn in SEnvir.RespawnInfoList.Binding)
            {
                MapInfo map = respawn.Region?.Map;
                MonsterInfo monster = respawn.Monster;

                if (map == null || monster == null) continue;
                if (monster.IsBoss) continue;

                // 这张图的刷怪区能在哪些等级被「打得动」：
                //   monster.Level <= 自身 + 4  →  自身 >= monster.Level - 4
                //   monster.Level >= 自身 - 8  →  自身 <= monster.Level + 8
                int from = Math.Max(1, Math.Max(monster.Level - LevelAbove, map.MinimumLevel));
                int to = Math.Min(maxLevel, monster.Level + LevelBelow);

                for (int level = from; level <= to; level++)
                    perLevel[level].Add(map);
            }

            List<HuntMapBand> bands = new();
            HashSet<int> all = new();

            for (int from = 1; from <= maxLevel; from += BandSize)
            {
                int to = Math.Min(maxLevel, from + BandSize - 1);

                SortedSet<int> indices = new();

                for (int level = from; level <= to; level++)
                    foreach (MapInfo map in perLevel[level]) indices.Add(map.Index);

                bands.Add(new HuntMapBand(from, to,
                    indices.Select(index => ResolveDescription(index)).ToList(),
                    indices.ToList()));

                all.UnionWith(indices);
            }

            List<int> sorted = all.OrderBy(x => x).ToList();

            return new HuntMapPlan(bands, sorted, string.Join(",", sorted));
        }

        private static string ResolveDescription(int index)
        {
            MapInfo info = SEnvir.MapInfoList?.Binding?.FirstOrDefault(x => x.Index == index);

            return string.IsNullOrEmpty(info?.Description) ? index.ToString() : info.Description;
        }
    }
}
