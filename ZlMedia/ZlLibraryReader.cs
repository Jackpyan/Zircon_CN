using System.Runtime.InteropServices;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using SkiaSharp;

namespace ZlMedia;

/// <summary>
/// `.Zl` 贴图库读取器（纯托管、跨平台）。
/// <para>
/// 支持两种容器：以 <c>ZL2</c> 开头的压缩容器（BC7 / atlas / deflate 分段），
/// 以及老的经典容器（头部长度 + 元数据表 + 原始 DXT 段）。
/// </para>
/// <para>
/// 解码链：容器 → 压缩段解压 → DXT/BC 解码 → RGBA → PNG（SkiaSharp）。
/// 原来这段是 <c>Server.Web.Services.MapImageService</c> 里的私有嵌套类，
/// 2026-11-13 抽出来单独成项目，图集工具和后台共用一份，避免两套解析各自跑偏。
/// </para>
/// </summary>
public sealed class ZlLibraryReader
{
    /// <summary>
    /// 解码或 PNG 编码方式一旦改变就要加一。它同时进浏览器 URL 和 ETag，
    /// 保证换了算法之后不会复用旧的错误像素。
    /// </summary>
    public const string Version = "4";

    private static readonly byte[] Zl2Signature = "ZL2"u8.ToArray();
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly string _path;
    private readonly ImageMetadata?[] _images;
    private readonly IReadOnlyDictionary<int, PayloadEntry>? _payloads;

    private ZlLibraryReader(string path, ImageMetadata?[] images, IReadOnlyDictionary<int, PayloadEntry>? payloads)
    {
        _path = path;
        _images = images;
        _payloads = payloads;
    }

    public string Path => _path;

    /// <summary>元数据表长度（包含空槽位）。真实可用的图用 <see cref="IsEmpty"/> 判断。</summary>
    public int ImageCount => _images.Length;

    public static ZlLibraryReader Load(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using BinaryReader reader = new(stream);
        byte[] signature = reader.ReadBytes(Zl2Signature.Length);
        stream.Position = 0;
        return signature.SequenceEqual(Zl2Signature) ? ReadCompressed(path, reader) : ReadClassic(path, reader);
    }

    /// <summary>空槽位（库里有位置但没数据）或越界，都算「没有这张图」。</summary>
    public bool IsEmpty(int imageIndex) =>
        imageIndex < 0 || imageIndex >= _images.Length || _images[imageIndex] is not { } image ||
        image.Width <= 0 || image.Height <= 0;

    /// <summary>只读尺寸，不解码。图集装箱需要先知道尺寸再决定放哪。</summary>
    public bool TryGetSize(int imageIndex, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (IsEmpty(imageIndex)) return false;

        ImageMetadata image = _images[imageIndex]!;
        width = image.Width;
        height = image.Height;
        return true;
    }

    /// <summary>
    /// 解码成 RGBA8888（非预乘）。图集打包走这条：一次解码拿到像素，
    /// 直接贴进大图，最后整页编码一次 PNG —— 比「每块编成 PNG 再解回来」省一半 CPU。
    /// </summary>
    public bool TryReadPixels(int imageIndex, out byte[] rgba, out int width, out int height)
    {
        rgba = [];
        width = 0;
        height = 0;

        if (!TryReadPrimary(imageIndex, out byte[] payload, out ImageMetadata image)) return false;

        width = image.Width;
        height = image.Height;
        rgba = DecodeToRgba(payload, image.Codec, width, height);
        return rgba.Length >= checked(width * height * 4);
    }

    /// <summary>解码成 PNG 字节。后台逐块接口走这条。</summary>
    public byte[]? ReadPng(int imageIndex)
    {
        if (!TryReadPrimary(imageIndex, out byte[] payload, out ImageMetadata image)) return null;

        // 库里本来就存 PNG 的直接透传，别重编码（重编码只会变大）。
        if (image.Codec == ImageCodec.Png && payload.AsSpan().StartsWith(PngSignature)) return payload;

        byte[] rgba = DecodeToRgba(payload, image.Codec, image.Width, image.Height);
        return EncodePng(rgba, image.Width, image.Height);
    }

    public static byte[] EncodePng(byte[] rgba, int width, int height)
    {
        int expected = checked(width * height * 4);
        if (rgba.Length < expected) throw new InvalidDataException("贴图像素数据被截断。");

        SKImageInfo info = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKBitmap bitmap = new(info);
        Marshal.Copy(rgba, 0, bitmap.GetPixels(), expected);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    /// <summary>
    /// 把 PNG 解成 RGBA（非预乘）。图集校验要拿页里的像素和贴图库原始像素对拍，
    /// 两边的编码/解码都在这一层，避免工具再引一份 SkiaSharp 配置。
    /// </summary>
    public static byte[]? DecodePng(byte[] png, out int width, out int height)
    {
        width = 0;
        height = 0;

        using SKBitmap? bitmap = SKBitmap.Decode(png);

        if (bitmap is null) return null;

        width = bitmap.Width;
        height = bitmap.Height;

        SKImageInfo info = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKBitmap target = new(info);

        // 同尺寸 1:1 拷贝，显式指定最近邻：与原来「不传 SKPaint」的默认行为等价，
        // 但换掉了已过时的重载（CS0618）。verify 的像素对拍依赖这条路径逐字节稳定。
        using (SKCanvas canvas = new(target))
            canvas.DrawBitmap(bitmap, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));

        byte[] rgba = new byte[checked(width * height * 4)];

        Marshal.Copy(target.GetPixels(), rgba, 0, rgba.Length);

        return rgba;
    }

    /// <summary>读出这一格的原始负载（已按元数据里记录的长度截断）。</summary>
    private bool TryReadPrimary(int imageIndex, out byte[] primary, out ImageMetadata image)
    {
        primary = [];
        image = null!;

        if (IsEmpty(imageIndex)) return false;
        image = _images[imageIndex]!;

        byte[] payload;
        using (FileStream stream = File.OpenRead(_path))
        using (BinaryReader reader = new(stream))
        {
            if (_payloads is not null)
            {
                if (!_payloads.TryGetValue(image.Position, out PayloadEntry? entry)) return false;
                stream.Position = entry.Offset;
                byte[] compressed = reader.ReadBytes(entry.CompressedSize);
                payload = Decompress(compressed, entry.UncompressedSize, entry.Compression);
            }
            else
            {
                if (image.Position <= 0) return false;
                stream.Position = image.Position;
                payload = reader.ReadBytes(image.PrimarySize);
            }
        }

        int size = Math.Min(image.PrimarySize, payload.Length);
        if (size <= 0) return false;
        primary = size == payload.Length ? payload : payload[..size];
        return true;
    }

    private static byte[] DecodeToRgba(byte[] payload, ImageCodec codec, int width, int height)
    {
        if (codec == ImageCodec.Png)
        {
            using SKBitmap? decoded = SKBitmap.Decode(payload);
            if (decoded is null) throw new InvalidDataException("贴图 PNG 解码失败。");
            return ToRgba(decoded, width, height);
        }

        if (codec == ImageCodec.Bgra32)
        {
            // 库里存的是 BGRA，转成 RGBA（原来靠 SKColorType.Bgra8888 免转换，
            // 现在对外统一吐 RGBA，这里换序）。
            int count = Math.Min(payload.Length, checked(width * height * 4));
            byte[] rgba = new byte[checked(width * height * 4)];
            Array.Copy(payload, rgba, count);
            for (int offset = 0; offset + 3 < rgba.Length; offset += 4)
                (rgba[offset], rgba[offset + 2]) = (rgba[offset + 2], rgba[offset]);

            return rgba;
        }

        CompressionFormat format = codec switch
        {
            ImageCodec.Dxt1 => CompressionFormat.Bc1WithAlpha,
            ImageCodec.Dxt5 => CompressionFormat.Bc3,
            ImageCodec.Bc7 => CompressionFormat.Bc7,
            _ => throw new InvalidDataException($"不支持的贴图编码：{codec}")
        };

        ColorRgba32[] decodedPixels = new BcDecoder().DecodeRaw(payload, width, height, format);
        byte[] pixels = new byte[checked(width * height * 4)];
        for (int index = 0; index < decodedPixels.Length && index * 4 + 3 < pixels.Length; index++)
        {
            int offset = index * 4;
            pixels[offset] = decodedPixels[index].r;
            pixels[offset + 1] = decodedPixels[index].g;
            pixels[offset + 2] = decodedPixels[index].b;
            pixels[offset + 3] = decodedPixels[index].a;
        }

        return pixels;
    }

    private static byte[] ToRgba(SKBitmap bitmap, int width, int height)
    {
        byte[] pixels = new byte[checked(width * height * 4)];
        SKImageInfo target = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);

        using SKBitmap converted = new(target);
        if (!bitmap.CopyTo(converted, SKColorType.Rgba8888)) throw new InvalidDataException("贴图 PNG 转换 RGBA 失败。");
        Marshal.Copy(converted.GetPixels(), pixels, 0, pixels.Length);
        return pixels;
    }

    private static ZlLibraryReader ReadClassic(string path, BinaryReader reader)
    {
        int headerSize = reader.ReadInt32();
        if (headerSize <= 0 || headerSize > reader.BaseStream.Length - sizeof(int))
            throw new InvalidDataException($"贴图库头无效：{System.IO.Path.GetFileName(path)}");

        using MemoryStream metadataStream = new(reader.ReadBytes(headerSize), writable: false);
        using BinaryReader metadataReader = new(metadataStream);
        int value = metadataReader.ReadInt32();
        int count = value & 0x1FFFFFF;
        int version = (value >> 25) & 0x7F;
        if (version == 0) count = value;
        ImageMetadata?[] images = ReadImages(metadataReader, count, version);
        return new ZlLibraryReader(path, images, null);
    }

    private static ZlLibraryReader ReadCompressed(string path, BinaryReader reader)
    {
        reader.ReadBytes(Zl2Signature.Length);
        reader.ReadInt32(); // container version
        reader.ReadInt32(); // image count
        reader.ReadInt32(); // atlas count
        reader.ReadByte();  // default compression
        reader.ReadByte();  // flags
        reader.ReadInt16(); // reserved
        long metadataOffset = reader.ReadInt64();
        int metadataSize = reader.ReadInt32();
        long indexOffset = reader.ReadInt64();
        int indexSize = reader.ReadInt32();

        reader.BaseStream.Position = indexOffset;
        using MemoryStream indexStream = new(reader.ReadBytes(indexSize), writable: false);
        using BinaryReader indexReader = new(indexStream);
        int entryCount = indexReader.ReadInt32();
        Dictionary<int, PayloadEntry> entries = new(entryCount);
        for (int index = 0; index < entryCount; index++)
        {
            indexReader.ReadByte(); // entry type
            int id = indexReader.ReadInt32();
            int uncompressedSize = indexReader.ReadInt32();
            int compressedSize = indexReader.ReadInt32();
            long offset = indexReader.ReadInt64();
            Compression compression = (Compression)indexReader.ReadByte();
            indexReader.ReadByte(); // codec
            entries[id] = new PayloadEntry(uncompressedSize, compressedSize, offset, compression);
        }

        reader.BaseStream.Position = metadataOffset;
        using MemoryStream metadataStream = new(reader.ReadBytes(metadataSize), writable: false);
        using BinaryReader metadataReader = new(metadataStream);
        int version = metadataReader.ReadInt32();
        int count = metadataReader.ReadInt32();
        metadataReader.ReadInt32(); // atlas group image count
        metadataReader.ReadInt32(); // atlas page size
        ImageMetadata?[] images = ReadImages(metadataReader, count, version);
        return new ZlLibraryReader(path, images, entries);
    }

    private static ImageMetadata?[] ReadImages(BinaryReader reader, int count, int version)
    {
        if (count < 0 || count > 10_000_000) throw new InvalidDataException($"贴图数量无效：{count}");

        ImageMetadata?[] images = new ImageMetadata?[count];
        for (int index = 0; index < count; index++)
        {
            if (!reader.ReadBoolean()) continue;
            int position = reader.ReadInt32();
            short width = reader.ReadInt16();
            short height = reader.ReadInt16();
            reader.BaseStream.Position += 17; // offsets, shadow type and shadow/overlay dimensions

            ImageCodec codec = version == 0 ? ImageCodec.Dxt1 : ImageCodec.Dxt5;
            int storedSize = 0;
            if (version >= 2)
            {
                reader.ReadInt32(); // atlas page
                reader.BaseStream.Position += 16; // source and visible rectangles
                codec = (ImageCodec)reader.ReadByte();
                reader.BaseStream.Position += 5; // shadow/overlay codecs and runtime preferences
                storedSize = reader.ReadInt32();
                reader.BaseStream.Position += 8; // image BC7 and fallback sizes
                reader.BaseStream.Position += 24; // shadow and overlay segment sizes
            }

            int primarySize = storedSize > 0 ? storedSize : GetDataSize(width, height, codec);
            images[index] = new ImageMetadata(position, width, height, codec, primarySize);
        }

        return images;
    }

    private static byte[] Decompress(byte[] payload, int uncompressedSize, Compression compression)
    {
        if (compression == Compression.None) return payload;
        if (compression is not (Compression.DeflateFast or Compression.DeflateBest))
            throw new InvalidDataException($"不支持的 ZL 压缩方式：{(byte)compression}");

        using MemoryStream input = new(payload, writable: false);
        using System.IO.Compression.DeflateStream deflate = new(input, System.IO.Compression.CompressionMode.Decompress);
        using MemoryStream output = new(Math.Max(0, uncompressedSize));
        deflate.CopyTo(output);
        return output.ToArray();
    }

    private static int GetDataSize(short width, short height, ImageCodec codec)
    {
        int blocks = width <= 0 || height <= 0 ? 0 : Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4);
        return codec switch
        {
            ImageCodec.Dxt1 => blocks * 8,
            ImageCodec.Dxt5 or ImageCodec.Bc7 => blocks * 16,
            ImageCodec.Bgra32 => Math.Max(0, (int)width) * Math.Max(0, (int)height) * 4,
            _ => 0
        };
    }

    private sealed record ImageMetadata(int Position, short Width, short Height, ImageCodec Codec, int PrimarySize);
    private sealed record PayloadEntry(int UncompressedSize, int CompressedSize, long Offset, Compression Compression);

    /// <summary>库内部编码。注意不是 <c>LibraryCore.ImageCodec</c>，两者无关。</summary>
    private enum ImageCodec : byte { Dxt1, Dxt5, Bgra32, Bc7, Png }

    private enum Compression : byte { None, DeflateFast, DeflateBest }
}
