using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Library;
using Server.Envir;

namespace Server.Web.Services;

/// <summary>
/// 主密码登录的 TOTP 二次验证：绑定信息落盘 + 恢复码管理，算法见 <see cref="TotpCore"/>。
/// </summary>
public sealed class TotpService
{
    private const int RecoveryCodeCount = 8;

    private readonly object _gate = new();
    private TotpState? _state;

    /// <summary>配置开关（服务器配置页 [AdminWeb] AdminWebRequireTotp）。关闭时整个功能失效。</summary>
    public static bool IsRequired => Config.AdminWebRequireTotp;

    private static string StorePath => PlatformPath.Resolve(@".\Totp\master-totp.json");

    public bool IsBound
    {
        get
        {
            TotpState state = Load();
            return state.Enabled && !string.IsNullOrEmpty(state.Secret);
        }
    }

    public TotpState Load()
    {
        lock (_gate)
        {
            if (_state != null) return _state;

            try
            {
                if (File.Exists(StorePath))
                {
                    string json = File.ReadAllText(StorePath);
                    _state = JsonSerializer.Deserialize<TotpState>(json) ?? new TotpState();
                }
            }
            catch (Exception ex)
            {
                SEnvir.Log($"[TOTP] 读取绑定信息失败：{ex.Message}");
            }

            return _state ??= new TotpState();
        }
    }

    private void Save(TotpState state)
    {
        lock (_gate)
        {
            string? directory = Path.GetDirectoryName(StorePath);

            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(StorePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            _state = state;
        }
    }

    /// <summary>开始绑定：生成新的 Base32 密钥（暂不落盘，校验通过后由 <see cref="CompleteSetup"/> 保存）。</summary>
    public string CreateSecret() => TotpCore.CreateSecret();

    /// <summary>otpauth:// 链接（手填或导入验证器 App）。</summary>
    public string BuildUri(string secret, string issuer = "Zircon", string account = "admin")
        => TotpCore.BuildUri(secret, issuer, account);

    /// <summary>校验 6 位验证码。</summary>
    public bool VerifyCode(string secret, string code) => TotpCore.VerifyCode(secret, code);

    /// <summary>绑定完成：保存密钥并生成 8 个一次性恢复码（返回明文，仅此一次）。</summary>
    public IReadOnlyList<string> CompleteSetup(string secret, string code)
    {
        if (!TotpCore.VerifyCode(secret, code))
            throw new InvalidOperationException("验证码不正确，请确认手机时间是否准确后重试。");

        List<string> plain = [];
        List<TotpRecoveryCode> hashed = [];

        for (int i = 0; i < RecoveryCodeCount; i++)
        {
            string raw = Convert.ToHexString(RandomNumberGenerator.GetBytes(5));
            string formatted = $"{raw[..5]}-{raw[5..]}";

            plain.Add(formatted);
            hashed.Add(new TotpRecoveryCode { Hash = Sha256(formatted), Used = false });
        }

        Save(new TotpState
        {
            Enabled = true,
            Secret = secret,
            CreatedUtc = DateTime.UtcNow,
            RecoveryCodes = hashed
        });

        return plain;
    }

    /// <summary>校验一次登录：先试 6 位动态码，再试恢复码（恢复码用后作废）。</summary>
    public bool VerifyLogin(string code)
    {
        TotpState state = Load();

        if (!state.Enabled || string.IsNullOrEmpty(state.Secret)) return false;

        if (TotpCore.VerifyCode(state.Secret, code)) return true;

        string normalized = (code ?? string.Empty).Trim().Replace(" ", string.Empty).ToUpperInvariant();

        if (normalized.Length < 5) return false;

        string hash = Sha256(normalized);

        TotpRecoveryCode? match = state.RecoveryCodes.FirstOrDefault(x => !x.Used && x.Hash == hash);

        if (match == null) return false;

        match.Used = true;
        Save(state);

        SEnvir.Log($"[TOTP] 使用恢复码登录，剩余 {state.RecoveryCodes.Count(x => !x.Used)} 个。");

        return true;
    }

    public int RemainingRecoveryCodes => Load().RecoveryCodes.Count(x => !x.Used);

    /// <summary>清除绑定（换手机时用；也可直接删除 Totp/master-totp.json）。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            if (File.Exists(StorePath)) File.Delete(StorePath);
            _state = new TotpState();
        }
    }

    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public sealed class TotpState
{
    public bool Enabled { get; set; }
    public string Secret { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public List<TotpRecoveryCode> RecoveryCodes { get; set; } = [];
}

public sealed class TotpRecoveryCode
{
    public string Hash { get; set; } = string.Empty;
    public bool Used { get; set; }
}
