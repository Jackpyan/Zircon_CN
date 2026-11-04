using System.Collections.Concurrent;
using System.Security.Cryptography;
using Server.Envir;

namespace Server.Web.Services;

/// <summary>
/// 后台会话登记表（内存，单实例）：负责「空闲超时」与「单点登录」。
///
/// 为什么需要它：Blazor Server 是长连接 SPA，页面内点菜单不发 HTTP 请求，
/// Cookie 的滑动过期续不上；反过来挂着不动时电路又一直活着 —— 所以必须由服务端
/// 自己记录「最后一次活动时间」，并由前端心跳来续期。
/// </summary>
public sealed class AdminSessionRegistry
{
    /// <summary>
    /// HttpContext.Items 里的键：OnValidatePrincipal 拒绝凭据时把「为什么」放进去，
    /// 供 /auth/ping 回给前端（否则端点只能看到「未登录」，前端无法区分空闲与被顶）。
    /// </summary>
    public const string ReasonItemKey = "zircon.admin.session.reason";

    private sealed class Entry
    {
        public string UserName { get; init; } = string.Empty;
        public DateTime LastActivityUtc { get; set; } = DateTime.UtcNow;
        public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    }

    private readonly ConcurrentDictionary<string, Entry> _sessions = new(StringComparer.Ordinal);

    /// <summary>
    /// 已判死的会话（空闲超时 / 被顶）：sid → 原因。
    ///
    /// 不能只把它们从 _sessions 删掉 —— 删掉之后下一次请求就是「sid 不存在」，
    /// 会走收编分支返回 Ok，等于「被踢掉的浏览器刷新一下就又活了」。
    /// 所以死会话要留墓碑，直到墓碑过期。
    /// </summary>
    private readonly ConcurrentDictionary<string, (string User, SessionCheck Reason, DateTime Utc)> _dead = new(StringComparer.Ordinal);

    private static readonly TimeSpan DeadTtl = TimeSpan.FromHours(12);

    /// <summary>账号 → 当前有效 sid（仅单点登录模式使用）。</summary>
    private readonly ConcurrentDictionary<string, string> _currentByUser = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>空闲超时；&lt;= 0 表示不启用。</summary>
    public static TimeSpan IdleTimeout => Config.AdminWebIdleTimeoutMinutes > 0
        ? TimeSpan.FromMinutes(Config.AdminWebIdleTimeoutMinutes)
        : TimeSpan.Zero;

    public static bool SingleSessionEnabled => Config.AdminWebSingleSession;

    /// <summary>登录成功：登记新会话，返回 sid（写入 Cookie 声明）。</summary>
    public string Register(string userName)
    {
        Cleanup();

        string sid = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

        _sessions[sid] = new Entry { UserName = userName };

        if (SingleSessionEnabled)
        {
            // 同账号的旧会话已经作废：立墓碑（不能只是删除，删除会让它下次被重新收编而复活）。
            foreach (KeyValuePair<string, Entry> pair in _sessions)
                if (!string.Equals(pair.Key, sid, StringComparison.Ordinal) &&
                    string.Equals(pair.Value.UserName, userName, StringComparison.OrdinalIgnoreCase))
                    MarkDead(pair.Key, pair.Value.UserName, SessionCheck.Superseded);

            _currentByUser[userName] = sid;
        }

        return sid;
    }

    /// <summary>
    /// 收编一个「服务端没见过」的 sid（进程重启后内存表丢失的情况）。
    /// 不这么做的话，重启前的旧会话会一直处于 Unknown，空闲超时永远不生效。
    /// sid 在签名 Cookie 里，无法伪造，所以收编是安全的。
    /// </summary>
    public void Adopt(string sid, string userName)
    {
        Cleanup();

        // 只登记会话本身，**不**改动「当前会话」：
        // 否则被空闲清理过的旧会话一重连就把当前会话挤掉，单点登录会变成"两个都 Ok"。
        _sessions[sid] = new Entry { UserName = userName };
    }

    /// <summary>续期（心跳 / HTTP 请求）。返回 false 表示会话已失效。</summary>
    public bool Touch(string? sid)
    {
        if (string.IsNullOrEmpty(sid)) return false;

        if (!_sessions.TryGetValue(sid, out Entry? entry)) return false;

        if (IsIdleExpired(entry))
        {
            Remove(sid);
            return false;
        }

        entry.LastActivityUtc = DateTime.UtcNow;
        return true;
    }

    /// <summary>完整校验：存在 + 未空闲超时 + （单点登录开启时）仍是最新会话。</summary>
    public SessionCheck Validate(string? sid, string? userName)
    {
        if (string.IsNullOrEmpty(sid)) return SessionCheck.Unknown;

        // 墓碑优先：判过死的会话永不复活
        if (_dead.TryGetValue(sid, out (string User, SessionCheck Reason, DateTime Utc) dead))
            return dead.Reason;

        if (!_sessions.TryGetValue(sid, out Entry? entry))
        {
            if (string.IsNullOrEmpty(userName)) return SessionCheck.Unknown;

            // 【坑】收编必须排在单点登录判定之后：
            // 旧会话被判 Superseded 时会被 Remove 掉，下一次请求就变成「sid 不存在」，
            // 若无条件收编就会返回 Ok —— 表现为「被顶掉的浏览器隔一次请求又活过来」，
            // 单点登录时灵时不灵。所以先确认该账号的当前会话不是别人，才允许收编。
            if (SingleSessionEnabled && _currentByUser.TryGetValue(userName, out string? known) &&
                !string.Equals(known, sid, StringComparison.Ordinal))
            {
                MarkDead(sid, userName, SessionCheck.Superseded);
                return SessionCheck.Superseded;
            }

            // 进程重启后首次见到该会话：收编并从头计时（不踢人，但空闲超时重新生效）
            Adopt(sid, userName);
            return SessionCheck.Ok;
        }

        if (IsIdleExpired(entry))
        {
            MarkDead(sid, entry.UserName, SessionCheck.Idle);
            return SessionCheck.Idle;
        }

        if (SingleSessionEnabled && !string.IsNullOrEmpty(userName))
        {
            // 延迟认领：开关是在两次登录「之后」才打开时，_currentByUser 里还没有记录。
            // 此时把「第一个来复检的会话」认作当前会话，后到的那个就会被判 Superseded，
            // 不必让用户把两处都重新登录一遍。
            if (!_currentByUser.TryGetValue(userName, out string? current))
            {
                _currentByUser[userName] = sid;
                return SessionCheck.Ok;
            }

            if (!string.Equals(current, sid, StringComparison.Ordinal))
            {
                MarkDead(sid, userName, SessionCheck.Superseded);
                return SessionCheck.Superseded;
            }
        }

        return SessionCheck.Ok;
    }

    /// <summary>给会话立墓碑：之后永远返回同一个失效原因。</summary>
    private void MarkDead(string sid, string? userName, SessionCheck reason)
    {
        _sessions.TryRemove(sid, out _);

        _dead[sid] = (userName ?? string.Empty, reason, DateTime.UtcNow);
    }

    public void Remove(string? sid)
    {
        if (string.IsNullOrEmpty(sid)) return;

        if (_sessions.TryRemove(sid, out Entry? entry) && SingleSessionEnabled &&
            _currentByUser.TryGetValue(entry.UserName, out string? current) &&
            string.Equals(current, sid, StringComparison.Ordinal))
            _currentByUser.TryRemove(entry.UserName, out _);
    }

    public void RemoveByUser(string userName)
    {
        if (_currentByUser.TryRemove(userName, out string? sid)) Remove(sid);
    }

    public int Count => _sessions.Count;

    /// <summary>该账号当前被认定的会话 sid（诊断用）。</summary>
    public string? GetCurrentSid(string? userName) =>
        !string.IsNullOrEmpty(userName) && _currentByUser.TryGetValue(userName, out string? sid) ? sid : null;

    /// <summary>会话表快照（诊断页 / 排查单点登录用）。</summary>
    public object[] Snapshot() => _sessions
        .Select(pair => (object)new
        {
            sid = pair.Key.Length >= 8 ? pair.Key[..8] : pair.Key,
            user = pair.Value.UserName,
            idleSeconds = (int)(DateTime.UtcNow - pair.Value.LastActivityUtc).TotalSeconds,
            current = _currentByUser.TryGetValue(pair.Value.UserName, out string? current) &&
                      string.Equals(current, pair.Key, StringComparison.Ordinal)
        })
        .ToArray();

    private static bool IsIdleExpired(Entry entry)
    {
        TimeSpan timeout = IdleTimeout;
        return timeout > TimeSpan.Zero && DateTime.UtcNow - entry.LastActivityUtc > timeout;
    }

    private void Cleanup()
    {
        DateTime now = DateTime.UtcNow;

        // 墓碑过期清理（与空闲超时开关无关）
        foreach (KeyValuePair<string, (string User, SessionCheck Reason, DateTime Utc)> pair in _dead)
            if (now - pair.Value.Utc > DeadTtl)
                _dead.TryRemove(pair.Key, out _);

        TimeSpan timeout = IdleTimeout;

        if (timeout <= TimeSpan.Zero) return;

        // 超时的不删、改立墓碑：删掉会让它在下次请求时被当成「未知会话」重新收编
        foreach (KeyValuePair<string, Entry> pair in _sessions)
            if (now - pair.Value.LastActivityUtc > timeout)
                MarkDead(pair.Key, pair.Value.UserName, SessionCheck.Idle);
    }
}

public enum SessionCheck
{
    /// <summary>会话有效。</summary>
    Ok,
    /// <summary>sid 不存在（进程重启 / 已被清理）。</summary>
    Unknown,
    /// <summary>空闲超时。</summary>
    Idle,
    /// <summary>被同账号的新登录顶掉（单点登录）。</summary>
    Superseded
}
