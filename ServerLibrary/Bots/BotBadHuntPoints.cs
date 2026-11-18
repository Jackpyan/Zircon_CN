using System;
using System.Collections.Generic;
using System.Drawing;

namespace Server.Bots
{
    /// <summary>
    /// 跨假人共享的「走不到的刷怪点」黑名单（D6，2026-11-16）。
    /// <para>
    /// <b>问题</b>：<c>BotBrain._badHuntPoints</c> 原本是**每个假人自己的实例级字典**。
    /// 一个假人绕圈确认某个刷怪点走不到（隔墙 / 隔河 / 被围死）后拉黑，别的假人不知道，
    /// 照样把它选上、再绕圈、再拉黑 —— 群体在**同一个障碍点**上反复浪费几十秒。
    /// </para>
    /// <para>
    /// <b>语义</b>：不可达是**地图地形属性**，不是归属权 —— 一个假人确认某点走不到，
    /// 同图其它假人也基本走不到。所以标记**跨假人共享**：谁标记，全图假人选点时自动避开，
    /// 避免群体重复尝试同一个障碍点。
    /// </para>
    /// <para>
    /// <b>与 <see cref="BotMapClaims"/> 的区别</b>：认领表是「这张图归我」的归属权，下线要
    /// <c>ReleaseAll</c>；本表是地形知识，**不按归属清理**，只靠 TTL 过期后重新验算
    ///（挡路的怪可能走开、或换条路能到，过期给原目标一次机会）。
    /// </para>
    /// <para>
    /// <b>键含地图索引</b>：刷怪点坐标是「某张图上的格子」，不同图的同坐标是不同点，
    /// 必须带 <c>mapIndex</c> 做键，否则跨图会误伤。
    /// </para>
    /// <para>
    /// <b>线程</b>：只在游戏主线程里用（<see cref="BotRoster"/> 与 <see cref="BotBrain"/>
    /// 都跑在主循环），所以用普通 <c>Dictionary</c>，不加锁。
    /// </para>
    /// </summary>
    public static class BotBadHuntPoints
    {
        /// <summary>
        /// 标记有效期。沿用原实例级黑名单的 3 分钟：足够跨过一轮绕圈脱困，
        /// 又不至于把「曾经走不到」永久封死 —— 过期后重新验算，条件变化（怪走开 / 换路）可再选。
        /// </summary>
        private const long TtlMs = 180000;

        /// <summary>键 =（地图索引, X, Y）。值 = 过期 tick（<see cref="Environment.TickCount64"/> 基准）。</summary>
        private static readonly Dictionary<(int Map, int X, int Y), long> _points = new();

        /// <summary>标记一个刷怪点为「近期走不到」，TTL 内同图所有假人选点时避开。</summary>
        public static void Mark(int mapIndex, Point point)
        {
            Mark(mapIndex, point, TtlMs);
        }

        /// <summary>
        /// 标记一个刷怪点为「近期走不到」，指定有效期（<paramref name="ttlMs"/>）。
        /// <para>带 ttl 的重载主要供单测钉死过期行为；生产路径用无参版本走默认 TTL。</para>
        /// </summary>
        public static void Mark(int mapIndex, Point point, long ttlMs)
        {
            _points[(mapIndex, point.X, point.Y)] = Environment.TickCount64 + ttlMs;
        }

        /// <summary>
        /// 该刷怪点是否在共享黑名单里（<see cref="BotBrain.TryPickHuntPoint"/> 选点时调用）。
        /// 顺带清掉过期的条目 —— 表只有几十项，遍历成本可忽略。
        /// </summary>
        public static bool IsBad(int mapIndex, Point point)
        {
            if (!_points.TryGetValue((mapIndex, point.X, point.Y), out long expire))
                return false;

            if (expire > Environment.TickCount64)
                return true;

            _points.Remove((mapIndex, point.X, point.Y));

            return false;
        }

        /// <summary>
        /// 清掉已过期的条目。由 <see cref="BotRoster"/> 定期调用，
        /// 避免没人查询的过期标记一直攥在表里。
        /// </summary>
        public static void Prune()
        {
            if (_points.Count == 0) return;

            long now = Environment.TickCount64;

            List<(int Map, int X, int Y)> stale = null;

            foreach (KeyValuePair<(int Map, int X, int Y), long> pair in _points)
            {
                if (pair.Value <= now)
                    (stale ??= new List<(int, int, int)>()).Add(pair.Key);
            }

            if (stale == null) return;

            foreach ((int Map, int X, int Y) key in stale)
                _points.Remove(key);
        }

        /// <summary>当前标记条数（后台/诊断用）。</summary>
        public static int Count => _points.Count;
    }
}
