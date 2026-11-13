using Server.Envir;
using Server.Web.Models;

namespace Server.Web.Services;

public sealed class ServerMetricsService(IGameServerController controller, HostResourceSampler host) : BackgroundService
{
    private volatile ServerMetricsSnapshot _current = Empty(GameServerState.Stopped, null);

    // 游戏服「从什么时候开始算在运行」。只在 Running 期间有值，停了就清空，
    // 所以页面上显示的是本轮运行时长，不是"最后一次启动到现在"。
    private DateTimeOffset? _gameRunningSince;

    public ServerMetricsSnapshot Current => _current;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
        do
        {
            try
            {
                GameServerState state = controller.State;
                if (state == GameServerState.Running) _gameRunningSince ??= DateTimeOffset.Now;
                else _gameRunningSince = null;

                HostResourceSample resources = host.Sample();

                _current = new ServerMetricsSnapshot(
                    DateTimeOffset.Now,
                    state,
                    SEnvir.Connections?.Count ?? 0,
                    SEnvir.ActiveObjects?.Count ?? 0,
                    SEnvir.Objects?.Count ?? 0,
                    SEnvir.ProcessObjectCount,
                    SEnvir.LoopCount,
                    SEnvir.TotalBytesReceived,
                    SEnvir.TotalBytesSent,
                    SEnvir.DownloadSpeed,
                    SEnvir.UploadSpeed,
                    SEnvir.ConDelay,
                    SEnvir.SaveDelay,
                    EmailService.EMailsSent,
                    controller.LastError)
                {
                    CpuPercent = resources.CpuPercent,
                    MemoryUsedBytes = resources.MemoryUsedBytes,
                    MemoryTotalBytes = resources.MemoryTotalBytes,
                    NetworkReceiveBytesPerSecond = resources.NetworkReceiveBytesPerSecond,
                    NetworkSendBytesPerSecond = resources.NetworkSendBytesPerSecond,
                    LoadAverage1 = resources.LoadAverage1,
                    ProcessUptime = resources.ProcessUptime,
                    ProcessCpuPercent = resources.ProcessCpuPercent,
                    ProcessMemoryBytes = resources.ProcessMemoryBytes,
                    GameServerUptime = _gameRunningSince is { } since
                        ? DateTimeOffset.Now - since
                        : TimeSpan.Zero
                };
            }
            catch
            {
                _current = Empty(controller.State, controller.LastError);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private static ServerMetricsSnapshot Empty(GameServerState state, string? error) =>
        new(DateTimeOffset.Now, state, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, error);
}
