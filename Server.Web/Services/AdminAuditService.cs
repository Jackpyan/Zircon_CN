using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;

namespace Server.Web.Services;

/// <summary>
/// 单条审计记录。后 7 个字段为增强项，旧记录缺失时反序列化为 null（向后兼容）。
/// <para>Source：来源，null/空 = 后台（Web），"GM" = 游戏内 GM 命令。</para>
/// </summary>
public sealed record AdminAuditEntry(
    DateTimeOffset Timestamp,
    string User,
    string Action,
    string Detail,
    string? Address,
    string? Role = null,
    string? Result = null,
    string? Target = null,
    string? Reason = null,
    string? UserAgent = null,
    string? Source = null);

/// <summary>
/// 后台审计流水：内存环形窗口（2,000 条）+ 按天追加写 <c>Audit/admin-audit-YYYYMMDD.jsonl</c>，保留 90 天。
/// <para>
/// 两条写入通道：
/// <list type="bullet">
/// <item><see cref="Record"/> —— 后台自身操作，同步落盘（原有行为，保持不变）。</item>
/// <item><see cref="Enqueue"/> —— 游戏内 GM 命令，走 <see cref="Channel{T}"/> 异步落盘。
/// 命令执行在游戏主循环线程上，绝不能因为磁盘慢/杀软扫描而卡住主循环。</item>
/// </list>
/// </para>
/// </summary>
public sealed class AdminAuditService : IHostedService
{
    private const int MemoryLimit = 2_000;
    private const int RetentionDays = 90;

    private readonly ConcurrentQueue<AdminAuditEntry> _entries = new();
    private readonly object _fileGate = new();
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "Audit");
    private string _currentPath = string.Empty;

    private readonly Channel<AdminAuditEntry> _queue =
        Channel.CreateUnbounded<AdminAuditEntry>(new UnboundedChannelOptions { SingleReader = true });

    private Task? _writerTask;

    public AdminAuditService()
    {
        try
        {
            Directory.CreateDirectory(_directory);

            // 载入最近若干天的记录，凑够内存窗口即可
            List<string> files = Directory.GetFiles(_directory, "admin-audit*.jsonl")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList();

            foreach (string file in files)
            {
                foreach (string line in File.ReadLines(file).TakeLast(MemoryLimit))
                {
                    AdminAuditEntry? entry = JsonSerializer.Deserialize<AdminAuditEntry>(line);
                    if (entry is not null) _entries.Enqueue(entry);
                }

                if (_entries.Count >= MemoryLimit) break;
            }

            while (_entries.Count > MemoryLimit) _entries.TryDequeue(out _);

            CleanupExpired();
        }
        catch
        {
            // A damaged historical audit file must not prevent the administration plane from starting.
        }
    }

    /// <summary>兼容旧调用：只记录基础字段。</summary>
    public void Record(string user, string action, string detail, string? address = null)
        => Record(user, action, detail, address, role: null, result: null, target: null, reason: null, userAgent: null);

    /// <summary>后台操作：同步落盘（保持原有行为）。</summary>
    public void Record(
        string user,
        string action,
        string detail,
        string? address,
        string? role,
        string? result,
        string? target,
        string? reason,
        string? userAgent,
        string? source = null)
    {
        AdminAuditEntry entry = new(
            DateTimeOffset.Now,
            string.IsNullOrWhiteSpace(user) ? "unknown" : user,
            action,
            detail,
            address,
            role,
            result,
            target,
            reason,
            userAgent,
            source);

        EnqueueMemory(entry);
        WriteEntry(entry);
    }

    /// <summary>
    /// 游戏内 GM 命令：只入队，落盘交给后台线程（不阻塞调用方）。
    /// 内存窗口立即更新，审计页不用等落盘就能看到。
    /// </summary>
    public void Enqueue(AdminAuditEntry entry)
    {
        EnqueueMemory(entry);
        _queue.Writer.TryWrite(entry);
    }

    public IReadOnlyList<AdminAuditEntry> GetEntries() => _entries.Reverse().ToArray();

    // ===== IHostedService：后台落盘线程 =====

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _writerTask = Task.Run(FlushLoopAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Writer.TryComplete();

        if (_writerTask == null) return;

        // 最多等 3 秒把队列刷完（停机流程本身只有 3 秒预算）
        await Task.WhenAny(_writerTask, Task.Delay(TimeSpan.FromSeconds(3), cancellationToken));
    }

    /// <summary>
    /// 故意不接 CancellationToken：停机时靠 <c>TryComplete()</c> 让循环把队列**排空**后自然退出。
    /// 若监听 host 的 stopping token，停机瞬间会直接抛 OperationCanceledException，队列里剩下的记录全丢。
    /// </summary>
    private async Task FlushLoopAsync()
    {
        ChannelReader<AdminAuditEntry> reader = _queue.Reader;

        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
                while (reader.TryRead(out AdminAuditEntry? entry))
                    WriteEntry(entry);
        }
        catch
        {
            // 落盘线程不允许把进程带崩
        }
    }

    // ===== 内部 =====

    private void EnqueueMemory(AdminAuditEntry entry)
    {
        _entries.Enqueue(entry);
        while (_entries.Count > MemoryLimit) _entries.TryDequeue(out _);
    }

    private void WriteEntry(AdminAuditEntry entry)
    {
        try
        {
            lock (_fileGate)
            {
                string path = ResolvePath(entry.Timestamp);

                Directory.CreateDirectory(_directory);
                File.AppendAllText(path, JsonSerializer.Serialize(entry) + Environment.NewLine);
            }
        }
        catch
        {
            // 写盘失败不影响业务（Logs.txt 里还有 GM 命令的兜底记录）
        }
    }

    private string ResolvePath(DateTimeOffset timestamp)
    {
        string path = Path.Combine(_directory, $"admin-audit-{timestamp:yyyyMMdd}.jsonl");

        if (!string.Equals(_currentPath, path, StringComparison.Ordinal))
        {
            _currentPath = path;
            CleanupExpired();
        }

        return path;
    }

    /// <summary>按天滚动，删除超过保留期的历史文件。</summary>
    private void CleanupExpired()
    {
        try
        {
            DateTime limit = DateTime.UtcNow.Date.AddDays(-RetentionDays);

            foreach (string file in Directory.GetFiles(_directory, "admin-audit-*.jsonl"))
            {
                if (File.GetLastWriteTimeUtc(file) >= limit) continue;

                File.Delete(file);
            }
        }
        catch
        {
            // 清理失败不影响写入
        }
    }
}
