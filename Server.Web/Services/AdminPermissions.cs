using System.Security.Claims;
using Server.DBModels;

namespace Server.Web.Services;

/// <summary>
/// Web 后台权限判定（二期）：把 Cookie 声明翻译成身份等级与登录方式，
/// 供授权策略（Program.cs）与页面按钮级判定共用。
///
/// 声明来源（登录时写入）：
///   identity  = AccountIdentity 名称（Normal/Supervisor/Operator/Admin）
///   auth_mode = MasterPassword / WebAdmin
///   role      = Administrator / WebAdmin（旧字段，仅作回退）
/// </summary>
public static class AdminPermissions
{
    public const string PolicyRead = "WebRead";       // 任意登录者（监督者及以上）
    public const string PolicyOperate = "WebOperate"; // 运营者及以上
    public const string PolicyManage = "WebManage";   // 管理员及以上
    public const string PolicyOwner = "WebOwner";     // 仅主密码登录

    public const string ClaimIdentity = "identity";
    public const string ClaimAuthMode = "auth_mode";

    public static AccountIdentity GetIdentity(ClaimsPrincipal? user)
    {
        string? raw = user?.FindFirst(ClaimIdentity)?.Value;

        if (Enum.TryParse(raw, true, out AccountIdentity identity))
            return identity;

        // 老会话（本次升级前登录，没有 identity 声明）：按角色回退
        return user?.IsInRole(AdminCredentialService.RoleAdministrator) == true
            ? AccountIdentity.Admin
            : AccountIdentity.Normal;
    }

    /// <summary>是否主密码登录（最高档：删账号 / 启停服务器 / 插件 / 加密密钥）。</summary>
    public static bool IsMasterPassword(ClaimsPrincipal? user) =>
        string.Equals(user?.FindFirst(ClaimAuthMode)?.Value, AdminCredentialService.AuthModeMasterPassword, StringComparison.Ordinal);

    public static bool CanRead(ClaimsPrincipal? user) => user?.Identity?.IsAuthenticated == true;

    public static bool CanOperate(ClaimsPrincipal? user) => GetIdentity(user) >= AccountIdentity.Operator;

    public static bool CanManage(ClaimsPrincipal? user) => GetIdentity(user) >= AccountIdentity.Admin;

    public static bool CanOwner(ClaimsPrincipal? user) => IsMasterPassword(user);

    /// <summary>等级中文名（用于提示与审计）。</summary>
    public static string Describe(ClaimsPrincipal? user) =>
        IsMasterPassword(user) ? "主密码管理员" : GetIdentity(user).ToCN();
}
