using Library;
using Library.SystemModels;
using Server.Envir;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;

namespace Server.Models.AutoPath
{
    internal sealed class AutoPathPlan
    {
        public AutoPathRoute Route;
        public List<Point> CurrentPath;
        public bool ChangesMap;
    }

    internal static class AutoPathRoutePlanner
    {
        private const int PathGuideDistance = 48;
        private const int NormalPathCost = 8;

        /// <summary>一「跳」最多试几个候选出口（每个候选要跑一次整图 A*，这是唯一的重活）。</summary>
        private const int MaxHopCandidates = 6;

        /// <summary>单次规划超过这么久就打一行日志（用来盯回归）。</summary>
        private const long SlowPlanMilliseconds = 8;

        /// <summary>
        /// 启发式权重（百分比）。100 = 标准 A*（路径最短，但迷宫图上展开得多）。
        ///
        /// <para>
        /// 这里用 150：迷宫型大地图（剧毒之地 800×800 / 38 万可走格）从一头走到另一头，
        /// 标准 A* 的启发式在墙里估不准，会退化成 Dijkstra 铺满整张图。
        /// 加权后展开量大幅下降，代价是路径可能比最短的长几个百分点 —— 玩家感知不到，
        /// 但省下的是主线程上几十毫秒。
        /// </para>
        /// </summary>
        private const int HeuristicWeightPercent = 150;

        /// <summary>本次规划里跑了几次 A*（只在游戏主线程用，所以 thread-static 就够）。</summary>
        [ThreadStatic] private static int _AStarCalls;

        /// <summary>
        /// 上一次 <see cref="TryBuild(MapObject, AutoPathDestination, bool, out AutoPathPlan)"/> 失败的原因。
        ///
        /// <para>
        /// <b>为什么需要它</b>：规划失败有八九种可能（起点格不存在、目标集合空、地图不连通、
        /// 出口被等级/道具卡住、A* 找不到路……），对调用方来说都只是 <c>false</c>。
        /// 少了这一行，线上只能看到「STARTED/ENDED 同秒」却查不出为什么，
        /// 2026-11-11 的「假人卡住刷失败」就是这么耗掉的。
        /// </para>
        /// </summary>
        public static string LastFailure => _lastFailure;

        [ThreadStatic] private static string _lastFailure;

        private static bool Fail(string reason)
        {
            _lastFailure = reason;
            return false;
        }

        /// <summary>A* 的复用缓冲，按地图尺寸懒分配。</summary>
        [ThreadStatic] private static SearchScratch _scratch;

        private static readonly MirDirection[] Directions =
        {
            MirDirection.Up, MirDirection.UpRight, MirDirection.Right, MirDirection.DownRight,
            MirDirection.Down, MirDirection.DownLeft, MirDirection.Left, MirDirection.UpLeft
        };

        /// <summary>
        /// <see cref="Directions"/> 每一项对应的坐标偏移。
        /// 必须和数组一一对应 —— 顺手把 <c>Functions.Move</c> 从热循环里拿掉，
        /// 一次寻路要调它上百万次。
        /// </summary>
        private static readonly int[] DirectionOffsetX = { 0, 1, 1, 1, 0, -1, -1, -1 };
        private static readonly int[] DirectionOffsetY = { -1, -1, 0, 1, 1, 1, 0, -1 };

        public static bool TryBuild(MapObject actor, AutoPathDestination destination, bool avoidLiveObjects, out AutoPathPlan plan)
            => TryBuild(actor, actor?.CurrentMap?.Info, actor?.CurrentLocation ?? Point.Empty, destination, avoidLiveObjects, out plan);

        /// <summary>
        /// 规划一条到目标的路。
        ///
        /// <para>
        /// <b>只规划「当前这张图」这一段</b>：走到本图通往下一张图的门口就结束，剩下的等玩家真的
        /// 走进下一张图再算（<c>AutoPathService</c> 一旦发现 <c>RouteMap != CurrentMap</c> 就会重建）。
        /// </para>
        ///
        /// <para>
        /// <b>为什么不是一次算完整条跨图路线</b>：老实现是「(地图, 格子) 上的 Dijkstra」，
        /// 每弹出一个节点就要对它那张图的每个出口跑一次整图 A*，而每个出口的落地区域里
        /// <b>每一个可走格都会变成一个节点</b>（实测落地区域平均 7~14 格、最大 32 格，
        /// 出口最多的图有 58 个出口）。于是 A* 调用次数 ≈ 节点数 × 出口数，
        /// 一条 5 张图的路线能跑到几百次整图 A* —— D5 压测里最坏 76.9 ms 卡在主线程上。
        /// </para>
        ///
        /// <para>
        /// 改成逐跳之后，单次规划的 A* 次数上界是 <see cref="MaxHopCandidates"/>，
        /// 和路线长度无关；总工作量摊到「每跨一张图算一次」上，每帧最多一次 A* 的钱。
        /// </para>
        ///
        /// <para>
        /// <b>代价</b>：① 路线选择从「全局最优」退成「每跳最优」（地图图几乎是一棵树，实测无差别）；
        /// ② <c>Route.Legs</c> 只剩当前图的这一段，所以在大地图里翻到路线后面的地图时看不到引导线
        /// （客户端本来就只画 <c>MapIndex == 当前显示图</c> 的那一段，走路完全不受影响）。
        /// </para>
        /// </summary>
        public static bool TryBuild(MapObject actor, MapInfo startMap, Point startLocation, AutoPathDestination destination, bool avoidLiveObjects, out AutoPathPlan plan)
        {
            plan = null;
            _lastFailure = null;

            if (actor?.CurrentMap == null) return Fail("没有当前地图");
            if (startMap == null) return Fail("起点地图为空");
            if (destination?.Map == null) return Fail("目的地地图为空");
            if (!startMap.CanAutoPath) return Fail($"起点图 {startMap.Description} 未开自动寻路");
            if (!destination.Map.CanAutoPath) return Fail($"目标图 {destination.Map.Description} 未开自动寻路");

            MapInfo destinationMapInfo = destination.Map;
            long startedAt = Stopwatch.GetTimestamp();
            long mapsDoneAt = startedAt;
            long startMapDoneAt = startedAt;
            int hops = 0;
            bool changesMap = false;

            _AStarCalls = 0;

            try
            {
                Dictionary<MapInfo, int> mapDistances = GetMapDistances(actor, destinationMapInfo);
                mapsDoneAt = Stopwatch.GetTimestamp();

                if (!mapDistances.TryGetValue(startMap, out int startDistance))
                    return Fail($"地图图不连通（{startMap.Description} 到不了 {destinationMapInfo.Description}，" +
                                $"可达图 {mapDistances.Count} 张）");

                Map startMapInstance = SEnvir.GetMap(startMap);
                startMapDoneAt = Stopwatch.GetTimestamp();

                if (startMapInstance == null) return Fail($"起点图 {startMap.Description} 加载失败");

                if (startMapInstance.GetCell(startLocation) == null)
                    return Fail($"起点格不存在 ({startLocation.X},{startLocation.Y})");

                bool avoidObjectsHere = avoidLiveObjects && startMap == actor.CurrentMap.Info;

                // ① 已经在目标图上：直接算到目标点，一次成型。
                if (startMap == destinationMapInfo)
                {
                    NPCObject destinationNPC = GetDestinationNPC(actor, startMapInstance, destination);
                    HashSet<Point> targets = GetDestinationTargets(startMapInstance, destination, destinationNPC);
                    if (targets.Count == 0) return Fail($"目标集合为空（NPC={destination.NPC?.NPCName ?? "-"}，" +
                                                         $"区域={destination.Region?.Description ?? "-"}，" +
                                                         $"点=({destination.Point.X},{destination.Point.Y})，" +
                                                         $"目标格{(startMapInstance.GetCell(destination.Point) == null ? "不存在" : "存在")}）");

                    if (!TryFindPreferredPath(actor, startMapInstance, startLocation, targets, avoidObjectsHere, out List<Point> path))
                        return Fail($"A* 找不到路径（同图，目标 {targets.Count} 格，" +
                                    $"避让活物 {avoidObjectsHere}，起点 ({startLocation.X},{startLocation.Y})）");

                    plan = new AutoPathPlan
                    {
                        Route = new AutoPathRoute
                        {
                            Legs = new List<AutoPathRouteLeg>
                            {
                                new AutoPathRouteLeg { MapIndex = startMap.Index, Points = path }
                            },
                            DestinationMapIndex = startMap.Index,
                            Destination = path[path.Count - 1],
                            DisplayDestination = GetDisplayDestination(destination, startMapInstance, destinationNPC, path[path.Count - 1]),
                        },
                        CurrentPath = path,
                        ChangesMap = false,
                    };
                    return true;
                }

                // ② 跨图：只算「走到本图出口」这一段。
                changesMap = true;

                List<Point> hopPath = null;

                int outgoing = 0, examined = 0, blockedByRules = 0, wrongDistance = 0;
                int emptySource = 0, astarFailed = 0, needHole = 0, emptyDestination = 0;

                foreach (MovementInfo movement in GetOutgoingMovements(startMap))
                {
                    outgoing++;

                    if (examined >= MaxHopCandidates) continue;
                    if (!CanUseMovement(actor, movement, startMap)) { blockedByRules++; continue; }

                    MapInfo nextMapInfo = movement.DestinationRegion?.Map;
                    if (nextMapInfo == null ||
                        !mapDistances.TryGetValue(nextMapInfo, out int nextDistance) ||
                        nextDistance != startDistance - 1)
                    {
                        wrongDistance++;
                        continue;
                    }

                    HashSet<Point> sourcePoints = GetValidPoints(
                        startMapInstance, GetRegionPoints(movement.SourceRegion, startMapInstance));
                    if (sourcePoints.Count == 0) { emptySource++; continue; }

                    examined++;
                    if (!TryFindPreferredPath(actor, startMapInstance, startLocation, sourcePoints, avoidObjectsHere, out List<Point> candidate))
                    {
                        astarFailed++;
                        continue;
                    }

                    if (movement.NeedHole && (startMap != actor.CurrentMap.Info ||
                        !actor.CurrentMap.Objects.OfType<SpellObject>().Any(x =>
                            x.Effect == SpellEffect.ZombieHole &&
                            x.CurrentLocation == candidate[candidate.Count - 1])))
                    {
                        needHole++;
                        continue;
                    }

                    Map nextMapInstance = SEnvir.GetMap(nextMapInfo);
                    if (nextMapInstance == null) continue;
                    if (GetValidPoints(nextMapInstance, GetRegionPoints(movement.DestinationRegion, nextMapInstance)).Count == 0)
                    {
                        emptyDestination++;
                        continue;
                    }

                    if (hopPath == null || candidate.Count < hopPath.Count)
                        hopPath = candidate;
                }

                if (hopPath == null)
                    return Fail($"跨图找不到可用出口（本图出口 {outgoing} 个，" +
                                $"规则拒绝 {blockedByRules} / 方向不对 {wrongDistance} / 来源区域空 {emptySource} / " +
                                $"A* 失败 {astarFailed} / 需要洞 {needHole} / 落地区域空 {emptyDestination} / " +
                                $"实际尝试 {examined} 个，" +
                                $"避让活物 {avoidObjectsHere}，起点 ({startLocation.X},{startLocation.Y})）");

                hops = 1;

                plan = new AutoPathPlan
                {
                    Route = new AutoPathRoute
                    {
                        Legs = new List<AutoPathRouteLeg>
                        {
                            new AutoPathRouteLeg { MapIndex = startMap.Index, Points = hopPath }
                        },
                        DestinationMapIndex = destinationMapInfo.Index,
                        Destination = hopPath[hopPath.Count - 1],
                        DisplayDestination = GetDisplayDestinationOn(destinationMapInfo, destination),
                    },
                    CurrentPath = hopPath,
                    ChangesMap = true,
                };
                return true;
            }
            finally
            {
                LogSlowPlan(actor, startMap, destinationMapInfo, hops, changesMap,
                    startedAt, mapsDoneAt, startMapDoneAt);
            }
        }

        /// <summary>
        /// 慢计划日志。**别删** —— 这一行是「逐跳规划」的唯一回归探针：
        /// 一旦哪天有人往出口循环里加了「每个落地点都试一遍」这类改动，这里会立刻响。
        ///
        /// <para>
        /// 分段计时是必须的：第一次实测出现过「A* 只有 1 次却花了 12 ms」，
        /// 原因是<b>第一次踩到一张没加载过的地图</b>（<c>Config.LazyLoadMaps</c> 延迟加载，
        /// <c>SEnvir.GetMap</c> 现场读 .map 文件 + 建格子 + 建 NPC/刷怪）。
        /// 光看总耗时会误判成 A* 慢。
        /// </para>
        /// </summary>
        private static void LogSlowPlan(MapObject actor, MapInfo startMap, MapInfo destinationMap,
            int hops, bool changesMap, long startedAt, long mapsDoneAt, long startMapDoneAt)
        {
            long elapsed = Elapsed(startedAt, Stopwatch.GetTimestamp());
            if (elapsed < SlowPlanMilliseconds) return;

            SEnvir.Log($"[AUTO PATH SLOW] {actor?.Name ?? "?"} {startMap?.Description}[{startMap?.FileName}] → " +
                       $"{destinationMap?.Description}[{destinationMap?.FileName}]，耗时 {elapsed} ms" +
                       $"（地图梯度 {Elapsed(startedAt, mapsDoneAt)} / 取起点图 {Elapsed(mapsDoneAt, startMapDoneAt)} / 其余 {Elapsed(startMapDoneAt, Stopwatch.GetTimestamp())}），" +
                       $"A* {_AStarCalls} 次，跳数 {hops}，跨图 {changesMap}");
        }

        private static long Elapsed(long from, long to)
            => (to - from) * 1000 / Stopwatch.Frequency;

        /// <summary>
        /// 目标图上的「显示用终点」——不跑 A*，只按目标类型取一个点。
        /// <para>
        /// 逐跳规划时手上没有目标图的路径，所以 <c>Route.Destination</c> 是「本图出口」的坐标，
        /// 不能直接拿去当 <c>DisplayDestination</c>（客户端只在「显示图 == 目标图」时用它画终点标记，
        /// 坐标必须是目标图上的）。这里单独按区域 / NPC / 指定点算一次。
        /// </para>
        /// </summary>
        private static Point GetDisplayDestinationOn(MapInfo destinationMapInfo, AutoPathDestination destination)
        {
            Map map = SEnvir.GetMap(destinationMapInfo);

            if (map == null) return destination.Point;

            if (destination.NPC != null)
            {
                NPCObject npc = map.NPCs.FirstOrDefault(x => x.NPCInfo == destination.NPC && x.Visible);
                if (npc != null) return npc.CurrentLocation;
            }

            if (destination.Region != null)
            {
                Point centre = RegionCentre(map, destination.Region);
                if (centre != Point.Empty) return centre;
            }

            return destination.Point;
        }

        private static Point RegionCentre(Map map, MapRegion region)
        {
            int minX = int.MaxValue, maxX = int.MinValue;
            int minY = int.MaxValue, maxY = int.MinValue;
            bool any = false;

            foreach (Point point in GetRegionPoints(region, map))
            {
                any = true;
                if (point.X < minX) minX = point.X;
                if (point.X > maxX) maxX = point.X;
                if (point.Y < minY) minY = point.Y;
                if (point.Y > maxY) maxY = point.Y;
            }

            return any ? new Point((minX + maxX) / 2, (minY + maxY) / 2) : Point.Empty;
        }

        public static bool TryBuildCurrentPath(MapObject actor, Point destination, bool avoidLiveObjects, out List<Point> path)
        {
            path = null;
            _lastFailure = null;

            if (actor?.CurrentMap?.GetCell(destination) == null)
                return Fail($"当前路径终点格不存在 ({destination.X},{destination.Y})");

            return TryFindPreferredPath(actor, actor.CurrentMap, actor.CurrentLocation, new HashSet<Point> { destination }, avoidLiveObjects, out path);
        }

        /// <summary>
        /// 当前图内规划一条到「目标集合中任一点」的路径。
        /// <para>
        /// 与 <see cref="TryBuildCurrentPath"/> 的区别：终点是**一组**格子而不是单个格子。
        /// </para>
        /// <para>
        /// 为什么需要它：NPC / 怪就站在自己那格上，那格是阻挡物，物理上迈不进去。
        /// 单终点 A* 必然把最后一步指向那格 → 每帧都被引擎拒绝 → 永远走不到。
        /// 把「目标周围的可走格」当终点集合，才能真正走到 NPC 跟前。
        /// </para>
        /// </summary>
        public static bool TryBuildCurrentPathTo(MapObject actor, HashSet<Point> destinations, bool avoidLiveObjects, out List<Point> path)
        {
            path = null;

            if (actor?.CurrentMap == null) return false;
            if (destinations == null || destinations.Count == 0) return false;

            HashSet<Point> targets = new HashSet<Point>();

            foreach (Point point in destinations)
            {
                if (actor.CurrentMap.GetCell(point) == null) continue;

                targets.Add(point);
            }

            if (targets.Count == 0) return false;

            return TryFindPreferredPath(actor, actor.CurrentMap, actor.CurrentLocation, targets, avoidLiveObjects, out path);
        }

        private static Dictionary<MapInfo, int> GetMapDistances(MapObject actor, MapInfo destination)
        {
            Dictionary<MapInfo, int> result = new Dictionary<MapInfo, int>
            {
                [destination] = 0,
            };
            Queue<MapInfo> pending = new Queue<MapInfo>();
            pending.Enqueue(destination);

            while (pending.Count > 0)
            {
                MapInfo current = pending.Dequeue();
                int distance = result[current] + 1;

                foreach (MovementInfo movement in GetIncomingMovements(current))
                {
                    MapInfo source = movement.SourceRegion?.Map;
                    if (source == null || result.ContainsKey(source) || !source.CanAutoPath ||
                        !CanUseMovement(actor, movement, source))
                        continue;

                    result[source] = distance;
                    pending.Enqueue(source);
                }
            }

            return result;
        }

        private static IEnumerable<MovementInfo> GetOutgoingMovements(MapInfo map)
        {
            if (map?.Regions == null) yield break;

            foreach (MapRegion region in map.Regions)
            {
                if (region.SourceMovements == null) continue;

                foreach (MovementInfo movement in region.SourceMovements)
                    yield return movement;
            }
        }

        private static IEnumerable<MovementInfo> GetIncomingMovements(MapInfo map)
        {
            if (map?.Regions == null) yield break;

            foreach (MapRegion region in map.Regions)
            {
                if (region.DestinationMovements == null) continue;

                foreach (MovementInfo movement in region.DestinationMovements)
                    yield return movement;
            }
        }

        private static bool CanUseMovement(MapObject actor, MovementInfo movement, MapInfo sourceMap)
        {
            MapInfo destination = movement.DestinationRegion?.Map;

            if (destination == null || !destination.CanAutoPath || movement.NeedInstance != null)
                return false;

            if (actor is not PlayerObject player)
                return true;

            if (destination.MinimumLevel > player.Level && !player.Character.Account.TempAdmin)
                return false;

            if (destination.MaximumLevel > 0 && destination.MaximumLevel < player.Level &&
                !player.Character.Account.TempAdmin)
                return false;

            if (movement.NeedItem != null && player.GetItemCount(movement.NeedItem) == 0)
                return false;

            if (movement.NeedSpawn != null)
            {
                SpawnInfo spawn = SEnvir.Spawns.FirstOrDefault(x => x.Info == movement.NeedSpawn);
                if (spawn?.AliveCount == 0 ||
                    (spawn == null && sourceMap == actor.CurrentMap.Info))
                    return false;
            }

            if (destination.RequiredClass == RequiredClass.None ||
                destination.RequiredClass == RequiredClass.All)
                return true;

            RequiredClass required = player.Class switch
            {
                MirClass.Warrior => RequiredClass.Warrior,
                MirClass.Wizard => RequiredClass.Wizard,
                MirClass.Taoist => RequiredClass.Taoist,
                MirClass.Assassin => RequiredClass.Assassin,
                _ => RequiredClass.None,
            };

            return (destination.RequiredClass & required) == required;
        }

        private static NPCObject GetDestinationNPC(MapObject actor, Map map, AutoPathDestination destination)
        {
            if (destination.NPC == null || actor.CurrentMap != map) return null;

            return map.NPCs.FirstOrDefault(x => x.NPCInfo == destination.NPC && x.Visible);
        }

        private static HashSet<Point> GetDestinationTargets(Map map, AutoPathDestination destination, NPCObject destinationNPC)
        {
            if (destinationNPC != null)
                return GetAdjacentPoints(map, destinationNPC.CurrentLocation);

            if (destination.Region != null)
            {
                if (destination.NPC == null)
                    return GetValidPoints(
                        map, GetRegionPoints(destination.Region, map));

                HashSet<Point> adjacent = new HashSet<Point>();
                foreach (Point point in GetRegionPoints(destination.Region, map))
                    adjacent.UnionWith(GetAdjacentPoints(map, point));
                if (adjacent.Count > 0) return adjacent;
            }

            return map.GetCell(destination.Point) == null
                ? new HashSet<Point>()
                : new HashSet<Point> { destination.Point };
        }

        private static IEnumerable<Point> GetRegionPoints(MapRegion region, Map map)
        {
            if (region == null) return Enumerable.Empty<Point>();

            return region.PointList ??
                   (IEnumerable<Point>)region.GetPoints(map.Width);
        }

        private static Point GetDisplayDestination(AutoPathDestination destination, Map map, NPCObject destinationNPC, Point routeDestination)
        {
            if (destinationNPC != null) return destinationNPC.CurrentLocation;
            if (destination.NPC == null || destination.Region == null) return routeDestination;

            List<Point> points = GetRegionPoints(destination.Region, map).ToList();
            if (points.Count == 0) return routeDestination;

            int minX = points.Min(x => x.X);
            int maxX = points.Max(x => x.X);
            int minY = points.Min(x => x.Y);
            int maxY = points.Max(x => x.Y);

            return new Point((minX + maxX) / 2, (minY + maxY) / 2);
        }

        private static HashSet<Point> GetAdjacentPoints(Map map, Point centre)
        {
            HashSet<Point> result = new HashSet<Point>();
            foreach (MirDirection direction in Directions)
            {
                Point point = Functions.Move(centre, direction);
                if (map.GetCell(point) != null)
                    result.Add(point);
            }

            return result;
        }

        private static HashSet<Point> GetValidPoints(Map map, IEnumerable<Point> points)
        {
            return points == null
                ? new HashSet<Point>()
                : new HashSet<Point>(points.Where(point => map.GetCell(point) != null));
        }

        private static bool TryFindPreferredPath(MapObject actor, Map map, Point start, HashSet<Point> targets, bool avoidLiveObjects, out List<Point> path)
        {
            path = null;
            if (targets.Count == 0) return Fail($"终点集合为空（{map?.Info?.Description}）");

            if (!TryFindPath(actor, map, start, targets, avoidLiveObjects, null,
                    out List<Point> directPath))
                return false;

            HashSet<Point> guides = (map.Info.Regions ?? Enumerable.Empty<MapRegion>())
                .Where(x => x.RegionType == RegionType.Path)
                .SelectMany(x => GetRegionPoints(x, map))
                .Where(point => map.GetCell(point) != null)
                .ToHashSet();

            if (guides.Count == 0)
            {
                path = directPath;
                return true;
            }

            bool[,] directPathArea = CreatePathArea(map, directPath);
            guides.RemoveWhere(point => !directPathArea[point.X, point.Y]);

            if (guides.Count == 0 ||
                !TryFindPath(actor, map, start, targets, avoidLiveObjects, guides, out path))
                path = directPath;

            return true;
        }

        private static bool[,] CreatePathArea(Map map, IEnumerable<Point> path)
        {
            bool[,] result = new bool[map.Width, map.Height];
            Queue<(Point Point, int Distance)> pending =
                new Queue<(Point Point, int Distance)>();

            foreach (Point point in path)
            {
                if (result[point.X, point.Y]) continue;

                result[point.X, point.Y] = true;
                pending.Enqueue((point, 0));
            }

            while (pending.TryDequeue(out var current))
            {
                if (current.Distance >= PathGuideDistance) continue;

                foreach (MirDirection direction in Directions)
                {
                    Point next = Functions.Move(current.Point, direction);
                    if (next.X < 0 || next.X >= map.Width ||
                        next.Y < 0 || next.Y >= map.Height ||
                        result[next.X, next.Y])
                        continue;

                    result[next.X, next.Y] = true;
                    pending.Enqueue((next, current.Distance + 1));
                }
            }

            return result;
        }

        /// <summary>
        /// 单张图上的 A*。**这是整个自动寻路里唯一的重活**，所以实现上刻意避开了
        /// <c>Dictionary&lt;Point, ...&gt;</c> / <c>HashSet&lt;Point&gt;</c>：
        ///
        /// <para>
        /// 老实现用三个 <c>Point</c> 为键的字典 + 没有 decrease-key 的
        /// <c>PriorityQueue</c>。迷宫型大地图（剧毒之地、西部荒地、潘夜神殿 8 层）
        /// 从一头走到另一头要展开十几万个格子，每个格子 8 个邻居、每个邻居都要做
        /// 哈希查找和一次入队 —— 离线基准实测最坏 <b>101 ms</b>，而且同一个节点会被
        /// 反复入队，队列长度爆炸。
        /// </para>
        ///
        /// <para>
        /// 现在改成：一维数组 + 访问代次戳（免清零）+ 带 decrease-key 的二叉堆，
        /// 每个节点只入队一次。坐标用 <c>y * Width + x</c> 编码成 int，邻居用
        /// 预置偏移量算，格子直接查 <c>Map.Cells</c>。语义完全不变（8 方向、步长代价
        /// 1 或 <see cref="NormalPathCost"/>、可选避让活物、可选偏好格）。
        /// </para>
        /// </summary>
        private static bool TryFindPath(MapObject actor, Map map,
            Point start, HashSet<Point> targets, bool avoidLiveObjects,
            HashSet<Point> preferredPoints, out List<Point> path)
        {
            path = null;

            // 计数只服务于「慢计划日志」，别拿它做业务判断。
            _AStarCalls++;

            if (targets.Contains(start))
            {
                path = new List<Point> { start };
                return true;
            }

            int width = map.Width;
            int height = map.Height;

            if (width <= 0 || height <= 0)
                return Fail($"地图尺寸异常 {width}x{height}（{map.Info?.Description}）");

            if (start.X < 0 || start.X >= width || start.Y < 0 || start.Y >= height)
                return Fail($"起点越界 ({start.X},{start.Y})，地图 {width}x{height}");

            GetBounds(targets, out int minimumX, out int maximumX, out int minimumY, out int maximumY);

            SearchScratch scratch = _scratch ??= new SearchScratch();
            scratch.Ensure(width * height);

            int generation = scratch.NextGeneration();
            scratch.Reset();

            Cell[,] cells = map.Cells;
            bool avoid = avoidLiveObjects && actor.CurrentMap == map;

            int startIndex = start.Y * width + start.X;

            scratch.Cost[startIndex] = 0;
            scratch.Previous[startIndex] = -1;
            scratch.Stamp[startIndex] = generation;

            int startHeuristic = DistanceToBounds(start.X, start.Y, minimumX, maximumX, minimumY, maximumY);

            HeapPush(scratch, startIndex, HeapKey(startHeuristic * HeuristicWeightPercent / 100, startHeuristic));

            // 只服务于失败日志（「展开多少格」是判断「走不通」还是「搜索被截断」的关键）。
            int expanded = 0;

            while (scratch.HeapCount > 0)
            {
                int index = HeapPop(scratch);

                if (scratch.Closed[index] == generation) continue;
                scratch.Closed[index] = generation;
                expanded++;

                int x = index % width;
                int y = index / width;

                if (targets.Contains(new Point(x, y)))
                {
                    path = ReconstructPath(scratch, index, width, generation);
                    return true;
                }

                int currentCost = scratch.Cost[index];

                for (int direction = 0; direction < 8; direction++)
                {
                    int nextX = x + DirectionOffsetX[direction];
                    int nextY = y + DirectionOffsetY[direction];

                    if (nextX < 0 || nextX >= width || nextY < 0 || nextY >= height) continue;

                    Cell cell = cells[nextX, nextY];
                    if (cell == null) continue;
                    if (avoid && cell.IsBlocking(actor, false)) continue;

                    int nextIndex = nextY * width + nextX;
                    if (scratch.Closed[nextIndex] == generation) continue;

                    int stepCost = preferredPoints == null || preferredPoints.Contains(new Point(nextX, nextY))
                        ? 1
                        : NormalPathCost;

                    int newCost = currentCost + stepCost;

                    if (scratch.Stamp[nextIndex] == generation && scratch.Cost[nextIndex] <= newCost)
                        continue;

                    scratch.Stamp[nextIndex] = generation;
                    scratch.Cost[nextIndex] = newCost;
                    scratch.Previous[nextIndex] = index;

                    int heuristic = DistanceToBounds(nextX, nextY, minimumX, maximumX, minimumY, maximumY);
                    long key = HeapKey(newCost + heuristic * HeuristicWeightPercent / 100, heuristic);

                    if (scratch.HeapPosition[nextIndex] >= 0)
                        HeapDecreaseKey(scratch, nextIndex, key);
                    else
                        HeapPush(scratch, nextIndex, key);
                }
            }

            return Fail($"A* 找不到路径（{map.Info?.Description} 起点 ({start.X},{start.Y})，" +
                        $"目标 {targets.Count} 格，避让活物 {avoid}，展开 {expanded} 格）");
        }

        /// <summary>堆键：主键是 f 值，同 f 时优先 h 更小的（更贴近目标），减少无谓展开。</summary>
        private static long HeapKey(int total, int heuristic)
            => ((long)total << 32) | (uint)heuristic;

        private static void HeapPush(SearchScratch scratch, int node, long key)
        {
            int position = scratch.HeapCount++;

            scratch.HeapNode[position] = node;
            scratch.HeapKey[position] = key;
            scratch.HeapPosition[node] = position;

            SiftUp(scratch, position);
        }

        private static void HeapDecreaseKey(SearchScratch scratch, int node, long key)
        {
            int position = scratch.HeapPosition[node];

            if (position < 0 || scratch.HeapKey[position] <= key) return;

            scratch.HeapKey[position] = key;
            SiftUp(scratch, position);
        }

        private static void SiftUp(SearchScratch scratch, int position)
        {
            while (position > 0)
            {
                int parent = (position - 1) >> 1;

                if (scratch.HeapKey[parent] <= scratch.HeapKey[position]) return;

                HeapSwap(scratch, parent, position);
                position = parent;
            }
        }

        private static void SiftDown(SearchScratch scratch, int position)
        {
            while (true)
            {
                int left = position * 2 + 1;

                if (left >= scratch.HeapCount) return;

                int right = left + 1;

                int smallest = right < scratch.HeapCount && scratch.HeapKey[right] < scratch.HeapKey[left]
                    ? right
                    : left;

                if (scratch.HeapKey[position] <= scratch.HeapKey[smallest]) return;

                HeapSwap(scratch, position, smallest);
                position = smallest;
            }
        }

        private static void HeapSwap(SearchScratch scratch, int a, int b)
        {
            int nodeA = scratch.HeapNode[a];
            int nodeB = scratch.HeapNode[b];

            long keyA = scratch.HeapKey[a];
            long keyB = scratch.HeapKey[b];

            scratch.HeapNode[a] = nodeB;
            scratch.HeapKey[a] = keyB;
            scratch.HeapPosition[nodeB] = a;

            scratch.HeapNode[b] = nodeA;
            scratch.HeapKey[b] = keyA;
            scratch.HeapPosition[nodeA] = b;
        }

        private static int HeapPop(SearchScratch scratch)
        {
            int node = scratch.HeapNode[0];

            scratch.HeapPosition[node] = -1;
            scratch.HeapCount--;

            if (scratch.HeapCount > 0)
            {
                scratch.HeapNode[0] = scratch.HeapNode[scratch.HeapCount];
                scratch.HeapKey[0] = scratch.HeapKey[scratch.HeapCount];
                scratch.HeapPosition[scratch.HeapNode[0]] = 0;

                SiftDown(scratch, 0);
            }

            return node;
        }

        private static void GetBounds(HashSet<Point> points,
            out int minimumX, out int maximumX, out int minimumY, out int maximumY)
        {
            minimumX = int.MaxValue;
            maximumX = int.MinValue;
            minimumY = int.MaxValue;
            maximumY = int.MinValue;

            foreach (Point point in points)
            {
                minimumX = Math.Min(minimumX, point.X);
                maximumX = Math.Max(maximumX, point.X);
                minimumY = Math.Min(minimumY, point.Y);
                maximumY = Math.Max(maximumY, point.Y);
            }
        }

        private static int DistanceToBounds(
            int x, int y, int minimumX, int maximumX, int minimumY, int maximumY)
        {
            int xDistance = x < minimumX
                ? minimumX - x
                : x > maximumX
                    ? x - maximumX
                    : 0;
            int yDistance = y < minimumY
                ? minimumY - y
                : y > maximumY
                    ? y - maximumY
                    : 0;

            return Math.Max(xDistance, yDistance);
        }

        private static List<Point> ReconstructPath(SearchScratch scratch, int end, int width, int generation)
        {
            List<Point> result = new List<Point>();

            int index = end;

            while (index >= 0 && scratch.Stamp[index] == generation)
            {
                result.Add(new Point(index % width, index / width));
                index = scratch.Previous[index];
            }

            result.Reverse();
            return result;
        }

        /// <summary>
        /// A* 的可复用缓冲。按地图尺寸分配一次，之后靠「访问代次戳」区分本次/上次，
        /// 免得每次寻路都清零几十万个 int（那本身就是一次可见的卡顿）。
        /// <para>只在游戏主线程用，所以 thread-static 一个就够，不需要锁。</para>
        /// </summary>
        private sealed class SearchScratch
        {
            public int[] Cost;
            public int[] Previous;
            public int[] Stamp;
            public int[] Closed;

            /// <summary>节点在堆里的位置；-1 = 不在堆里。</summary>
            public int[] HeapPosition;

            public int[] HeapNode;
            public long[] HeapKey;
            public int HeapCount;

            private int _generation;

            /// <summary>
            /// 只增不减地保证容量。
            /// <para>
            /// <b>别按「尺寸完全相同」来判断复用</b>：索引是 <c>y * width + x</c>，
            /// 只要容量够大就能容纳任何更小的地图，而按尺寸判断会导致
            /// 「大地图 → 小地图 → 大地图」时反复重新分配（800×800 一次就是 20 MB，
            /// 分分钟变成 GC 尖峰 —— 离线基准里小地图出现 16 ms 假尖峰就是这么来的）。
            /// </para>
            /// </summary>
            public void Ensure(int size)
            {
                if (Cost != null && Cost.Length >= size) return;

                // 留一点余量，免得地图比上一张大一点点就重新分配。
                size = Math.Max(size, 1024) * 5 / 4;

                Cost = new int[size];
                Previous = new int[size];
                Stamp = new int[size];
                Closed = new int[size];
                HeapPosition = new int[size];
                HeapNode = new int[size];
                HeapKey = new long[size];

                // HeapPosition 默认 0 会被误判成「已经在堆里」，必须全部置 -1。
                Array.Fill(HeapPosition, -1);

                HeapCount = 0;
                _generation = 0;
            }

            /// <summary>
            /// 开始新一轮搜索。**必须先把上一轮还留在堆里的节点的位置标记清掉**，
            /// 否则新一轮的 decrease-key 会写到越界位置，把堆写坏。
            /// </summary>
            public void Reset()
            {
                for (int i = 0; i < HeapCount; i++)
                    HeapPosition[HeapNode[i]] = -1;

                HeapCount = 0;
            }

            public int NextGeneration()
            {
                if (_generation == int.MaxValue)
                {
                    Array.Clear(Stamp);
                    Array.Clear(Closed);
                    _generation = 0;
                }

                return ++_generation;
            }
        }

    }
}
