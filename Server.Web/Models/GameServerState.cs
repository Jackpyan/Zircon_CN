namespace Server.Web.Models;

public enum GameServerState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Faulted
}

public sealed record ServerMetricsSnapshot(
    DateTimeOffset CapturedAt,
    GameServerState State,
    int Connections,
    int ActiveObjects,
    int Objects,
    int ProcessedObjects,
    int LoopCount,
    long TotalBytesReceived,
    long TotalBytesSent,
    long DownloadSpeed,
    long UploadSpeed,
    long ConnectionDelayMilliseconds,
    long SaveDelayMilliseconds,
    int EmailsSent,
    string? LastError)
{
    // ===== 主机资源（HostResourceSampler 采样，默认值让老调用点不用改）=====
    /// <summary>进程 CPU 占整机的百分比（已按核数归一）。</summary>
    public double CpuPercent { get; init; }
    /// <summary>进程工作集（含原生内存，比 GC 堆更接近真实占用）。</summary>
    public long MemoryBytes { get; init; }
    /// <summary>可用物理内存总量（cgroup 有限额时是限额）。</summary>
    public long MemoryTotalBytes { get; init; }
    public long NetworkReceiveBytesPerSecond { get; init; }
    public long NetworkSendBytesPerSecond { get; init; }
    /// <summary>Linux 的 1 分钟负载均值；Windows 恒为 0。</summary>
    public double LoadAverage1 { get; init; }
    /// <summary>Web 宿主进程（同时承载游戏服）已运行时长。</summary>
    public TimeSpan ProcessUptime { get; init; }
    /// <summary>游戏服已运行时长；未运行时为 <see cref="TimeSpan.Zero"/>。</summary>
    public TimeSpan GameServerUptime { get; init; }
}
