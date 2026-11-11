using Library;
using Server.Models;
using Server.Models.AutoPath;
using System.Drawing;
using System.Collections.Generic;

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
    /// </summary>
    public static class BotNavigator
    {
        /// <summary>朝目标迈一步。每次只走一格，下一步由调用方下一帧再算（怪会跑，缓存整条路径会走偏）。</summary>
        public static BotMoveResult TryStepTowards(PlayerObject player, Point target)
        {
            if (player == null || player.Dead || player.CurrentMap == null) return BotMoveResult.Blocked;

            if (player.CurrentLocation == target) return BotMoveResult.Arrived;

            if (!player.CanMove) return BotMoveResult.Busy;

            if (!TryGetNextStep(player, target, out Point next)) return BotMoveResult.Blocked;

            Point before = player.CurrentLocation;

            player.Move(Functions.DirectionFromPoint(player.CurrentLocation, next), 1);

            // Move 内部还有一道 IsBlocking 校验（A* 用的是不含时序的判据），
            // 被动态阻挡时坐标不会变 —— 直接当 Blocked 返回，让调用方走直线逼近的兜底。
            return player.CurrentLocation != before ? BotMoveResult.Moved : BotMoveResult.Blocked;
        }

        /// <summary>只算路不走路，用于「这个点到底去不去得」的预检和调试。</summary>
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
        /// 追怪兜底：A* 算不出路、或者算出来的那一步被怪自己的格子挡住时用。
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
    }
}
