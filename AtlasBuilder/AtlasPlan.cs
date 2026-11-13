using ZlMedia;

namespace AtlasBuilder;

/// <summary>一块要进图集的贴图：来自哪个库的第几张，多大。</summary>
internal readonly record struct AtlasBlock(int Library, int Image, int W, int H);

/// <summary>一页图集：装了哪些块、每块落在哪、编码后多大。</summary>
internal sealed class AtlasPage
{
    public List<(AtlasBlock Block, int X, int Y)> Blocks { get; } = new();

    /// <summary>编码成 PNG 后的字节数（合成并编码后才有效）。</summary>
    public long Bytes { get; set; }

    /// <summary>页里被贴图真实占用的像素面积（不含 padding 与空白）。</summary>
    public long UsedArea => Blocks.Sum(x => (long)x.Block.W * x.Block.H);
}

/// <summary>装箱策略。</summary>
internal enum PackStrategy
{
    /// <summary>全部块按「高降序 + 宽降序」混装，行高取行首块（NFDH）。</summary>
    Shelf,

    /// <summary>按高度精确分桶，桶内行高恒定 = 桶高（零高度浪费，但桶多时页尾浪费大）。</summary>
    Bucket,
}

/// <summary>
/// 把「一张图用到的全部贴图」排进若干页 2048²。
///
/// **关键：必须跨贴图库合装**。一张图通常用到 7~15 个库，
/// 如果按库分别装箱，每个库都会各自浪费页尾（地图 `15` 实测：按库 36 页、跨库 25~28 页，
/// 而按总面积算理论下限只有 25 页）。库只是「块的来源」，与装箱毫无关系。
/// </summary>
internal static class AtlasPlanner
{
    public static List<AtlasPage> Plan(IReadOnlyList<AtlasBlock> blocks, int pageSize, int padding,
        PackStrategy strategy)
    {
        // 用「块在数组里的下标」当装箱用的 id，装完再映射回 AtlasBlock。
        List<(int W, int H, int Index)> items = new(blocks.Count);

        for (int index = 0; index < blocks.Count; index++)
            items.Add((blocks[index].W, blocks[index].H, index));

        List<List<Placement>> packed = ShelfPacker.Pack(items, pageSize, padding, strategy);

        List<AtlasPage> pages = new(packed.Count);

        foreach (List<Placement> placements in packed)
        {
            AtlasPage page = new();

            foreach (Placement placement in placements)
                page.Blocks.Add((blocks[placement.Index], placement.X, placement.Y));

            pages.Add(page);
        }

        return pages;
    }

    /// <summary>
    /// 合成一页并编码成 PNG。返回 RGBA 画布的同时给出字节，调用方自己决定写不写盘。
    /// 透明区域保持全 0（不预乘），PNG 对全透明区域压缩率极高，所以空白几乎不占体积。
    /// </summary>
    public static byte[] Composite(AtlasPage page, IReadOnlyDictionary<int, ZlLibraryReader> readers,
        int pageSize, out long usedBlocks)
    {
        byte[] canvas = new byte[pageSize * pageSize * 4];
        usedBlocks = 0;

        foreach ((AtlasBlock block, int x, int y) in page.Blocks)
        {
            if (!readers.TryGetValue(block.Library, out ZlLibraryReader? reader)) continue;
            if (!reader.TryReadPixels(block.Image, out byte[] rgba, out int w, out int h)) continue;
            if (w <= 0 || h <= 0) continue;
            if (x + w > pageSize || y + h > pageSize) continue;

            int rowBytes = w * 4;

            for (int row = 0; row < h; row++)
            {
                int source = row * rowBytes;
                int target = ((y + row) * pageSize + x) * 4;
                Array.Copy(rgba, source, canvas, target, rowBytes);
            }

            usedBlocks++;
        }

        return ZlLibraryReader.EncodePng(canvas, pageSize, pageSize);
    }
}
