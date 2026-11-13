namespace AtlasBuilder;

/// <summary>
/// `.map` 文件扫描器：只抽「这张图引用了哪些贴图」，不建地图对象、不碰 SEnvir。
///
/// 格式与 <c>Server.Web/Services/MapDataService.cs</c> 保持一致（同一份二进制布局，
/// 改那边的时候记得同步这里）：
///   0..22   头部
///   22      宽（int16）
///   24      高（int16）
///   28      背景层：(width/2) × (height/2) 个 [file:byte, image:ushort]，只覆盖偶数格
///   之后    每格 14 字节：flag / 中层动画 / 前层动画 / 前层file / 中层file /
///           中层image(ushort) / 前层image(ushort) / 3 字节保留 / 光照 / 1 字节保留
///
/// 注意：文件里存的 image 就是**最终下标**。服务端为兼容客户端会 +1 再传，
/// 客户端再 -1 用，这里直接取原值即可。
/// </summary>
internal static class MapScanner
{
    private const int CellBytes = 14;

    public static void Scan(string path, Action<int, int> onSize, Action<int, int> onReference)
    {
        byte[] data = File.ReadAllBytes(path);
        if (data.Length < 28) return;

        int width = BitConverter.ToInt16(data, 22);
        int height = BitConverter.ToInt16(data, 24);
        if (width <= 0 || height <= 0) return;

        long expected = 28L + (width / 2L) * (height / 2L) * 3L + (long)width * height * CellBytes;
        if (data.LongLength < expected) return;

        onSize(width, height);

        int offset = 28;

        // 背景层：只覆盖偶数坐标格（原客户端就是这么存的，奇数格留空）
        for (int x = 0; x < width / 2; x++)
        {
            for (int y = 0; y < height / 2; y++)
            {
                byte file = data[offset];
                int image = BitConverter.ToUInt16(data, offset + 1);
                offset += 3;

                if (file != 0) onReference(file, image);
            }
        }

        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                byte frontFile = data[offset + 3];
                byte middleFile = data[offset + 4];
                int middleImage = BitConverter.ToUInt16(data, offset + 5);
                int frontImage = BitConverter.ToUInt16(data, offset + 7);
                // 动画帧数藏在 cell 头两字节的低 4 位（0x80 是混色位）。
                // **必须把动画帧一起收进图集**：地图文件里只存首帧下标，
                // 后续帧是 首帧+1..首帧+count-1，客户端播放时按相位去取 ——
                // 漏掉它们，动画格会全部退回「逐块请求」，图集等于白做一半。
                int middleFrames = data[offset + 1] & 0x0f;
                int frontFrames = data[offset + 2] & 0x0f;
                offset += CellBytes;

                if (middleFile != 0) Emit(middleFile, middleImage, middleFrames, onReference);
                if (frontFile != 0) Emit(frontFile, frontImage, frontFrames, onReference);
            }
        }
    }

    /// <summary>
    /// 发出一个格子的全部贴图引用。count ≤ 1 或动画位为 255（表示「不播放」）时只发首帧，
    /// 与客户端 <c>map-editor.js</c> 里 <c>count &gt; 1 &amp;&amp; animationFrame &lt; 255</c> 的判据逐字一致。
    /// </summary>
    private static void Emit(int file, int image, int count, Action<int, int> onReference)
    {
        if (count > 1 && count != 255)
        {
            for (int index = 0; index < count; index++) onReference(file, image + index);
            return;
        }

        onReference(file, image);
    }
}
