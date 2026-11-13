using System.Text.Json;
using Server.Bots;

namespace Server.Web.Services;

/// <summary>
/// 物品图标图集索引。
///
/// <para>
/// 图标不是一张张 png，而是打在一张 <c>wwwroot/icons/items/atlas.png</c> 里，
/// 由 <c>wwwroot/icons/items/atlas.json</c> 记录「ItemInfo.Image → 第几列第几行」。
/// 为什么要打包：System.db 有 1000+ 物品、700+ 个不同图标，一张张铺在部署目录里
/// 既难拷也难看（用户 2026-11-13 反馈「光这个目录就 736 个文件」）。
/// </para>
/// <para>
/// 生成方式：<c>iconpack.exe &lt;Data&gt; &lt;Server.Web\wwwroot\icons\items&gt; &lt;Server.Web&gt; --equip-only --atlas</c>。
/// 文件不存在时 <see cref="Ready"/> 为 false，页面退回「一张张 png」的老渲染方式（不报错）。
/// </para>
/// </summary>
public static class ItemIconAtlas
{
    private static readonly object Sync = new();

    /// <summary>ItemInfo.Image → (列, 行)。行左移 16 位塞进同一个 int，省一次元组分配。</summary>
    private static Dictionary<int, int>? _cells;

    private static bool _tried;
    private static int _cell = 36;
    private static int _cols;
    private static int _rows;

    /// <summary>图集是否可用（文件存在且解析成功）。</summary>
    public static bool Ready => _cells is { Count: > 0 };

    /// <summary>每格边长（像素）。</summary>
    public static int Cell => _cell;

    /// <summary>整张图集的像素宽高，直接给 CSS 的 background-size 用。</summary>
    public static int Width => _cell * _cols;

    public static int Height => _cell * _rows;

    /// <summary>加载失败的原因（给页面提示用，成功时为 null）。</summary>
    public static string? LastError { get; private set; }

    /// <summary>
    /// 读一次 <c>icons/items/atlas.json</c>。线程安全、只读一次；传 null 或文件不存在都安全。
    /// </summary>
    public static void Initialize(string? webRoot)
    {
        if (_tried) return;

        lock (Sync)
        {
            if (_tried) return;

            _tried = true;

            try
            {
                string? path = Locate(webRoot);

                if (path == null)
                {
                    LastError = "icons/items/atlas.json 不存在";
                    BotLog.SystemError("图标图集缺失：icons/items/atlas.json —— 假人详情的装备图标会显示不出来" +
                        "（开发机跑 iconpack --equip-only --atlas 生成）");

                    return;
                }

                using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
                JsonElement root = document.RootElement;

                _cell = root.TryGetProperty("cell", out JsonElement cell) ? cell.GetInt32() : 36;
                _cols = root.TryGetProperty("cols", out JsonElement cols) ? cols.GetInt32() : 32;
                _rows = root.TryGetProperty("rows", out JsonElement rows) ? rows.GetInt32() : 0;

                if (_cell <= 0 || _cols <= 0 || _rows <= 0)
                {
                    LastError = "atlas.json 的 cell/cols/rows 不合法";

                    return;
                }

                if (!root.TryGetProperty("map", out JsonElement map) || map.ValueKind != JsonValueKind.Object)
                {
                    LastError = "atlas.json 缺少 map";

                    return;
                }

                Dictionary<int, int> cells = new();

                foreach (JsonProperty property in map.EnumerateObject())
                {
                    if (!int.TryParse(property.Name, out int image)) continue;
                    if (property.Value.ValueKind != JsonValueKind.Array || property.Value.GetArrayLength() < 2) continue;

                    int col = property.Value[0].GetInt32();
                    int row = property.Value[1].GetInt32();

                    cells[image] = col | (row << 16);
                }

                _cells = cells;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _cells = null;
            }
        }
    }

    /// <summary>
    /// 找到 atlas.json。优先 <paramref name="webRoot"/>（<c>IWebHostEnvironment.WebRootPath</c>），
    /// 拿不到就退回「程序目录/wwwroot」—— 有的部署方式 content root 和程序目录不是同一个，
    /// 只认一个路径会导致图标静默消失，而现象只是「图标空白」，很难查。
    /// </summary>
    private static string? Locate(string? webRoot)
    {
        if (!string.IsNullOrWhiteSpace(webRoot))
        {
            string path = Path.Combine(webRoot, "icons", "items", "atlas.json");

            if (File.Exists(path)) return path;
        }

        string fallback = Path.Combine(AppContext.BaseDirectory, "wwwroot", "icons", "items", "atlas.json");

        return File.Exists(fallback) ? fallback : null;
    }

    /// <summary>查某个图标序号在图集的哪一格。图集不可用时恒 false。</summary>
    public static bool TryGet(int image, out int col, out int row)
    {
        col = 0;
        row = 0;

        if (image < 0 || _cells == null) return false;

        if (!_cells.TryGetValue(image, out int packed)) return false;

        col = packed & 0xFFFF;
        row = packed >> 16;

        return true;
    }
}
