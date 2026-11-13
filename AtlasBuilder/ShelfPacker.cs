namespace AtlasBuilder;

/// <summary>一张图集页里的一块贴图落在哪。</summary>
internal readonly record struct Placement(int Index, int X, int Y, int W, int H);

/// <summary>
/// 货架装箱（shelf packing）的两种策略。都不追求最优解 —— 图集离线生成、只做一次，
/// 简单算法够用，也比「按面积排序 + 最优适配」更容易复现和排查。
/// <para>
/// 2026-11-13 实测（地图 `15`，21,981 块、31 种尺寸、跨库合装）：
/// **混装（<see cref="PackStrategy.Shelf"/>）明显优于精确分桶** —— 尺寸种类多时
/// 分桶会让 31 个小桶各占若干行、每桶最后一行都填不满，页数反而多 50%。
/// 所以默认用混装，分桶留着做对照（`--pack bucket`）。
/// </para>
/// </summary>
internal static class ShelfPacker
{
    public static List<List<Placement>> Pack(IEnumerable<(int W, int H, int Index)> items, int pageSize, int padding,
        PackStrategy strategy = PackStrategy.Shelf)
    {
        IEnumerable<(int W, int H, int Index)> usable = items
            .Where(item => item.W > 0 && item.H > 0)
            .Where(item => item.W + padding <= pageSize && item.H + padding <= pageSize); // 单块比页还大，直接放弃

        return strategy == PackStrategy.Bucket
            ? PackBuckets(usable, pageSize, padding)
            : PackShelves(usable, pageSize, padding);
    }

    /// <summary>
    /// 混装：全部块按「高降序 + 宽降序」，行高取**行首块**的高度。
    /// 因为按高降序，行首块一定是这一行里最高的 —— 后面更矮的块顶到行顶放，底部留空。
    /// </summary>
    private static List<List<Placement>> PackShelves(IEnumerable<(int W, int H, int Index)> items, int pageSize,
        int padding)
    {
        List<List<Placement>> pages = new();
        List<Placement> current = new();

        int x = 0;
        int y = 0;
        int rowHeight = 0;

        foreach ((int w, int h, int index) in items.OrderByDescending(item => item.H).ThenByDescending(item => item.W))
        {
            if (x + w + padding > pageSize)
            {
                y += rowHeight + padding;
                x = 0;
            }

            // 行高取「这一行第一个块」的高度：换行时它才更新。
            if (x == 0) rowHeight = h;

            if (y + rowHeight + padding > pageSize)
            {
                if (current.Count > 0) pages.Add(current);

                current = new List<Placement>();
                x = 0;
                y = 0;
                rowHeight = h;
            }

            current.Add(new Placement(index, x, y, w, h));
            x += w + padding;
        }

        if (current.Count > 0) pages.Add(current);

        return pages;
    }

    /// <summary>精确分桶：按高度分组，行高恒等于桶高（桶内零高度浪费），代价是桶多时页尾浪费。</summary>
    private static List<List<Placement>> PackBuckets(IEnumerable<(int W, int H, int Index)> items, int pageSize,
        int padding)
    {
        List<List<Placement>> pages = new();
        List<Placement> current = new();

        int x = 0;
        int y = 0;

        foreach (IGrouping<int, (int W, int H, int Index)> bucket in items
                     .GroupBy(item => item.H)
                     .OrderByDescending(group => group.Key))
        {
            int rowHeight = bucket.Key;

            foreach ((int w, int h, int index) in bucket.OrderByDescending(item => item.W))
            {
                if (x + w + padding > pageSize)
                {
                    y += rowHeight + padding;
                    x = 0;
                }

                if (y + rowHeight + padding > pageSize)
                {
                    if (current.Count > 0) pages.Add(current);

                    current = new List<Placement>();
                    x = 0;
                    y = 0;
                }

                current.Add(new Placement(index, x, y, w, h));
                x += w + padding;
            }

            // 桶结束必须换行：下一个桶行高不同，接着往右放会压在本桶这一行上。
            y += rowHeight + padding;
            x = 0;
        }

        if (current.Count > 0) pages.Add(current);

        return pages;
    }
}
