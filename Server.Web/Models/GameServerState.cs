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
    /// <summary>**整机** CPU 使用率（所有进程加起来，0~100）。</summary>
    public double CpuPercent { get; init; }
    /// <summary>**整机**已用物理内存。</summary>
    public long MemoryUsedBytes { get; init; }
    /// <summary>**整机**物理内存总量。</summary>
    public long MemoryTotalBytes { get; init; }
    public long NetworkReceiveBytesPerSecond { get; init; }
    public long NetworkSendBytesPerSecond { get; init; }
    /// <summary>Linux 的 1 分钟负载均值；Windows 恒为 0。</summary>
    public double LoadAverage1 { get; init; }
    /// <summary>Web 宿主进程（同时承载游戏服）已运行时长。</summary>
    public TimeSpan ProcessUptime { get; init; }
    /// <summary>本进程 CPU 占整机的百分比（已按核数归一）。只作副信息显示。</summary>
    public double ProcessCpuPercent { get; init; }
    /// <summary>本进程工作集（含原生内存，比 GC 堆更接近真实占用）。只作副信息显示。</summary>
    public long ProcessMemoryBytes { get; init; }
    /// <summary>游戏服已运行时长；未运行时为 <see cref="TimeSpan.Zero"/>。</summary>
    public TimeSpan GameServerUptime { get; init; }
}
