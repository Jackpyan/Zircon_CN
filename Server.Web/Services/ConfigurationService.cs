using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using Library;
using Library.Network;
using Library.SystemModels;
using MirDB;
using Server.DBModels;
using Server.Envir;
using Server.Models;
using Server.Web.Models;
using S = Library.Network.ServerPackets;

namespace Server.Web.Services;

public sealed class ConfigurationService(AdminAuditService audit, IGameServerController gameServer, GameDataSessionService catalog)
{
    private readonly object _sync = new();
    private readonly Assembly _assembly = Assembly.GetAssembly(typeof(Config))!;
    private byte[]? _activeEncryptionKey = Config.EncryptionEnabled ? Convert.FromBase64String(Config.EncryptionKey) : null;
    private static readonly HashSet<string> SecretNames = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(Config.MasterPassword), nameof(Config.SyncKey), nameof(Config.MailPassword),
        nameof(Config.EncryptionKey), nameof(Config.ReceiverEMail)
    };

    public IReadOnlyList<ConfigurationSectionModel> Read()
    {
        lock (_sync)
        {
            List<ConfigurationSectionModel> sections = [];
            string? currentSection = null;
            List<ConfigurationField> fields = [];

            foreach (PropertyInfo property in GetProperties())
            {
                ConfigSection? section = property.GetCustomAttribute<ConfigSection>();
                if (section is not null)
                {
                    if (currentSection is not null)
                        sections.Add(new ConfigurationSectionModel(currentSection, UiText.ConfigSection(currentSection), fields.ToArray()));
                    currentSection = section.Section;
                    fields = [];
                }

                if (currentSection is null || property.GetCustomAttribute<ConfigPropertyIgnore>() is not null)
                    continue;

                bool secret = SecretNames.Contains(property.Name);
                object? raw = property.GetValue(null);
                fields.Add(new ConfigurationField(
                    currentSection,
                    property.Name,
                    UiText.ConfigField(property.Name),
                    property.PropertyType,
                    secret ? string.Empty : Format(raw),
                    secret,
                    GetApplyMode(property.Name)));
            }

            if (currentSection is not null)
                sections.Add(new ConfigurationSectionModel(currentSection, UiText.ConfigSection(currentSection), fields.ToArray()));
            return sections;
        }
    }

    public void Save(IReadOnlyDictionary<string, string?> values, string user)
    {
        lock (_sync)
        {
            // 计时：2026-11-13 用户反馈「点保存后网页和客户端一起卡几秒」，
            // 但当时没有任何耗时数据，只能猜。
            //
            // 一开始只在 ≥200ms 时才落日志，结果用户实测「保存正常、但没看到那行」——
            // 因为本地保存本来就只要几十毫秒（慢的是**首次**反射 + 冷启动，而且卡的是电路线程）。
            // 现在**每次都落**：后台保存是低频动作（一天几次），多一行日志换来「有数可看」值得。
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

            Dictionary<PropertyInfo, object?> originals = [];
            try
            {
                foreach (PropertyInfo property in GetProperties())
                {
                    string key = property.Name;
                    if (!values.TryGetValue(key, out string? text)) continue;
                    if (SecretNames.Contains(property.Name) && string.IsNullOrEmpty(text)) continue;

                    originals[property] = property.GetValue(null);
                    property.SetValue(null, ConvertValue(text ?? string.Empty, property.PropertyType));
                }

                AdminEndpointValidator.ValidateAndResolve();
                ConfigReader.Save(_assembly);
                Config.LoadVersion();
                if (SEnvir.Started) SEnvir.ServerBuffChanged = true;

                // 假人调度器把 Bots 配置缓存了一秒（每帧现建会吃掉主循环），
                // 后台保存后必须打掉缓存，否则「改完即时生效」变成「最多慢一秒」。
                Server.Bots.BotRoster.InvalidateOptions();

                // 寄售费率/最低费变化 → 立即推给在线玩家，避免「弹窗显示旧费、实际按新费扣」
                if (SEnvir.Started && originals.Keys.Any(property =>
                        property.Name is nameof(Config.MarketPlaceFeeRate) or nameof(Config.MarketPlaceFeeMinimum)))
                {
                    _ = SEnvir.InvokeOnGameThreadAsync(() =>
                    {
                        foreach (PlayerObject player in SEnvir.Players)
                            player.SendConsignments();

                        return true;
                    });
                }
                audit.Record(user, "Configuration.Save", $"Updated {originals.Count} configuration values");

                watch.Stop();

                SEnvir.Log(string.Format("[配置] 保存 {0} 项耗时 {1} ms{2}", originals.Count, watch.ElapsedMilliseconds,
                    watch.ElapsedMilliseconds >= 200 ? "（写 Server.ini + 算客户端哈希 + 审计落盘，偏慢）" : ""));
            }
            catch
            {
                foreach ((PropertyInfo property, object? original) in originals)
                    property.SetValue(null, original);
                throw;
            }
        }
    }

    public void Reload(string user)
    {
        lock (_sync)
        {
            ConfigReader.Load(_assembly);
            Config.LoadVersion();
            Server.Bots.BotRoster.InvalidateOptions();
            audit.Record(user, "Configuration.Reload", "Reloaded Server.ini");
        }
    }

    public async Task<bool> CheckClientVersionAsync(string user, CancellationToken cancellationToken = default)
    {
        byte[]? previous = Config.ClientHash?.ToArray();
        Config.LoadVersion();
        bool changed = !Functions.IsMatch(previous, Config.ClientHash);
        if (changed && gameServer.State == GameServerState.Running)
            await SEnvir.InvokeOnGameThreadAsync(() =>
            {
                SEnvir.Broadcast(new S.Chat { Text = "已有新版本可用，请尽快更新。", Type = MessageType.Announcement });
                return true;
            }, cancellationToken);
        audit.Record(user, "Configuration.CheckVersion", changed ? "Client hash changed" : "Client hash unchanged");
        return changed;
    }

    public async Task LocalSyncAsync(string user, CancellationToken cancellationToken = default)
    {
        string source = await catalog.SaveAndGetPathAsync(cancellationToken);
        string targetDirectory = Path.Combine(PlatformPath.Resolve(Config.ClientPath), "Data");
        Directory.CreateDirectory(targetDirectory);
        File.Copy(source, Path.Combine(targetDirectory, Path.GetFileName(source)), true);
        audit.Record(user, "Configuration.LocalSync", targetDirectory);
    }

    public async Task RemoteSyncAsync(string user, CancellationToken cancellationToken = default)
    {
        string source = await catalog.SaveAndGetPathAsync(cancellationToken);
        UriBuilder endpoint = new(Config.SyncRemotePreffix);
        string query = $"Type={Uri.EscapeDataString(WebServer.SystemDBSyncCommand)}&Key={Uri.EscapeDataString(Config.SyncKey)}";
        endpoint.Query = string.IsNullOrEmpty(endpoint.Query) ? query : endpoint.Query.TrimStart('?') + "&" + query;
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(30) };
        await using FileStream stream = File.OpenRead(source);
        using StreamContent content = new(stream);
        using HttpResponseMessage response = await client.PostAsync(endpoint.Uri, content, cancellationToken);
        response.EnsureSuccessStatusCode();
        audit.Record(user, "Configuration.RemoteSync", endpoint.Uri.GetLeftPart(UriPartial.Path));
    }

    public async Task ReencryptDatabasesAsync(string user, CancellationToken cancellationToken = default)
    {
        if (gameServer.State != GameServerState.Stopped)
            throw new InvalidOperationException("数据库加密转换只能在游戏服停止后执行。");

        byte[]? nextKey = null;
        if (Config.EncryptionEnabled)
        {
            nextKey = Convert.FromBase64String(Config.EncryptionKey);
            if (nextKey.Length != 32) throw new InvalidOperationException("EncryptionKey 必须是 32 字节密钥的 Base64 文本。");
        }

        byte[]? previousKey = _activeEncryptionKey;
        try
        {
            Encryption.SetKey(previousKey);
            Session session = new(SessionMode.Both) { BackUpDelay = 60 };
            session.Initialize(Assembly.GetAssembly(typeof(ItemInfo))!, Assembly.GetAssembly(typeof(AccountInfo))!);
            Encryption.SetKey(nextKey);
            session.Save(true);
            _activeEncryptionKey = nextKey;
            await catalog.ReloadAsync(cancellationToken);
            audit.Record(user, "Configuration.Reencrypt", Config.EncryptionEnabled ? "Encryption enabled" : "Encryption disabled");
        }
        catch
        {
            Encryption.SetKey(previousKey);
            throw;
        }
    }

    public string GenerateEncryptionKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// <see cref="Config"/> 的公开静态属性，按声明顺序缓存。
    /// <para>
    /// 原来每次 <see cref="Read"/>/<see cref="Save"/> 都现调 <c>assembly.GetTypes()</c> 再筛一遍 ——
    /// 那是整个程序集的元数据遍历，首次调用要几百毫秒（2026-11-13「点保存卡几秒」的一半成本）。
    /// Config 的属性集合在进程生命周期内不会变，缓存是安全的。
    /// </para>
    /// </summary>
    private static readonly PropertyInfo[] ConfigProperties = typeof(Config)
        .GetProperties(BindingFlags.Public | BindingFlags.Static)
        .OrderBy(property => property.MetadataToken)
        .ToArray();

    private static IEnumerable<PropertyInfo> GetProperties() => ConfigProperties;

    private static string Format(object? value) => value switch
    {
        null => string.Empty,
        DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
        TimeSpan timeSpan => timeSpan.ToString("c", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    private static object? ConvertValue(string text, Type type)
    {
        Type actualType = Nullable.GetUnderlyingType(type) ?? type;
        if (actualType == typeof(string)) return text;
        if (actualType == typeof(TimeSpan)) return TimeSpan.Parse(text, CultureInfo.InvariantCulture);
        if (actualType == typeof(DateTime)) return DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        if (actualType.IsEnum) return Enum.Parse(actualType, text, true);
        TypeConverter converter = TypeDescriptor.GetConverter(actualType);
        return converter.ConvertFromInvariantString(text);
    }

    private static string GetApplyMode(string name)
    {
        if (name is nameof(Config.AdminWebHost) or nameof(Config.AdminWebPort)) return "重启进程";
        if (name is nameof(Config.Port) or nameof(Config.UserCountPort) or nameof(Config.IPAddress) or
            nameof(Config.MapPath) or nameof(Config.ClientPath) or nameof(Config.EncryptionEnabled) or nameof(Config.EncryptionKey))
            return "重启游戏服";
        return "立即/下一循环";
    }
}
