using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace Server.Web.Services;

/// <summary>
/// 主机资源的一次采样结果。
/// <para>
/// <b>CPU / 内存是「整机」口径</b>（所有进程加起来），不是本进程 —— 运维在 SSH 里
/// 看到 CPU 99% 时，这里必须也是 99%。本进程的数字另存两份，只作为副信息显示
/// （用来回答「这台机器上到底是谁在吃 CPU」）。
/// </para>
/// </summary>
public sealed record HostResourceSample(
    double CpuPercent,
    long MemoryUsedBytes,
    long MemoryTotalBytes,
    long NetworkReceiveBytesPerSecond,
    long NetworkSendBytesPerSecond,
    double LoadAverage1,
    TimeSpan ProcessUptime,
    double ProcessCpuPercent,
    long ProcessMemoryBytes)
{
    public static readonly HostResourceSample Empty =
        new(0, 0, 0, 0, 0, 0, TimeSpan.Zero, 0, 0);
}

/// <summary>
/// 采集主机层面的 CPU / 内存 / 网卡流量 / 运行时长，跨平台、无第三方依赖。
/// <list type="bullet">
/// <item>整机 CPU：Linux 读 <c>/proc/stat</c> 的 <c>cpu</c> 汇总行；Windows 调 <c>GetSystemTimes</c>。</item>
/// <item>整机内存：Linux 读 <c>/proc/meminfo</c>（<c>MemTotal - MemAvailable</c>）；Windows 调 <c>GlobalMemoryStatusEx</c>。</item>
/// <item>网卡：<see cref="NetworkInterface"/> 的 IPv4 统计（Linux 读 <c>/proc/net/dev</c>，Windows 读 IP Helper）。</item>
/// <item>负载均值：只有 Linux 有，Windows 返回 0（页面据此隐藏）。</item>
/// </list>
/// <para>
/// 为什么不用 <c>GC.GetGCMemoryInfo().TotalAvailableMemoryBytes</c> 当总量：那是「GC 可用上限」，
/// 在容器里是 cgroup 限额、在没有限额时是物理内存，语义和「主机内存」并不一致；
/// 而且它给不出「整机已用」。所以改成直接读系统计数器。
/// </para>
/// </summary>
public sealed class HostResourceSampler
{
    private readonly object _sync = new();
    private readonly Process _self = Process.GetCurrentProcess();
    private readonly int _processorCount = Math.Max(1, Environment.ProcessorCount);

    private TimeSpan _lastProcessCpuTime;
    private double _lastHostBusy;
    private double _lastHostTotal;
    private long _lastReceived;
    private long _lastSent;
    private long _lastSampleTicks;
    private DateTimeOffset? _startedAt;

    /// <summary>进程启动时刻。Unix 下要读 /proc/&lt;pid&gt;/stat，失败就退化成"第一次采样的时刻"。</summary>
    private DateTimeOffset StartedAt
    {
        get
        {
            if (_startedAt is null)
            {
                try { _startedAt = new DateTimeOffset(_self.StartTime); }
                catch { _startedAt = DateTimeOffset.Now; }
            }

            return _startedAt.Value;
        }
    }

    public HostResourceSample Sample()
    {
        lock (_sync)
        {
            long nowTicks = Stopwatch.GetTimestamp();

            TimeSpan processCpuTime;
            long processMemory;
            try
            {
                // Unix 下这两个属性每次都重读 /proc，不要调 Refresh()（它会把整份 /proc 再解析一遍）。
                processCpuTime = _self.TotalProcessorTime;
                processMemory = _self.WorkingSet64;
            }
            catch
            {
                processCpuTime = _lastProcessCpuTime;
                processMemory = 0;
            }

            (double hostBusy, double hostTotal) = ReadHostCpuTimes();
            (long memoryUsed, long memoryTotal) = ReadHostMemory();

            // 整机计数器读不到（非 Linux/Windows、权限受限、容器没挂 /proc）→ 退回本进程口径，
            // 至少还能看出「这个程序自己在忙」。下面的 ProcessNote 会如实标注。
            bool hostCpuAvailable = hostTotal > 0;
            bool hostMemoryAvailable = memoryTotal > 0;

            if (!hostMemoryAvailable)
            {
                memoryUsed = processMemory;
                memoryTotal = ReadGcTotalMemory();
            }

            (long received, long sent) = ReadNetworkCounters();

            double cpuPercent = 0;
            double processCpuPercent = 0;
            long receiveRate = 0;
            long sendRate = 0;

            if (_lastSampleTicks != 0)
            {
                double elapsed = (nowTicks - _lastSampleTicks) / (double)Stopwatch.Frequency;

                if (elapsed > 0.05)
                {
                    // 本进程：TotalProcessorTime 是「所有核心累加」，除以核数才是占整机的比例。
                    double cpuSeconds = (processCpuTime - _lastProcessCpuTime).TotalSeconds;
                    processCpuPercent = Math.Clamp(cpuSeconds / (elapsed * _processorCount) * 100, 0, 100);

                    // 整机：计数器是「累计 CPU 时间」，两次相减得区间内的占用比。
                    double totalDelta = hostTotal - _lastHostTotal;
                    double busyDelta = hostBusy - _lastHostBusy;

                    // 计数器回绕 / 被清空 / 休眠唤醒会让差值为负或超过总量 → 这一拍按 0 处理，
                    // 千万别 Clamp 成 100%：那会在页面刷出一条假的高负载告警。
                    if (totalDelta > 0 && busyDelta >= 0 && busyDelta <= totalDelta)
                        cpuPercent = busyDelta / totalDelta * 100;

                    // 整机计数器读不到（非 Linux/Windows、容器没挂 /proc）→ 退回本进程，
                    // 宁可显示一个偏小的数字，也别让这一格永远是 0%。
                    if (!hostCpuAvailable) cpuPercent = processCpuPercent;

                    // 网卡掉线/重连会让累计值归零，差值为负就按 0 算，别让页面显示负流量。
                    receiveRate = (long)Math.Max(0, (received - _lastReceived) / elapsed);
                    sendRate = (long)Math.Max(0, (sent - _lastSent) / elapsed);
                }
            }

            _lastProcessCpuTime = processCpuTime;
            _lastHostBusy = hostBusy;
            _lastHostTotal = hostTotal;
            _lastReceived = received;
            _lastSent = sent;
            _lastSampleTicks = nowTicks;

            return new HostResourceSample(
                Math.Round(Math.Clamp(cpuPercent, 0, 100), 1),
                memoryUsed,
                memoryTotal,
                receiveRate,
                sendRate,
                ReadLoadAverage(),
                DateTimeOffset.Now - StartedAt,
                Math.Round(processCpuPercent, 1),
                processMemory);
        }
    }

    // ===== 整机 CPU =====

    /// <summary>
    /// 返回「累计忙时 / 累计总时」。单位随平台（Linux 是 USER_HZ，Windows 是 100ns），
    /// 只有比值有意义，所以不做归一。
    /// </summary>
    private static (double Busy, double Total) ReadHostCpuTimes()
    {
        if (OperatingSystem.IsLinux()) return ReadLinuxCpuTimes();
        if (OperatingSystem.IsWindows()) return ReadWindowsCpuTimes();

        return (0, 0);
    }

    private static (double Busy, double Total) ReadLinuxCpuTimes()
    {
        try
        {
            // 第一行形如：cpu  1234 56 789 101112 13 0 14 0 0 0
            //             user nice system idle iowait irq softirq steal guest guest_nice
            using StreamReader reader = new("/proc/stat");
            string? line = reader.ReadLine();

            if (line is null || !line.StartsWith("cpu ", StringComparison.Ordinal)) return (0, 0);

            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // parts[0] = "cpu"，后面是各状态累计时间。只取 user..steal 这 8 项：
            // guest 已包含在 user 里，再加会重复计数。
            double total = 0;
            double idle = 0;

            for (int index = 1; index < parts.Length && index <= 8; index++)
            {
                if (!double.TryParse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out double value))
                    continue;

                total += value;

                // idle + iowait：等待 IO 的 CPU 没有在算东西，算「闲」。
                if (index is 4 or 5) idle += value;
            }

            return (total - idle, total);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static (double Busy, double Total) ReadWindowsCpuTimes()
    {
        try
        {
            if (!GetSystemTimes(out FileTimeValue idle, out FileTimeValue kernel, out FileTimeValue user))
                return (0, 0);

            // kernel 时间里已经包含 idle，所以 total = kernel + user，busy = total - idle。
            double idleTicks = idle.Ticks;
            double totalTicks = kernel.Ticks + user.Ticks;

            return (totalTicks - idleTicks, totalTicks);
        }
        catch
        {
            return (0, 0);
        }
    }

    // ===== 整机内存 =====

    private static (long Used, long Total) ReadHostMemory()
    {
        if (OperatingSystem.IsLinux()) return ReadLinuxMemory();
        if (OperatingSystem.IsWindows()) return ReadWindowsMemory();

        return (0, 0);
    }

    private static (long Used, long Total) ReadLinuxMemory()
    {
        try
        {
            long total = 0;
            long available = 0;
            long free = 0;
            long buffers = 0;
            long cached = 0;

            foreach (string line in File.ReadLines("/proc/meminfo"))
            {
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;

                string key = line[..colon];

                // 只有关心这五个键，其它行直接跳过，别解析整份 meminfo。
                if (key is not ("MemTotal" or "MemAvailable" or "MemFree" or "Buffers" or "Cached")) continue;

                string rest = line[(colon + 1)..].Trim();

                int space = rest.IndexOf(' ');
                string number = space > 0 ? rest[..space] : rest;

                if (!long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)) continue;

                value *= 1024; // meminfo 单位是 kB

                switch (key)
                {
                    case "MemTotal": total = value; break;
                    case "MemAvailable": available = value; break;
                    case "MemFree": free = value; break;
                    case "Buffers": buffers = value; break;
                    case "Cached": cached = value; break;
                }
            }

            if (total <= 0) return (0, 0);

            // MemAvailable 是内核给的「还能分配多少」（已扣掉不可回收的缓存），
            // 比 MemFree 贴近真实占用；3.14 之前的内核没有它，退回「free + buffers + cached」。
            if (available <= 0) available = free + buffers + cached;

            long used = Math.Clamp(total - available, 0, total);

            return (used, total);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static (long Used, long Total) ReadWindowsMemory()
    {
        try
        {
            MemoryStatusEx status = new() { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };

            if (!GlobalMemoryStatusEx(ref status)) return (0, 0);

            long total = (long)status.TotalPhysical;
            long available = (long)status.AvailablePhysical;

            if (total <= 0) return (0, 0);

            return (Math.Clamp(total - available, 0, total), total);
        }
        catch
        {
            return (0, 0);
        }
    }

    /// <summary>整机内存读不到时的兜底总量（cgroup 有限额时是限额）。</summary>
    private static long ReadGcTotalMemory()
    {
        try { return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes; }
        catch { return 0; }
    }

    /// <summary>所有「在用、非回环、非隧道」网卡的累计收发字节。</summary>
    private static (long Received, long Sent) ReadNetworkCounters()
    {
        long received = 0;
        long sent = 0;
        try
        {
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                if (adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

                try
                {
                    IPv4InterfaceStatistics statistics = adapter.GetIPv4Statistics();
                    received += statistics.BytesReceived;
                    sent += statistics.BytesSent;
                }
                catch
                {
                    // 单个网卡取不到（容器 veth 正在拆除等）不影响其它网卡。
                }
            }
        }
        catch
        {
            // 整体枚举失败就返回 0，页面显示 0 而不是崩掉。
        }

        return (received, sent);
    }

    /// <summary>Linux 的 1 分钟负载均值。Windows 没有这个概念，返回 0（页面据此隐藏）。</summary>
    private static double ReadLoadAverage()
    {
        if (!OperatingSystem.IsLinux()) return 0;

        try
        {
            string text = File.ReadAllText("/proc/loadavg");
            int space = text.IndexOf(' ');
            string first = space > 0 ? text[..space] : text;
            return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : 0;
        }
        catch
        {
            return 0;
        }
    }

    // ===== Windows 计数器 =====

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTimeValue
    {
        public uint LowDateTime;
        public uint HighDateTime;

        /// <summary>FILETIME 的 100ns 累计值。</summary>
        public readonly ulong Ticks => ((ulong)HighDateTime << 32) | LowDateTime;
    }

    /// <summary>MEMORYSTATUSEX（winbase.h）。字段顺序不能动，Length 必须先赋值。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTimeValue idleTime, out FileTimeValue kernelTime,
        out FileTimeValue userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
