using System.Reflection;
using System.Runtime.CompilerServices;
using Library;
using Library.SystemModels;
using MirDB;
using Server.DBModels;
using Server.Envir;
using Server.Models;
using Server.Models.AutoPath;

// 自动寻路「失败策略」的回归检查。
//
// 背景（2026-11-12 查「假人卡住刷失败重试」）：
//   压测日志 Logs.txt.pathfix-base 里有 19162 组「STARTED 与 ENDED 落在同一秒」的记录，
//   表现为假人一步不动、每秒重试 4~6 次、持续几秒到一分钟。
//
//   其中一半以上落在「角色已经死亡、但假人大脑还没跑到 ProcessDead」的那几帧里：
//   大脑按原状态反复调 AutoPathService.TryStart（规划成功 → STARTED），
//   而 AutoPathService.Process 第一行看到 player.Dead 就把整条路线作废（→ ENDED）。
//
//   修法：TryStart 在角色死亡时直接拒绝，根本不建状态。这样死亡窗口里一次 STARTED 都不会产生。
//
// 跑在一次性临时库上，不碰正式 Users.db。
string root = Path.Combine(Path.GetTempPath(), "ZirconAutoPath-" + Guid.NewGuid().ToString("N"));

Session session = new(SessionMode.System | SessionMode.Users, root + Path.DirectorySeparatorChar);
session.Initialize(typeof(ItemInfo).Assembly, typeof(UserItem).Assembly);

foreach (FieldInfo field in typeof(SEnvir).GetFields(BindingFlags.Public | BindingFlags.Static))
    if (field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition() == typeof(DBCollection<>))
        field.SetValue(null, session.GetCollection(field.FieldType.GetGenericArguments()[0]));

SEnvir.Session = session;
SEnvir.Random = new Random(20261112);
SEnvir.Now = DateTime.Now;

Globals.CurrencyInfoList = SEnvir.CurrencyInfoList;

int checks = 0;
int failures = 0;

void Check(bool condition, string name, string detail = null)
{
    checks++;

    if (condition)
    {
        Console.WriteLine($"  ok   {name}");
        return;
    }

    failures++;
    Console.WriteLine($"  FAIL {name}{(detail == null ? "" : "  → " + detail)}");
}

// ---------- 造基础数据 ----------

MapInfo map = SEnvir.MapInfoList.CreateNewObject();
map.Description = "测试城";
map.MinimumLevel = 1;
map.CanAutoPath = true;

MapRegion region = SEnvir.MapRegionList.CreateNewObject();
region.Map = map;
region.Description = "药店";
region.PointList = new List<System.Drawing.Point> { new(20, 20) };

NPCInfo npc = SEnvir.NPCInfoList.CreateNewObject();
npc.NPCName = "测试药师";
npc.Region = region;

CheckPlayer MakePlayer()
{
    // 走 GetUninitializedObject 而不是 new：PlayerObject 的构造函数会读
    // Character.CurrentMP 等一整套角色数据，而这些检查只碰 TryStart 的最前面几个判断。
    // 同仓库的 BotLifeChecks 也是这么造壳的。
    return (CheckPlayer)RuntimeHelpers.GetUninitializedObject(typeof(CheckPlayer));
}

// AutoPathState 是 internal，测试从反射读「有没有真的建出寻路状态」。
FieldInfo autoPathField = typeof(PlayerObject)
    .GetField("AutoPath", BindingFlags.NonPublic | BindingFlags.Instance)!;

bool HasAutoPath(PlayerObject player) => autoPathField.GetValue(player) != null;

// ---------- 1) 死亡时不规划（点终点） ----------

Console.WriteLine("死亡角色不应开始自动寻路：");

Config.AllowWaypoints = true;

CheckPlayer dead = MakePlayer();
dead.Dead = true;

Check(!AutoPathService.Instance.TryStart(dead, map, new System.Drawing.Point(30, 30)),
    "死亡时 TryStart(地图, 点) 返回 false");
Check(!HasAutoPath(dead), "死亡时不创建 AutoPath 状态");

// ---------- 2) 死亡时不规划（NPC 终点） ----------
//
// NPC 重载**不检查 Config.AllowWaypoints**（NPC 寻路一直可用），
// 所以它是最容易漏掉死亡守卫的入口 —— 单独盯一条。

Check(!AutoPathService.Instance.TryStart(dead, npc),
    "死亡时 TryStart(NPC) 返回 false（NPC 重载不查 AllowWaypoints，最容易漏）");
Check(!HasAutoPath(dead), "死亡时 NPC 寻路也不创建状态");

// ---------- 3) 既有行为不回退 ----------

Console.WriteLine();
Console.WriteLine("既有行为不回退：");

Config.AllowWaypoints = false;

CheckPlayer alive = MakePlayer();

Check(!AutoPathService.Instance.TryStart(alive, map, new System.Drawing.Point(30, 30)),
    "AllowWaypoints=false 时点终点仍被拒");

Config.AllowWaypoints = true;

Check(!AutoPathService.Instance.TryStart(alive, null!, new System.Drawing.Point(30, 30)),
    "地图为 null 时返回 false");
Check(!AutoPathService.Instance.TryStart(null!, map, new System.Drawing.Point(30, 30)),
    "角色为 null 时返回 false");

// ---------- 4) 死亡状态优先于一切 ----------
//
// 大脑在死亡窗口里可能带着任意旧状态来调用；死亡判断必须在最前面，
// 不能因为 AllowWaypoints / 地图合法性之类的分支先返回而绕过。

Console.WriteLine();
Console.WriteLine("死亡判断的优先级：");

Config.AllowWaypoints = false;

CheckPlayer dead2 = MakePlayer();
dead2.Dead = true;

Check(!AutoPathService.Instance.TryStart(dead2, npc),
    "AllowWaypoints=false + 死亡 → NPC 寻路仍被拒");

Config.AllowWaypoints = true;

Console.WriteLine();
Console.WriteLine("────────────────────────────");
Console.WriteLine($"共 {checks} 项，失败 {failures} 项");

if (failures > 0)
{
    Console.WriteLine("有失败项。");
    return 1;
}

Console.WriteLine("全部通过。");

return 0;

/// <summary>
/// 只用来跑寻路判定的壳：<c>Stats</c> 与全服广播不在检查范围内。
/// </summary>
sealed class CheckPlayer : PlayerObject
{
    public CheckPlayer() : base(null, null) { }

    public override void RefreshStats() { }
}
