using Server.Envir;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Server.Bots
{
    /// <summary>
    /// 动态「高危地图」表（W10，2026-11-22）。
    /// <para>
    /// <b>为什么需要</b>：U1 规定「BOSS 等级 ≥ 自己 +50 → 必须烧卷」（250 级 BOSS 跑不掉）。
    /// 逻辑没错，但实测 55 分钟烧了 329 张卷（占全部烧卷的 45%），而库存只有 12 张 ——
    /// 卷被打光后，假人在回城/卖货路上遇袭就没有逃生手段，13/18 次死亡都是「卷=0」。
    /// </para>
    /// <para>
    /// <b>做法</b>：假人在某图被致命 BOSS 锁定过一次，就把这张图记进「高危」，
    /// 30 分钟内选点/跨图都跳过它 —— **不去有 BOSS 的图，就不会遇到 BOSS**，
    /// 比「遇到再烧卷」省得多。
    /// </para>
    /// <para>
    /// 到期自动失效（不需要重启）；全表上限 200 张图，满了先清过期项。
    /// </para>
    /// </summary>
    public static class BotDangerMaps
    {
        /// <summary>高危状态的保留时长（分钟）。</summary>
        public const int DangerMinutes = 30;

        private const int MaxEntries = 200;

        private static readonly object Sync = new object();

        private static readonly Dictionary<int, DateTime> Expiries = new Dictionary<int, DateTime>();

        /// <summary>把某张图标记为高危（记录 mapIndex，30 分钟内生效）。</summary>
        public static void Mark(int mapIndex, string reason)
        {
            if (mapIndex <= 0) return;

            lock (Sync)
            {
                if (Expiries.Count >= MaxEntries) Prune();

                bool isNew = !Expiries.ContainsKey(mapIndex);

                Expiries[mapIndex] = SEnvir.Now.AddMinutes(DangerMinutes);

                // 只在「第一次标记 / 已过期后重新标记」时打日志，避免同一张图反复刷屏。
                if (isNew)
                    BotLog.System(string.Format(
                        "地图 {0} 标记为高危 {1} 分钟：{2}（期间不再选这张图）",
                        mapIndex, DangerMinutes, reason));
            }
        }

        /// <summary>这张图当前是不是高危（过期自动失效）。</summary>
        public static bool IsDanger(int mapIndex)
        {
            if (mapIndex <= 0) return false;

            lock (Sync)
            {
                if (!Expiries.TryGetValue(mapIndex, out DateTime expiry)) return false;

                if (expiry > SEnvir.Now) return true;

                Expiries.Remove(mapIndex);

                return false;
            }
        }

        /// <summary>当前高危地图数量（后台/诊断用）。</summary>
        public static int Count
        {
            get
            {
                lock (Sync)
                {
                    Prune();
                    return Expiries.Count;
                }
            }
        }

        private static void Prune()
        {
            DateTime now = SEnvir.Now;

            foreach (int key in Expiries.Where(x => x.Value <= now).Select(x => x.Key).ToList())
                Expiries.Remove(key);
        }
    }
}
