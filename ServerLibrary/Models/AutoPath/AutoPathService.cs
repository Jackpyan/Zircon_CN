using Library;
using Library.SystemModels;
using Server.Envir;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using S = Library.Network.ServerPackets;

namespace Server.Models.AutoPath
{
    public sealed class AutoPathService
    {
        public static AutoPathService Instance { get; } = new AutoPathService();

        /// <summary>
        /// 上一次规划失败的原因（诊断用，只读）。
        /// <para>
        /// 规划失败有八九种可能，对调用方来说都只是 <c>false</c>。失败日志和后台排查
        /// 都靠这一行区分「真的没路」和「起点格不存在 / 目标集合空 / 出口被规则卡住」。
        /// </para>
        /// </summary>
        public static string LastFailureReason => AutoPathRoutePlanner.LastFailure;

        private const int PathAdvanceSearchDistance = 4;

        /// <summary>
        /// 连续「没能前进一步」持续这么久，就放弃整条自动寻路。
        /// <para>
        /// 按**时间**算，不按帧数：<see cref="Process"/> 每帧被调一次，而帧率随负载浮动
        /// （30 个假人同屏时实测会明显掉帧），同样的 40 帧空载是 0.7 秒、满载可能两三秒，
        /// 容忍度会跟着人数漂移。4 秒足够等城镇里的人群挪开一步，也不至于让真卡死拖上一分钟。
        /// （大脑层另有一层 <c>BotBrain.TrackStuck</c>，那个按 tick 计，两者互补。）
        /// </para>
        /// </summary>
        private const int StallLimitMs = 4000;

        /// <summary>
        /// 「前方被活物占着」最多忍这么久。
        /// <para>
        /// 比 <see cref="StallLimitMs"/> 长得多，因为性质不同：城镇里被别的假人挤住是常态，
        /// 对方几百毫秒到几秒就会挪开，此时把整条自动寻路拆掉等于白扔一张传送卷。
        /// 真正堵死（窄口被两个不动的假人对夹）才该放弃。
        /// </para>
        /// </summary>
        private const int BlockedLimitMs = 15000;

        /// <summary>
        /// 一次「没前进」之后隔多久再试。
        /// <para>
        /// <see cref="Process"/> 每帧调一次，而一次重规划是整图 A*（实测 20~30 ms）。
        /// 不节流的话，一个卡住的假人就能让主线程每帧跑一次 A* —— 30 个假人压测时
        /// 8 个卡住角色 10 分钟重规划 6436 次、平均 29.1 ms，等于三成主线程在算路。
        /// 而「走不动」在几百毫秒内不会自己变，所以失败后先歇一下。
        /// </para>
        /// </summary>
        private const int RetryDelayMs = 400;

        private AutoPathService()
        {
        }

        public bool TryStart(PlayerObject player, MapInfo map, Point point)
        {
            if (player == null || map == null) return false;
            if (!Config.AllowWaypoints) return false;

            return Start(player, new AutoPathDestination
            {
                Map = map,
                Point = point,
            });
        }

        public bool TryStart(PlayerObject player, MapRegion region)
        {
            if (player == null || region?.Map == null) return false;
            if (!Config.AllowWaypoints) return false;

            return Start(player, new AutoPathDestination
            {
                Map = region.Map,
                Region = region,
            });
        }

        public bool TryStart(PlayerObject player, NPCInfo npc)
        {
            if (player == null || npc?.Region?.Map == null) return false;

            return Start(player, new AutoPathDestination
            {
                Map = npc.Region.Map,
                Region = npc.Region,
                NPC = npc,
            });
        }

        public bool TryAddWaypoint(PlayerObject player, MapInfo map, Point point)
        {
            if (player == null || map == null) return false;

            if (!Config.AllowWaypoints) return false;

            int maximum = Math.Max(0, Config.MaxWaypoints);
            if (maximum == 0 || player.AutoPath?.Destinations.Count >= maximum)
            {
                player.Connection.ReceiveChat(player.Connection.Language.AutoPathWaypointLimit, MessageType.System);
                return false;
            }

            AutoPathDestination destination = new AutoPathDestination
            {
                Map = map,
                Point = point,
            };

            if (player.AutoPath == null)
                return Start(player, destination);

            AutoPathState state = player.AutoPath;
            AutoPathRoute previous = state.Routes.LastOrDefault();
            MapInfo startMap = previous == null
                ? player.CurrentMap?.Info
                : SEnvir.MapInfoList.Binding.FirstOrDefault(x => x.Index == previous.DestinationMapIndex);
            Point start = previous?.Destination ?? player.CurrentLocation;

            if (!AutoPathRoutePlanner.TryBuild(player, startMap, start, destination, false, out AutoPathPlan plan))
            {
                player.Connection.ReceiveChat(player.Connection.Language.AutoPathNoRoute, MessageType.System);
                return false;
            }

            plan.Route.WaypointNumber = state.NextWaypointNumber++;
            state.Destinations.Add(destination);
            state.Routes.Add(plan.Route);
            SendRoutes(player, state);
            return true;
        }

        public bool TryBuildRoute(MapObject actor, MapInfo map, Point point, out AutoPathRoute route)
        {
            route = null;
            if (actor?.CurrentMap == null || map == null) return false;

            AutoPathDestination destination = new AutoPathDestination
            {
                Map = map,
                Point = point,
            };

            return TryBuildRoute(actor, destination, out route);
        }

        public bool TryBuildRoute(MapObject actor, MapRegion region, out AutoPathRoute route)
        {
            route = null;
            if (actor?.CurrentMap == null || region?.Map == null) return false;

            AutoPathDestination destination = new AutoPathDestination
            {
                Map = region.Map,
                Region = region,
            };

            return TryBuildRoute(actor, destination, out route);
        }

        public bool TryBuildCurrentPath(MapObject actor, Point destination, bool avoidLiveObjects, out List<Point> path)
        {
            return AutoPathRoutePlanner.TryBuildCurrentPath(actor, destination, avoidLiveObjects, out path);
        }

        private static bool TryBuildRoute(MapObject actor, AutoPathDestination destination, out AutoPathRoute route)
        {
            route = null;
            if (!AutoPathRoutePlanner.TryBuild(actor, destination, false, out AutoPathPlan plan))
                return false;

            route = plan.Route;
            return true;
        }

        public void Cancel(PlayerObject player, bool notifyClient = true)
        {
            if (player == null) return;

            EndAutoPathLog(player, player.AutoPath);
            player.AutoPath = null;

            if (notifyClient)
                player.Enqueue(new S.AutoPathChanged { Routes = new List<AutoPathRoute>() });
        }

        public void MoveStarted(PlayerObject player)
        {
            if (player?.AutoPath == null) return;

            player.AutoPath.WaitingForMovementStart = false;
        }

        public void Process(PlayerObject player)
        {
            AutoPathState state = player?.AutoPath;
            if (state == null) return;

            if (!Config.AllowWaypoints && state.Destination?.NPC == null)
            {
                Cancel(player);
                return;
            }

            // 死亡不是「没路」。这里**不能 Fail**：
            // 假人被打死的那一刻，大脑还没跑到 ProcessDead（它只在自己的 tick 里检查），
            // 于是会一直往 TryStart 里塞新任务，而 Process 每帧都把任务拆掉 ——
            // 日志上就是「STARTED / ENDED 同秒、一秒刷好几次、人一步不动」。
            // 2026-11-11 的压测日志里这种成对记录占全部失败的一半以上，根因就是这一行。
            // 挂起即可：复活后 RouteMap != CurrentMap 会自动重建路径。
            if (player.Dead) return;

            if (player.CurrentMap == null || !player.CurrentMap.Info.CanAutoPath)
            {
                Fail(player, player.CurrentMap == null
                    ? "没有当前地图"
                    : $"当前地图 {player.CurrentMap.Info.Description} 未开自动寻路");
                return;
            }

            if (state.WaitingForMovementStart) return;

            // 上一次「没前进」之后歇够了没有。放行前不做任何规划 —— 这个分支每帧都会走到，
            // 而一次重规划是整图 A*，不挡住的话卡住的假人会每帧重算一次（见 RetryDelayMs）。
            if (Environment.TickCount64 < state.NextAttemptTick) return;

            TraceSlowPath(player, state);

            if (ReachedDestination(player, state.Destination))
            {
                if (!AdvanceWaypoint(player, state))
                    Cancel(player);
                return;
            }

            if (player.PacketWaiting) return;

            bool avoidLiveObjects = state.LiveObjectRepathRequired;
            if (state.LiveObjectRepathRequired ||
                state.RouteMap != player.CurrentMap ||
                state.Path == null ||
                state.Path.Count == 0 ||
                !AdvancePath(state.Path, player.CurrentLocation))
            {
                if (!RebuildAny(player, state, avoidLiveObjects))
                {
                    if (!WaitAnotherAttempt(player, state, "重建路径失败")) return;
                }
            }

            if (ShouldMount(player))
            {
                if (!state.MountRequested)
                {
                    state.MountRequested = true;
                    player.Mount();
                }

                return;
            }

            if (!player.CanMove) return;

            if (state.Path.Count < 2)
            {
                if (!WaitAnotherAttempt(player, state,
                        $"路径只剩 {state.Path.Count} 点（ReachedDestination 却判否，{DescribeDestination(state)}）"))
                    return;
            }

            if (!TryGetExecutionStep(player, state, out MirDirection direction, out int distance))
            {
                if ((!RebuildAny(player, state, true) ||
                     !TryGetExecutionStep(player, state, out direction, out distance)))
                {
                    if (!WaitAnotherAttempt(player, state, "取不到执行步")) return;
                }
            }

            Point destination = Functions.Move(player.CurrentLocation, direction, distance);
            if (!HasClearTravel(player, player.CurrentMap, player.CurrentLocation, destination, true))
            {
                // 明知道下一步被活物占着就别调 Move 了：引擎会静默拒绝，
                // 这一步白算一次停滞（等于把容忍额度打对折），下一帧还要重建路径。
                WaitForLiveObject(player, state, destination);

                return;
            }

            Point before = player.CurrentLocation;

            player.Move(direction, distance);

            state.FullStrideAvailable = true;
            state.WaitingForMovementStart = true;

            // 只有**真的动了**才算有进展。引擎会静默拒绝走不过去的格子，
            // 光看「调过 Move」会把「一步没挪」误判成在前进。
            if (player.CurrentLocation != before)
                ClearStall(state);
            else
                WaitAnotherAttempt(player, state, $"引擎拒绝从 ({before.X},{before.Y}) 走向 ({destination.X},{destination.Y})");
        }

        /// <summary>
        /// 一次「没能前进一步」的记帐。返回 true = 还在容忍范围内，本帧什么都不做；
        /// 返回 false = 已经卡够久，<paramref name="player"/> 的自动寻路已被取消。
        ///
        /// <para>
        /// <b>为什么需要它</b>：自动寻路的所有失败分支以前都是「立刻 Fail」，
        /// 而调用方（假人大脑）下一 tick 就会再规划一次 —— 于是一次拥挤、一次死亡、
        /// 一个判定口径不一致，都会变成 4~6 次/秒、持续几十秒的 STARTED/ENDED 刷屏。
        /// 有了这层容忍，「被活物挤住」变成「等一会儿」，真的卡住也会在几秒内收尾并留下原因。
        /// </para>
        /// </summary>
        private bool WaitAnotherAttempt(PlayerObject player, AutoPathState state, string reason)
        {
            state.StallCount++;

            long now = Environment.TickCount64;

            if (state.StallSince == 0) state.StallSince = now;

            // 歇一下再试。不设这一行的话，卡住的假人会每帧重规划一次整图 A*
            // （30 假人压测实测：8 个卡住角色 10 分钟 6436 次、平均 29.1 ms，三成主线程）。
            state.NextAttemptTick = now + RetryDelayMs;

            long stalled = now - state.StallSince;

            if (stalled <= StallLimitMs) return true;

            Fail(player, $"连续 {state.StallCount} 次没能前进一步（卡了 {stalled / 1000d:F1} 秒）：{reason}");
            return false;
        }

        /// <summary>
        /// 下一步被活物占着时先等，不算「卡死」。
        ///
        /// <para>
        /// 和 <see cref="WaitAnotherAttempt"/> 分开，是因为语义完全不同：被别的假人挤住
        /// 是城镇里的常态，对方挪开就好了；用 4 秒的容忍度收尾会白扔一张传送卷并把
        /// 整张图拉黑 5 分钟（上一轮就是这么发现 <c>AutoPathToNpcDistance = 20</c> 的）。
        /// 这里给 <see cref="BlockedLimitMs"/>，期间每 <see cref="RetryDelayMs"/> 试一次，
        /// 顺带重算一次避让路径 —— 人群挪动之后，之前算不出的避让路可能就有解了。
        /// </para>
        /// </summary>
        private void WaitForLiveObject(PlayerObject player, AutoPathState state, Point blocked)
        {
            long now = Environment.TickCount64;

            if (state.BlockedSince == 0) state.BlockedSince = now;

            state.NextAttemptTick = now + RetryDelayMs;
            RequireLiveObjectRepath(state);

            if (now - state.BlockedSince <= BlockedLimitMs) return;

            state.BlockedSince = 0;
            Fail(player, $"前方 ({blocked.X},{blocked.Y}) 被活物占了 {BlockedLimitMs / 1000} 秒");
        }

        /// <summary>真的迈出一步（或换了一段路）就把停滞计数归零。</summary>
        private static void ClearStall(AutoPathState state)
        {
            state.StallCount = 0;
            state.StallSince = 0;
            state.BlockedSince = 0;
        }

        /// <summary>
        /// 重建路径：先按要求的避让语义算，算不出来再退回「不避让活物」。
        ///
        /// <para>
        /// 退这一步是必须的：<c>Start</c> 用的是乐观规划（不看活物），而执行层每步都要求
        /// 前方无活物。城镇里一堆假人挤在商店门口时，「避让活物」的 A* 会真的算不出路，
        /// 但「不避让」的路径是好的 —— 这时该等，不该把整条自动寻路拆掉。
        /// </para>
        /// </summary>
        private bool RebuildAny(PlayerObject player, AutoPathState state, bool avoidLiveObjects)
        {
            if (RebuildCurrentPath(player, state, avoidLiveObjects) || Rebuild(player, state, avoidLiveObjects))
                return true;

            if (!avoidLiveObjects) return false;

            return RebuildCurrentPath(player, state, false) || Rebuild(player, state, false);
        }

        private static bool ShouldMount(PlayerObject player)
        {
            return player.Character.Horse != HorseType.None &&
                   player.Horse == HorseType.None &&
                   player.CurrentMap.Info.CanHorse &&
                   !player.Dead;
        }

        private static void RequireLiveObjectRepath(AutoPathState state)
        {
            state.LiveObjectRepathRequired = true;
        }

        private static bool AdvancePath(List<Point> path, Point location)
        {
            if (path == null || path.Count == 0) return false;

            int searchCount = Math.Min(path.Count, PathAdvanceSearchDistance + 1);

            for (int i = 0; i < searchCount; i++)
            {
                if (path[i] != location) continue;

                if (i > 0)
                    path.RemoveRange(0, i);

                return true;
            }

            return false;
        }

        private static bool TryGetExecutionStep(PlayerObject player, AutoPathState state, out MirDirection direction, out int distance)
        {
            direction = MirDirection.Up;
            distance = 0;

            if (state.Path == null || state.Path.Count < 2) return false;

            int maximumDistance = GetMaximumDistance(player, state);
            int maximumAnchor = Math.Min(state.Path.Count - 1, maximumDistance);

            for (int i = maximumAnchor; i > 0; i--)
            {
                Point anchor = state.Path[i];
                if (!Functions.IsStraightEightDirection(player.CurrentLocation, anchor))
                    continue;

                if (!HasClearTravel(player, player.CurrentMap, player.CurrentLocation, anchor, state.PathAvoidsLiveObjects))
                    continue;

                direction = Functions.DirectionFromPoint(player.CurrentLocation, anchor);

                int xDistance = Math.Abs(anchor.X - player.CurrentLocation.X);
                int yDistance = Math.Abs(anchor.Y - player.CurrentLocation.Y);
                distance = Math.Max(xDistance, yDistance);

                if (IsEnteringMapTransition(player, state, direction, distance))
                    distance = 1;

                return distance > 0;
            }

            return false;
        }

        private static int GetMaximumDistance(PlayerObject player, AutoPathState state)
        {
            if (!state.FullStrideAvailable) return 1;

            return player.Horse == HorseType.None ? 2 : 3;
        }

        private static bool IsEnteringMapTransition(PlayerObject player, AutoPathState state, MirDirection direction, int distance)
        {
            return state.PathEndsAtMapTransition && Functions.Move(player.CurrentLocation, direction, distance) == state.Path[state.Path.Count - 1];
        }

        private static bool HasClearTravel(MapObject actor, Map map, Point start, Point destination, bool avoidLiveObjects)
        {
            Point current = start;

            while (current != destination)
            {
                MirDirection direction = Functions.DirectionFromPoint(current, destination);
                current = Functions.Move(current, direction);

                Cell cell = map.GetCell(current);
                if (cell == null || avoidLiveObjects && cell.IsBlocking(actor, false)) return false;
            }

            return true;
        }

        private bool Start(PlayerObject player, AutoPathDestination destination)
        {
            // 死了不规划。
            //
            // 从「玩家被打死」到「假人大脑跑到 ProcessDead」之间还隔着若干帧，
            // 这段时间里大脑仍会按原状态反复调用 TryStart：每次都规划成功（STARTED），
            // 然后被 Process 的死亡分支当帧作废（ENDED），日志上就是同秒成对刷屏、人一步不动。
            // 2026-11-11 压测日志里这类占全部「同秒失败」的一半以上。
            if (player.Dead) return false;

            AutoPathState state = player.AutoPath;
            if (state == null)
            {
                state = new AutoPathState();
                player.AutoPath = state;
            }

            EndAutoPathLog(player, state);
            state.Destinations.Clear();
            state.Routes.Clear();
            state.NextWaypointNumber = 1;
            state.Destinations.Add(destination);
            state.Route = null;
            state.RouteMap = null;
            state.Path = null;
            state.LiveObjectRepathRequired = false;
            state.PathAvoidsLiveObjects = false;
            state.MountRequested = false;
            state.WaitingForMovementStart = false;
            state.FullStrideAvailable = false;
            state.NextAttemptTick = 0;
            ClearStall(state);

            if (Rebuild(player, state))
            {
                StartAutoPathLog(player, state);
                return true;
            }

            Fail(player, $"启动时规划失败：{AutoPathRoutePlanner.LastFailure}");
            return false;
        }

        private bool Rebuild(PlayerObject player, AutoPathState state, bool avoidLiveObjects = false)
        {
            if (state?.Destination == null) return false;

            if (!AutoPathRoutePlanner.TryBuild(player, state.Destination, avoidLiveObjects, out AutoPathPlan plan))
                return false;

            bool mapChanged = state.RouteMap != null && state.RouteMap != player.CurrentMap;

            state.Route = plan.Route;
            if (state.Routes.Count == 0)
            {
                plan.Route.WaypointNumber = state.NextWaypointNumber++;
                state.Routes.Add(plan.Route);
            }
            else
            {
                plan.Route.WaypointNumber = state.Routes[0].WaypointNumber;
                state.Routes[0] = plan.Route;
            }
            state.RouteMap = player.CurrentMap;
            state.Path = plan.CurrentPath;
            state.PathEndsAtMapTransition = plan.ChangesMap;
            state.LiveObjectRepathRequired = false;
            state.PathAvoidsLiveObjects = avoidLiveObjects;

            if (mapChanged)
            {
                state.FullStrideAvailable = false;
                state.MountRequested = false;
            }

            SendRoutes(player, state);
            return true;
        }

        private bool RebuildCurrentPath(PlayerObject player, AutoPathState state, bool avoidLiveObjects)
        {
            AutoPathRouteLeg leg = state.Route?.Legs?.FirstOrDefault(x => x.MapIndex == player.CurrentMap.Info.Index);
            if (leg?.Points == null || leg.Points.Count == 0) return false;

            Point destination = leg.Points[leg.Points.Count - 1];
            if (!AutoPathRoutePlanner.TryBuildCurrentPath(player, destination, avoidLiveObjects, out List<Point> path))
                return false;

            bool mapChanged = state.RouteMap != player.CurrentMap;

            leg.Points = path;
            state.RouteMap = player.CurrentMap;
            state.Path = path;
            state.PathEndsAtMapTransition = player.CurrentMap.Info.Index != state.Route.DestinationMapIndex;
            state.LiveObjectRepathRequired = false;
            state.PathAvoidsLiveObjects = avoidLiveObjects;

            if (mapChanged)
            {
                state.FullStrideAvailable = false;
                state.MountRequested = false;
            }

            SendRoutes(player, state);
            return true;
        }

        private bool AdvanceWaypoint(PlayerObject player, AutoPathState state)
        {
            if (state.Destinations.Count == 0) return false;

            EndAutoPathLog(player, state);
            state.Destinations.RemoveAt(0);
            if (state.Routes.Count > 0)
                state.Routes.RemoveAt(0);

            if (state.Destinations.Count == 0) return false;

            state.Route = null;
            state.RouteMap = null;
            state.Path = null;
            state.FullStrideAvailable = false;
            state.LiveObjectRepathRequired = false;
            state.PathAvoidsLiveObjects = false;
            state.MountRequested = false;
            state.PathEndsAtMapTransition = false;
            state.WaitingForMovementStart = false;
            ClearStall(state);

            if (!Rebuild(player, state))
            {
                AutoPathLogger.Failed(player, DescribeDestination(state),
                    $"切到下一个航点时规划失败：{AutoPathRoutePlanner.LastFailure}");
                return false;
            }

            StartAutoPathLog(player, state);
            return true;
        }

        private static void StartAutoPathLog(PlayerObject player, AutoPathState state)
        {
            if (player?.CurrentMap == null || state?.Route == null) return;

            state.AutoPathSourceMap = player.CurrentMap.Info;
            state.AutoPathSource = player.CurrentLocation;
            state.AutoPathDestinationMap = state.Destination?.Map;
            state.AutoPathDestination = state.Route.Destination;
            state.AutoPathLogged = true;

            long now = Environment.TickCount64;
            state.StartedTick = now;
            state.TraceTick = now;

            AutoPathLogger.Started(player, state.AutoPathSourceMap, state.AutoPathSource, state.AutoPathDestinationMap, state.AutoPathDestination);
        }

        /// <summary>跑满这么久才值得补一行「停在哪」——几秒内正常走完的短途不用刷屏。</summary>
        private const int SlowPathReportMs = 3000;

        private static void EndAutoPathLog(PlayerObject player, AutoPathState state)
        {
            if (player == null || state?.AutoPathLogged != true) return;

            AutoPathLogger.Ended(player, state.AutoPathSourceMap, state.AutoPathSource, state.AutoPathDestinationMap, state.AutoPathDestination);

            long elapsed = state.StartedTick == 0 ? 0 : Environment.TickCount64 - state.StartedTick;

            if (elapsed >= SlowPathReportMs)
                AutoPathLogger.Stopped(player, state.AutoPathDestinationMap, state.AutoPathDestination,
                    player.CurrentLocation, player.CurrentMap?.Info, elapsed, state.Path?.Count ?? 0);

            state.AutoPathLogged = false;
            state.StartedTick = 0;
            state.TraceTick = 0;
        }

        /// <summary>长路追踪的间隔。</summary>
        private const int SlowPathTraceMs = 10000;

        /// <summary>
        /// 一条自动寻路跑太久就报一次进度。
        /// <para>
        /// 加这一行是因为实测有「同图 60 格走了 2~5 分钟」的个案：<c>ENDED</c> 只给起点，
        /// 看不出是稳步在走还是原地摆；<c>StallCount</c> 也抓不到来回摆动（每步都算「动了」）。
        /// </para>
        /// </summary>
        private static void TraceSlowPath(PlayerObject player, AutoPathState state)
        {
            if (state.StartedTick == 0) return;

            long now = Environment.TickCount64;
            long elapsed = now - state.StartedTick;

            if (elapsed < SlowPathTraceMs || now - state.TraceTick < SlowPathTraceMs) return;

            state.TraceTick = now;

            AutoPathLogger.Progress(player, state.AutoPathDestinationMap, state.AutoPathDestination,
                player.CurrentLocation, player.CurrentMap?.Info, elapsed, state.Path?.Count ?? 0, state.StallCount);
        }

        private static void SendRoutes(PlayerObject player, AutoPathState state)
        {
            player.Enqueue(new S.AutoPathChanged
            {
                Routes = state.Routes.ToList(),
            });
        }

        private static bool ReachedDestination(PlayerObject player, AutoPathDestination destination)
        {
            if (player.CurrentMap.Info != destination.Map) return false;

            if (destination.NPC != null)
            {
                NPCObject npc = player.CurrentMap.NPCs.FirstOrDefault(x => x.NPCInfo == destination.NPC && x.Visible);

                if (npc != null)
                    return Functions.InRange(player.CurrentLocation, npc.CurrentLocation, 1);

                return destination.Region?.PointList?.Any(x => Functions.InRange(player.CurrentLocation, x, 1)) == true;
            }

            if (destination.Region != null)
                return destination.Region.PointList?.Contains(player.CurrentLocation) == true;

            return player.CurrentLocation == destination.Point;
        }

        private void Fail(PlayerObject player, string reason)
        {
            AutoPathLogger.Failed(player, DescribeDestination(player?.AutoPath), reason);

            Cancel(player);

            player.Connection.ReceiveChat(player.Connection.Language.AutoPathNoRoute, MessageType.System);
        }

        /// <summary>把目的地摊成人话，只用于失败日志。</summary>
        private static string DescribeDestination(AutoPathState state)
        {
            AutoPathDestination destination = state?.Destination;

            if (destination?.Map == null) return "（无目的地）";

            string kind = destination.NPC != null
                ? $"NPC '{destination.NPC.NPCName}'"
                : destination.Region != null
                    ? $"区域 '{destination.Region.Description}'"
                    : "指定点";

            return $"{destination.Map.Description}[{destination.Map.FileName}] " +
                   $"({destination.Point.X},{destination.Point.Y}) {kind}";
        }
    }
}
