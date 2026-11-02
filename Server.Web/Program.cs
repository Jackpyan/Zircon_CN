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

            // 必须按角色校验：主密码登录用主密码指纹，账号登录用账号指纹。
            // 之前一律比对主密码指纹，导致账号登录后每个请求都被 RejectPrincipal → 弹回登录页。
            if (!credentials.ValidateStamp(name, role, stamp))
                context.RejectPrincipal();

            return Task.CompletedTask;
        };
    });

builder.Services.AddAuthorization(options =>
{
    // 高危操作只允许主密码登录的 Administrator（账号登录为 WebAdmin，只读/常规运维）
    options.AddPolicy("Administrator", policy => policy.RequireRole(AdminCredentialService.RoleAdministrator));
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
builder.Services.AddSingleton<AdminAuditService>();
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

WebApplication app = builder.Build();

// System.db must be initialized before an administrator can start the game loop.
// Resolving the singleton here applies the same defaults/migrations that the old SMain load path applied.
_ = app.Services.GetRequiredService<GameDataSessionService>();

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

app.MapPost("/auth/login", async (HttpContext context, IAntiforgery antiforgery, AdminCredentialService credentials, AdminAuditService audit, LoginThrottleService throttle) =>
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
        throttle.RecordFailure(address, userName);

        audit.Record(userName, "LoginFailed", "登录失败", address,
            role: null, result: "Failed", target: "WebConsole", reason: result.Reason, userAgent: userAgent);

        return Results.Redirect("/login?error=1");
    }

    throttle.Reset(address, userName);

    Claim[] claims =
    [
        new(ClaimTypes.Name, result.UserName),
        new(ClaimTypes.Role, result.Role),
        new("admin_stamp", credentials.GetStamp(result.UserName, result.Role))
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

app.MapGet("/audit/export", (AdminAuditService audit) =>
{
    System.Text.StringBuilder builder = new();
    builder.AppendLine("时间,操作者,身份,操作,结果,详情,原因,来源IP");

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
            Escape(entry.Action),
            Escape(entry.Result),
            Escape(entry.Detail),
            Escape(entry.Reason),
            Escape(entry.Address)));
    }

    byte[] content = System.Text.Encoding.UTF8.GetPreamble()
        .Concat(System.Text.Encoding.UTF8.GetBytes(builder.ToString()))
        .ToArray();

    return Results.File(content, "text/csv; charset=utf-8", $"admin-audit-{DateTime.Now:yyyyMMdd}.csv");
}).RequireAuthorization("Administrator");

app.MapGet("/auth/whoami", (HttpContext context) => Results.Json(new
{
    authenticated = context.User.Identity?.IsAuthenticated == true,
    name = context.User.Identity?.Name,
    role = context.User.FindFirst(ClaimTypes.Role)?.Value,
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
