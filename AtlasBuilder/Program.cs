using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Library;
using ZlMedia;

namespace AtlasBuilder;

/// <summary>
/// 地图贴图图集的预生成工具（2026-11-13）。
///
/// 为什么要它：地图区域编辑器点一次区域，客户端要按「一块一个 HTTP 请求」去取贴图，
/// 一张 500×500 的图去重后 7000 多块、约 95 MB，浏览器 6 并发下光排队就十几秒。
/// 图集把这些块合并成少数几张 2048² 的大图，请求数降到个位数。
///
/// 阶段划分：
///   scan   —— 只统计不写盘：算出「现状 vs 图集」的请求数、页数、字节，用来决定要不要继续。
///   digest —— 给指定贴图算 MD5，用来做「抽取 ZlLibraryReader 前后像素有没有变」的回归比对。
///
/// 后续（等 scan 的数据确认后再做）：build / update / verify / report。
/// </summary>
internal static class Program
{
    private const int DefaultPageSize = 2048;
    internal const int AtlasPadding = 2;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return 0;
        }

        string command = args[0];
        Options options = Options.Parse(args);

        try
        {
            return command switch
            {
                "scan" => Scan(options),
                "sizes" => Sizes(options),
                "build" => AtlasCommands.Build(options),
                "update" => AtlasCommands.Update(options),
                "verify" => AtlasCommands.Verify(options),
                "digest" => Digest(options),
                _ => Unknown(command)
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("失败：" + ex.Message);
            return 1;
        }
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine("未知命令：" + command);
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            AtlasBuilder —— 地图贴图图集工具

            用法：
              AtlasBuilder scan   [--client <客户端根目录>] [--maps <地图目录>] [--page 2048] [--top 12] [--permap 5]
              AtlasBuilder sizes  <地图名> [--client <客户端根目录>] [--top 20]
              AtlasBuilder digest <库>:<图> [<库>:<图> ...] [--client <客户端根目录>]

              AtlasBuilder build  [--maplist 15,00,D3400] [--out <产物目录>] [--page 2048] [--pack shelf|bucket] [--jobs 8]
              AtlasBuilder update [同上]     增量：只重建地图文件变过的图，清理已删除的图
              AtlasBuilder verify --client <含 Data 的目录> --out <产物目录> [--pixels 10]   校验索引与 PNG 是否自洽

            参数：
              --client   客户端根目录，默认 F:/DSH/Debug/Client（里面要有 Data/ 和 Map/）。
                         **服务器上必须显式给**，否则贴图库读不到，build 会全图失败、verify 会假通过。
              --maps     地图目录，默认 <client>/Map。只改「地图从哪读」，不选图。
              --maplist  只处理这几张图，逗号/分号/空格分隔；不写 = 全部 258 张。
              --out      产物目录，默认 <本程序目录>/../Atlas（发布到 <Server.Web>/AtlasBuilder/ 时正好落在 <Server.Web>/Atlas）
              --page     图集页边长，默认 2048（显存优先；4096 请求更少但一页 67MB 显存）
              --pack     装箱策略，默认 shelf（跨库混装，实测优于分桶）；bucket = 按高度精确分桶，仅用于对照
              --jobs     并行度，默认 CPU 核数
              --top      列表显示前 N 项，默认 12
              --permap   scan 时额外对最重的前 N 张图做「按图单独装箱」实测，默认 0（关）
              --pixels   verify 时每张图抽 N 块对拍像素，默认 0（只校验结构）
              --sample   scan 时采样多少块估算单块平均体积，默认 40
            """);
    }

    // ===== scan =====

    private static int Scan(Options options)
    {
        Stopwatch watch = Stopwatch.StartNew();
        string mapsDir = options.MapsDir;
        if (!Directory.Exists(mapsDir))
        {
            Console.Error.WriteLine("地图目录不存在：" + mapsDir);
            return 1;
        }

        string[] mapFiles = Directory.GetFiles(mapsDir, "*.map");
        if (mapFiles.Length == 0)
        {
            Console.Error.WriteLine("地图目录里没有 .map 文件：" + mapsDir);
            return 1;
        }

        Console.WriteLine($"客户端根目录：{options.ClientRoot}");
        Console.WriteLine($"地图目录    ：{mapsDir}（{mapFiles.Length} 张）");
        Console.WriteLine($"图集页边长  ：{options.PageSize}（padding {AtlasPadding}px）");
        Console.WriteLine();

        // 全局引用集合：库 → 图下标集合
        Dictionary<int, HashSet<int>> global = new();
        // 单图统计：地图名 → 库 → 图下标集合
        List<(string Name, Dictionary<int, HashSet<int>> Refs, int Width, int Height)> perMap = new();

        foreach (string file in mapFiles)
        {
            Dictionary<int, HashSet<int>> refs = new();
            int width = 0;
            int height = 0;

            MapScanner.Scan(file, (w, h) => { width = w; height = h; },
                (library, image) => AddRef(refs, library, image));

            if (width <= 0 || height <= 0) continue;

            foreach (KeyValuePair<int, HashSet<int>> pair in refs)
            {
                if (!global.TryGetValue(pair.Key, out HashSet<int>? set))
                    global[pair.Key] = set = new HashSet<int>();

                set.UnionWith(pair.Value);
            }

            perMap.Add((Path.GetFileNameWithoutExtension(file), refs, width, height));
        }

        long globalCount = global.Sum(x => (long)x.Value.Count);
        Console.WriteLine($"=== 全量引用 ===");
        Console.WriteLine($"  涉及 {global.Count} 个贴图库，去重后 {globalCount:N0} 张唯一贴图");
        Console.WriteLine();

        // 逐库规划：装箱 → 量每页真实 PNG 体积 → 记「图下标 → 页号」映射。
        // 后面单图对照全靠这张映射：一张图触及哪些页、要下多少字节，只能这么算。
        List<LibraryPlan> plans = new();
        List<int> missing = new();

        foreach (int library in global.Keys.OrderBy(x => x))
        {
            HashSet<int> images = global[library];
            string name = ResolveLibraryName(library);
            string? path = ResolveLibraryPath(options.ClientRoot, library);

            if (path is null || !File.Exists(path))
            {
                missing.Add(library);
                plans.Add(new LibraryPlan(library, name, null, images.Count));
                continue;
            }

            ZlLibraryReader reader;
            try { reader = ZlLibraryReader.Load(path); }
            catch (Exception ex)
            {
                Console.WriteLine($"  ! 库 {library} 解析失败：{ex.Message}");
                plans.Add(new LibraryPlan(library, name, null, images.Count));
                continue;
            }

            // 真实尺寸装箱 → 页
            List<(int W, int H, int Index)> sized = new(images.Count);
            foreach (int image in images.OrderBy(x => x))
            {
                if (!reader.TryGetSize(image, out int w, out int h)) continue;
                sized.Add((w, h, image));
            }

            List<List<Placement>> pages = ShelfPacker.Pack(sized, options.PageSize, AtlasPadding);

            int[] imagePage = new int[Math.Max(reader.ImageCount, 1)];
            Array.Fill(imagePage, -1);
            long[] pageBytes = new long[pages.Count];
            for (int page = 0; page < pages.Count; page++)
            {
                foreach (Placement placement in pages[page]) imagePage[placement.Index] = page;
                pageBytes[page] = MeasureAtlasPage(reader, pages[page], options.PageSize);
            }

            plans.Add(new LibraryPlan(library, name, reader, images.Count)
            {
                Pages = pages,
                ImagePage = imagePage,
                PageBytes = pageBytes,
                AvgBlockBytes = MeasureAverageBlockBytes(reader, sized, options.SampleCount)
            });
        }

        if (missing.Count > 0)
            Console.WriteLine($"  ! 有 {missing.Count} 个库找不到文件（索引 {string.Join(",", missing)}），这些块将保持透明");

        // 按库统计：引用数 / 页数 / 现状字节（采样外推）/ 图集字节（真拼真编码，逐页量）
        Console.WriteLine();
        Console.WriteLine("=== 按贴图库 ===");
        Console.WriteLine($"{"库",4} {"文件",-18} {"库内图数",8} {"引用",7} {"页数",4} {"现状字节",10} {"图集字节",10} {"比例",6}");

        long totalNow = 0;
        long totalAtlas = 0;
        int totalPages = 0;

        foreach (LibraryPlan plan in plans)
        {
            if (plan.Reader is null)
            {
                Console.WriteLine($"{plan.Library,4} {plan.Name,-18} {"-",8} {plan.RefCount,7} {"-",4} {"-",10} {"-",10} {"-",6}");
                continue;
            }

            long nowBytes = (long)(plan.AvgBlockBytes * plan.RefCount);
            long atlasBytes = plan.PageBytes.Sum();

            totalNow += nowBytes;
            totalAtlas += atlasBytes;
            totalPages += plan.Pages.Count;

            string ratio = nowBytes > 0 ? $"{(double)atlasBytes / nowBytes:P0}" : "-";
            Console.WriteLine($"{plan.Library,4} {plan.Name,-18} {plan.Reader.ImageCount,8} {plan.RefCount,7} {plan.Pages.Count,4} " +
                $"{FormatBytes(nowBytes),10} {FormatBytes(atlasBytes),10} {ratio,6}");
        }

        Console.WriteLine(new string('-', 84));
        Console.WriteLine($"{"合计",4} {"",-18} {"",8} {globalCount,7} {totalPages,4} " +
            $"{FormatBytes(totalNow),10} {FormatBytes(totalAtlas),10} " +
            $"{(totalNow > 0 ? (double)totalAtlas / totalNow : 0):P0}");

        // 单图对照：这是真正决定成败的一栏。
        // 页是跨图共享的，所以「全量 90%」说明不了问题 —— 一张图只下它触及的那几页。
        Dictionary<int, LibraryPlan> byLibrary = plans.Where(x => x.Reader is not null).ToDictionary(x => x.Library);

        List<MapCost> costs = new();
        foreach (var item in perMap)
        {
            double nowBytes = 0;
            int nowRequests = 0;
            long atlasBytes = 0;
            int atlasPages = 0;
            HashSet<(int Library, int Page)> touched = new();

            foreach (KeyValuePair<int, HashSet<int>> pair in item.Refs)
            {
                nowRequests += pair.Value.Count;

                if (!byLibrary.TryGetValue(pair.Key, out LibraryPlan? plan)) continue;

                nowBytes += plan.AvgBlockBytes * pair.Value.Count;
                foreach (int image in pair.Value)
                {
                    if (image < 0 || image >= plan.ImagePage.Length) continue;
                    int page = plan.ImagePage[image];
                    if (page < 0) continue;
                    touched.Add((pair.Key, page));
                }
            }

            foreach ((int library, int page) in touched)
            {
                atlasBytes += byLibrary[library].PageBytes[page];
                atlasPages++;
            }

            costs.Add(new MapCost(item.Name, item.Width, item.Height, nowRequests, (long)nowBytes,
                atlasPages, touched.Select(x => x.Library).Distinct().Count(), atlasBytes));
        }

        Console.WriteLine();
        Console.WriteLine($"=== 单图对照（按现状字节，前 {options.Top}） ===");
        Console.WriteLine($"{"地图",-12} {"尺寸",9} {"现状请求",8} {"现状字节",10} {"图集页",6} {"图集请求",8} {"图集字节",10} {"字节比",6}");

        foreach (MapCost cost in costs.OrderByDescending(x => x.NowBytes).Take(options.Top))
        {
            string ratio = cost.NowBytes > 0 ? $"{(double)cost.AtlasBytes / cost.NowBytes:P0}" : "-";
            // 图集请求 = 触及的页 + 每个库一份 index.json
            Console.WriteLine($"{cost.Name,-12} {$"{cost.Width}×{cost.Height}",9} {cost.NowRequests,8} " +
                $"{FormatBytes(cost.NowBytes),10} {cost.AtlasPages,6} {cost.AtlasPages + cost.AtlasLibraries,8} " +
                $"{FormatBytes(cost.AtlasBytes),10} {ratio,6}");
        }

        long viewNow = costs.Sum(x => x.NowBytes);
        long viewAtlas = costs.Sum(x => x.AtlasBytes);
        long requestNow = costs.Sum(x => (long)x.NowRequests);
        long requestAtlas = costs.Sum(x => (long)x.AtlasPages + x.AtlasLibraries);
        MapCost heaviest = costs.OrderByDescending(x => x.NowBytes).First();

        Console.WriteLine(new string('-', 96));
        Console.WriteLine($"{"全部 258 图",-12} {"",9} {requestNow,8} {FormatBytes(viewNow),10} {"",6} {requestAtlas,8} " +
            $"{FormatBytes(viewAtlas),10} {(viewNow > 0 ? (double)viewAtlas / viewNow : 0),6:P0}");

        // 按图单独出图集：只装这张图自己用到的块，零浪费，但跨图不共享。
        // **必须跨库合装**：库只是块的来源，按库分别装箱会让每个库各浪费一段页尾。
        if (options.PerMap > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"=== 按图单独出图集（前 {options.PerMap} 张，页 {options.PageSize}²，跨库合装） ===");
            Console.WriteLine($"{"地图",-12} {"唯一贴图",8} {"理论页",6} " +
                $"{"混装页",6} {"混装字节",10} {"混装率",7} " +
                $"{"分桶页",6} {"分桶字节",10} {"分桶率",7}");

            foreach (MapCost cost in costs.OrderByDescending(x => x.NowBytes).Take(options.PerMap))
            {
                var item = perMap.First(x => x.Name == cost.Name);

                List<AtlasBlock> blocks = CollectBlocks(item.Refs, byLibrary);
                long area = blocks.Sum(x => (long)x.W * x.H);
                int theoretical = (int)Math.Ceiling((double)area / options.PageSize / options.PageSize);

                (int shelfPages, long shelfBytes) = MeasurePlan(blocks, byLibrary, options.PageSize, PackStrategy.Shelf);
                (int bucketPages, long bucketBytes) = MeasurePlan(blocks, byLibrary, options.PageSize, PackStrategy.Bucket);

                string shelfRatio = cost.NowBytes > 0 ? $"{(double)shelfBytes / cost.NowBytes:P0}" : "-";
                string bucketRatio = cost.NowBytes > 0 ? $"{(double)bucketBytes / cost.NowBytes:P0}" : "-";

                Console.WriteLine($"{cost.Name,-12} {cost.NowRequests,8} {theoretical,6} " +
                    $"{shelfPages,6} {FormatBytes(shelfBytes),10} {shelfRatio,7} " +
                    $"{bucketPages,6} {FormatBytes(bucketBytes),10} {bucketRatio,7}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("=== 结论 ===");
        Console.WriteLine($"  全量字节：{FormatBytes(totalNow)} → {FormatBytes(totalAtlas)}" +
            $"（{(totalNow > 0 ? (double)totalAtlas / totalNow : 0):P0}，{totalPages} 页 {options.PageSize}²）");
        Console.WriteLine($"  全量请求：{globalCount:N0} 次（逐块） → {totalPages} 页 + {byLibrary.Count} 份索引");
        Console.WriteLine($"  单图最重：{heaviest.Name} {heaviest.Width}×{heaviest.Height} " +
            $"{heaviest.NowRequests:N0} 次 / {FormatBytes(heaviest.NowBytes)} → " +
            $"{heaviest.AtlasPages} 页 / {FormatBytes(heaviest.AtlasBytes)}");
        Console.WriteLine($"  逐图累计：{requestNow:N0} 次 / {FormatBytes(viewNow)} → {requestAtlas:N0} 次 / {FormatBytes(viewAtlas)}" +
            $"（字节 {(viewNow > 0 ? (double)viewAtlas / viewNow : 0):P0}，请求 {(requestNow > 0 ? 1 - (double)requestAtlas / requestNow : 0):P0} 省）");
        Console.WriteLine($"  耗时：{watch.Elapsed.TotalSeconds:0.0}s");
        return 0;
    }

    /// <summary>把一张图的「库 → 图集合」摊平成块列表（跨库合装的输入）。</summary>
    private static List<AtlasBlock> CollectBlocks(Dictionary<int, HashSet<int>> refs,
        Dictionary<int, LibraryPlan> byLibrary)
    {
        List<AtlasBlock> blocks = new();

        foreach (KeyValuePair<int, HashSet<int>> pair in refs)
        {
            if (!byLibrary.TryGetValue(pair.Key, out LibraryPlan? plan)) continue;

            foreach (int image in pair.Value)
                if (plan.Reader!.TryGetSize(image, out int w, out int h)) blocks.Add(new AtlasBlock(pair.Key, image, w, h));
        }

        return blocks;
    }

    /// <summary>按给定策略装一次、逐页真编码，返回页数与总字节。</summary>
    private static (int Pages, long Bytes) MeasurePlan(List<AtlasBlock> blocks,
        Dictionary<int, LibraryPlan> byLibrary, int pageSize, PackStrategy strategy)
    {
        Dictionary<int, ZlLibraryReader> readers = byLibrary.ToDictionary(x => x.Key, x => x.Value.Reader!);

        List<AtlasPage> pages = AtlasPlanner.Plan(blocks, pageSize, AtlasPadding, strategy);

        long bytes = 0;

        foreach (AtlasPage page in pages)
            bytes += AtlasPlanner.Composite(page, readers, pageSize, out _).LongLength;

        return (pages.Count, bytes);
    }

    /// <summary>单张图的成本对照。AtlasLibraries 是它触及的库数（每个库要一份 index.json）。</summary>
    private sealed record MapCost(string Name, int Width, int Height, int NowRequests, long NowBytes,
        int AtlasPages, int AtlasLibraries, long AtlasBytes);

    private sealed class LibraryPlan
    {
        public LibraryPlan(int library, string name, ZlLibraryReader? reader, int refCount)
        {
            Library = library;
            Name = name;
            Reader = reader;
            RefCount = refCount;
        }

        public int Library { get; }
        public string Name { get; }
        public ZlLibraryReader? Reader { get; }
        public int RefCount { get; }
        public List<List<Placement>> Pages { get; init; } = new();
        public int[] ImagePage { get; init; } = Array.Empty<int>();
        public long[] PageBytes { get; init; } = Array.Empty<long>();
        public double AvgBlockBytes { get; init; }
    }

    /// <summary>采样量「现状」的单块平均体积（每块单独编一张 PNG）。</summary>
    private static double MeasureAverageBlockBytes(ZlLibraryReader reader,
        List<(int W, int H, int Index)> sized, int sampleCount)
    {
        if (sized.Count == 0) return 0;

        int step = Math.Max(1, sized.Count / Math.Max(1, sampleCount));
        long bytes = 0;
        int sampled = 0;
        for (int index = 0; index < sized.Count; index += step)
        {
            byte[]? png = reader.ReadPng(sized[index].Index);
            if (png is null) continue;
            bytes += png.Length;
            sampled++;
        }

        return sampled == 0 ? 0 : (double)bytes / sampled;
    }

    /// <summary>真拼一页并编码，返回这一页的 PNG 体积。用真实像素量，不靠估算。</summary>
    private static long MeasureAtlasPage(ZlLibraryReader reader, List<Placement> page, int pageSize)
    {
        byte[] canvas = new byte[pageSize * pageSize * 4];

        foreach (Placement placement in page)
        {
            if (!reader.TryReadPixels(placement.Index, out byte[] rgba, out int w, out int h)) continue;
            if (w <= 0 || h <= 0) continue;
            if (placement.X + w > pageSize || placement.Y + h > pageSize) continue;

            int rowBytes = w * 4;
            for (int row = 0; row < h; row++)
            {
                int source = row * rowBytes;
                int target = ((placement.Y + row) * pageSize + placement.X) * 4;
                Array.Copy(rgba, source, canvas, target, rowBytes);
            }
        }

        return ZlLibraryReader.EncodePng(canvas, pageSize, pageSize).LongLength;
    }

    // ===== sizes =====

    /// <summary>看一张图的贴图尺寸分布：决定装箱怎么分档（尺寸种类多时，精确分桶会让小桶各占一行）。</summary>
    private static int Sizes(Options options)
    {
        if (options.MapName is null)
        {
            Console.Error.WriteLine("sizes 需要一个地图名，例如：AtlasBuilder sizes 15");
            return 1;
        }

        string path = Path.Combine(options.MapsDir, options.MapName + ".map");
        if (!File.Exists(path)) path = Path.Combine(options.MapsDir, options.MapName);

        if (!File.Exists(path))
        {
            Console.Error.WriteLine("地图文件不存在：" + path);
            return 1;
        }

        Dictionary<int, HashSet<int>> refs = new();
        MapScanner.Scan(path, (_, _) => { }, (library, image) => AddRef(refs, library, image));

        Dictionary<(int W, int H), int> histogram = new();
        long area = 0;
        int total = 0;

        foreach (KeyValuePair<int, HashSet<int>> pair in refs)
        {
            string? libraryPath = ResolveLibraryPath(options.ClientRoot, pair.Key);
            if (libraryPath is null || !File.Exists(libraryPath)) continue;

            ZlLibraryReader reader = ZlLibraryReader.Load(libraryPath);

            foreach (int image in pair.Value)
            {
                if (!reader.TryGetSize(image, out int w, out int h)) continue;

                histogram.TryGetValue((w, h), out int count);
                histogram[(w, h)] = count + 1;
                area += (long)w * h;
                total++;
            }
        }

        Console.WriteLine($"地图 {options.MapName}：{total:N0} 块，总面积 {area:N0} px²" +
            $"（{options.PageSize}² 页理论最少 {(int)Math.Ceiling((double)area / options.PageSize / options.PageSize)} 页）");
        Console.WriteLine();
        Console.WriteLine($"{"尺寸",12} {"块数",7} {"占比",7} {"面积占比",9}");

        foreach (KeyValuePair<(int W, int H), int> pair in histogram.OrderByDescending(x => x.Value).Take(options.Top))
        {
            long size = (long)pair.Key.W * pair.Key.H * pair.Value;
            Console.WriteLine($"{$"{pair.Key.W}×{pair.Key.H}",12} {pair.Value,7} {(double)pair.Value / total,7:P1} " +
                $"{(double)size / area,9:P1}");
        }

        Console.WriteLine();
        Console.WriteLine($"尺寸种类：{histogram.Count} 种；只占 1 块的尺寸：{histogram.Count(x => x.Value == 1)} 种");
        return 0;
    }

    // ===== digest =====

    private static int Digest(Options options)
    {
        if (options.Pairs.Count == 0)
        {
            Console.Error.WriteLine("digest 需要至少一个 <库>:<图> 参数。");
            return 1;
        }

        Dictionary<int, ZlLibraryReader> cache = new();
        foreach ((int library, int image) in options.Pairs)
        {
            string? path = ResolveLibraryPath(options.ClientRoot, library);
            if (path is null || !File.Exists(path))
            {
                Console.WriteLine($"{library}:{image}  MISSING");
                continue;
            }

            if (!cache.TryGetValue(library, out ZlLibraryReader? reader))
                cache[library] = reader = ZlLibraryReader.Load(path);

            byte[]? png = reader.ReadPng(image);
            if (png is null)
            {
                Console.WriteLine($"{library}:{image}  EMPTY");
                continue;
            }

            string md5 = Convert.ToHexString(MD5.HashData(png)).ToLowerInvariant();
            Console.WriteLine($"{library}:{image}  {png.Length,7}  {md5}");
        }

        return 0;
    }

    // ===== 公共 =====

    private static void AddRef(Dictionary<int, HashSet<int>> refs, int library, int image)
    {
        if (library <= 0 || image < 0) return;
        if (!refs.TryGetValue(library, out HashSet<int>? set)) refs[library] = set = new HashSet<int>();
        set.Add(image);
    }

    internal static string? ResolveLibraryPath(string clientRoot, int library)
    {
        if (!Libraries.KROrder.TryGetValue(library, out LibraryFile file)) return null;
        if (!Libraries.LibraryList.TryGetValue(file, out string? relative)) return null;

        string normalized = relative.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(clientRoot, normalized));
    }

    private static string ResolveLibraryName(int library) =>
        Libraries.KROrder.TryGetValue(library, out LibraryFile file) ? file.ToString() : "?";

    private static string FormatBytes(long value)
    {
        string[] suffix = ["B", "KB", "MB", "GB"];
        double current = value;
        int index = 0;
        while (current >= 1024 && index < suffix.Length - 1) { current /= 1024; index++; }
        return $"{current:0.#}{suffix[index]}";
    }

    internal sealed class Options
    {
        public string ClientRoot { get; private set; } = @"F:\DSH\Debug\Client";
        public string? MapsOverride { get; private set; }
        public int PageSize { get; private set; } = DefaultPageSize;
        public int Top { get; private set; } = 12;
        public int SampleCount { get; private set; } = 40;
        public int PerMap { get; private set; }
        public PackStrategy Strategy { get; private set; } = PackStrategy.Shelf;
        public int Jobs { get; private set; } = Environment.ProcessorCount;
        public int PixelSamples { get; private set; }
        public string? OutputOverride { get; private set; }
        public List<string> MapList { get; } = new();

        /// <summary>产物目录：默认落在本程序目录的上一级（发布到 &lt;Server.Web&gt;/AtlasBuilder/ 时就是 &lt;Server.Web&gt;/Atlas）。</summary>
        public string OutputDir => OutputOverride ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Atlas"));
        public List<(int Library, int Image)> Pairs { get; } = new();
        public string? MapName { get; private set; }

        public string MapsDir => MapsOverride ?? Path.Combine(ClientRoot, "Map");

        public static Options Parse(string[] args)
        {
            Options options = new();

            for (int index = 1; index < args.Length; index++)
            {
                string arg = args[index];
                switch (arg)
                {
                    case "--client":
                        options.ClientRoot = Next(args, ref index, "--client");
                        break;
                    case "--maps":
                        options.MapsOverride = Next(args, ref index, "--maps");
                        break;
                    case "--page":
                        options.PageSize = ParseInt(Next(args, ref index, "--page"), DefaultPageSize);
                        break;
                    case "--top":
                        options.Top = ParseInt(Next(args, ref index, "--top"), 12);
                        break;
                    case "--sample":
                        options.SampleCount = ParseInt(Next(args, ref index, "--sample"), 40);
                        break;
                    case "--permap":
                        options.PerMap = ParseInt(Next(args, ref index, "--permap"), 5);
                        break;
                    case "--out":
                        options.OutputOverride = Next(args, ref index, "--out");
                        break;
                    case "--jobs":
                        options.Jobs = ParseInt(Next(args, ref index, "--jobs"), Environment.ProcessorCount);
                        break;
                    case "--pixels":
                        options.PixelSamples = ParseInt(Next(args, ref index, "--pixels"), 4);
                        break;
                    case "--maplist":
                        options.MapList.AddRange(Next(args, ref index, "--maplist")
                            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries));
                        break;
                    case "--pack":
                        options.Strategy = Next(args, ref index, "--pack") == "bucket"
                            ? PackStrategy.Bucket
                            : PackStrategy.Shelf;
                        break;
                    default:
                        if (TryParsePair(arg, out (int, int) pair)) options.Pairs.Add(pair);
                        else if (!arg.StartsWith("--")) options.MapName ??= arg;
                        break;
                }
            }

            return options;
        }

        private static string Next(string[] args, ref int index, string name)
        {
            if (index + 1 >= args.Length) throw new ArgumentException($"{name} 缺少参数值。");
            return args[++index];
        }

        private static int ParseInt(string text, int fallback) =>
            int.TryParse(text, out int value) && value > 0 ? value : fallback;

        private static bool TryParsePair(string text, out (int, int) pair)
        {
            pair = default;
            string[] parts = text.Split(':');
            if (parts.Length != 2) return false;
            if (!int.TryParse(parts[0], out int library) || !int.TryParse(parts[1], out int image)) return false;
            pair = (library, image);
            return true;
        }
    }
}
