using Library;
using Library.SystemModels;
using Server.Models.AutoPath;
using System.Collections.Generic;
using System.Drawing;

namespace Server.Models
{
    public partial class PlayerObject
    {
        internal AutoPathState AutoPath;

        public bool StartAutoPath(MapInfo map, Point point)
            => AutoPathService.Instance.TryStart(this, map, point);

        public bool StartAutoPath(MapRegion region)
            => AutoPathService.Instance.TryStart(this, region);

        public void CancelAutoPath()
            => AutoPathService.Instance.Cancel(this);
    }

    internal sealed class AutoPathState
    {
        public readonly List<AutoPathDestination> Destinations = new List<AutoPathDestination>();
        public readonly List<AutoPathRoute> Routes = new List<AutoPathRoute>();
        public int NextWaypointNumber = 1;

        public AutoPathDestination Destination => Destinations.Count == 0 ? null : Destinations[0];

        public AutoPathRoute Route;
        public Map RouteMap;
        public List<Point> Path;

        public bool AutoPathLogged;
        public MapInfo AutoPathSourceMap;
        public Point AutoPathSource;
        public MapInfo AutoPathDestinationMap;
        public Point AutoPathDestination;

        public bool FullStrideAvailable;
        public bool LiveObjectRepathRequired;
        public bool PathAvoidsLiveObjects;
        public bool MountRequested;
        public bool PathEndsAtMapTransition;
        public bool WaitingForMovementStart;

        /// <summary>本轮自动寻路的开始时刻（<see cref="System.Environment.TickCount64"/>），只给长路诊断用。</summary>
        public long StartedTick;

        /// <summary>上一次打长路诊断的时刻，用来限流（每 <c>SlowPathTraceMs</c> 最多一行）。</summary>
        public long TraceTick;

        /// <summary>
        /// 连续「没能前进一步」的次数。真的迈出一步就清零。
        /// <para>
        /// 用来区分「被别的假人挤住一会儿」和「这条路线根本走不通」：
        /// 前者应该等，后者应该在几秒内收尾。没有这个计数，
        /// 每个失败分支都只能立刻拆掉整条自动寻路，而大脑下一 tick 又会重新规划，
        /// 结果是每秒好几次 STARTED/ENDED、人一步不动（见 <c>AutoPathService.StallLimitMs</c>）。
        /// </para>
        /// </summary>
        public int StallCount;

        /// <summary>这一串「没前进」是从什么时候开始的（<see cref="System.Environment.TickCount64"/>），前进一次就归零。</summary>
        public long StallSince;

        /// <summary>
        /// 下一次允许「再试一次」的时刻（<see cref="System.Environment.TickCount64"/>）。
        /// <para>
        /// <see cref="AutoPathService.Process"/> 是每帧调用的，而一次重规划要 20~30 ms 的整图 A*。
        /// 走不动时如果每帧都重算，8 个假人就能吃掉三成主线程（实测 10 分钟 6436 次规划、
        /// 平均 29.1 ms）。而「走不动」这件事在几百毫秒内不会变，所以失败后先歇一下。
        /// </para>
        /// </summary>
        public long NextAttemptTick;

        /// <summary>
        /// 「前方被活物占着」是从什么时候开始的（<see cref="System.Environment.TickCount64"/>）。
        /// <para>
        /// 和 <see cref="StallSince"/> 分开计时：被别的假人挤住是城镇里的常态，
        /// 该多等一会儿（几秒就放弃等于白扔一张传送卷），而引擎拒绝移动之类的异常
        /// 才该用短的容忍度收尾。
        /// </para>
        /// </summary>
        public long BlockedSince;
    }

}
