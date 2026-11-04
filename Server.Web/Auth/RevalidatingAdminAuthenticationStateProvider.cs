using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Server.Web.Services;

namespace Server.Web.Auth;

public sealed class RevalidatingAdminAuthenticationStateProvider(
    ILoggerFactory loggerFactory,
    AdminCredentialService credentials,
    AdminSessionRegistry sessions)
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
        if (!credentials.ValidateStamp(name, role, stamp, authMode))
            return Task.FromResult(false);

        // 会话策略：空闲超时 / 单点登录被顶（电路每分钟复检一次）
        string? sid = authenticationState.User.FindFirst("sid")?.Value;

        SessionCheck check = sessions.Validate(sid, name);

        if (check == SessionCheck.Ok) sessions.Touch(sid);

        // Unknown（进程重启后内存表丢失）放行，避免把所有人踢下线
        return Task.FromResult(check is not (SessionCheck.Idle or SessionCheck.Superseded));
    }
}
