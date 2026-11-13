using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ZlMedia;

// Options 是 Program 的嵌套类（同一个文件里，命令分发和参数解析放在一起）。
// 这里用别名引用，避免把 Program 的私有成员一股脑暴露给本文件。
using Options = AtlasBuilder.Program.Options;

namespace AtlasBuilder;

/// <summary>
/// <c>build</c> / <c>update</c> / <c>verify</c> 三个子命令（2026-11-13）。
///
/// 产物布局（<c>--out</c> 指向的目录）：
/// <code>
/// manifest.json          全局清单：每张图的 mapTag / 页数 / 字节 / 版本
/// {地图名}/index.json    这张图的帧表：库 → 图 → [页, x, y, w, h]
/// {地图名}/{n}.png       图集页（2048²）
/// </code>
///
/// 之所以「按图」而不是「全局共享」：实测一张图只用全库 12% 的块，
/// 全局共享会让每张图下 33~60 页、九成是别的图的块（单图字节涨 1.8~4 倍）。
/// </summary>
internal static class AtlasCommands
{
    // ===== build / update =====

    public static int Build(Options options) => Run(options, update: false);

    public static int Update(Options options) => Run(options, update: true);

    private static int Run(Options options, bool update)
    {
        Stopwatch watch = Stopwatch.StartNew();

        string outDir = options.OutputDir;
        string mapsDir = options.MapsDir;

        if (!Directory.Exists(mapsDir))
        {
            Console.Error.WriteLine("地图目录不存在：" + mapsDir);
            return 1;
        }

        List<string> mapFiles = ResolveMapFiles(options, mapsDir);

        if (mapFiles.Count == 0)
        {
            Console.Error.WriteLine("没有匹配的地图文件。");
            return 1;
        }

        AtlasManifest manifest;

        if (update)
        {
            manifest = AtlasWriter.ReadManifest(outDir) ?? new AtlasManifest();

            // 参数变了（页大小 / 装箱策略 / 解码版本）→ 老产物全部作废，只能全量重建。
            if (manifest.Maps.Count > 0 &&
                (manifest.PageSize != options.PageSize || manifest.Padding != Program.AtlasPadding ||
                 !string.Equals(manifest.Pack, options.Strategy.ToString().ToLowerInvariant(), StringComparison.Ordinal) ||
                 !string.Equals(manifest.Decoder, ZlLibraryReader.Version, StringComparison.Ordinal)))
            {
                Console.WriteLine("! 页大小 / 装箱策略 / 解码版本 与已有产物不同 → 全部重建");
                manifest = new AtlasManifest();
            }
        }
        else
        {
            manifest = new AtlasManifest();
        }

        Directory.CreateDirectory(outDir);

        Console.WriteLine($"输出目录：{outDir}");
        Console.WriteLine($"地图目录：{mapsDir}（{mapFiles.Count} 张）");
        Console.WriteLine($"页边长  ：{options.PageSize}（padding {Program.AtlasPadding}，装箱 {options.Strategy}）");
        Console.WriteLine();

        ReaderCache readers = new(options.ClientRoot);

        int done = 0;
        int skipped = 0;
        int failed = 0;
        object gate = new();

        // 每张图完全独立（各自的目录、各自的读），可以并行。
        // PNG 编码是 CPU 密集，串行跑 258 张图要一个多小时。
        ParallelOptions parallel = new() { MaxDegreeOfParallelism = options.Jobs };

        Parallel.ForEach(mapFiles, parallel, file =>
        {
            string name = Path.GetFileNameWithoutExtension(file);
            string tag = AtlasWriter.FileTag(file);

            if (update && manifest.Maps.TryGetValue(name, out MapAtlasInfo? existing) &&
                existing.Tag == tag && Directory.Exists(Path.Combine(outDir, name)))
            {
                lock (gate)
                {
                    skipped++;
                }

                return;
            }

            try
            {
                MapAtlasInfo info = AtlasWriter.BuildMap(file, name, outDir, options.PageSize, Program.AtlasPadding,
                    options.Strategy, readers);

                lock (gate)
                {
                    manifest.Maps[name] = info;
                    done++;

                    Console.WriteLine($"  [{done,3}/{mapFiles.Count}] {name,-12} {info.Pages,3} 页 " +
                        $"{FormatBytes(info.Bytes),9} {info.Blocks,6} 块 {info.BuildMs,6} ms");
                }
            }
            catch (Exception ex)
            {
                lock (gate)
                {
                    failed++;
                    Console.WriteLine($"  FAIL {name,-12} {ex.Message}");
                }
            }
        });

        // 清理孤儿：磁盘上有目录、但地图已经不存在了（删图 / 改名）
        //
        // ⚠ 判据必须用**地图目录里的全部 .map**，不能用本次处理的 mapFiles：
        // 用 `--maplist 15` 只更新一张时，mapFiles 里只有 15，
        // 其余上百个目录会被当成「孤儿」整批删掉 —— 静默、且要重跑几十分钟才能补回来。
        int removed = 0;

        if (update)
        {
            HashSet<string> alive = new(
                Directory.GetFiles(mapsDir, "*.map").Select(x => Path.GetFileNameWithoutExtension(x)!),
                StringComparer.OrdinalIgnoreCase);

            foreach (string dir in Directory.GetDirectories(outDir))
            {
                string name = Path.GetFileName(dir);

                if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                {
                    Directory.Delete(dir, true);
                    continue;
                }

                if (alive.Contains(name)) continue;

                Directory.Delete(dir, true);
                manifest.Maps.Remove(name);
                removed++;
            }

            foreach (string name in manifest.Maps.Keys.Where(x => !alive.Contains(x)).ToList())
                manifest.Maps.Remove(name);
        }

        manifest.Generated = Library.Time.LocalNow.ToString("yyyy-MM-dd HH:mm:ss");
        manifest.PageSize = options.PageSize;
        manifest.Padding = Program.AtlasPadding;
        manifest.Pack = options.Strategy.ToString().ToLowerInvariant();
        manifest.Decoder = ZlLibraryReader.Version;
        manifest.ClientRoot = options.ClientRoot;

        AtlasWriter.WriteManifest(outDir, manifest);

        Console.WriteLine();
        Console.WriteLine(new string('-', 78));
        Console.WriteLine($"{(update ? "增量更新" : "全量构建")}完成：新建 {done} 张 / 跳过 {skipped} 张" +
            $"{(failed > 0 ? $" / **失败 {failed} 张**" : "")}{(removed > 0 ? $" / 清理 {removed} 张" : "")}");
        // 合计一律从清单算：`--maplist` 只处理几张时，本次循环里的累加只覆盖这几张，
        // 会打出「2 张图、7 页、29MB」这种自相矛盾的数字。
        Console.WriteLine($"产物合计：{manifest.Maps.Count} 张图、" +
            $"{manifest.Maps.Values.Sum(x => x.Pages)} 页、" +
            $"{FormatBytes(manifest.Maps.Values.Sum(x => x.Bytes))}");
        Console.WriteLine($"耗时：{watch.Elapsed.TotalSeconds:0.0}s");
        Console.WriteLine($"清单：{Path.Combine(outDir, AtlasWriter.ManifestFileName)}");

        return failed == 0 ? 0 : 1;
    }

    // ===== verify =====

    /// <summary>
    /// 校验产物：索引与页文件是否自洽。默认只做「结构 + 尺寸」，<c>--pixels N</c> 再加像素抽样对拍。
    /// <para>
    /// 为什么必须有这一步：图集是「索引 + 一堆 PNG」，两者一旦不同步，
    /// 前端表现是「某些格子贴错图 / 花屏」，而服务端没有任何异常。
    /// </para>
    /// </summary>
    public static int Verify(Options options)
    {
        string outDir = options.OutputDir;

        if (!Directory.Exists(outDir))
        {
            Console.Error.WriteLine("产物目录不存在：" + outDir);
            return 1;
        }

        AtlasManifest? manifest = AtlasWriter.ReadManifest(outDir);

        if (manifest is null)
            Console.WriteLine("! 没有 manifest.json（只按目录内容校验）");

        ReaderCache readers = new(options.ClientRoot);
        List<string> problems = new();
        int checkedMaps = 0;
        int checkedFrames = 0;
        long totalBytes = 0;
        // 抽样对拍的账要算清楚：抽了几块 / 真比了几块 / 哪些库根本读不到。
        // 只报「抽 N 块」会骗人 —— `--client` 指错时一块都比不了，却照样打印「全部一致」。
        int pixelSampled = 0;
        int pixelCompared = 0;
        HashSet<int> unreadableLibraries = new();

        foreach (string mapDir in Directory.GetDirectories(outDir))
        {
            string name = Path.GetFileName(mapDir);

            if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;

            string indexPath = Path.Combine(mapDir, AtlasWriter.IndexFileName);

            if (!File.Exists(indexPath))
            {
                problems.Add($"{name}: 缺 index.json");
                continue;
            }

            MapAtlasIndex? index;

            try { index = JsonSerializer.Deserialize<MapAtlasIndex>(File.ReadAllText(indexPath)); }
            catch (Exception ex)
            {
                problems.Add($"{name}: index.json 解析失败（{ex.Message}）");
                continue;
            }

            if (index is null)
            {
                problems.Add($"{name}: index.json 为空");
                continue;
            }

            checkedMaps++;

            if (index.PageSize != options.PageSize)
                problems.Add($"{name}: 页边长 {index.PageSize} 与当前参数 {options.PageSize} 不一致");

            // ① 页文件
            int actualPages = Directory.GetFiles(mapDir, "*.png").Length;

            if (actualPages != index.Pages.Count)
                problems.Add($"{name}: 索引写 {index.Pages.Count} 页，目录里有 {actualPages} 个 png");

            Dictionary<int, byte[]?> pageImages = new();

            for (int page = 0; page < index.Pages.Count; page++)
            {
                string path = Path.Combine(mapDir, index.Pages[page]);

                if (!File.Exists(path))
                {
                    problems.Add($"{name}: 缺页文件 {index.Pages[page]}");
                    continue;
                }

                totalBytes += new FileInfo(path).Length;

                if (ReadPngSize(path) is not { } size)
                {
                    problems.Add($"{name}/{index.Pages[page]}: 不是合法 PNG");
                    continue;
                }

                if (size.Width != index.PageSize || size.Height != index.PageSize)
                    problems.Add($"{name}/{index.Pages[page]}: 尺寸 {size.Width}×{size.Height}，应为 {index.PageSize}²");
            }

            // ② 帧矩形
            foreach (KeyValuePair<int, Dictionary<int, int[]>> library in index.Frames)
            {
                foreach (KeyValuePair<int, int[]> frame in library.Value)
                {
                    checkedFrames++;

                    int[] rect = frame.Value;

                    if (rect.Length != 5)
                    {
                        problems.Add($"{name}: 库 {library.Key} 图 {frame.Key} 的帧不是 5 元组");
                        continue;
                    }

                    (int page, int x, int y, int w, int h) = (rect[0], rect[1], rect[2], rect[3], rect[4]);

                    if (page < 0 || page >= index.Pages.Count)
                        problems.Add($"{name}: 库 {library.Key} 图 {frame.Key} 指向不存在的页 {page}");
                    else if (x < 0 || y < 0 || w <= 0 || h <= 0 || x + w > index.PageSize || y + h > index.PageSize)
                        problems.Add($"{name}: 库 {library.Key} 图 {frame.Key} 的矩形越界 ({x},{y},{w},{h})");
                }
            }

            // ③ 像素抽样：拿贴图库原始像素和页里对应区域逐像素比
            if (options.PixelSamples > 0)
            {
                int seed = name.GetHashCode(StringComparison.Ordinal) & 0x7FFFFFF;
                Random random = new(seed);

                List<(int Library, int Image)> all = index.Frames
                    .SelectMany(x => x.Value.Keys.Select(image => (x.Key, image)))
                    .OrderBy(_ => random.Next())
                    .Take(options.PixelSamples)
                    .ToList();

                foreach ((int library, int image) in all)
                {
                    pixelSampled++;

                    int[] rect = index.Frames[library][image];
                    (int page, int x, int y, int w, int h) = (rect[0], rect[1], rect[2], rect[3], rect[4]);

                    if (!pageImages.TryGetValue(page, out byte[]? pageRgba))
                    {
                        pageRgba = ZlLibraryReader.DecodePng(File.ReadAllBytes(Path.Combine(mapDir, index.Pages[page])),
                            out _, out _);

                        pageImages[page] = pageRgba;
                    }

                    ZlLibraryReader? reader = readers.Get(library);

                    if (reader is null) unreadableLibraries.Add(library);
                    if (pageRgba is null || reader is null) continue;
                    // 空槽位（库里有下标但没有像素）是正常数据，跳过不算问题。
                    if (!reader.TryReadPixels(image, out byte[] source, out int sw, out int sh)) continue;

                    pixelCompared++;

                    if (sw != w || sh != h)
                    {
                        problems.Add($"{name}: 库 {library} 图 {image} 尺寸 {sw}×{sh}，索引写 {w}×{h}");
                        continue;
                    }

                    if (!SameBlock(pageRgba, index.PageSize, x, y, source, w, h))
                        problems.Add($"{name}: 库 {library} 图 {image} 在页 {page} 的像素与贴图库不一致");
                }
            }
        }

        // 贴图库读不到时像素对拍会**整块跳过**。这时候说「全部一致」是假阳性，
        // 必须单独拎出来：十有八九是 --client 没指向含 Data/*.Zl 的目录。
        if (unreadableLibraries.Count > 0)
            problems.Add($"有 {unreadableLibraries.Count} 个贴图库读不到（索引 {string.Join(",", unreadableLibraries.OrderBy(x => x))}）" +
                $"：--client 当前为 {options.ClientRoot}，应指向含 Data\\*.Zl 的目录");

        if (options.PixelSamples > 0 && pixelCompared == 0)
            problems.Add($"一次像素对拍都没做成（抽了 {pixelSampled} 块，全部因贴图库读不到或空槽位跳过）" +
                $"：先确认 --client 指向含 Data\\*.Zl 的目录");

        Console.WriteLine($"校验 {checkedMaps} 张图、{checkedFrames:N0} 个帧、{FormatBytes(totalBytes)}" +
            (options.PixelSamples > 0
                ? $"（每张图抽 {options.PixelSamples} 块，实际对拍 {pixelCompared} 块）"
                : ""));

        if (problems.Count == 0)
        {
            Console.WriteLine("结果：**全部一致** ✔");
            return 0;
        }

        Console.WriteLine($"结果：发现 {problems.Count} 个问题");
        foreach (string problem in problems.Take(40)) Console.WriteLine("  - " + problem);
        if (problems.Count > 40) Console.WriteLine($"  …（还有 {problems.Count - 40} 条）");

        return 1;
    }

    /// <summary>
    /// 逐像素比一块（源是 RGBA，页是整页 RGBA）。
    /// <para>
    /// **两边 alpha 都为 0 的像素跳过 RGB 比较**：PNG 存的是「未预乘」颜色，
    /// 而 alpha=0 的像素在编码/解码往返中 RGB 没有意义（Skia 会把它抹成 0），
    /// 真实数据里确实存在「全透明但 RGB 非零」的块（混色层、光照层）。
    /// 这种像素画出来看不见，比它只会制造假告警 —— 实测就撞到过一次
    /// （库 4 图 6293，43003 字节的透明块）。
    /// 只要有一边 alpha &gt; 0，RGB 就必须一致。
    /// </para>
    /// </summary>
    private static bool SameBlock(byte[] page, int pageSize, int x, int y, byte[] source, int w, int h)
    {
        for (int row = 0; row < h; row++)
        {
            int sourceOffset = row * w * 4;
            int pageOffset = ((y + row) * pageSize + x) * 4;

            for (int column = 0; column < w * 4; column += 4)
            {
                int pageAlpha = page[pageOffset + column + 3];
                int sourceAlpha = source[sourceOffset + column + 3];
                if (pageAlpha == 0 && sourceAlpha == 0) continue;

                if (page[pageOffset + column] != source[sourceOffset + column]) return false;
                if (page[pageOffset + column + 1] != source[sourceOffset + column + 1]) return false;
                if (page[pageOffset + column + 2] != source[sourceOffset + column + 2]) return false;
            }
        }

        return true;
    }

    /// <summary>从 PNG 的 IHDR 读宽高（不解码像素）。</summary>
    private static (int Width, int Height)? ReadPngSize(string path)
    {
        using FileStream stream = File.OpenRead(path);

        byte[] header = new byte[24];

        if (stream.Read(header, 0, 24) != 24) return null;

        ReadOnlySpan<byte> png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        if (!header.AsSpan(0, 8).SequenceEqual(png)) return null;

        int width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
        int height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];

        return (width, height);
    }

    // ===== 公共 =====

    private static List<string> ResolveMapFiles(Options options, string mapsDir)
    {
        if (options.MapList.Count == 0)
            return Directory.GetFiles(mapsDir, "*.map").OrderBy(x => x, StringComparer.Ordinal).ToList();

        List<string> files = new();

        foreach (string name in options.MapList)
        {
            string path = Path.Combine(mapsDir, name.EndsWith(".map", StringComparison.OrdinalIgnoreCase) ? name : name + ".map");

            if (File.Exists(path)) files.Add(path);
            else Console.WriteLine($"! 地图不存在，跳过：{name}");
        }

        return files;
    }

    private static string FormatBytes(long value)
    {
        string[] suffix = ["B", "KB", "MB", "GB"];
        double current = value;
        int index = 0;

        while (current >= 1024 && index < suffix.Length - 1) { current /= 1024; index++; }

        return $"{current:0.#}{suffix[index]}";
    }
}
