using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;

namespace Server.Web.Services;

/// <summary>主机资源的一次采样结果（进程 + 网卡 + 运行时长）。</summary>
public sealed record HostResourceSample(
    double CpuPercent,
    long MemoryBytes,
    long MemoryTotalBytes,
    long NetworkReceiveBytesPerSecond,
    long NetworkSendBytesPerSecond,
    double LoadAverage1,
    TimeSpan ProcessUptime)
{
    public static readonly HostResourceSample Empty = new(0, 0, 0, 0, 0, 0, TimeSpan.Zero);
}

/// <summary>
/// 采集「服务器」层面的 CPU / 内存 / 网卡流量 / 运行时长，全部跨平台、无需 P/Invoke：
/// CPU 与内存取当前进程（游戏服和 Web 后台是同一个进程，所以它就是"服务器占用"），
/// 网卡取 <see cref="NetworkInterface"/> 的 IPv4 统计（Linux 下读 /proc/net/dev，Windows 下读 IP Helper），
/// 负载均值只有 Linux 有，Windows 返回 0。
/// </summary>
public sealed class HostResourceSampler
{
    private readonly object _sync = new();
    private readonly Process _self = Process.GetCurrentProcess();
    private readonly int _processorCount = Math.Max(1, Environment.ProcessorCount);

    private TimeSpan _lastCpuTime;
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
            TimeSpan cpuTime;
            long memory;
            try
            {
                // Unix 下这两个属性每次都重读 /proc，不要调 Refresh()（它会把整份 /proc 再解析一遍）。
                cpuTime = _self.TotalProcessorTime;
                memory = _self.WorkingSet64;
            }
            catch
            {
                cpuTime = _lastCpuTime;
                memory = 0;
            }

            (long received, long sent) = ReadNetworkCounters();

            double cpuPercent = 0;
            long receiveRate = 0;
            long sendRate = 0;
            if (_lastSampleTicks != 0)
            {
                double elapsed = (nowTicks - _lastSampleTicks) / (double)Stopwatch.Frequency;
                if (elapsed > 0.05)
                {
                    // TotalProcessorTime 是「所有核心累加」，除以核数才是占整机的比例。
                    double cpuSeconds = (cpuTime - _lastCpuTime).TotalSeconds;
                    cpuPercent = Math.Clamp(cpuSeconds / (elapsed * _processorCount) * 100, 0, 100);

                    // 网卡掉线/重连会让累计值归零，差值为负就按 0 算，别让页面显示负流量。
                    receiveRate = (long)Math.Max(0, (received - _lastReceived) / elapsed);
                    sendRate = (long)Math.Max(0, (sent - _lastSent) / elapsed);
                }
            }

            _lastCpuTime = cpuTime;
            _lastReceived = received;
            _lastSent = sent;
            _lastSampleTicks = nowTicks;

            return new HostResourceSample(
                Math.Round(cpuPercent, 1),
                memory,
                ReadTotalMemory(),
                receiveRate,
                sendRate,
                ReadLoadAverage(),
                DateTimeOffset.Now - StartedAt);
        }
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

    /// <summary>可用的物理内存总量。cgroup 有限额时框架会返回限额，比"宿主机总量"更贴合实际。</summary>
    private static long ReadTotalMemory()
    {
        try { return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes; }
        catch { return 0; }
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
}
