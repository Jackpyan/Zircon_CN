using Library;
using Server.Envir;
using Server.Models;
using Server.Models.AutoPath;
using System;
using System.Drawing;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Server.Bots
{
    /// <summary>走一步的结果。区分「等着再来」和「根本走不通」，上层据此决定是重试还是换目标。</summary>
    public enum BotMoveResult
    {
        /// <summary>这一步已经迈出去了。</summary>
        Moved = 0,
        /// <summary>已经在目标点上。</summary>
        Arrived = 1,
        /// <summary>上一个动作还没结束，下一帧再来（不是错误）。</summary>
        Busy = 2,
        /// <summary>算不出路径（隔墙、隔河、目标不可达）或角色不可用。</summary>
        Blocked = 3,
    }

    /// <summary>
    /// 假人的腿。**不用 <c>AutoPathService</c>** —— 它被 <c>MapInfo.CanAutoPath</c> 挡着，
    /// 本库只有 1 张图开了这个开关（有刷怪的 195 张里 0 张），假人调 <c>StartAutoPath</c> 一律失败。
    /// 这里直接复用它的同图 A*（<c>TryBuildCurrentPath</c> 不检查该开关）+ <c>Player.Move</c> 逐格走，
    /// 而 <c>Move</c> 就是真人 <c>C.Move</c> 的服务端入口，广播、速度、碰撞全走原生逻辑。
    /// <para>只能同图移动；跨图 P0 不做（见方案 §6.6）。</para>
    /// <para>
    /// 路径缓存（第 77 章改动点 1）：<see cref="TryStepTowards"/> / <see cref="TryStepTowardsNear"/>
    /// 原来每迈一步都重算一次全图 A*（30 人 ≈ 50 步/秒 → 每秒 50~100 次 A* + 32MB 分配，
    /// 实测吃掉 0.8 个核）。现在按「目标点 + 半径 + 地图」缓存整条路径，只在失效时重算。
    /// </para>
    /// <para>
    /// 跑动（P2-7）：直线连续无阻挡时按「跑」移动（<c>distance=2</c>，600ms 走 2 格），
    /// 转弯 / 最后一步 / 被挡时退回「走」（<c>distance=1</c>）。复刻真人「直线跑、转弯走」的观感，
    /// 同时把移动效率翻倍。受 <see cref="Config.BotRunEnabled"/> 控制。
    /// </para>
    /// </summary>
    public static class BotNavigator
    {
        /// <summary>路径缓存最长存活时间（秒）。超过则强制重算，避免长时间缓存被动态阻挡穿透。</summary>
        private const int PathCacheMaxAgeSeconds = 10;

        /// <summary>每个假人的路径缓存。用 <see cref="ConditionalWeakTable"/>，假人下线后自动回收，不污染 PlayerObject。</summary>
        private static readonly ConditionalWeakTable<PlayerObject, BotPathCache> _pathCaches =
            new ConditionalWeakTable<PlayerObject, BotPathCache>();

        /// <summary>一个假人的路径缓存：目标 + 半径 + 整条路径 + 已消费步数。</summary>
        private sealed class BotPathCache
        {
            /// <summary>缓存的目标点。</summary>
            public Point Target;
            /// <summary>终点半径：-1 = <see cref="TryStepTowards"/>（单点终点），≥0 = <see cref="TryStepTowardsNear"/>。</summary>
            public int Radius;
            /// <summary>A* 返回的路径，<b>含当前位置</b>：<c>Path[0]</c> 是起点，<c>Path[Consumed]</c> 是当前所在格。</summary>
            public List<Point> Path;
            /// <summary>已走的步数。下一步是 <c>Path[Consumed + 1]</c>；走到末格（<c>Consumed + 1 == Count</c>）即到终点。</summary>
            public int Consumed;
            /// <summary>缓存时的地图索引，换图即失效。</summary>
            public int MapIndex;
            /// <summary>缓存过期时间。</summary>
            public DateTime ExpiresAt;
        }

        /// <summary>朝目标迈一步。每次只走一格，下一步由调用方下一帧再算（怪会跑，缓存整条路径会走偏）。</summary>
        public static BotMoveResult TryStepTowards(PlayerObject player, Point target)
        {
            if (player == null || player.Dead || player.CurrentMap == null) return BotMoveResult.Blocked;

            if (player.CurrentLocation == target) return BotMoveResult.Arrived;

            if (!player.CanMove) return BotMoveResult.Busy;

            if (!TryGetCachedStep(player, target, -1, out Point next, out int distance)) return BotMoveResult.Blocked;

            Point before = player.CurrentLocation;

            player.Move(Functions.DirectionFromPoint(before, next), distance);

            // Move 内部还有一道 IsBlocking 校验（A* 用的是不含时序的判据），
            // 被动态阻挡时坐标不会变 —— 直接当 Blocked 返回，并让缓存失效，下次重算。
            if (player.CurrentLocation == before)
            {
                ClearCache(player);
                return BotMoveResult.Blocked;
            }

            AdvanceCache(player, distance);
            return BotMoveResult.Moved;
        }

        /// <summary>
        /// 朝目标**附近**迈一步（终点可以是目标周围的任一格）。
        /// <para>
        /// 为什么需要这个：A* 的终点是**格子**，而 NPC / 怪就站在自己那格上 ——
        /// 那格是阻挡物，物理上永远迈不进去。于是「走向 NPC」会一直算出一条
        /// 最后一步指向 NPC 所在格的路径，那一步必然被引擎拒绝 → 每帧都 Blocked。
        /// </para>
        /// <para>
        /// 实测踩到过：假人买完药去杂货商买回城卷，伦纳德在 43 格外，
        /// 假人连走 20 步都过不去，最后报「走不到杂货商」—— 药买到了，卷没买到。
        /// 药店因为离出生点近，之前没暴露这个问题。
        /// </para>
        /// </summary>
        public static BotMoveResult TryStepTowardsNear(PlayerObject player, Point target, int radius)
        {
            if (player == null || player.Dead || player.CurrentMap == null) return BotMoveResult.Blocked;

            if (player.CurrentLocation == target) return BotMoveResult.Arrived;

            if (!player.CanMove) return BotMoveResult.Busy;

            if (!TryGetCachedStep(player, target, radius, out Point next, out int distance)) return BotMoveResult.Blocked;

            Point before = player.CurrentLocation;

            player.Move(Functions.DirectionFromPoint(before, next), distance);

            if (player.CurrentLocation == before)
            {
                ClearCache(player);
                return BotMoveResult.Blocked;
            }

            AdvanceCache(player, distance);
            return BotMoveResult.Moved;
        }

        /// <summary>
        /// 「避让活物」地朝目标迈一步 —— 专门用来破「原地打转」。
        /// <para>
        /// <see cref="TryStepTowards"/> 内部调 A* 时传的是 <c>avoidLiveObjects: false</c>
        /// （不把别的角色/宝宝当障碍），于是被一个站着不动的假人挡住时会变成：
        /// A* 指向同一格 → 迈不动 → 侧移一步 → 下一帧 A* 又指回原来那格 → 再侧移回来。
        /// 每步都在动，看起来就是「围着遮挡物绕圈」，实测能把假人钉在原地好几分钟。
        /// </para>
        /// <para>
        /// 这里改用 <c>avoidLiveObjects: true</c> 重算，A* 会主动绕开人群。
        /// <b>调用方必须自己节流</b>：整图 A* 一次 20~30ms，30 个假人每帧都算会把主线程吃光。
        /// </para>
        /// <para>算不出「绕开活物」的路时返回 false，调用方应退回原来的侧移/换点逻辑。</para>
        /// </summary>
        public static bool TryStepAvoidingLive(PlayerObject player, Point target)
        {
            if (player == null || player.Dead || player.CurrentMap == null) return false;

            if (player.CurrentLocation == target) return false;

            if (!player.CanMove) return false;

            if (!AutoPathRoutePlanner.TryBuildCurrentPath(player, target, true, out List<Point> path)) return false;

            if (path == null || path.Count < 2) return false;

            Point before = player.CurrentLocation;

            player.Move(Functions.DirectionFromPoint(before, path[1]), 1);

            return player.CurrentLocation != before;
        }

        /// <summary>
        /// 「避让活物」地朝目标**附近**迈一步 —— 追怪专用。
        /// <para>
        /// 为什么不能直接用 <see cref="TryStepAvoidingLive"/> 追怪：怪自己站的那格是阻挡物，
        /// A* 的终点永远迈不进去，路径算不出来。所以终点集合要换成「目标周围 radius 格内的可走格」，
        /// 这和 <see cref="TryStepTowardsNear"/> 的做法一致。
        /// </para>
        /// <para>
        /// 实测（2026-11-13 第三轮）：BOT#21 在潘夜村 (157,182) 追一头 11 格外的猪，
        /// 8 秒内活动范围只有 1×1 格、一刀没砍出去 —— 就是「侧移 → 下一帧 A* 又指回去」的横跳。
        /// <b>调用方必须自己节流</b>（整图 A* 一次 20~30ms）。
        /// </para>
        /// </summary>
        public static bool TryStepAvoidingLiveNear(PlayerObject player, Point target, int radius)
        {
            if (player == null || player.Dead || player.CurrentMap == null) return false;

            if (!player.CanMove) return false;

            HashSet<Point> targets = CollectWalkableAround(player, player.CurrentMap, target, radius);

            if (targets.Count == 0) return false;

            if (!AutoPathRoutePlanner.TryBuildCurrentPathTo(player, targets, true, out List<Point> path)) return false;

            if (path == null || path.Count < 2) return false;

            Point before = player.CurrentLocation;

            player.Move(Functions.DirectionFromPoint(before, path[1]), 1);

            return player.CurrentLocation != before;
        }

        /// <summary>只算路不走路，用于「这个点到底去不去得」的预检和调试。**不碰路径缓存**，每次都重算。</summary>
        public static bool TryGetNextStep(PlayerObject player, Point target, out Point next)
        {
            next = Point.Empty;

            if (player?.CurrentMap == null) return false;

            if (!AutoPathRoutePlanner.TryBuildCurrentPath(player, target, false, out List<Point> path)) return false;

            if (path == null || path.Count < 2) return false;

            next = path[1];

            return true;
        }

        /// <summary>
        /// 多终点预检：一次 A* 判断「这批候选点里有没有走得通的」，并返回选中的终点与下一步。
        /// **不碰路径缓存**，每次都重算（用于预检）。
        /// <para>
        /// 选练级点时候选点多达几十个，逐个试路最坏要跑 40 次整图 A*（单次 20~30ms，
        /// 最坏 1 秒、制造 135ms 尖峰）；多终点 A* 一次算完，选中的终点就是 A* 最先到达的
        /// （即最近的可达点），语义与「按距离逐个试、第一个走通的用」一致。
        /// </para>
        /// </summary>
        public static bool TryGetNextStepToAny(PlayerObject player, HashSet<Point> targets, out Point next, out Point chosen)
        {
            next = Point.Empty;
            chosen = Point.Empty;

            if (player?.CurrentMap == null) return false;

            if (targets == null || targets.Count == 0) return false;

            if (!AutoPathRoutePlanner.TryBuildCurrentPathTo(player, targets, false, out List<Point> path)) return false;

            if (path == null || path.Count < 2) return false;

            // 路径含起点：path[^1] 就是 A* 选中的终点，path[1] 是下一步。
            chosen = path[^1];
            next = path[1];

            return true;
        }

        /// <summary>
        /// 算「走到目标附近」的下一格：A* 的终点集合是目标周围 <paramref name="radius"/> 格内的所有可走格。
        /// **不碰路径缓存**，每次都重算（用于预检）。
        /// </summary>
        public static bool TryGetNextStepNear(PlayerObject player, Point target, int radius, out Point next)
        {
            next = Point.Empty;

            Map map = player?.CurrentMap;

            if (map == null) return false;

            HashSet<Point> targets = CollectWalkableAround(player, map, target, radius);

            if (targets.Count == 0) return false;

            if (!AutoPathRoutePlanner.TryBuildCurrentPathTo(player, targets, false, out List<Point> path)) return false;

            if (path == null || path.Count < 2) return false;

            next = path[1];

            return true;
        }

        /// <summary>收集目标周围的**可走**格子。</summary>
        /// <para>
        /// 过滤掉阻挡格很关键：NPC 自己那格必然是阻挡的，
        /// 不滤掉的话 A* 还是会挑中它当终点。
        /// </para>
        /// </summary>
        private static HashSet<Point> CollectWalkableAround(PlayerObject player, Map map, Point target, int radius)
        {
            HashSet<Point> result = new HashSet<Point>();

            if (radius < 0) radius = 0;

            for (int x = target.X - radius; x <= target.X + radius; x++)
            {
                for (int y = target.Y - radius; y <= target.Y + radius; y++)
                {
                    Point point = new Point(x, y);

                    Cell cell = map.GetCell(point);

                    if (cell == null) continue;

                    // 阻挡格（NPC 站的那格、墙、水）不能当终点。
                    // checker 传玩家本人：IsBlocking 内部会读 checker.Level，
                    // 传 null 会在隐身 Buff 分支直接 NRE。
                    if (cell.IsBlocking(player, false)) continue;

                    result.Add(point);
                }
            }

            return result;
        }

        /// <summary>追怪兜底：A* 算不出路、或者算出来的那一步被怪自己的格子挡住时用。
        /// 从「正对目标」开始向两边各偏 45°，挑第一个**可走、没被挡、且确实更接近目标**的格子。
        /// <para>追会跑的怪必须有这条兜底：怪站在 A* 的终点上，最后一步永远迈不进去。</para>
        /// </summary>
        public static bool TryStepAdjacent(PlayerObject player, Point target)
        {
            if (player?.CurrentMap == null || player.Dead) return false;
            if (!player.CanMove) return false;
            if (player.CurrentLocation == target) return false;

            MirDirection primary = Functions.DirectionFromPoint(player.CurrentLocation, target);
            int currentDistance = Distance(player.CurrentLocation, target);

            int[] offsets = { 0, 1, -1, 2, -2, 3, -3, 4 };

            foreach (int offset in offsets)
            {
                MirDirection direction = (MirDirection)(((int)primary + offset + 8) % 8);
                Point next = Functions.Move(player.CurrentLocation, direction, 1);
                Cell cell = player.CurrentMap.GetCell(next);

                if (cell == null) continue;

                // 与 PlayerObject.Move 的校验完全一致：只有越界（null）和格子被挡两种失败。
                if (cell.IsBlocking(player, false)) continue;

                // 不许绕远：偏 90° 以上往外挪的格子直接跳过。
                if (Distance(next, target) >= currentDistance) continue;

                player.Move(direction, 1);

                return true;
            }

            return false;
        }

        /// <summary>两点之间的曼哈顿距离，用于「够不够近」这类粗判断（与引擎的 Functions.Distance 不同，后者是对角距离）。</summary>
        public static int Manhattan(Point a, Point b)
        {
            return System.Math.Abs(a.X - b.X) + System.Math.Abs(a.Y - b.Y);
        }

        /// <summary>真实距离（含对角），与引擎保持一致。</summary>
        public static int Distance(Point a, Point b)
        {
            return (int)Functions.Distance(a, b);
        }

        /// <summary>
        /// 刺杀剑术直线遮挡检测（D13）：检查玩家正前方 2 格直线是否有阻挡格。
        /// <para>
        /// 刺杀剑术（<see cref="MagicType.Thrusting"/>）的 <c>SecondaryAttackLocation</c>
        /// 是 <c>AttackLocation(Move(CurrentLocation, Direction, 2), ...)</c> —— 打正前方 2 格直线。
        /// 若中途（第 1 格或第 2 格）有墙/怪/不可走格，技能打出去也是空放（命中不到目标），
        /// 与真人「隔位刺杀被挡就砍不出」一致。假人需在施放前校验，有遮挡则退回平砍或调整朝向。
        /// </para>
        /// <para>
        /// 用 <c>Cell.IsBlocking(player, false)</c> 判据（与 <see cref="TryStepAdjacent"/> 一致，
        /// 不含时序），逐格检查第 1、2 格。第 2 格是目标怪所在格时不算遮挡（怪本身不挡自己的技能命中，
        /// 引擎的 <c>AttackLocation</c> 会自行判定命中）。
        /// </para>
        /// <param name="player">施放者。</param>
        /// <param name="direction">正前方朝向。</param>
        /// <param name="targetLocation">目标位置（第 2 格若是目标则不判为遮挡）。</param>
        /// <returns>直线无遮挡返回 true。</returns>
        /// </summary>
        public static bool IsThrustingLineClear(PlayerObject player, MirDirection direction, Point? targetLocation = null)
        {
            if (player?.CurrentMap == null || player.Dead) return false;

            Point origin = player.CurrentLocation;

            // 刺杀剑术打 2 格直线：检查第 1 格与第 2 格。
            for (int step = 1; step <= 2; step++)
            {
                Point cell = Functions.Move(origin, direction, step);

                // 目标所在格不算遮挡（怪本身不挡技能命中，引擎的 AttackLocation 会自行判定）。
                if (targetLocation.HasValue && cell == targetLocation.Value)
                    continue;

                Cell c = player.CurrentMap.GetCell(cell);

                if (c == null) return false;          // 越界 = 直线不通
                if (c.IsBlocking(player, false)) return false;  // 有阻挡物
            }

            return true;
        }

        #region 路径缓存 + 走/跑决策

        /// <summary>取（或新建）假人的路径缓存。</summary>
        private static BotPathCache GetCache(PlayerObject player)
        {
            if (_pathCaches.TryGetValue(player, out BotPathCache cache))
                return cache;

            cache = new BotPathCache();
            _pathCaches.Add(player, cache);
            return cache;
        }

        /// <summary>清空缓存内容（不移除表项，下次复用）。被挡 / 路径耗尽时调用。</summary>
        private static void ClearCache(PlayerObject player)
        {
            if (_pathCaches.TryGetValue(player, out BotPathCache cache))
            {
                cache.Target = default;
                cache.Radius = -1;
                cache.Path = null;
                cache.Consumed = 0;
                cache.ExpiresAt = default;
            }
        }

        /// <summary>缓存是否仍可用于当前目标 / 半径 / 地图。</summary>
        private static bool IsCacheUsable(PlayerObject player, BotPathCache cache, Point target, int radius)
        {
            return cache != null
                && cache.Target == target
                && cache.Radius == radius
                && cache.Path != null
                && cache.Path.Count > 1
                && cache.Consumed + 1 < cache.Path.Count
                // 兜底对齐检查：缓存假设玩家正站在 Path[Consumed]。
                // 被击退 / 被顶开等意外位移会让缓存错位，此时必须重算（否则会朝错误方向跑）。
                && cache.Path[cache.Consumed] == player.CurrentLocation
                && cache.MapIndex == player.CurrentMap.Info.Index
                && SEnvir.Now < cache.ExpiresAt;
        }

        /// <summary>按目标 / 半径构建一条完整路径（单点终点或周围终点集合）。</summary>
        private static bool BuildPath(PlayerObject player, Point target, int radius, out List<Point> path)
        {
            if (radius < 0)
                return AutoPathRoutePlanner.TryBuildCurrentPath(player, target, false, out path);

            Map map = player.CurrentMap;

            HashSet<Point> targets = CollectWalkableAround(player, map, target, radius);

            if (targets.Count == 0)
            {
                path = null;
                return false;
            }

            return AutoPathRoutePlanner.TryBuildCurrentPathTo(player, targets, false, out path);
        }

        /// <summary>
        /// 取下一步（带缓存）：缓存可用则直接复用，否则重算 A* 并写入缓存。
        /// 同时返回这一步该「走」还是「跑」（<paramref name="distance"/> = 1 或 2）。
        /// </summary>
        private static bool TryGetCachedStep(PlayerObject player, Point target, int radius, out Point next, out int distance)
        {
            next = Point.Empty;
            distance = 1;

            BotPathCache cache = GetCache(player);

            if (!IsCacheUsable(player, cache, target, radius))
            {
                if (!BuildPath(player, target, radius, out List<Point> path) || path == null || path.Count < 2)
                {
                    ClearCache(player);
                    return false;
                }

                cache.Target = target;
                cache.Radius = radius;
                cache.Path = path;
                cache.Consumed = 0;
                cache.MapIndex = player.CurrentMap.Info.Index;
                cache.ExpiresAt = SEnvir.Now.AddSeconds(PathCacheMaxAgeSeconds);
            }

            next = cache.Path[cache.Consumed + 1];
            distance = ResolveMoveDistance(player, cache);

            return true;
        }

        /// <summary>
        /// 走 / 跑决策：直线连续无阻挡且不跑过头时返回 2（跑），否则返回 1（走）。
        /// <para>
        /// 复刻真人行为：直线连续跑、转弯走一步、最后一步走。
        /// 跑 2 格需要中间格无阻挡，<see cref="PlayerObject.Move"/> 内部会用 <c>IsBlocking</c>
        /// 校验每一格，被挡则整个请求被拒（坐标不变），调用方据此清缓存退化为走。
        /// </para>
        /// </summary>
        private static int ResolveMoveDistance(PlayerObject player, BotPathCache cache)
        {
            if (!Config.BotRunEnabled) return 1;

            // 剩余不足 2 格（next、next2 不存在）→ 走（末段不跑，避免跑过头）。
            if (cache.Consumed + 2 >= cache.Path.Count) return 1;

            Point next = cache.Path[cache.Consumed + 1];
            Point next2 = cache.Path[cache.Consumed + 2];

            // 转弯 → 走（只有直线才跑）。
            MirDirection dir1 = Functions.DirectionFromPoint(player.CurrentLocation, next);
            MirDirection dir2 = Functions.DirectionFromPoint(next, next2);

            if (dir1 != dir2) return 1;

            // 到终点距离不足 2 格 → 走（不跑过头）。
            int remaining = Manhattan(player.CurrentLocation, cache.Path[cache.Path.Count - 1]);

            if (remaining < 2) return 1;

            return 2;
        }

        /// <summary>按实际前进格数消费缓存（走消费 1、跑消费 2）。走到末格（无可再迈）则清缓存。</summary>
        private static void AdvanceCache(PlayerObject player, int steps)
        {
            if (_pathCaches.TryGetValue(player, out BotPathCache cache) && cache.Path != null)
            {
                cache.Consumed += steps;

                if (cache.Consumed + 1 >= cache.Path.Count)
                    ClearCache(player);
            }
        }

        #endregion
    }
}
