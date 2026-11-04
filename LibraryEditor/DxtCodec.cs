using System;
using System.Runtime.InteropServices;
using BCnEncoder.Shared;
using BcCompressionQuality = BCnEncoder.Encoder.CompressionQuality;
using BcDecoder = BCnEncoder.Decoder.BcDecoder;
using BcEncoder = BCnEncoder.Encoder.BcEncoder;
using BcPixelFormat = BCnEncoder.Encoder.PixelFormat;
using BcCompressionFormat = BCnEncoder.Shared.CompressionFormat;

namespace Zircon.Imaging
{
    /// <summary>DXT 压缩格式。</summary>
    internal enum DxtFormat
    {
        Dxt1,
        Dxt3,
        Dxt5
    }

    /// <summary>
    /// DXT1/3/5 编解码。
    ///
    /// 为什么不用 ManagedSquish：它依赖 <c>NativeSquish_x64.dll</c>，那是一个面向
    /// .NET Framework 4.0 的 **C++/CLI 混合模式程序集**，.NET 10 无法加载
    /// （报 "Could not load file or assembly 'NativeSquish_x64'" → 静态构造抛异常，
    /// 打开资源库直接失败）。改用项目里已有的纯托管 BCnEncoder（BC1= DXT1，BC2 = DXT3，BC3 = DXT5）。
    ///
    /// 约定：像素一律 **BGRA**（与 GDI+ 的 Format32bppArgb 内存布局一致），
    /// 不再像 Squish 那样需要调用方先做 R/B 交换。
    /// </summary>
    internal static class DxtCodec
    {
        /// <summary>压缩为 DXT 块数据。</summary>
        public static byte[] Compress(byte[] bgraPixels, int width, int height, DxtFormat format)
        {
            if (bgraPixels == null || width <= 0 || height <= 0) return Array.Empty<byte>();

            BcCompressionFormat target = format switch
            {
                DxtFormat.Dxt3 => BcCompressionFormat.Bc2,
                DxtFormat.Dxt5 => BcCompressionFormat.Bc3,
                // DXT1 带 1 位透明：有半透明/全透明像素时走 Bc1WithAlpha，否则用 4 色模式保住画质
                _ => HasTransparency(bgraPixels) ? BcCompressionFormat.Bc1WithAlpha : BcCompressionFormat.Bc1
            };

            BcEncoder encoder = new BcEncoder(target);
            encoder.OutputOptions.GenerateMipMaps = false;
            encoder.OutputOptions.Quality = BcCompressionQuality.Balanced;

            return encoder.EncodeToRawBytes(bgraPixels, width, height, BcPixelFormat.Bgra32)[0];
        }

        /// <summary>把 DXT 块数据解码到已锁定的 32bppArgb 位图缓冲（BGRA 行序）。</summary>
        public static void Decompress(byte[] blocks, int width, int height, IntPtr scan0, int stride, DxtFormat format)
        {
            if (width <= 0 || height <= 0) return;

            BcCompressionFormat source = format switch
            {
                DxtFormat.Dxt3 => BcCompressionFormat.Bc2,
                DxtFormat.Dxt5 => BcCompressionFormat.Bc3,
                // DXT1 一律按 Bc1WithAlpha 解：4 色模式的解码结果与 Bc1 完全相同，
                // 但能还原「黑边挖空」用的 1 位透明（用 Bc1 解会把 alpha 全变 255）
                _ => BcCompressionFormat.Bc1WithAlpha
            };

            blocks ??= Array.Empty<byte>();

            int expected = ExpectedSize(width, height, format);
            if (blocks.Length < expected)
            {
                byte[] padded = new byte[expected];
                Buffer.BlockCopy(blocks, 0, padded, 0, blocks.Length);
                blocks = padded;
            }

            ColorRgba32[] rgba = new BcDecoder().DecodeRaw(blocks, width, height, source);
            int rowBytes = width * 4;

            for (int y = 0; y < height; y++)
            {
                byte[] row = new byte[rowBytes];

                for (int x = 0; x < width; x++)
                {
                    ColorRgba32 pixel = rgba[y * width + x];
                    int offset = x * 4;
                    row[offset] = pixel.b;
                    row[offset + 1] = pixel.g;
                    row[offset + 2] = pixel.r;
                    row[offset + 3] = pixel.a;
                }

                Marshal.Copy(row, 0, IntPtr.Add(scan0, y * stride), rowBytes);
            }
        }

        /// <summary>DXT 块数据的理论长度。</summary>
        public static int ExpectedSize(int width, int height, DxtFormat format)
        {
            int blockBytes = format == DxtFormat.Dxt1 ? 8 : 16;
            int blocksWide = Math.Max(1, (width + 3) / 4);
            int blocksHigh = Math.Max(1, (height + 3) / 4);

            return blocksWide * blocksHigh * blockBytes;
        }

        private static bool HasTransparency(byte[] bgraPixels)
        {
            for (int i = 3; i < bgraPixels.Length; i += 4)
                if (bgraPixels[i] < 255)
                    return true;

            return false;
        }
    }
}
