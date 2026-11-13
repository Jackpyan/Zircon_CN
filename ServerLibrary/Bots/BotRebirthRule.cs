using System;
using System.Text;

namespace Server.Bots
{
    /// <summary>
    /// 假人「个人转生阈值」的算法（2026-11-13）。
    /// <para>
    /// 需求：假人不是一到 75 级就转生，而是在 75~100 之间各有一个自己的阈值 ——
    /// 否则 30 个假人会同一时刻集体转生，在线等级整齐地掉回 1 级，比不转还假。
    /// </para>
    /// <para>
    /// 做法：**确定性散列**，不落库。
    /// <c>阈值 = Min + Hash(角色名 + 转生次数) % (Max - Min + 1)</c>
    /// <list type="bullet">
    /// <item>重启、重登都不变（同一个名字同一个次数 → 同一个阈值），可复现、可核对；</item>
    /// <item>转生成功后 <c>Rebirth</c> 变了，下一次阈值自动重抽，不用额外写字段；</item>
    /// <item>不碰 <c>Users.db</c>，也就没有「加字段要整库同步」的风险。</item>
    /// </list>
    /// </para>
    /// <para>
    /// 为什么不用 <see cref="Random"/>：那要额外存种子或存结果，而且进程重启后
    /// 「阈值从 88 变成 42」会让人以为配置没生效。散列天然是幂等的。
    /// </para>
    /// </summary>
    public static class BotRebirthRule
    {
        /// <summary>
        /// 算某个假人的转生阈值。区间会先被 <paramref name="maxLevel"/> 夹住，
        /// 并保证 <c>Min &lt;= 阈值 &lt;= 上限</c>；夹完等于上限时「到顶必转」。
        /// </summary>
        /// <param name="name">角色名（区分大小写，和角色名唯一性判定一致）。</param>
        /// <param name="rebirth">当前已转生次数（<c>CharacterInfo.Rebirth</c>）。</param>
        /// <param name="minLevel">区间下限（含），配置 <c>BotRebirthMinLevel</c>。</param>
        /// <param name="maxLevel">区间上限（含），配置 <c>BotRebirthMaxLevel</c>。</param>
        /// <param name="serverMaxLevel">全服等级上限 <c>Config.MaxLevel</c>，再夹一次防「阈值永远等不到」。</param>
        public static int Threshold(string name, int rebirth, int minLevel, int maxLevel, int serverMaxLevel)
        {
            int upper = Math.Min(Math.Max(1, maxLevel), Math.Max(1, serverMaxLevel));
            int lower = Math.Clamp(minLevel, 1, upper);

            int span = upper - lower + 1;

            if (span <= 1) return upper;

            int hash = StableHash(name ?? string.Empty, rebirth);

            return lower + hash % span;
        }

        /// <summary>
        /// FNV-1a 32 位。不用 <c>string.GetHashCode()</c>：那个在 .NET 上默认随机化种子，
        /// 每次进程启动结果都不一样 —— 阈值会「自己变」，正是要避免的现象。
        /// </summary>
        private static int StableHash(string name, int rebirth)
        {
            const uint offset = 2166136261;
            const uint prime = 16777619;

            uint hash = offset;

            foreach (byte b in Encoding.UTF8.GetBytes(name))
            {
                hash ^= b;
                hash *= prime;
            }

            // 把转生次数也揉进去：每转一次重抽一次阈值。
            foreach (byte b in BitConverter.GetBytes(rebirth))
            {
                hash ^= b;
                hash *= prime;
            }

            return (int)(hash & 0x7FFFFFFF);
        }
    }
}
