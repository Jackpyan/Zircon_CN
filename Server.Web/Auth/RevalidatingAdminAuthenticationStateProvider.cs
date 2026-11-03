using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Server.Web.Services;

namespace Server.Web.Auth;

public sealed class RevalidatingAdminAuthenticationStateProvider(
    ILoggerFactory loggerFactory,
    AdminCredentialService credentials)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);

    protected override Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState,
        CancellationToken cancellationToken)
    {
        if (authenticationState.User.Identity?.IsAuthenticated != true)
            return Task.FromResult(false);

        string? name = authenticationState.User.Identity.Name;
        string? role = authenticationState.User.FindFirst(ClaimTypes.Role)?.Value;
        string? stamp = authenticationState.User.FindFirst("admin_stamp")?.Value;
        string? authMode = authenticationState.User.FindFirst("auth_mode")?.Value;

        // 主密码改过 / 账号改密码 / 改身份等级 / 收回 WebAdmin / 关闭主密码登录 → 立即失效
        return Task.FromResult(credentials.ValidateStamp(name, role, stamp, authMode));
    }
}
