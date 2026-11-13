using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Library;
using Microsoft.Extensions.Caching.Memory;
using Server.Envir;
using ZlMedia;

namespace Server.Web.Services;

public sealed class MapImageService : IDisposable
{
    // 解码/编码版本号现在归 ZlLibraryReader 管（它才是真正决定像素的那一层）。
    // 保留这个别名是为了不惊动调用点（Program.cs 的 X-Map-Asset-Decoder 头）。
    public const string DecoderVersion = ZlLibraryReader.Version;

    private readonly ConcurrentDictionary<string, CachedLibrarySlot> _libraries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<DecodedImageKey, Lazy<byte[]?>> _imageLoads = new();
    private readonly MemoryCache _decodedImages = new(new MemoryCacheOptions
    {
        SizeLimit = 256L * 1024 * 1024
    });

    public MapAssetStatus GetStatus()
    {
        LibraryFile[] required = Libraries.KROrder.Values.Distinct().ToArray();
        List<string> assetVersions = new(required.Length);
        int available = 0;
        foreach (LibraryFile file in required)
        {
            if (!TryResolveLibrary(file, out string path)) continue;
            FileInfo info = new(path);
            available++;
            assetVersions.Add($"{(int)file}:{info.Length}:{info.LastWriteTimeUtc.Ticks}");
        }

        assetVersions.Sort(StringComparer.Ordinal);
        string versionSource = $"{DecoderVersion}|{string.Join('|', assetVersions)}";
        string version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(versionSource)))[..16];
        string message = available == 0
            ? "未找到地图贴图库。请将客户端 Data 目录放在 ClientPath 下，或在 Server.ini 中正确设置 ClientPath。"
            : available < required.Length
                ? $"已找到 {available}/{required.Length} 个地图贴图库；缺失素材对应的地图块将保持透明。"
                : "地图贴图库可用。";
        return new MapAssetStatus(available > 0, available, required.Length, version, message);
    }

    public MapImageLookup GetImage(int mapFile, int imageIndex)
    {
        if (imageIndex < 0 || !Libraries.KROrder.TryGetValue(mapFile, out LibraryFile libraryFile) ||
            !TryResolveLibrary(libraryFile, out string path)) return new MapImageLookup(MapImageState.MissingLibrary, null);

        FileInfo fileInfo = new(path);
        long stamp = fileInfo.LastWriteTimeUtc.Ticks;
        CachedLibrary cached = _libraries.GetOrAdd(path, _ => new CachedLibrarySlot()).Get(path, stamp);
        DecodedImageKey cacheKey = new(path, stamp, imageIndex);
        if (!_decodedImages.TryGetValue(cacheKey, out byte[]? png))
        {
            Lazy<byte[]?> load = _imageLoads.GetOrAdd(cacheKey,
                _ => new Lazy<byte[]?>(() => cached.Reader.ReadPng(imageIndex), LazyThreadSafetyMode.ExecutionAndPublication));
            try
            {
                png = load.Value;
                if (png is not null)
                {
                    _decodedImages.Set(cacheKey, png, new MemoryCacheEntryOptions
                    {
                        Size = Math.Max(1, png.LongLength),
                        SlidingExpiration = TimeSpan.FromMinutes(20)
                    });
                }
            }
            finally
            {
                _imageLoads.TryRemove(cacheKey, out _);
            }
        }
        return png is null
            ? new MapImageLookup(MapImageState.Empty, null)
            : new MapImageLookup(MapImageState.Available,
                new MapImageResult(png, $"\"map-{DecoderVersion}-{stamp:x}-{fileInfo.Length:x}-{mapFile:x2}-{imageIndex:x}\""));
    }

    public void Dispose() => _decodedImages.Dispose();

    private static bool TryResolveLibrary(LibraryFile file, out string path)
    {
        path = string.Empty;
        if (!Libraries.LibraryList.TryGetValue(file, out string? relative)) return false;
        string clientRoot = PlatformPath.Resolve(string.IsNullOrWhiteSpace(Config.ClientPath) ? "." : Config.ClientPath);
        path = Path.GetFullPath(Path.Combine(clientRoot, PlatformPath.Normalize(relative)));
        return File.Exists(path);
    }

    private sealed record CachedLibrary(long LastWriteTicks, ZlLibraryReader Reader);

    private sealed record DecodedImageKey(string Path, long LastWriteTicks, int ImageIndex);

    private sealed class CachedLibrarySlot
    {
        private readonly object _sync = new();
        private CachedLibrary? _current;

        public CachedLibrary Get(string path, long stamp)
        {
            CachedLibrary? current = Volatile.Read(ref _current);
            if (current?.LastWriteTicks == stamp) return current;
            lock (_sync)
            {
                current = _current;
                if (current?.LastWriteTicks == stamp) return current;
                current = new CachedLibrary(stamp, ZlLibraryReader.Load(path));
                Volatile.Write(ref _current, current);
                return current;
            }
        }
    }
}

public sealed record MapImageResult(byte[] Content, string ETag);
public sealed record MapImageLookup(MapImageState State, MapImageResult? Image);
public enum MapImageState { Available, Empty, MissingLibrary }
public sealed record MapAssetStatus(bool Available, int AvailableLibraries, int ExpectedLibraries, string Version, string Message);
