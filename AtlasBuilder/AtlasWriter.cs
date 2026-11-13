using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ZlMedia;

namespace AtlasBuilder;

/// <summary>单张图的图集产出信息（写进 manifest，供 update 判断要不要重建）。</summary>
internal sealed class MapAtlasInfo
{
    /// <summary>地图文件的「长度-mtime」标记。变了就说明地图改过，图集要重建。</summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>页数。</summary>
    public int Pages { get; set; }

    /// <summary>所有页 PNG 的字节数合计。</summary>
    public long Bytes { get; set; }

    /// <summary>块数（去重后的唯一贴图数）。</summary>
    public int Blocks { get; set; }

    /// <summary>索引 + 页文件的版本号：进 URL 的 <c>?v=</c>，内容一变就变。</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>构建耗时（毫秒），只是给人看。</summary>
    public long BuildMs { get; set; }
}

/// <summary>全局清单。</summary>
internal sealed class AtlasManifest
{
    public string Generated { get; set; } = string.Empty;
    public int PageSize { get; set; }
    public int Padding { get; set; }
    public string Pack { get; set; } = string.Empty;
    public string Decoder { get; set; } = string.Empty;
    public string ClientRoot { get; set; } = string.Empty;
    public Dictionary<string, MapAtlasInfo> Maps { get; set; } = new();
}

/// <summary>索引文件的结构（前端按 <c>frames[库][图]</c> 直接查）。</summary>
internal sealed class MapAtlasIndex
{
    public string Map { get; set; } = string.Empty;
    public string Tag { get; set; } = string.Empty;
    public int PageSize { get; set; }
    public string Pack { get; set; } = string.Empty;
    public string Decoder { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;

    /// <summary>页文件名，下标就是帧矩形里的 page 字段。</summary>
    public List<string> Pages { get; set; } = new();

    /// <summary>库索引 → 图下标 → [页, x, y, w, h]。</summary>
    public Dictionary<int, Dictionary<int, int[]>> Frames { get; set; } = new();
}

/// <summary>
/// 图集落盘：一张图一套 <c>index.json</c> + 若干页 PNG。
/// <para>
/// 为什么是「按图」而不是「全局共享」：实测（见 `图集工具与100级开放方案.md` §1.9）
/// 一张图只用全库 12% 的块，全局共享会让每张图下载 33~60 页、其中九成是别的图的块，
/// 单图字节涨 1.8~4 倍。按图出图集则请求数降 99.8%、字节基本持平。
/// </para>
/// </summary>
internal static class AtlasWriter
{
    public const string IndexFileName = "index.json";
    public const string ManifestFileName = "manifest.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>地图文件的廉价版本标记（长度 + mtime），不解析内容。</summary>
    public static string FileTag(string path)
    {
        FileInfo info = new(path);

        return $"{info.Length}-{info.LastWriteTimeUtc.Ticks}";
    }

    /// <summary>生成一张图的图集。返回产出信息；失败抛异常，由调用方汇总。</summary>
    public static MapAtlasInfo BuildMap(string mapPath, string mapName, string outDir, int pageSize, int padding,
        PackStrategy strategy, ReaderCache readers)
    {
        long started = Environment.TickCount64;

        // ① 扫出这张图引用了哪些块
        Dictionary<int, HashSet<int>> refs = new();

        MapScanner.Scan(mapPath, (_, _) => { }, (library, image) =>
        {
            if (library <= 0 || image < 0) return;

            if (!refs.TryGetValue(library, out HashSet<int>? set)) refs[library] = set = new HashSet<int>();

            set.Add(image);
        });

        // ② 摊平成块列表（带真实尺寸），跨库合装
        List<AtlasBlock> blocks = new();

        foreach (KeyValuePair<int, HashSet<int>> pair in refs)
        {
            ZlLibraryReader? reader = readers.Get(pair.Key);

            if (reader is null) continue;

            foreach (int image in pair.Value)
                if (reader.TryGetSize(image, out int w, out int h))
                    blocks.Add(new AtlasBlock(pair.Key, image, w, h));
        }

        if (blocks.Count == 0)
            throw new InvalidOperationException("这张图没有可用的贴图（贴图库缺失？）");

        List<AtlasPage> pages = AtlasPlanner.Plan(blocks, pageSize, padding, strategy);

        // ③ 合成 + 编码 + 写盘
        string mapDir = Path.Combine(outDir, mapName);

        // 先写到一个临时目录再整体替换：中途失败不会留下「页数和索引对不上」的半成品。
        string tempDir = mapDir + ".tmp";

        if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);

        Directory.CreateDirectory(tempDir);

        Dictionary<int, ZlLibraryReader> pageReaders = new();

        foreach (int library in refs.Keys)
        {
            ZlLibraryReader? reader = readers.Get(library);

            if (reader is not null) pageReaders[library] = reader;
        }

        long bytes = 0;
        List<string> pageNames = new(pages.Count);
        StringBuilder versionSeed = new();

        for (int index = 0; index < pages.Count; index++)
        {
            byte[] png = AtlasPlanner.Composite(pages[index], pageReaders, pageSize, out _);

            string fileName = index + ".png";

            File.WriteAllBytes(Path.Combine(tempDir, fileName), png);

            pageNames.Add(fileName);
            bytes += png.Length;

            // 版本号吃进每页字节数：内容一变，版本必然变（不用对 200MB 算 SHA256）。
            versionSeed.Append(png.Length).Append('-');
        }

        string tag = FileTag(mapPath);
        string version = ShortHash($"{mapName}|{tag}|{pageSize}|{padding}|{strategy}|{ZlLibraryReader.Version}|{versionSeed}");

        MapAtlasIndex atlasIndex = new()
        {
            Map = mapName,
            Tag = tag,
            PageSize = pageSize,
            Pack = strategy.ToString().ToLowerInvariant(),
            Decoder = ZlLibraryReader.Version,
            Version = version,
            Pages = pageNames,
        };

        // 帧表：库 → 图 → [页, x, y, w, h]
        for (int page = 0; page < pages.Count; page++)
        {
            foreach ((AtlasBlock block, int x, int y) in pages[page].Blocks)
            {
                if (!atlasIndex.Frames.TryGetValue(block.Library, out Dictionary<int, int[]>? frames))
                    atlasIndex.Frames[block.Library] = frames = new Dictionary<int, int[]>();

                frames[block.Image] = [page, x, y, block.W, block.H];
            }
        }

        File.WriteAllText(Path.Combine(tempDir, IndexFileName),
            JsonSerializer.Serialize(atlasIndex, JsonOptions), new UTF8Encoding(false));

        // ④ 整体替换
        if (Directory.Exists(mapDir)) Directory.Delete(mapDir, true);

        Directory.Move(tempDir, mapDir);

        return new MapAtlasInfo
        {
            Tag = tag,
            Pages = pages.Count,
            Bytes = bytes,
            Blocks = blocks.Count,
            Version = version,
            BuildMs = Environment.TickCount64 - started,
        };
    }

    public static AtlasManifest? ReadManifest(string outDir)
    {
        string path = Path.Combine(outDir, ManifestFileName);

        if (!File.Exists(path)) return null;

        try { return JsonSerializer.Deserialize<AtlasManifest>(File.ReadAllText(path)); }
        catch { return null; } // 清单坏了就当没有，全部重建（比读一半更安全）
    }

    public static void WriteManifest(string outDir, AtlasManifest manifest)
    {
        Directory.CreateDirectory(outDir);

        File.WriteAllText(Path.Combine(outDir, ManifestFileName),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            }), new UTF8Encoding(false));
    }

    private static string ShortHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()[..16];
}

/// <summary>
/// 贴图库读取器的进程内缓存。<see cref="ZlLibraryReader"/> 加载后只读，
/// 每次 <c>ReadPng</c> 自己开 FileStream，所以可以多线程共享。
/// </summary>
internal sealed class ReaderCache
{
    private readonly ConcurrentDictionary<int, ZlLibraryReader?> _cache = new();
    private readonly string _clientRoot;

    public ReaderCache(string clientRoot) => _clientRoot = clientRoot;

    public ZlLibraryReader? Get(int library) => _cache.GetOrAdd(library, Load);

    private ZlLibraryReader? Load(int library)
    {
        string? path = Program.ResolveLibraryPath(_clientRoot, library);

        if (path is null || !File.Exists(path)) return null;

        try { return ZlLibraryReader.Load(path); }
        catch { return null; }
    }
}
