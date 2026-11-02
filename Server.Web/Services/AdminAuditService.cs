using System.Collections.Concurrent;
using System.Text.Json;

namespace Server.Web.Services;

/// <summary>
/// 单条审计记录。后 6 个字段为增强项，旧记录缺失时反序列化为 null（向后兼容）。
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
    string? UserAgent = null);

public sealed class AdminAuditService
{
    private const int MemoryLimit = 2_000;
    private const int RetentionDays = 90;

    private readonly ConcurrentQueue<AdminAuditEntry> _entries = new();
    private readonly object _fileGate = new();
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "Audit");
    private string _currentPath = string.Empty;

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

    public void Record(
        string user,
        string action,
        string detail,
        string? address,
        string? role,
        string? result,
        string? target,
        string? reason,
        string? userAgent)
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
            userAgent);

        _entries.Enqueue(entry);
        while (_entries.Count > MemoryLimit) _entries.TryDequeue(out _);

        lock (_fileGate)
        {
            string path = ResolvePath(entry.Timestamp);

            Directory.CreateDirectory(_directory);
            File.AppendAllText(path, JsonSerializer.Serialize(entry) + Environment.NewLine);
        }
    }

    public IReadOnlyList<AdminAuditEntry> GetEntries() => _entries.Reverse().ToArray();

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
