using System.Security.Cryptography;
using System.Text;
using Server.Envir;

namespace Server.Web.Services;

/// <summary>Web 后台登录结果。</summary>
public sealed record AdminLoginResult(bool Ok, string UserName, string Role, string Reason)
{
    public static AdminLoginResult Fail(string reason) => new(false, string.Empty, string.Empty, reason);
}

public sealed class AdminCredentialService
{
    public const string RoleAdministrator = "Administrator";
    public const string RoleWebAdmin = "WebAdmin";

    public string UserName => Config.AdminUserName;

    /// <summary>
    /// 双分支校验：
    /// 1) 主密码应急入口（`AdminUserName` + `MasterPassword`）：需 `WebAllowMasterLogin=true` 且来源为内网；
    /// 2) 玩家账号（邮箱 + 游戏密码）：账号需 `WebAdmin=true`、已激活、未封禁。
    /// </summary>
    public AdminLoginResult Validate(string? userName, string? password, bool isIntranet)
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(password))
            return AdminLoginResult.Fail("EmptyCredentials");

        if (FixedTimeEquals(userName, Config.AdminUserName) &&
            FixedTimeEquals(password, Config.MasterPassword))
        {
            if (!Config.WebAllowMasterLogin)
                return AdminLoginResult.Fail("MasterLoginDisabled");

            if (!isIntranet)
                return AdminLoginResult.Fail("MasterLoginIntranetOnly");

            return new AdminLoginResult(true, Config.AdminUserName, RoleAdministrator, "MasterPassword");
        }

        if (SEnvir.TryValidateWebAccount(userName, password, out _, out string reason))
            return new AdminLoginResult(true, userName, RoleWebAdmin, "WebAdmin");

        return AdminLoginResult.Fail(reason);
    }

    /// <summary>主密码凭据指纹（改主密码即全端失效）。</summary>
    public string GetSecurityStamp()
    {
        byte[] value = SHA256.HashData(Encoding.UTF8.GetBytes($"{Config.AdminUserName}\0{Config.MasterPassword}"));
        return Convert.ToHexString(value);
    }

    /// <summary>按角色取凭据指纹：管理员=主密码派生，WebAdmin=账号+密码哈希+权限派生。</summary>
    public string GetStamp(string? userName, string? role)
    {
        if (string.Equals(role, RoleAdministrator, StringComparison.Ordinal))
            return GetSecurityStamp();

        return string.IsNullOrWhiteSpace(userName) ? string.Empty : SEnvir.GetWebAccountStamp(userName);
    }

    /// <summary>会话复检：凭据指纹一致才有效（改密码 / 收回 WebAdmin / 关闭主密码登录都会失效）。</summary>
    public bool ValidateStamp(string? userName, string? role, string? stamp)
    {
        if (string.IsNullOrEmpty(stamp)) return false;

        // 主密码入口：关闭开关后，已登录的管理员会话也要立刻失效
        if (string.Equals(role, RoleAdministrator, StringComparison.Ordinal))
        {
            if (!Config.WebAllowMasterLogin) return false;

            return string.Equals(GetSecurityStamp(), stamp, StringComparison.Ordinal);
        }

        // 账号入口：账号表不可用（游戏服未启动）时无法判定，放行以免误踢；
        // 只要账号表可读，就必须「账号存在 + 仍有 WebAdmin 权限 + 密码未改」才有效。
        if (!SEnvir.TryGetWebAccountStamp(userName, out string expected))
            return true;

        return !string.IsNullOrEmpty(expected) && string.Equals(expected, stamp, StringComparison.Ordinal);
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        byte[] leftHash = SHA256.HashData(Encoding.UTF8.GetBytes(left));
        byte[] rightHash = SHA256.HashData(Encoding.UTF8.GetBytes(right));
        return CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
    }
}
