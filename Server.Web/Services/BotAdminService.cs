using Library;
using Server.Bots;
using Server.DBModels;
using Server.Envir;
using Server.Models;
using System.Security.Claims;

namespace Server.Web.Services;

/// <summary>假人总览（后台首屏那一排卡片用的聚合值）。</summary>
public sealed record BotOverview(
    bool SchedulerStarted,
    bool Enabled,
    int Online,
    int Sessions,
    int Disabled,
    int Broken,
    int RealPlayers,
    int Target,
    bool NightActive,
    double AverageLevel,
    int TotalKills,
    int TotalDeaths,
    long TotalGold,
    long ConnectionDelayMs,
    long SaveDelayMs,
    int SurvivalRate,
    int Overweight,
    int NearOverweight,
    long GoldEarned,
    long GoldPerHour,
    DateTime SnapshotAt)
{
    /// <summary>首屏还没取到快照时的占位值（页面初始化用）。</summary>
    public static BotOverview Empty { get; } = new(false, false, 0, 0, 0, 0, 0, 0, false, 0, 0, 0, 0, 0, 0, -1, 0, 0, 0, 0, DateTime.MinValue);

    /// <summary>存活率文本：没有样本时显示 <c>—</c>，而不是 0%（0% 会被误读成「全死光了」）。</summary>
    public string SurvivalRateText => SurvivalRate >= 0 ? $"{SurvivalRate}%" : "—";
}

/// <summary>
/// 假人性能快照（P4-5 后台「假人性能」卡片）。
/// <para>
/// 与 <see cref="BotOverview"/> 分开一个 record 而不是往里塞字段：
/// 那份是「业务指标」（在线 / 等级 / 金币），这份是「机器指标」（帧耗时 / 熔断状态），
/// 页面上的位置也不同（业务卡在上、性能卡在下），混在一起以后只会越来越臃肿。
/// </para>
/// <para>
/// 所有值都是<b>跨线程只读</b>的静态字段，不经过游戏线程排队 —— 性能指标滞后一秒完全无所谓，
/// 但要是在这里排一次 <c>InvokeOnGameThreadAsync</c>，页面每 2 秒刷新一次就会给主循环添堵，
/// 那就成了「监控把被监控对象拖慢」。
/// </para>
/// </summary>
public sealed record BotPerf(
    double BotFrameMs,
    double LoopFrameMs,
    double BotMsPerSecond,
    double BotFrameMaxMs,
    double LoopFrameMaxMs,
    int FramesPerSecond,
    double BotSharePercent,
    long ConnectionDelayMs,
    long SaveDelayMs,
    long MapChanges,
    long Chats,
    bool BreakerEnabled,
    bool BreakerOpen,
    int BreakerCeiling,
    int BreakerOverloadSeconds,
    int BreakerTripCount,
    DateTime BreakerOpenedAt,
    int BreakerSharePercent,
    int BreakerTripSeconds,
    int BreakerRecoverSeconds)
{
    /// <summary>首屏还没取到数据时的占位值。</summary>
    public static BotPerf Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false, false, -1, 0, 0, DateTime.MinValue, 25, 60, 60);

    /// <summary>熔断状态文本。</summary>
    public string BreakerText
    {
        get
        {
            if (!BreakerEnabled) return "未启用";

            if (BreakerOpen) return $"已熔断 · 上限 {BreakerCeiling}";

            return BreakerOverloadSeconds > 0
                ? $"观察中 {BreakerOverloadSeconds}/{BreakerTripSeconds} 秒"
                : "正常";
        }
    }

    /// <summary>熔断触发时刻文本。</summary>
    public string BreakerOpenedText => BreakerOpenedAt == DateTime.MinValue ? "—" : BreakerOpenedAt.ToString("HH:mm:ss");
}

/// <summary>命令结果：成功与否 + 一句能直接显示给运营的中文。</summary>
public sealed record BotCommandResult(bool Ok, string Message)
{
    public static BotCommandResult Fail(string message) => new(false, message);
    public static BotCommandResult Success(string message) => new(true, message);
}

/// <summary>传送目标下拉里的一项。</summary>
public sealed record BotMapOption(int Index, string Name)
{
    public override string ToString() => $"#{Index} {Name}";
}

/// <summary>
/// 假人档案一行（2026-11-11 新增）。
/// <para>
/// 与 <see cref="BotSnapshot"/> 的区别：快照只反映**本次进程**（重启归零），
/// 档案直接读 <c>Users.db</c> 里的角色记录，所以服务器重启后等级、累计击杀、累计在线时长都还在。
/// </para>
/// </summary>
public sealed record BotProfileRow(
    int Seq,
    string Email,
    string CharacterName,
    string ClassName,
    int Level,
    decimal Experience,
    decimal MaxExperience,
    int Kills,
    long OnlineSeconds,
    long Gold,
    int Items,
    bool Online,
    string MapName,
    DateTime LastLogin,
    int Restocks,
    int RestockFails,
    int Trades,
    int Harvests,
    int TownScrollUses,
    int TownScrollBuys)
{
    /// <summary>经验百分比（满级时按 100 算，避免除零）。</summary>
    public int ExpPercent => MaxExperience <= 0 ? 0 : (int)Math.Clamp(Experience * 100 / MaxExperience, 0, 100);

    /// <summary>
    /// 补给成功率（成功 /（成功 + 失败））。D5 量化「断药能回城买」的核心指标。
    /// </summary>
    public string RestockRateText
    {
        get
        {
            int total = Restocks + RestockFails;

            if (total <= 0) return "—";

            return $"{Restocks * 100 / total}%";
        }
    }

    /// <summary>累计在线时长（人类可读）。</summary>
    public string OnlineTimeText
    {
        get
        {
            if (OnlineSeconds <= 0) return "—";

            TimeSpan span = TimeSpan.FromSeconds(OnlineSeconds);

            if (span.TotalDays >= 1) return $"{(int)span.TotalDays} 天 {span.Hours} 小时";
            if (span.TotalHours >= 1) return $"{(int)span.TotalHours} 小时 {span.Minutes} 分";
            return $"{span.Minutes} 分 {span.Seconds} 秒";
        }
    }

    /// <summary>
    /// 上次登录时间（没登录过显示「—」）。
    /// <para>
    /// <c>LastLogin</c> 在库里存的是 <b>UTC</b>（引擎统一用 <c>Time.Now</c>，底层 <c>DateTime.UtcNow</c>），
    /// 直接 <c>ToString</c> 会比北京时间早 8 小时 —— 页面上曾经出现「10:40 登录的假人，上次登录 02:40」。
    /// 展示前必须转本机时区。
    /// </para>
    /// </summary>
    public string LastLoginText =>
        LastLogin == DateTime.MinValue
            ? "—"
            : DateTime.SpecifyKind(LastLogin, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}

/// <summary>
/// AI 假人后台门面。三层职责，写在一处，页面只管画：
/// <list type="number">
/// <item><b>只读快照</b>：直接读 <see cref="BotRoster.Snapshot"/>（游戏线程每 1 秒重建一次，任意线程可读），不排队。</item>
/// <item><b>命令投递</b>：所有写操作都走 <see cref="SEnvir.InvokeOnGameThreadAsync{T}"/>，
/// 与 GM 命令同一个队列，保证只在主循环线程上动 <c>PlayerObject</c>（D1 的老坑：跨线程直接调 Move/Attack 会崩）。</item>
/// <item><b>审计</b>：只在「关闭总开关」「全部重置」写一条。单人的踢人/补给/传送一秒能点十次，
/// 全写审计会把流水刷爆（任务清单 §6.5 的决策）。</item>
/// </list>
/// </summary>
public sealed class BotAdminService(AdminAuditService audit, RuntimeDataService runtimeData)
{
    /// <summary>游戏主循环是否在跑。<see cref="SEnvir.InvokeOnGameThreadAsync"/> 在未启动时会直接抛异常。</summary>
    public bool ServerRunning
    {
        get
        {
            try { return SEnvir.Started; }
            catch { return false; }
        }
    }

    public BotOverview GetOverview()
    {
        BotSnapshot[] rows = BotRoster.Snapshot();

        // 聚合口径统一交给 ServerLibrary.BotRoster.BuildTotals（同一份代码，页面和日志不会算出两个数）。
        BotTotals totals = BotRoster.Totals();

        int disabled = rows.Count(x => !x.Enabled);
        int broken = rows.Count(x => x.Broken);

        int real = BotRoster.RealPlayerCount;

        // 生效目标一律读调度器算好的那个值（已含夜间缩水 + 可用上限 + 熔断压制）。
        //
        // 原先这里自己算了一遍 Math.Clamp(TargetOnline - real, ...)：漏了夜间缩水，
        // 于是「水位目标 30 / 在线 15」同时出现在页面上 —— 2026-11-13 排查「水位只到
        // 15/30」时就是这么被误导的（真实原因是夜间作息，判据还错用了 UTC 小时）。
        // 同一套公式在三个地方各写一遍，迟早再分叉；现在只有 BotRoster.Evaluate 一处。
        //
        // 调度器还没跑过第一轮（刚启动的那几秒）才退回配置值，否则页面会显示 0。
        int target;

        if (BotRoster.Started)
        {
            target = BotRoster.EffectiveTarget;
        }
        else
        {
            target = Config.BotEnabled
                ? Math.Clamp(Config.BotTargetOnline - real, Config.BotMinOnline, Config.BotMaxOnline)
                : 0;
        }

        return new BotOverview(
            BotRoster.Started,
            Config.BotEnabled,
            totals.Online,
            BotRoster.SessionCount,
            disabled,
            broken,
            real,
            target,
            BotRoster.IsNightNow && Config.BotNightEnabled,
            totals.AverageLevel,
            totals.TotalKills,
            totals.TotalDeaths,
            totals.TotalGold,
            SEnvir.ConDelay,
            SEnvir.SaveDelay,
            totals.SurvivalRate,
            totals.Overweight,
            totals.NearOverweight,
            totals.GoldEarned,
            totals.GoldPerHour,
            totals.SnapshotAt);
    }

    public BotSnapshot[] GetSnapshot() => BotRoster.Snapshot();

    /// <summary>
    /// 性能快照（P4-5）。全部读静态字段，<b>不进游戏线程队列</b> —— 见 <see cref="BotPerf"/> 的说明。
    /// 服务端没起来时 <see cref="SEnvir"/> 的静态字段仍是默认值，页面会显示 0，不会抛异常。
    /// </summary>
    public BotPerf GetPerf()
    {
        try
        {
            return new BotPerf(
                ServerPerf.BotMs,
                ServerPerf.LoopMs,
                ServerPerf.BotMsPerSecond,
                ServerPerf.BotMaxMs,
                ServerPerf.LoopMaxMs,
                ServerPerf.FramesPerSecond,
                ServerPerf.BotSharePercent,
                SEnvir.ConDelay,
                SEnvir.SaveDelay,
                BotMetrics.MapChanges,
                BotMetrics.Chats,
                Config.BotBreakerEnabled,
                BotBreaker.Open,
                BotBreaker.Ceiling,
                BotBreaker.OverloadSeconds,
                BotBreaker.TripCount,
                BotBreaker.OpenedAt,
                Config.BotBreakerSharePercent,
                Config.BotBreakerTripSeconds,
                Config.BotBreakerRecoverSeconds);
        }
        catch
        {
            // 读静态字段本身不会抛，但配置类没初始化过时也不该让整页崩掉。
            return BotPerf.Empty;
        }
    }

    /// <summary>
    /// 读某个假人的角色详情（P5-3 详情弹窗）。
    /// <para>
    /// <b>必须走游戏线程</b>：详情直接读 <c>PlayerObject.Equipment</c>，在 Blazor 线程读是数据竞争
    /// （见任务清单 §8.7 坑 1）。返回 <c>null</c> 表示「这个 Seq 不在线 / 还没进图」，页面据此显示提示。
    /// </para>
    /// <para>异常一律吞成 <c>null</c>：详情弹窗拉不到数据不该把整页 circuit 带崩。</para>
    /// </summary>
    public async Task<BotDetail?> GetDetailAsync(int seq)
    {
        if (!ServerRunning) return null;

        try
        {
            return await SEnvir.InvokeOnGameThreadAsync(() => BotRoster.GetDetail(seq));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>传送目标下拉的数据源。地图库没加载（服务未启动）时返回空列表，页面显示「先启动服务器」。</summary>
    public IReadOnlyList<BotMapOption> MapOptions()
    {
        try
        {
            return SEnvir.MapInfoList.Binding
                .Where(x => x != null)
                .OrderBy(x => x.Index)
                .Select(x => new BotMapOption(x.Index, x.Description ?? x.FileName ?? x.Index.ToString()))
                .ToList();
        }
        catch
        {
            return Array.Empty<BotMapOption>();
        }
    }

    /// <summary>假人名单 / 聊天语料的状态（给页面显示「名单 30 条 · 语料 70 条」）。</summary>
    public (string Path, int Count, int ChatCount, bool DirectoryReady) DataFiles()
    {
        string path = BotData.NamesPath;

        int count;

        try { count = BotData.Names().Count; }
        catch { count = 0; }

        // 语料条数走 BotChat.Count（内部按文件 mtime 热加载），
        // 后台看到 0 就说明 chat.txt 没放对位置或被写坏了。
        int chatCount;

        try { chatCount = BotChat.Count; }
        catch { chatCount = 0; }

        return (path, count, chatCount, Directory.Exists(BotData.Root));
    }

    #region 命令

    /// <summary>总开关。关闭时写审计（这是唯一会影响全服的开关）。</summary>
    public async Task<BotCommandResult> SetEnabledAsync(bool enabled, ClaimsPrincipal? user, string? address)
    {
        if (!ServerRunning) return BotCommandResult.Fail("游戏服务器未运行，假人调度器不在线。");

        Config.BotEnabled = enabled;
        ConfigReader.Save(typeof(Config).Assembly);

        // 配置快照是每秒缓存的，不通知的话「点启用」最多要等 1 秒才真的开始拉人。
        BotRoster.InvalidateOptions();

        if (!enabled)
        {
            audit.Record(
                user?.Identity?.Name ?? "unknown",
                "假人-关闭总开关",
                "关闭 BotEnabled，在线假人将按轮次逐个下线",
                address,
                AdminPermissions.Describe(user),
                "Success",
                target: null,
                reason: null,
                userAgent: null);
        }

        await Task.CompletedTask;

        return BotCommandResult.Success(enabled ? "已启用假人调度（按水位拉人）。" : "已停用假人调度，在线假人会逐个优雅下线。");
    }

    /// <summary>
    /// 单人操作统一守卫。
    /// <para>
    /// 页面上的按钮靠 <c>Disabled</c> 挡，但那只是渲染属性 —— 去掉 disabled 之后事件照样
    /// 能发到服务端。所以每个会改动游戏状态的方法都必须在这里再判一次，不能只信前端。
    /// </para>
    /// </summary>
    private static BotCommandResult? RequireOperate(ClaimsPrincipal? user) =>
        AdminPermissions.CanOperate(user)
            ? null
            : BotCommandResult.Fail("当前身份没有操作权限（需要「运营」及以上）。");

    /// <summary>会改角色数据的操作（技能自检会给假人加经验），只对主密码开放。</summary>
    private static BotCommandResult? RequireOwner(ClaimsPrincipal? user) =>
        AdminPermissions.CanOwner(user)
            ? null
            : BotCommandResult.Fail("这项操作需要主密码身份。");

    /// <summary>记一条「谁点了什么」的审计。失败的操作也记，因为「点了但没生效」同样要能追溯。</summary>
    private void Record(ClaimsPrincipal? user, string action, string detail) =>
        audit.Record(
            user?.Identity?.Name ?? "unknown",
            action,
            detail,
            address: null,
            AdminPermissions.Describe(user),
            "Success",
            target: null,
            reason: null,
            userAgent: null);

    public async Task<BotCommandResult> ForceSpawnAsync(int count, ClaimsPrincipal? user)
    {
        BotCommandResult? denied = RequireOperate(user);
        if (denied != null) return denied;

        BotCommandResult result = await InvokeAsync(() => BotRoster.ForceSpawn(count),
            n => n > 0 ? $"已拉起 {n} 个假人。" : "没有可拉起的空闲序号（都在线 / 已停用 / 故障）。");

        Record(user, "假人-手动上线", $"{count} 个：{result.Message}");

        return result;
    }

    public async Task<BotCommandResult> ForceDespawnAsync(int count, ClaimsPrincipal? user)
    {
        BotCommandResult? denied = RequireOperate(user);
        if (denied != null) return denied;

        BotCommandResult result = await InvokeAsync(() => BotRoster.ForceDespawn(count),
            n => n > 0 ? $"已下线 {n} 个假人。" : "当前没有可下线的假人。");

        Record(user, "假人-手动下线", $"{count} 个：{result.Message}");

        return result;
    }

    /// <summary>单人开关：关掉之后调度器不再拉起这个序号。</summary>
    public async Task<BotCommandResult> SetEnabledAsync(int seq, bool enabled, ClaimsPrincipal? user)
    {
        BotCommandResult? denied = RequireOperate(user);
        if (denied != null) return denied;

        BotCommandResult result = await InvokeAsync(() => BotRoster.SetEnabled(seq, enabled),
            ok => ok ? $"BOT#{seq:00} 已{(enabled ? "启用" : "停用")}。" : $"找不到 BOT#{seq:00}。");

        Record(user, enabled ? "假人-启用单人" : "假人-停用单人", $"BOT#{seq:00}");

        return result;
    }

    public async Task<BotCommandResult> KickAsync(int seq, ClaimsPrincipal? user)
    {
        BotCommandResult? denied = RequireOperate(user);
        if (denied != null) return denied;

        BotCommandResult result = await InvokeAsync(() => BotRoster.Kick(seq),
            ok => ok ? $"BOT#{seq:00} 开始下线（调度器稍后会按水位重新拉起）。" : $"BOT#{seq:00} 不在线或不存在。");

        Record(user, "假人-踢下线", $"BOT#{seq:00}：{result.Message}");

        return result;
    }

    public async Task<BotCommandResult> RestockAsync(int seq, ClaimsPrincipal? user)
    {
        BotCommandResult? denied = RequireOperate(user);
        if (denied != null) return denied;

        BotCommandResult result = await InvokeAsync(() => BotRoster.RequestRestock(seq),
            ok => ok ? $"BOT#{seq:00} 已收到补给指令，下一次决策就回城。" : $"BOT#{seq:00} 不在线，无法补给。");

        Record(user, "假人-立即补给", $"BOT#{seq:00}");

        return result;
    }

    public async Task<BotCommandResult> ResetOneAsync(int seq, ClaimsPrincipal? user)
    {
        BotCommandResult? denied = RequireOperate(user);
        if (denied != null) return denied;

        BotCommandResult result = await InvokeAsync(() => BotRoster.Reset(seq),
            ok => ok ? $"BOT#{seq:00} 已重置为可调度状态。" : $"找不到 BOT#{seq:00}。");

        Record(user, "假人-单人重置", $"BOT#{seq:00}");

        return result;
    }

    /// <summary>
    /// 调试用：把某个在线假人直接加到<b>指定</b>等级，跑一次技能补齐，回报「学没学到、学到几个」。
    /// 会给假人加经验（等于作弊），所以只对 <c>CanOwner</c>（主密码）开放。
    /// 目标等级由页面弹窗输入传入，非法值由 <see cref="BotRoster.SkillCheck"/> 拒绝，不夹不猜。
    /// </summary>
    public async Task<BotCommandResult> SkillCheckAsync(int seq, int targetLevel, ClaimsPrincipal? user)
    {
        BotCommandResult? denied = RequireOwner(user);
        if (denied != null) return denied;

        BotCommandResult result = await InvokeAsync(() => BotRoster.SkillCheck(seq, targetLevel),
            r => r.Ok ? "技能自检：" + r.Detail : "技能自检失败：" + r.Detail);

        Record(user, "假人-技能自检", $"BOT#{seq:00}：{result.Message}");

        return result;
    }

    public async Task<BotCommandResult> TeleportAsync(int seq, int mapIndex, int x, int y, ClaimsPrincipal? user)
    {
        BotCommandResult? denied = RequireOperate(user);
        if (denied != null) return denied;

        BotCommandResult result = await InvokeAsync(() => BotRoster.Teleport(seq, mapIndex, x, y),
            ok => ok ? $"BOT#{seq:00} 已传送到地图 #{mapIndex}（{x},{y}）。" : $"传送失败：BOT#{seq:00} 不在线，或地图 #{mapIndex} 不可用。");

        Record(user, "假人-传送", $"BOT#{seq:00} → 地图 #{mapIndex} ({x},{y})：{result.Message}");

        return result;
    }

    /// <summary>全部重置：把 Broken / 卡住的都拉回可调度。写审计（一次重排 30 个假人）。</summary>
    public async Task<BotCommandResult> ResetAllAsync(ClaimsPrincipal? user, string? address)
    {
        if (!ServerRunning) return BotCommandResult.Fail("游戏服务器未运行。");

        try
        {
            int count = await SEnvir.InvokeOnGameThreadAsync(() =>
            {
                int sessions = BotRoster.SessionCount;

                BotRoster.ResetAll();

                return sessions;
            });

            audit.Record(
                user?.Identity?.Name ?? "unknown",
                "假人-全部重置",
                $"重置 {count} 个假人序号（清理 Broken / 断线状态）",
                address,
                AdminPermissions.Describe(user),
                "Success",
                target: null,
                reason: null,
                userAgent: null);

            return BotCommandResult.Success($"已重置 {count} 个假人。");
        }
        catch (Exception ex)
        {
            return BotCommandResult.Fail($"重置失败：{ex.Message}");
        }
    }

    #endregion

    #region 假人档案与清理（2026-11-11）

    /// <summary>
    /// 假人档案（跨重启的历史视角）。
    /// <para>
    /// 直接读 <c>Users.db</c> 里的角色记录，不依赖在线状态 ——
    /// 假人下线、服务器重启之后，等级 / 累计击杀 / 累计在线时长都还在。
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<BotProfileRow>> GetProfilesAsync(CancellationToken cancellationToken = default)
    {
        if (!ServerRunning) return Array.Empty<BotProfileRow>();

        try
        {
            // 必须 await，不能 return 这个 Task —— 原先写成 `return InvokeOnGameThreadAsync(...)`
            // 外面套 try，等于把异常留给调用方：任务里抛出的异常不会经过这个 catch。
            // 页面那边的 catch 又是「一抛就 return」，于是刷新循环悄悄死掉，档案表永远空白。
            return await SEnvir.InvokeOnGameThreadAsync<IReadOnlyList<BotProfileRow>>(ReadProfiles, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // 页面关掉了，正常路径，不记日志。
            return Array.Empty<BotProfileRow>();
        }
        catch (Exception ex)
        {
            // 服务没起来 / 数据未加载：返回空列表，页面显示「先启动服务器」。
            // 但一定要留痕：空表和「读库炸了」在页面上长得一模一样。
            LastProfileError = ex.Message;

            BotLog.SystemError($"假人档案读取失败：{ex.GetType().Name}: {ex.Message}");

            return Array.Empty<BotProfileRow>();
        }
    }

    /// <summary>最近一次读假人档案的异常信息（页面在空表时把它显示出来，避免「静默空白」）。</summary>
    public string? LastProfileError { get; private set; }

    private string? _lastRefreshError;

    /// <summary>
    /// 记录页面刷新循环里的异常。<b>同一条错只记一次</b> —— 循环每 2 秒一轮，
    /// 每次都写会把 Logs.txt 刷爆，反而看不见别的线索。
    /// </summary>
    public void ReportRefreshError(Exception ex)
    {
        string message = $"{ex.GetType().Name}: {ex.Message}";

        if (string.Equals(message, _lastRefreshError, StringComparison.Ordinal)) return;

        _lastRefreshError = message;

        BotLog.SystemError($"假人页刷新循环异常（循环继续）：{message}");
    }

    private static IReadOnlyList<BotProfileRow> ReadProfiles()
    {
        // 在线角色索引：判断这个假人现在在不在线、在哪张图。
        Dictionary<int, string> onlineMaps = new();

        foreach (SConnection con in SEnvir.Connections)
        {
            PlayerObject? player = con.Player;

            if (player?.Character == null) continue;

            onlineMaps[player.Character.Index] = player.CurrentMap?.Info?.Description ?? "-";
        }

        List<BotProfileRow> rows = new();

        foreach (AccountInfo account in SEnvir.AccountInfoList.Binding)
        {
            if (!account.IsBot) continue;

            foreach (CharacterInfo character in account.Characters)
            {
                if (character.Deleted) continue;

                bool online = onlineMaps.TryGetValue(character.Index, out string? mapName);

                // 经验上限按等级查表（与 PlayerObject 里的算法一致：MaxExperience = ExperienceList[Level]）
                decimal maxExperience = character.Level < Globals.ExperienceList.Count
                    ? Globals.ExperienceList[character.Level]
                    : 0;

                rows.Add(new BotProfileRow(
                    ParseSeq(account.EMailAddress),
                    account.EMailAddress,
                    character.CharacterName,
                    character.Class.ToString(),
                    character.Level,
                    character.Experience,
                    maxExperience,
                    character.BotKills,
                    character.BotOnlineSeconds,
                    character.Gold?.Amount ?? 0,
                    character.Items.Count,
                    online,
                    online ? mapName ?? "-" : "-",
                    account.LastLogin,
                    character.BotRestocks,
                    character.BotRestockFails,
                    character.BotTrades,
                    character.BotHarvests,
                    character.BotTownScrollUses,
                    character.BotTownScrollBuys));
            }
        }

        return rows.OrderBy(x => x.Seq).ThenBy(x => x.CharacterName).ToList();
    }

    /// <summary>从假人邮箱里取序号（hnt07@mir3.local → 7）。取不到就按 0 排最后。</summary>
    private static int ParseSeq(string email)
    {
        if (string.IsNullOrEmpty(email)) return 0;

        int at = email.IndexOf('@');
        if (at <= 0) return 0;

        string digits = new(email[..at].Where(char.IsDigit).ToArray());

        return int.TryParse(digits, out int seq) ? seq : 0;
    }

    /// <summary>清理前的 dry-run 预览：将被清掉多少东西。</summary>
    public Task<BotCleanupStats> PreviewCleanupAsync(CancellationToken cancellationToken = default)
    {
        if (!ServerRunning) return Task.FromResult(BotCleanupStats.Empty);

        try
        {
            return runtimeData.PreviewBotCleanupAsync(cancellationToken);
        }
        catch
        {
            return Task.FromResult(BotCleanupStats.Empty);
        }
    }

    /// <summary>
    /// 清理假人（删账号 + 角色 + 全部关联数据），2026-11-11。
    /// <para>
    /// 流程：停调度 → 让在线假人优雅下线（在线账号删不掉）→ 等连接断开 → 按 <c>IsBot</c> 精确删除。
    /// 只删假人，真人账号一个都不碰。
    /// </para>
    /// <para>
    /// <paramref name="resumeScheduler"/> 为 true 时清完立刻恢复调度 ——
    /// 假人会被重新建号，从 1 级重新开始（等于「重置假人」）；
    /// 为 false 时调度保持停用，假人不再上线（等于「清理并停用」）。
    /// </para>
    /// </summary>
    public async Task<BotCommandResult> CleanupBotsAsync(ClaimsPrincipal? user, string? address, bool resumeScheduler)
    {
        if (!ServerRunning) return BotCommandResult.Fail("游戏服务器未运行。");

        string operatorName = user?.Identity?.Name ?? "unknown";

        try
        {
            // 1) 先停调度：调度还开着的话，撤下的假人会被立刻重新拉起来，删不掉。
            int despawned = await SEnvir.InvokeOnGameThreadAsync(() =>
            {
                Config.BotEnabled = false;
                BotRoster.InvalidateOptions();

                return BotRoster.ForceDespawn(int.MaxValue);
            });

            // 配置落盘放游戏线程外面：写 ini 是同步 IO，压在主循环里会拖慢这一帧。
            ConfigReader.Save(typeof(Config).Assembly);

            // 2) 等连接真正断开（StopGame → Disconnect 是异步的，在线账号删不掉）。
            if (despawned > 0) await Task.Delay(3000);

            // 3) 按 IsBot 精确删除。
            BotCleanupStats result = await runtimeData.DeleteBotAccountsAsync(operatorName);

            // 4) 按需恢复调度。
            if (resumeScheduler)
            {
                await SEnvir.InvokeOnGameThreadAsync(() =>
                {
                    Config.BotEnabled = true;
                    BotRoster.InvalidateOptions();

                    return true;
                });

                ConfigReader.Save(typeof(Config).Assembly);
            }

            audit.Record(
                operatorName,
                "假人-清理假人",
                $"删除假人账号 {result.Accounts} 个、角色 {result.Characters} 个" +
                (resumeScheduler ? "，已恢复调度（假人将重新建号，从 1 级开始）" : "，调度保持停用"),
                address,
                AdminPermissions.Describe(user),
                "Success",
                target: null,
                reason: null,
                userAgent: null);

            string tail = resumeScheduler
                ? "调度已恢复，假人会重新建号并从 1 级开始。"
                : "调度保持停用，假人不会再上线（要恢复请点右上角「启用假人」）。";

            return BotCommandResult.Success(
                $"已清理假人账号 {result.Accounts} 个、角色 {result.Characters} 个" +
                $"（物品 {result.Items}、货币 {result.Currencies}、宠物 {result.Companions}、" +
                $"邮件 {result.Mails}、寄售 {result.Auctions}、任务 {result.Quests}）。{tail}");
        }
        catch (Exception ex)
        {
            return BotCommandResult.Fail($"清理失败：{ex.Message}");
        }
    }

    #endregion

    /// <summary>
    /// 统一投递模板：排队 → 执行 → 用一句中文描述结果。异常一律转成「失败 + 原因」，绝不冒泡到 Blazor circuit。
    /// </summary>
    /// <summary>
    /// 后台手动解除全局熔断（P4-5）。
    /// <para>
    /// 逃生口：阈值调得太低导致误触发时，不用等 60 秒恢复窗口，也不用去改 <c>Server.ini</c> 重启。
    /// 注意解除后如果假人段耗时仍然超标，60 秒后会重新触发 —— 那是设计如此，不是没生效。
    /// </para>
    /// </summary>
    public async Task<BotCommandResult> ResetBreakerAsync(ClaimsPrincipal? user)
    {
        BotCommandResult? denied = RequireOperate(user);
        if (denied != null) return denied;

        if (!BotBreaker.Open) return BotCommandResult.Success("当前没有处于熔断状态。");

        BotCommandResult result = await InvokeAsync(
            () => { BotBreaker.Reset("后台手动解除"); return BotBreaker.Open; },
            open => open ? "熔断仍未解除（下一秒可能又触发了）。" : "熔断已解除，水位上限恢复。");

        Record(user, "假人-解除熔断", result.Message);

        return result;
    }

    private async Task<BotCommandResult> InvokeAsync<T>(Func<T> action, Func<T, string> describe)
    {
        if (!ServerRunning) return BotCommandResult.Fail("游戏服务器未运行。");

        try
        {
            T result = await SEnvir.InvokeOnGameThreadAsync(action);

            return BotCommandResult.Success(describe(result));
        }
        catch (Exception ex)
        {
            return BotCommandResult.Fail($"操作失败：{ex.Message}");
        }
    }

    #region 练级地图白名单（2026-11-13）

    /// <summary>
    /// 按等级段重算练级白名单。**只算不写**：结果给页面预览，确认后再走
    /// <c>ConfigurationService.Save</c> 落盘 —— 白名单一旦写错，现象是「假人原地不动」
    /// 且只有 Debug 日志，所以必须留一步人工确认。
    /// <para>
    /// 必须进游戏线程：要遍历 <c>SEnvir.RespawnInfoList</c> / <c>MapInfoList</c> 的
    /// <c>Binding</c>，那两个集合会被存库与地图加载改写。
    /// </para>
    /// </summary>
    public async Task<(HuntMapPlan? Plan, string? Error)> PlanHuntMapsAsync()
    {
        if (!ServerRunning) return (null, "游戏服务器未运行。");

        try
        {
            HuntMapPlan plan = await SEnvir.InvokeOnGameThreadAsync(() => BotHuntMapPlanner.Build(Config.MaxLevel));

            return (plan, null);
        }
        catch (Exception ex)
        {
            return (null, $"重算失败：{ex.Message}");
        }
    }

    #endregion
}
