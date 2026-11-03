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

        if (SEnvir.TryValidateWebAccount(userName, password, out Server.DBModels.AccountInfo account, out string reason))
        {
            // 一期权限分级：身份 = 管理员 → Administrator 角色（37 个高危页可用，行为与主密码一致）；
            // 监督者/运营者 → WebAdmin 角色（Runtime 只读页可用，数据页二期再细化）。
            string role = account.GetEffectiveIdentity() >= Server.DBModels.AccountIdentity.Admin
                ? RoleAdministrator
                : RoleWebAdmin;

            return new AdminLoginResult(true, userName, role, "WebAdmin");
        }

        return AdminLoginResult.Fail(reason);
    }

    /// <summary>主密码凭据指纹（改主密码即全端失效）。</summary>
    public string GetSecurityStamp()
    {
        byte[] value = SHA256.HashData(Encoding.UTF8.GetBytes($"{Config.AdminUserName}\0{Config.MasterPassword}"));
        return Convert.ToHexString(value);
    }

    /// <summary>登录方式：主密码（admin + MasterPassword）或账号（邮箱 + 游戏密码）。</summary>
    public const string AuthModeMasterPassword = "MasterPassword";
    public const string AuthModeWebAccount = "WebAdmin";

    /// <summary>是否为主密码登录。缺省（老会话）时按角色推断。</summary>
    public static bool IsMasterPasswordMode(string? authMode, string? role) =>
        string.Equals(authMode, AuthModeMasterPassword, StringComparison.Ordinal) ||
        (string.IsNullOrEmpty(authMode) && string.Equals(role, RoleAdministrator, StringComparison.Ordinal));

    /// <summary>
    /// 按登录方式取凭据指纹：
    /// 主密码登录 = 主密码派生；账号登录 = 账号 + 密码哈希 + 身份等级派生
    /// （账号即使身份=管理员、角色被映射成 Administrator，也必须用账号指纹，
    /// 否则改身份/改密码都不会让会话失效）。
    /// </summary>
    public string GetStamp(string? userName, string? role, string? authMode = null)
    {
        if (IsMasterPasswordMode(authMode, role))
            return GetSecurityStamp();

        return string.IsNullOrWhiteSpace(userName) ? string.Empty : SEnvir.GetWebAccountStamp(userName);
    }

    /// <summary>会话复检：凭据指纹一致才有效（改密码 / 收回 WebAdmin / 关闭主密码登录都会失效）。</summary>
    public bool ValidateStamp(string? userName, string? role, string? stamp, string? authMode = null)
    {
        if (string.IsNullOrEmpty(stamp)) return false;

        // 主密码入口：关闭开关后，已登录的管理员会话也要立刻失效
        if (IsMasterPasswordMode(authMode, role))
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
