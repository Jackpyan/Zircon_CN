using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Server.Web.Services;

/// <summary>
/// 主密码校验通过后、TOTP 校验通过前的「待验证登录」暂存（内存，5 分钟有效）。
/// 避免用户在验证码页面重复输入密码；进程重启即失效（重新登录即可）。
/// </summary>
public sealed class TotpChallengeStore
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, TotpChallenge> _items = new(StringComparer.Ordinal);

    public string Create(AdminLoginResult result)
    {
        Cleanup();

        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

        _items[token] = new TotpChallenge
        {
            UserName = result.UserName,
            Role = result.Role,
            Reason = result.Reason,
            Identity = result.Identity,
            ExpiresUtc = DateTime.UtcNow.Add(Lifetime)
        };

        return token;
    }

    public TotpChallenge? Take(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;

        Cleanup();

        return _items.TryGetValue(token, out TotpChallenge? challenge) && challenge.ExpiresUtc > DateTime.UtcNow
            ? challenge
            : null;
    }

    /// <summary>验证成功后销毁（一次性）。</summary>
    public void Remove(string? token)
    {
        if (!string.IsNullOrEmpty(token)) _items.TryRemove(token, out _);
    }

    private void Cleanup()
    {
        DateTime now = DateTime.UtcNow;

        foreach (KeyValuePair<string, TotpChallenge> pair in _items)
            if (pair.Value.ExpiresUtc <= now)
                _items.TryRemove(pair.Key, out _);
    }
}

public sealed class TotpChallenge
{
    public string UserName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string Identity { get; set; } = string.Empty;
    public DateTime ExpiresUtc { get; set; }

    /// <summary>绑定流程中待确认的密钥（避免在表单里传密钥，防篡改）。</summary>
    public string? PendingSecret { get; set; }
}
