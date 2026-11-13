using Library;
using Server.Models;
using System;
using System.Collections.Generic;

namespace Server.Bots
{
    /// <summary>
    /// 假人「这只怪归我」的认领表 —— 解决多人抢同一只怪。
    /// <para>
    /// <b>问题</b>：<see cref="BotPerception.FindHuntTarget"/> 给每只怪算的分是
    /// <c>CurrentHP + 距离 × 20</c>，30 个假人各自独立算，算出来必然是<b>同一只</b>
    /// （最近的、血最少的）。于是刚刷出来的怪会被一群人同时扑上去，
    /// 一多半的人白跑一趟，击杀数上不去（用户 2026-11-13 观察：「几个人打一个怪」）。
    /// </para>
    /// <para>
    /// <b>为什么不用引擎自己的 <c>MonsterObject.EXPOwner</c></b>：那个要等**第一刀砍中**
    /// 才赋值，而抢怪恰恰发生在「还在往过走」的阶段 —— 那时谁都还没打，
    /// 所有假人看到的是同一只没人认领的怪。
    /// </para>
    /// <para>
    /// <b>语义</b>：软认领。被认领的怪不是「不能打」，而是在打分时加一个大惩罚 ——
    /// 附近还有别的怪就优先去打别的；实在只剩这一只，共打也好过站着不动。
    /// </para>
    /// <para>
    /// <b>生命周期</b>：认领 12 秒有效，认领者每 tick 续期一次；
    /// 目标死了 / 消失了 / 认领者下线了，靠 TTL 或 <see cref="Prune"/> 回收。
    /// 所以忘记调 <see cref="Release"/> 也不会永久占着一只怪。
    /// </para>
    /// <para>
    /// <b>线程</b>：只在游戏主线程里用（<see cref="BotRoster"/> 与 <see cref="BotBrain"/>
    /// 都跑在主循环），所以用普通 <see cref="Dictionary{TKey,TValue}"/>，不加锁。
    /// </para>
    /// </summary>
    internal static class BotClaims
    {
        /// <summary>认领有效期。比一次「走 10 格 + 砍两刀」略长，又不至于让人下线后长时间占着怪。</summary>
        private const long TtlMs = 12000;

        /// <summary>
        /// 「别人认领了」的分数惩罚。
        /// <para>
        /// 必须大于正常打分区间的最大值：正常分是 <c>HP + 距离×20</c>，
        /// HP 最多几百、距离上限 <c>HuntRadius</c>（30）× 20 = 600，合计不到 2000。
        /// 取 100000 保证「有别的怪就一定打别的」，同时保留「只剩它也能打」的兜底。
        /// </para>
        /// </summary>
        public const int ClaimPenalty = 100000;

        private struct Entry
        {
            public int Seq;
            public long ExpireTick;
        }

        private static readonly Dictionary<MonsterObject, Entry> _claims = new Dictionary<MonsterObject, Entry>();

        /// <summary>认领（或续期）一只怪。</summary>
        public static void Claim(MonsterObject monster, int seq)
        {
            if (monster == null || seq <= 0) return;

            _claims[monster] = new Entry
            {
                Seq = seq,
                ExpireTick = Environment.TickCount64 + TtlMs,
            };
        }

        /// <summary>这只怪是否已被**别的**假人认领（过期视为没人认领）。</summary>
        public static bool IsClaimedByOther(MonsterObject monster, int seq)
        {
            if (monster == null) return false;

            if (!_claims.TryGetValue(monster, out Entry claim)) return false;

            if (claim.ExpireTick <= Environment.TickCount64)
            {
                _claims.Remove(monster);

                return false;
            }

            return claim.Seq != seq;
        }

        /// <summary>主动释放（目标死了 / 换目标时调，能省一次 TTL 等待）。</summary>
        public static void Release(MonsterObject monster, int seq)
        {
            if (monster == null) return;

            if (!_claims.TryGetValue(monster, out Entry claim)) return;

            // 只释放自己的，别把别人的认领删掉。
            if (claim.Seq != seq) return;

            _claims.Remove(monster);
        }

        /// <summary>某个假人下线 / 重置时，把它名下所有认领一次清掉。</summary>
        public static void ReleaseAll(int seq)
        {
            if (seq <= 0 || _claims.Count == 0) return;

            List<MonsterObject> owned = null;

            foreach (KeyValuePair<MonsterObject, Entry> pair in _claims)
            {
                if (pair.Value.Seq != seq) continue;

                (owned ??= new List<MonsterObject>()).Add(pair.Key);
            }

            if (owned == null) return;

            foreach (MonsterObject monster in owned)
                _claims.Remove(monster);
        }

        /// <summary>
        /// 清掉死掉 / 已消失 / 已过期的条目。
        /// 由 <see cref="BotRoster"/> 定期调用，避免字典一直攥着已经 despawn 的怪物对象。
        /// </summary>
        public static void Prune()
        {
            if (_claims.Count == 0) return;

            long now = Environment.TickCount64;

            List<MonsterObject> dead = null;

            foreach (KeyValuePair<MonsterObject, Entry> pair in _claims)
            {
                MonsterObject monster = pair.Key;

                bool stale = pair.Value.ExpireTick <= now
                    || monster == null
                    || monster.Node == null
                    || monster.Dead;

                if (!stale) continue;

                (dead ??= new List<MonsterObject>()).Add(monster);
            }

            if (dead == null) return;

            foreach (MonsterObject monster in dead)
                _claims.Remove(monster);
        }

        /// <summary>当前认领条数（后台/诊断用）。</summary>
        public static int Count => _claims.Count;
    }
}
