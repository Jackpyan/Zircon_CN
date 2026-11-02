using System.Collections.Concurrent;
using System.Text.Json;

namespace Server.Web.Services;

/// <summary>登录失败计数条目。</summary>
public sealed record LoginThrottleEntry(string Key, string Kind, int Failures, DateTimeOffset FirstFailure, DateTimeOffset BlockedUntil)
{
    public bool IsBlocked => BlockedUntil > DateTimeOffset.Now;
    public int RemainingMinutes => IsBlocked ? Math.Max(1, (int)Math.Ceiling((BlockedUntil - DateTimeOffset.Now).TotalMinutes)) : 0;
}

/// <summary>
/// 登录失败节流：按 IP 与账号分别统计失败次数，超过阈值临时封禁，可在后台「安全 → 登录黑名单」解除。
/// 替代原先单一的按 IP 限流（那个一旦触发连 admin 都进不来，且无法手动解除）。
/// </summary>
public sealed class LoginThrottleService
{
    private const int MaxFailures = 5;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan BlockDuration = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, LoginThrottleEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly string _path = Path.Combine(AppContext.BaseDirectory, "Audit", "login-blocks.json");

    public LoginThrottleService()
    {
        Load();
    }

    /// <summary>判断本次登录是否被拦截。</summary>
    public bool IsBlocked(string? address, string? account, out LoginThrottleEntry? blocked)
    {
        blocked = null;

        if (!string.IsNullOrWhiteSpace(address) && _entries.TryGetValue(IpKey(address), out LoginThrottleEntry? ipEntry) && ipEntry.IsBlocked)
        {
            blocked = ipEntry;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(account) && _entries.TryGetValue(AccountKey(account), out LoginThrottleEntry? accountEntry) && accountEntry.IsBlocked)
        {
            blocked = accountEntry;
            return true;
        }

        return false;
    }

    /// <summary>记录一次失败（达到阈值即封禁 15 分钟）。</summary>
    public void RecordFailure(string? address, string? account)
    {
        if (!string.IsNullOrWhiteSpace(address)) Bump(IpKey(address), "IP", address);
        if (!string.IsNullOrWhiteSpace(account)) Bump(AccountKey(account), "账号", account);

        Save();
    }

    /// <summary>登录成功：清掉该 IP 与账号的失败计数。</summary>
    public void Reset(string? address, string? account)
    {
        if (!string.IsNullOrWhiteSpace(address)) _entries.TryRemove(IpKey(address), out _);
        if (!string.IsNullOrWhiteSpace(account)) _entries.TryRemove(AccountKey(account), out _);

        Save();
    }

    public IReadOnlyList<LoginThrottleEntry> GetBlocks()
    {
        Cleanup();
        return _entries.Values
            .Where(x => x.IsBlocked)
            .OrderByDescending(x => x.BlockedUntil)
            .ToArray();
    }

    public bool Unblock(string key)
    {
        bool removed = _entries.TryRemove(key, out _);
        if (removed) Save();
        return removed;
    }

    public int UnblockAll()
    {
        int count = _entries.Count(x => x.Value.IsBlocked);
        _entries.Clear();
        Save();
        return count;
    }

    private void Bump(string key, string kind, string display)
    {
        DateTimeOffset now = DateTimeOffset.Now;

        _entries.AddOrUpdate(key,
            _ => new LoginThrottleEntry(key, kind, 1, now, DateTimeOffset.MinValue),
            (_, existing) =>
            {
                if (existing.BlockedUntil > now)
                    return existing;

                // 超出统计窗口则重新计数
                int failures = existing.FirstFailure + FailureWindow < now ? 1 : existing.Failures + 1;
                DateTimeOffset first = existing.FirstFailure + FailureWindow < now ? now : existing.FirstFailure;

                DateTimeOffset blockedUntil = failures >= MaxFailures ? now.Add(BlockDuration) : DateTimeOffset.MinValue;

                return new LoginThrottleEntry(key, kind, failures, first, blockedUntil);
            });
    }

    private void Cleanup()
    {
        DateTimeOffset now = DateTimeOffset.Now;

        foreach (KeyValuePair<string, LoginThrottleEntry> pair in _entries.ToArray())
        {
            if (pair.Value.BlockedUntil > now) continue;
            if (pair.Value.FirstFailure + FailureWindow > now) continue;

            _entries.TryRemove(pair.Key, out _);
        }
    }

    private static string IpKey(string address) => $"ip:{address}";
    private static string AccountKey(string account) => $"account:{account.Trim().ToLowerInvariant()}";

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;

            foreach (LoginThrottleEntry entry in JsonSerializer.Deserialize<List<LoginThrottleEntry>>(File.ReadAllText(_path)) ?? [])
            {
                if (string.IsNullOrEmpty(entry.Key)) continue;
                _entries[entry.Key] = entry;
            }
        }
        catch
        {
            // 损坏的黑名单文件不影响后台启动
        }
    }

    private void Save()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(_entries.Values.ToList(), new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // 写入失败不影响登录流程
            }
        }
    }
}
