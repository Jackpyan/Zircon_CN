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
    double AverageLevel,
    int TotalKills,
    int TotalDeaths,
    long TotalGold,
    long ConnectionDelayMs,
    long SaveDelayMs)
{
    /// <summary>首屏还没取到快照时的占位值（页面初始化用）。</summary>
    public static BotOverview Empty { get; } = new(false, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
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

    /// <summary>上次登录时间（没登录过显示「—」）。</summary>
    public string LastLoginText => LastLogin == DateTime.MinValue ? "—" : LastLogin.ToString("yyyy-MM-dd HH:mm");
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

        int online = rows.Count(x => x.Phase == "在线");
        int disabled = rows.Count(x => !x.Enabled);
        int broken = rows.Count(x => x.Broken);

        double avgLevel = online > 0 ? rows.Where(x => x.Phase == "在线").Average(x => (double)x.Level) : 0;

        int real = BotRoster.RealPlayerCount;

        int target = Config.BotEnabled
            ? Math.Clamp(Config.BotTargetOnline - real, Config.BotMinOnline, Config.BotMaxOnline)
            : 0;

        return new BotOverview(
            BotRoster.Started,
            Config.BotEnabled,
            online,
            BotRoster.SessionCount,
            disabled,
            broken,
            real,
            target,
            Math.Round(avgLevel, 1),
            rows.Sum(x => x.Kills),
            rows.Sum(x => x.Deaths),
            rows.Sum(x => x.Gold),
            SEnvir.ConDelay,
            SEnvir.SaveDelay);
    }

    public BotSnapshot[] GetSnapshot() => BotRoster.Snapshot();

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

    /// <summary>假人名单文件的状态（给页面显示「名单 30 条 / chat.txt 未实现」）。</summary>
    public (string Path, int Count, bool ChatReady, bool DirectoryReady) DataFiles()
    {
        string path = BotData.NamesPath;

        int count;

        try { count = BotData.Names().Count; }
        catch { count = 0; }

        return (path, count, BotData.ChatFileExists, Directory.Exists(BotData.Root));
    }

    #region 命令

    /// <summary>总开关。关闭时写审计（这是唯一会影响全服的开关）。</summary>
    public async Task<BotCommandResult> SetEnabledAsync(bool enabled, ClaimsPrincipal? user, string? address)
    {
        if (!ServerRunning) return BotCommandResult.Fail("游戏服务器未运行，假人调度器不在线。");

        Config.BotEnabled = enabled;
        ConfigReader.Save(typeof(Config).Assembly);

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

    public Task<BotCommandResult> ForceSpawnAsync(int count) =>
        InvokeAsync(() => BotRoster.ForceSpawn(count),
            n => n > 0 ? $"已拉起 {n} 个假人。" : "没有可拉起的空闲序号（都在线 / 已停用 / 故障）。");

    public Task<BotCommandResult> ForceDespawnAsync(int count) =>
        InvokeAsync(() => BotRoster.ForceDespawn(count),
            n => n > 0 ? $"已下线 {n} 个假人。" : "当前没有可下线的假人。");

    /// <summary>单人开关：关掉之后调度器不再拉起这个序号。</summary>
    public Task<BotCommandResult> SetEnabledAsync(int seq, bool enabled) =>
        InvokeAsync(() => BotRoster.SetEnabled(seq, enabled),
            ok => ok ? $"BOT#{seq:00} 已{(enabled ? "启用" : "停用")}。" : $"找不到 BOT#{seq:00}。");

    public Task<BotCommandResult> KickAsync(int seq) =>
        InvokeAsync(() => BotRoster.Kick(seq),
            ok => ok ? $"BOT#{seq:00} 开始下线（调度器稍后会按水位重新拉起）。" : $"BOT#{seq:00} 不在线或不存在。");

    public Task<BotCommandResult> RestockAsync(int seq) =>
        InvokeAsync(() => BotRoster.RequestRestock(seq),
            ok => ok ? $"BOT#{seq:00} 已收到补给指令，下一次决策就回城。" : $"BOT#{seq:00} 不在线，无法补给。");

    public Task<BotCommandResult> ResetOneAsync(int seq) =>
        InvokeAsync(() => BotRoster.Reset(seq),
            ok => ok ? $"BOT#{seq:00} 已重置为可调度状态。" : $"找不到 BOT#{seq:00}。");

    /// <summary>
    /// D4 验收用：把某个在线假人直接加到 14 级，跑一次技能补齐，回报「学没学到、学到几个」。
    /// 会给假人加经验（等于作弊），所以只对 <c>CanOwner</c>（主密码）开放。
    /// </summary>
    public Task<BotCommandResult> SkillCheckAsync(int seq) =>
        InvokeAsync(() => BotRoster.SkillCheck(seq),
            r => r.Ok ? "技能自检：" + r.Detail : "技能自检失败：" + r.Detail);

    public Task<BotCommandResult> TeleportAsync(int seq, int mapIndex, int x, int y) =>
        InvokeAsync(() => BotRoster.Teleport(seq, mapIndex, x, y),
            ok => ok ? $"BOT#{seq:00} 已传送到地图 #{mapIndex}（{x},{y}）。" : $"传送失败：BOT#{seq:00} 不在线，或地图 #{mapIndex} 不可用。");

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
    public Task<IReadOnlyList<BotProfileRow>> GetProfilesAsync(CancellationToken cancellationToken = default)
    {
        if (!ServerRunning) return Task.FromResult<IReadOnlyList<BotProfileRow>>(Array.Empty<BotProfileRow>());

        try
        {
            return SEnvir.InvokeOnGameThreadAsync<IReadOnlyList<BotProfileRow>>(ReadProfiles, cancellationToken);
        }
        catch
        {
            // 服务没起来 / 数据未加载：返回空列表，页面显示「先启动服务器」。
            return Task.FromResult<IReadOnlyList<BotProfileRow>>(Array.Empty<BotProfileRow>());
        }
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
}
