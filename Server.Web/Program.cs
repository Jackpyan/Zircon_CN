using System.Collections.Generic;
using System.Reflection;
using System.Runtime;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Library;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.RateLimiting;
using Radzen;
using Server.Envir;
using Server.Web.Auth;
using Server.Web.Components;
using Server.Web.Models;
using Server.Web.Services;

Assembly configAssembly = Assembly.GetAssembly(typeof(Config))!;
ConfigReader.Load(configAssembly);
Config.LoadVersion();
Encryption.SetKey(Config.EncryptionEnabled ? Convert.FromBase64String(Config.EncryptionKey) : null);
GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

System.Net.IPAddress adminAddress = AdminEndpointValidator.ValidateAndResolve();

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// 静态资源解析（后台页面「全是文字、没有样式」的根因就在这里）：
// 框架默认只在 Development 环境加载 *.staticwebassets.runtime.json 清单，而本程序是 Production。
// 于是 app.MapStaticAssets() 的端点虽然能匹配，但底下的文件提供器是空的 ——
// /css/app.css、/_framework/blazor.web.js、/_content/Radzen.Blazor/* 全部返回 200 + 0 字节。
// 这里不依赖环境变量，只要清单文件在就显式加载一次。
// 注意：清单里记的是**构建机的绝对路径**，所以跨机部署（尤其 Windows 构建拷到 Linux）必须用
// dotnet publish —— 发布产物自带 wwwroot，不依赖这份清单。
if (File.Exists(Path.Combine(AppContext.BaseDirectory, "Server.Web.staticwebassets.runtime.json")))
{
    try
    {
        Microsoft.AspNetCore.Hosting.StaticWebAssets.StaticWebAssetsLoader
            .UseStaticWebAssets(builder.Environment, builder.Configuration);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"静态资源清单加载失败（CSS/JS 可能返回 0 字节）：{ex.GetType().Name}: {ex.Message}");
    }
}

// 停服流程：游戏服停机 + 存库放在 ApplicationStopping 里同步完成（见文件末尾）。
// 这里的 ShutdownTimeout 只影响「Kestrel 等浏览器长连接关闭」的时间。
// 实测存库仅 0.3s，而默认 30s 全耗在等 Blazor 的 WebSocket 上 → 收到 3s。
builder.Services.Configure<Microsoft.Extensions.Hosting.HostOptions>(options =>
{
    options.ShutdownTimeout = TimeSpan.FromSeconds(3);
});
builder.WebHost.ConfigureKestrel(options => options.Listen(adminAddress, Config.AdminWebPort));

// 根据 Server.ini 的 AdminWebHost 自动放行对应的 Host 头，避免以内网 IP 访问时被 Host Filtering 返回 400 Bad Request - Invalid Hostname
var adminHost = Config.AdminWebHost;
var allowedHosts = new List<string> { "localhost", "127.0.0.1", "[::1]" };

if (string.Equals(adminHost, "0.0.0.0", StringComparison.OrdinalIgnoreCase))
{
    // 监听所有网卡时，放行所有 Host；具体允许哪台机器访问由防火墙/反向代理控制
    allowedHosts.Clear();
    allowedHosts.Add("*");
}
else if (!string.Equals(adminHost, "localhost", StringComparison.OrdinalIgnoreCase) &&
         !string.Equals(adminHost, "127.0.0.1", StringComparison.OrdinalIgnoreCase))
{
    // 绑定到具体内网 IP 时，放行该 IP 作为 Host 头
    allowedHosts.Add(adminHost);
}

builder.Services.Configure<HostFilteringOptions>(options =>
{
    options.AllowedHosts = allowedHosts.ToArray();
});

string keyDirectory = Path.Combine(AppContext.BaseDirectory, "DataProtectionKeys");
Directory.CreateDirectory(keyDirectory);

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory))
    .SetApplicationName("Zircon.Server.Web");

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/auth/logout";
        options.AccessDeniedPath = "/denied";
        options.Cookie.Name = "zircon.admin";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
        options.Events.OnValidatePrincipal = context =>
        {
            AdminCredentialService credentials = context.HttpContext.RequestServices.GetRequiredService<AdminCredentialService>();

            string? name = context.Principal?.Identity?.Name;
            string? role = context.Principal?.FindFirst(ClaimTypes.Role)?.Value;
            string? stamp = context.Principal?.FindFirst("admin_stamp")?.Value;
            string? authMode = context.Principal?.FindFirst("auth_mode")?.Value;

            // 必须按「登录方式」校验：主密码登录用主密码指纹，账号登录用账号指纹
            // （账号身份=管理员时角色也是 Administrator，但指纹必须走账号，否则降权不会失效）。
            if (!credentials.ValidateStamp(name, role, stamp, authMode))
            {
                context.RejectPrincipal();
                return Task.CompletedTask;
            }

            // 会话策略：空闲超时 / 单点登录被顶
            AdminSessionRegistry sessions = context.HttpContext.RequestServices.GetRequiredService<AdminSessionRegistry>();
            string? sid = context.Principal?.FindFirst("sid")?.Value;

            SessionCheck check = sessions.Validate(sid, name);

            if (check is SessionCheck.Idle or SessionCheck.Superseded)
            {
                // 把原因留给本次请求的端点（/auth/ping 靠它回 superseded / idle）。
                // 只 RejectPrincipal 的话端点只能看到「未登录」，前端就分不清是被顶还是没登录。
                context.HttpContext.Items[AdminSessionRegistry.ReasonItemKey] = check;

                // 不再 Remove：Superseded 的会话要稳定保持「被顶」状态，
                // 删掉记录会让它下次变成「sid 不存在」，容易被误收编而复活。
                context.RejectPrincipal();
                return Task.CompletedTask;
            }

            // Unknown（进程重启后内存表丢失）时不做判定，避免把所有人踢下线；
            // 正常情况由登录重新登记。Ok 时顺手续期。
            if (check == SessionCheck.Ok) sessions.Touch(sid);

            return Task.CompletedTask;
        };

        // 整页跳转（非 SPA 内部导航）时也带上失效原因，
        // 否则用户只看到一张没有提示的登录页，不知道自己是「被顶」还是「超时」。
        options.Events.OnRedirectToLogin = context =>
        {
            if (context.HttpContext.Items.TryGetValue(AdminSessionRegistry.ReasonItemKey, out object? raw) &&
                raw is SessionCheck check && check is SessionCheck.Idle or SessionCheck.Superseded)
            {
                string reason = check == SessionCheck.Superseded ? "superseded" : "idle";
                context.Response.Redirect($"/auth/session/expire?reason={reason}");
                return Task.CompletedTask;
            }

            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
    });

builder.Services.AddAuthorization(options =>
{
    // 旧策略：保留兼容（主密码登录或身份≥管理员）
    options.AddPolicy("Administrator", policy => policy.RequireRole(AdminCredentialService.RoleAdministrator));

    // ===== 二期：四档权限（读 / 运营 / 管理 / 主密码）=====
    // 读：任意登录者（登录本身已要求身份≥监督者）
    options.AddPolicy(AdminPermissions.PolicyRead, policy => policy.RequireAuthenticatedUser());

    // 运营：发放物品 / 踢人 / 解封 / 改角色运行时数据
    options.AddPolicy(AdminPermissions.PolicyOperate, policy => policy.RequireAssertion(context =>
        AdminPermissions.CanOperate(context.User)));

    // 管理：改静态数据（物品/怪物/地图）、保存配置、改账号身份、新建账号
    options.AddPolicy(AdminPermissions.PolicyManage, policy => policy.RequireAssertion(context =>
        AdminPermissions.CanManage(context.User)));

    // 主密码：删账号 / 启停服务器 / 插件 / 加密密钥 / 配置同步
    options.AddPolicy(AdminPermissions.PolicyOwner, policy => policy.RequireAssertion(context =>
        AdminPermissions.CanOwner(context.User)));
});
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, RevalidatingAdminAuthenticationStateProvider>();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddRadzenComponents();

builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("admin-login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(15),
            QueueLimit = 0
        }));
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownProxies.Add(System.Net.IPAddress.Loopback);
    options.KnownProxies.Add(System.Net.IPAddress.IPv6Loopback);
    options.ForwardLimit = 1;
});

builder.Services.AddSingleton<AdminCredentialService>();
// TOTP 二次验证（主密码登录用）：开关在服务器配置页 [AdminWeb] AdminWebRequireTotp
builder.Services.AddSingleton<TotpService>();
builder.Services.AddSingleton<TotpChallengeStore>();
// 会话策略：空闲超时 + 单点登录（配置页 [AdminWeb] AdminWebIdleTimeoutMinutes / AdminWebSingleSession）
builder.Services.AddSingleton<AdminSessionRegistry>();
builder.Services.AddSingleton<AdminAuditService>();
// 审计异步落盘的后台线程（GM 命令从游戏线程 Enqueue，不能在那里等磁盘）
builder.Services.AddHostedService(provider => provider.GetRequiredService<AdminAuditService>());
builder.Services.AddSingleton<LoginThrottleService>();
builder.Services.AddSingleton<IGameServerController, GameServerController>();
builder.Services.AddHostedService(provider => (GameServerController)provider.GetRequiredService<IGameServerController>());
builder.Services.AddSingleton<ServerMetricsService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<ServerMetricsService>());
builder.Services.AddSingleton<LogBufferService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<LogBufferService>());
builder.Services.AddSingleton<ConfigurationService>();
builder.Services.AddSingleton<GameDataViewCatalog>();
builder.Services.AddSingleton<GameDataSessionService>();
builder.Services.AddSingleton<PluginRegistry>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<PluginRegistry>());
builder.Services.AddSingleton<MapDataService>();
builder.Services.AddSingleton<MapImageService>();
builder.Services.AddSingleton<RuntimeDataService>();
builder.Services.AddSingleton<OrphanDiagnosticService>();
// AI 假人后台门面：只读快照 + 命令投递（不建后台线程，调度本身在游戏主循环里跑）
builder.Services.AddSingleton<BotAdminService>();

WebApplication app = builder.Build();

// System.db must be initialized before an administrator can start the game loop.
// Resolving the singleton here applies the same defaults/migrations that the old SMain load path applied.
_ = app.Services.GetRequiredService<GameDataSessionService>();

// 游戏内 GM 命令 → 后台审计流水（与 Web 操作同一份文件，靠 Source="GM" 区分）。
// 走 Enqueue 而不是 Record：命令跑在游戏主循环线程上，落盘交给后台线程，不能阻塞它。
AdminAuditService auditSink = app.Services.GetRequiredService<AdminAuditService>();
CommandAudit.Sink = record =>
{
    string detail = string.IsNullOrWhiteSpace(record.Arguments)
        ? record.Command
        : $"{record.Command} {record.Arguments}";

    if (!string.IsNullOrWhiteSpace(record.Map))
        detail += $"（{record.Map}）";

    auditSink.Enqueue(new AdminAuditEntry(
        new DateTimeOffset(record.Time),
        string.IsNullOrWhiteSpace(record.Account) ? "unknown" : record.Account,
        "GMCommand",
        detail,
        record.Address,
        Role: record.Identity,
        Result: record.Result,
        Target: record.Character,
        Reason: record.Reason,
        UserAgent: null,
        Source: "GM"));
};

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseForwardedHeaders();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapPost("/auth/login", async (HttpContext context, IAntiforgery antiforgery, AdminCredentialService credentials,
    AdminAuditService audit, LoginThrottleService throttle, TotpService totp, TotpChallengeStore challenges) =>
{
    await antiforgery.ValidateRequestAsync(context);
    IFormCollection form = await context.Request.ReadFormAsync(context.RequestAborted);
    string userName = form["username"].ToString();
    string password = form["password"].ToString();

    string? address = context.Connection.RemoteIpAddress?.ToString();
    string? userAgent = context.Request.Headers.UserAgent.ToString();
    bool isIntranet = AdminEndpointValidator.IsIntranet(context.Connection.RemoteIpAddress);

    // 先看是否已被封禁（可在后台「安全 → 登录黑名单」解除）
    if (throttle.IsBlocked(address, userName, out LoginThrottleEntry? blocked))
    {
        audit.Record(userName, "LoginBlocked", "登录被拦截", address,
            role: null, result: "Denied", target: "WebConsole",
            reason: $"BlockedUntil={blocked!.BlockedUntil:yyyy-MM-dd HH:mm:ss}", userAgent: userAgent);

        return Results.Redirect("/login?error=2");
    }

    AdminLoginResult result = credentials.Validate(userName, password, isIntranet);

    if (!result.Ok)
    {
        // 游戏服未启动 → 账号库不可用，无法核对权限。这既不是密码错也不该计入登录失败次数，
        // 单独提示并让管理员去启动服务进程（否则用户会一直以为密码错了）。
        if (string.Equals(result.Reason, "ServerNotRunning", StringComparison.Ordinal))
        {
            audit.Record(userName, "LoginDeferred", "登录未完成：游戏服未启动", address,
                role: null, result: "Deferred", target: "WebConsole", reason: result.Reason, userAgent: userAgent);

            return Results.Redirect("/login?error=3");
        }

        throttle.RecordFailure(address, userName);

        audit.Record(userName, "LoginFailed", "登录失败", address,
            role: null, result: "Failed", target: "WebConsole", reason: result.Reason, userAgent: userAgent);

        return Results.Redirect("/login?error=1");
    }

    throttle.Reset(address, userName);

    // TOTP 二次验证：仅主密码登录；服务器配置页 AdminWebRequireTotp 关闭时整体失效
    if (TotpService.IsRequired && string.Equals(result.Reason, AdminCredentialService.AuthModeMasterPassword, StringComparison.Ordinal))
    {
        string token = challenges.Create(result);

        audit.Record(result.UserName, "LoginTotpPending", "主密码校验通过，等待 TOTP 验证", address,
            role: result.Role, result: "Pending", target: "WebConsole", reason: result.Reason, userAgent: userAgent);

        return Results.Redirect(totp.IsBound
            ? $"/totp?token={token}"
            : $"/totp?token={token}&setup=1");
    }

    await SignInAdminAsync(context, result, credentials, address, userAgent, audit);

    return Results.Redirect("/");
});

app.MapPost("/auth/logout", async (HttpContext context, IAntiforgery antiforgery, AdminAuditService audit) =>
{
    await antiforgery.ValidateRequestAsync(context);
    audit.Record(context.User.Identity?.Name ?? "unknown", "Logout", "退出登录",
        context.Connection.RemoteIpAddress?.ToString(),
        role: context.User.FindFirst(ClaimTypes.Role)?.Value,
        result: "Success", target: "WebConsole", reason: null,
        userAgent: context.Request.Headers.UserAgent.ToString());
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
}).RequireAuthorization();

// ===== 会话策略：心跳续期 / 空闲或顶号后跳登录页 =====
app.MapGet("/auth/ping", (HttpContext context, AdminSessionRegistry sessions) =>
{
    if (context.User.Identity?.IsAuthenticated != true)
    {
        // OnValidatePrincipal 已把「为什么失效」放进 Items；读不到才是真的没登录。
        string reason = context.Items.TryGetValue(AdminSessionRegistry.ReasonItemKey, out object? raw) && raw is SessionCheck
            ? raw switch
            {
                SessionCheck.Superseded => "superseded",
                SessionCheck.Idle => "idle",
                _ => "unauthenticated"
            }
            : "unauthenticated";

        return Results.Json(new { ok = false, reason }, statusCode: 401);
    }

    string? sid = context.User.FindFirst("sid")?.Value;
    string? name = context.User.Identity?.Name;

    SessionCheck check = sessions.Validate(sid, name);

    if (check == SessionCheck.Ok)
    {
        sessions.Touch(sid);
        return Results.Ok(new { ok = true, idleMinutes = Config.AdminWebIdleTimeoutMinutes });
    }

    string reason2 = check switch
    {
        SessionCheck.Idle => "idle",
        SessionCheck.Superseded => "superseded",
        _ => "unknown"
    };

    return Results.Json(new { ok = false, reason = reason2 }, statusCode: 401);
});

// 会话表诊断：单点登录 / 空闲超时排查（管理权限即可，不泄露完整 sid）
app.MapGet("/auth/sessions", (HttpContext context, AdminSessionRegistry sessions) => Results.Json(new
{
    singleSession = AdminSessionRegistry.SingleSessionEnabled,
    idleTimeoutMinutes = Config.AdminWebIdleTimeoutMinutes,
    currentSid = sessions.GetCurrentSid(context.User.Identity?.Name) is string current && current.Length >= 8 ? current[..8] : null,
    sessionCount = sessions.Count,
    sessions = sessions.Snapshot()
})).RequireAuthorization("WebManage");

// 前端检测到空闲/被顶后跳这里：清 Cookie 并带原因回登录页
app.MapGet("/auth/session/expire", async (HttpContext context, AdminSessionRegistry sessions, AdminAuditService audit) =>
{
    string reason = context.Request.Query["reason"].ToString();
    string? sid = context.User.FindFirst("sid")?.Value;
    string? name = context.User.Identity?.Name;

    sessions.Remove(sid);

    if (!string.IsNullOrEmpty(name))
        audit.Record(name, "SessionExpired", reason switch
            {
                "superseded" => "会话被同账号新登录顶掉",
                "idle" => "会话空闲超时",
                _ => "登录状态失效"
            },
            context.Connection.RemoteIpAddress?.ToString(),
            role: context.User.FindFirst(ClaimTypes.Role)?.Value, result: "Expired", target: "WebConsole",
            reason: reason, userAgent: context.Request.Headers.UserAgent.ToString());

    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

    // 5=空闲超时 6=被顶 7=登录状态失效（Cookie 丢失 / 凭据指纹已变）
    string code = reason switch { "superseded" => "6", "idle" => "5", _ => "7" };

    return Results.Redirect($"/login?error={code}");
});

// ===== TOTP 二次验证（主密码登录）=====
app.MapPost("/auth/totp/verify", async (HttpContext context, IAntiforgery antiforgery, AdminCredentialService credentials,
    AdminAuditService audit, TotpService totp, TotpChallengeStore challenges) =>
{
    await antiforgery.ValidateRequestAsync(context);
    IFormCollection form = await context.Request.ReadFormAsync(context.RequestAborted);

    string? token = form["token"].ToString();
    string? code = form["code"].ToString();

    TotpChallenge? challenge = challenges.Take(token);

    if (challenge == null) return Results.Redirect("/login?error=4");
    if (!TotpService.IsRequired) return Results.Redirect("/login?error=4");

    string? address = context.Connection.RemoteIpAddress?.ToString();
    string? userAgent = context.Request.Headers.UserAgent.ToString();

    if (!totp.VerifyLogin(code ?? string.Empty))
    {
        audit.Record(challenge.UserName, "LoginTotpFailed", "TOTP 验证失败", address,
            role: challenge.Role, result: "Failed", target: "WebConsole", reason: "BadTotpCode", userAgent: userAgent);

        return Results.Redirect($"/totp?token={token}&bad=1");
    }

    challenges.Remove(token);

    AdminLoginResult result = new(true, challenge.UserName, challenge.Role, challenge.Reason, challenge.Identity);

    await SignInAdminAsync(context, result, credentials, address, userAgent, audit);

    return Results.Redirect("/");
});

app.MapPost("/auth/totp/setup", async (HttpContext context, IAntiforgery antiforgery, AdminCredentialService credentials,
    AdminAuditService audit, TotpService totp, TotpChallengeStore challenges) =>
{
    await antiforgery.ValidateRequestAsync(context);
    IFormCollection form = await context.Request.ReadFormAsync(context.RequestAborted);

    string? token = form["token"].ToString();
    string? code = form["code"].ToString();

    TotpChallenge? challenge = challenges.Take(token);

    if (challenge?.PendingSecret == null) return Results.Redirect("/login?error=4");
    if (!TotpService.IsRequired) return Results.Redirect("/login?error=4");

    string? address = context.Connection.RemoteIpAddress?.ToString();
    string? userAgent = context.Request.Headers.UserAgent.ToString();

    IReadOnlyList<string> recoveryCodes;

    try
    {
        recoveryCodes = totp.CompleteSetup(challenge.PendingSecret, code ?? string.Empty);
    }
    catch (InvalidOperationException ex)
    {
        audit.Record(challenge.UserName, "TotpSetupFailed", "TOTP 绑定失败", address,
            role: challenge.Role, result: "Failed", target: "WebConsole", reason: ex.Message, userAgent: userAgent);

        return Results.Redirect($"/totp?token={token}&setup=1&bad=1");
    }

    challenges.Remove(token);

    AdminLoginResult result = new(true, challenge.UserName, challenge.Role, challenge.Reason, challenge.Identity);

    await SignInAdminAsync(context, result, credentials, address, userAgent, audit);

    audit.Record(challenge.UserName, "TotpSetup", "TOTP 绑定成功", address,
        role: challenge.Role, result: "Success", target: "WebConsole", reason: null, userAgent: userAgent);

    // 恢复码只显示这一次
    string codes = string.Join("", recoveryCodes.Select(c => $"<li><code>{c}</code></li>"));
    string html = $$"""
        <!DOCTYPE html><html lang="zh-CN"><head><meta charset="utf-8"><title>TOTP 已绑定</title>
        <style>body{background:#12151b;color:#e6e6e6;font-family:system-ui,-apple-system,"Segoe UI",sans-serif;padding:40px;line-height:1.7}
        .card{max-width:640px;margin:0 auto;background:#1a1f27;border:1px solid #2a3240;border-radius:12px;padding:28px}
        h1{font-size:20px;margin:0 0 12px}code{background:#0d1117;padding:2px 6px;border-radius:4px;font-size:15px}
        ul{columns:2;list-style:none;padding:0}li{margin:6px 0}.warn{color:#ffb020}.btn{display:inline-block;margin-top:18px;background:#ffb020;color:#12151b;padding:10px 18px;border-radius:8px;text-decoration:none;font-weight:600}</style>
        </head><body><div class="card">
        <h1>TOTP 绑定成功</h1>
        <p class="warn">以下 8 个恢复码<strong>只显示这一次</strong>，请立即抄下并离线保存。手机丢失时可用它们登录（每个只能用一次）。</p>
        <ul>{{codes}}</ul>
        <a class="btn" href="/">我已保存，进入后台</a>
        </div></body></html>
        """;

    return Results.Content(html, "text/html; charset=utf-8");
});

app.MapGet("/audit/export", (AdminAuditService audit) =>
{
    System.Text.StringBuilder builder = new();
    builder.AppendLine("时间,操作者,身份,来源,操作,结果,详情,目标,原因,来源IP");

    static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }

    foreach (AdminAuditEntry entry in audit.GetEntries().Reverse())
    {
        builder.AppendLine(string.Join(',',
            Escape(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss")),
            Escape(entry.User),
            Escape(entry.Role),
            Escape(string.Equals(entry.Source, "GM", StringComparison.Ordinal) ? "游戏内" : "后台"),
            Escape(entry.Action),
            Escape(entry.Result),
            Escape(entry.Detail),
            Escape(entry.Target),
            Escape(entry.Reason),
            Escape(entry.Address)));
    }

    byte[] content = System.Text.Encoding.UTF8.GetPreamble()
        .Concat(System.Text.Encoding.UTF8.GetBytes(builder.ToString()))
        .ToArray();

    return Results.File(content, "text/csv; charset=utf-8", $"admin-audit-{DateTime.Now:yyyyMMdd}.csv");
}).RequireAuthorization("Administrator");

app.MapGet("/auth/whoami", (HttpContext context, AdminSessionRegistry sessions) => Results.Json(new
{
    authenticated = context.User.Identity?.IsAuthenticated == true,
    name = context.User.Identity?.Name,
    // 会话诊断：单点登录/空闲超时排查用
    sid = context.User.FindFirst("sid")?.Value?[..8],
    sessionCheck = sessions.Validate(context.User.FindFirst("sid")?.Value, context.User.Identity?.Name).ToString(),
    // 该账号当前被认定的会话（前 8 位）；与上面的 sid 不同 = 本会话已被顶
    currentSid = sessions.GetCurrentSid(context.User.Identity?.Name) is string current && current.Length >= 8 ? current[..8] : null,
    singleSession = AdminSessionRegistry.SingleSessionEnabled,
    idleTimeoutMinutes = Config.AdminWebIdleTimeoutMinutes,
    sessionCount = sessions.Count,
    role = context.User.FindFirst(ClaimTypes.Role)?.Value,
    identity = AdminPermissions.GetIdentity(context.User).ToString(),
    authMode = context.User.FindFirst(AdminPermissions.ClaimAuthMode)?.Value,
    canOperate = AdminPermissions.CanOperate(context.User),
    canManage = AdminPermissions.CanManage(context.User),
    canOwner = AdminPermissions.CanOwner(context.User),
    stamp = context.User.FindFirst("admin_stamp")?.Value,
    masterLoginAllowed = Config.WebAllowMasterLogin,
    accountLoaded = SEnvir.AccountInfoList != null
})).RequireAuthorization();

app.MapGet("/health/live", () => Results.Ok(new { status = "live", time = DateTimeOffset.UtcNow }));

app.MapGet("/api/maps/{fileName}", async (string fileName, MapDataService maps, CancellationToken cancellationToken) =>
    Results.Json(await maps.LoadAsync(fileName, cancellationToken))).RequireAuthorization();

app.MapGet("/api/map-assets/status", (MapImageService images, HttpContext context) =>
    {
        context.Response.Headers.CacheControl = "no-store";
        return Results.Json(images.GetStatus());
    })
    .RequireAuthorization();

app.MapGet("/api/map-assets/{mapFile:int}/{imageIndex:int}",
    (int mapFile, int imageIndex, MapImageService images, HttpContext context) =>
    {
        try
        {
            MapImageLookup lookup = images.GetImage(mapFile, imageIndex);
            if (lookup.State == MapImageState.Empty)
            {
                context.Response.Headers.CacheControl = "private,max-age=31536000,immutable";
                context.Response.Headers["X-Map-Asset-State"] = "empty";
                return Results.NoContent();
            }
            if (lookup.State == MapImageState.MissingLibrary || lookup.Image is null) return Results.NotFound();
            MapImageResult image = lookup.Image;
            context.Response.ContentLength = image.Content.Length;
            context.Response.Headers.ETag = image.ETag;
            context.Response.Headers.CacheControl = "private,max-age=31536000,immutable";
            context.Response.Headers.ContentDisposition = "inline";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Map-Asset-Decoder"] = MapImageService.DecoderVersion;
            return Results.Bytes(image.Content, "image/png");
        }
        catch (Exception ex)
        {
            return Results.Problem($"地图贴图解码失败：{ex.Message}");
        }
    }).RequireAuthorization();

app.MapGet("/plugin-assets/{pluginId}/{**assetPath}", (string pluginId, string assetPath, PluginRegistry plugins) =>
{
    string? path = plugins.ResolveAsset(pluginId, assetPath);
    return path is null ? Results.NotFound() : Results.File(path);
}).RequireAuthorization();

app.MapGet("/api/map-regions/{regionIndex:int}/points", async (
    int regionIndex, int width, GameDataSessionService catalog, CancellationToken cancellationToken) =>
    Results.Json(await catalog.GetMapRegionPointsAsync(regionIndex, width, cancellationToken))).RequireAuthorization();

app.MapPut("/api/map-regions/{regionIndex:int}", async (
    int regionIndex,
    SaveMapRegionRequest request,
    HttpContext context,
    GameDataSessionService catalog,
    CancellationToken cancellationToken) =>
{
    string etag = await catalog.SaveMapRegionAsync(regionIndex, request.Width, request.Height, request.ETag, request.Points,
        context.User.Identity?.Name ?? "unknown", cancellationToken);
    return Results.Json(new { etag });
}).RequireAuthorization();

app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Lifetime.ApplicationStopping.Register(() =>
{
    ShutdownLog("ApplicationStopping：开始关机");

    // 关键：游戏服停机 + 存库必须在这里同步做完。
    // 之后的托管服务停止阶段，Kestrel 要等浏览器长连接（Blazor WebSocket）关闭，
    // 会一直等到 ShutdownTimeout 才放手 —— 若把存库放在那之后，就会被这段等待吞掉。
    if (SEnvir.EnvirThread != null)
    {
        DateTime start = DateTime.Now;
        try
        {
            if (!SEnvir.StopServerAsync().Wait(TimeSpan.FromSeconds(90)))
                ShutdownLog($"ApplicationStopping：等待游戏服停止超时（90s，已过 {(DateTime.Now - start).TotalSeconds:F1}s）");
            else
                ShutdownLog($"ApplicationStopping：游戏服已停止并保存数据库，耗时 {(DateTime.Now - start).TotalSeconds:F1}s");
        }
        catch (Exception ex)
        {
            ShutdownLog($"ApplicationStopping：停止游戏服异常 {ex.GetType().Name}: {ex.Message}");
        }
    }
    else
    {
        ShutdownLog("ApplicationStopping：游戏服未运行，跳过存库");
    }

    try { ConfigReader.Save(configAssembly); }
    catch { /* graceful shutdown must continue */ }

    ShutdownLog("ApplicationStopping：Config 已保存");
});

app.Lifetime.ApplicationStopped.Register(() => ShutdownLog("ApplicationStopped：进程即将退出"));

/// <summary>签发后台登录 Cookie 并写审计（登录端点与 TOTP 端点共用）。</summary>
static async Task SignInAdminAsync(HttpContext context, AdminLoginResult result, AdminCredentialService credentials,
    string? address, string? userAgent, AdminAuditService audit)
{
    AdminSessionRegistry sessions = context.RequestServices.GetRequiredService<AdminSessionRegistry>();

    // 登记会话：单点登录开启时会把同账号的旧会话顶掉
    string sid = sessions.Register(result.UserName);

    Claim[] claims =
    [
        new(ClaimTypes.Name, result.UserName),
        new(ClaimTypes.Role, result.Role),
        // 会话 ID：空闲超时与单点登录都靠它
        new("sid", sid),
        new("admin_stamp", credentials.GetStamp(result.UserName, result.Role, result.Reason)),
        // 登录方式：MasterPassword / WebAdmin（决定用哪套指纹复检）
        new("auth_mode", result.Reason),
        // 身份等级：按钮级权限判定用
        new("identity", result.Identity),
        // 操作者来源 IP：后台「新建账号」等操作把创建 IP 记成使用者电脑的地址
        new("client_ip", address ?? "unknown")
    ];

    ClaimsPrincipal principal = new(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));

    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties
    {
        IsPersistent = false,
        AllowRefresh = true,
        ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
    });

    audit.Record(result.UserName, "Login", "登录成功", address,
        role: result.Role, result: "Success", target: "WebConsole", reason: result.Reason, userAgent: userAgent);
}

app.Run();

/// <summary>关机阶段直接落盘（日志系统可能已停），用于排查停服慢在哪一步。</summary>
static void ShutdownLog(string message)
{
    try
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "Audit");
        Directory.CreateDirectory(directory);
        File.AppendAllText(Path.Combine(directory, "shutdown.log"),
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
    }
    catch
    {
        // 关机日志失败不影响停机
    }
}

public sealed record SaveMapRegionRequest(int Width, int Height, string ETag, IReadOnlyList<MapCellPoint> Points);
