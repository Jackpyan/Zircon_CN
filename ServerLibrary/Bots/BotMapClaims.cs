using System;
using System.Collections.Generic;

namespace Server.Bots
{
    /// <summary>
    /// 假人「这张目标图归我」的选图认领表 —— 解决群体同步跨图。
    /// <para>
    /// <b>问题</b>：<see cref="BotBrain.TryTravelToHuntMap"/> 选图按「等级贴近 → 有六合石 →
    /// 地图索引距离」**确定性**排序，同一等级段的假人各自独立算，算出来必然是**同一张**目标图；
    /// <c>pass=1</c> 又放开容量、无负载均衡，于是群体同时满足跨图条件就**同时涌向同一张图**
    ///（用户 2026-11-14 观察：「一群人同时传送到另一张地图打怪」）。
    /// </para>
    /// <para>
    /// <b>为什么不用纯排序负载均衡</b>：按 <c>CountBotsOnMap</c> 升序排，同帧多个假人看到的
    /// 计数是**相同**的（都还没出发），排序结果依然一致，还是挤同一张。必须有一个「谁先认领」
    /// 的互斥机制，让先提交的假人占住目标图，后来的跳过它选下一张。
    /// </para>
    /// <para>
    /// <b>语义</b>：软认领。被认领的图不是「绝对不能去」，而是选图时**优先跳过**它；
    /// 若所有候选都被认领（极端情况），<see cref="BotBrain.TryTravelToHuntMap"/> 会退回原候选，
    /// 保证不会因认领而完全卡死跨图。
    /// </para>
    /// <para>
    /// <b>生命周期</b>：认领在**提交跨图**时建立（<c>TryStartTeleport</c> / A* 跨图成功），
    /// 在**到达**或**放弃**（<c>FinishTravel</c>）或**回城重选**时释放。TTL 只是兜底 ——
    /// 防止假人异常下线后认领永久占位；正常路径都靠显式 <see cref="Release"/>。
    /// </para>
    /// <para>
    /// <b>线程</b>：只在游戏主线程里用（<see cref="BotRoster"/> 与 <see cref="BotBrain"/>
    /// 都跑在主循环），所以用普通 <see cref="Dictionary{TKey,TValue}"/>，不加锁。
    /// </para>
    /// </summary>
    internal static class BotMapClaims
    {
        /// <summary>
        /// 认领有效期。跨图传送通常秒到、A* 跨图在 5 分钟时限内，TTL 只是「异常下线」的兜底，
        /// 正常路径靠显式释放。取 180 秒：覆盖绝大多数跨图耗时，又不至于让下线假人长期占位。
        /// </summary>
        private const long TtlMs = 180000;

        private struct Entry
        {
            public int Seq;
            public long ExpireTick;
        }

        /// <summary>键 = 目标图索引。</summary>
        private static readonly Dictionary<int, Entry> _claims = new Dictionary<int, Entry>();

        /// <summary>
        /// 认领（或续期）一张目标图。
        /// <para>**不覆盖别人的认领**：若该图已被别的假人认领且未过期，保持原认领者不动 ——
        /// 兜底路径里后来的假人仍可能去同一张，但认领记录应留给**最先**出发的那个，
        /// 让它到达后正常释放，避免认领被中途抢走导致释放错人。</para>
        /// </summary>
        public static void Claim(int mapIndex, int seq)
        {
            if (seq <= 0) return;

            if (_claims.TryGetValue(mapIndex, out Entry existing)
                && existing.ExpireTick > Environment.TickCount64
                && existing.Seq != seq)
            {
                return;
            }

            _claims[mapIndex] = new Entry
            {
                Seq = seq,
                ExpireTick = Environment.TickCount64 + TtlMs,
            };
        }

        /// <summary>这张目标图是否已被**别的**假人认领（过期视为没人认领）。</summary>
        public static bool IsClaimedByOther(int mapIndex, int seq)
        {
            if (!_claims.TryGetValue(mapIndex, out Entry claim)) return false;

            if (claim.ExpireTick <= Environment.TickCount64)
            {
                _claims.Remove(mapIndex);

                return false;
            }

            return claim.Seq != seq;
        }

        /// <summary>主动释放（到达目标图 / 放弃跨图 / 回城重选时调，能省一次 TTL 等待）。</summary>
        public static void Release(int mapIndex, int seq)
        {
            if (!_claims.TryGetValue(mapIndex, out Entry claim)) return;

            // 只释放自己的，别把别人的认领删掉。
            if (claim.Seq != seq) return;

            _claims.Remove(mapIndex);
        }

        /// <summary>某个假人下线 / 重置时，把它名下所有认领一次清掉。</summary>
        public static void ReleaseAll(int seq)
        {
            if (seq <= 0 || _claims.Count == 0) return;

            List<int> owned = null;

            foreach (KeyValuePair<int, Entry> pair in _claims)
            {
                if (pair.Value.Seq != seq) continue;

                (owned ??= new List<int>()).Add(pair.Key);
            }

            if (owned == null) return;

            foreach (int mapIndex in owned)
                _claims.Remove(mapIndex);
        }

        /// <summary>
        /// 清掉已过期的条目。由 <see cref="BotRoster"/> 定期调用，避免字典一直攥着已释放的图。
        /// </summary>
        public static void Prune()
        {
            if (_claims.Count == 0) return;

            long now = Environment.TickCount64;

            List<int> stale = null;

            foreach (KeyValuePair<int, Entry> pair in _claims)
            {
                if (pair.Value.ExpireTick <= now)
                    (stale ??= new List<int>()).Add(pair.Key);
            }

            if (stale == null) return;

            foreach (int mapIndex in stale)
                _claims.Remove(mapIndex);
        }

        /// <summary>当前认领条数（后台/诊断用）。</summary>
        public static int Count => _claims.Count;
    }
}
