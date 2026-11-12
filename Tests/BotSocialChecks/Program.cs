using System.Reflection;
using System.Runtime.CompilerServices;
using Library;
using Server.Bots;
using Server.DBModels;
using Server.Envir;
using Server.Envir.Translations;
using Server.Models;

// P4-3 真人优先：BotPerception.IsOwnedByRealPlayer 的四种情形。
//
// 这个判断决定「假人会不会抢真人正在打的怪」，是 P4 里唯一一条
// 「不做就得罪真人」的功能，所以单独钉死：
//   1) 没人打过（EXPOwner == null）      → 假人可以打
//   2) 击杀权在假人手上                   → 假人可以打（自己人互抢没关系）
//   3) 击杀权在真人手上                   → 假人必须放过
//   4) 击杀权挂在一个已下线的人身上       → 当作没人打（否则怪会烂在地上）
//
// 全部用 GetUninitializedObject 构造，不碰数据库、不起服务器。

ConfigReader.ConfigObjects[typeof(ChineseMessages)] = new ChineseMessages();
ConfigReader.ConfigObjects[typeof(EnglishMessages)] = new EnglishMessages();

int checks = 0;
int failures = 0;

void Check(bool condition, string name)
{
    checks++;

    if (condition)
    {
        Console.WriteLine($"  ok   {name}");
        return;
    }

    failures++;
    Console.WriteLine($"  FAIL {name}");
}

PlayerObject MakePlayer(bool isBot)
{
    SConnection con = new(isBot ? "bot:test" : "127.0.0.1", isBot);

    PlayerObject player = (PlayerObject)RuntimeHelpers.GetUninitializedObject(typeof(PlayerObject));

    player.Connection = con;
    con.Player = player;

    return player;
}

MonsterObject MakeMonster(PlayerObject? owner)
{
    MonsterObject monster = (MonsterObject)RuntimeHelpers.GetUninitializedObject(typeof(MonsterObject));

    // 直接写私有字段 _EXPOwner，绕开属性 setter：
    // setter 会去改两边玩家的 TaggedMonsters，而这两个玩家是未初始化对象，集合为 null 会 NRE。
    typeof(MonsterObject)
        .GetField("_EXPOwner", BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(monster, owner);

    return monster;
}

Console.WriteLine("真人优先（P4-3）：");

// 1) 没人打过
Check(!BotPerception.IsOwnedByRealPlayer(MakeMonster(null)), "EXPOwner 为 null → 不算真人持有");

// 2) 击杀权在假人手上
Check(!BotPerception.IsOwnedByRealPlayer(MakeMonster(MakePlayer(true))), "击杀权在假人手上 → 假人可以打");

// 3) 击杀权在真人手上（核心用例）
Check(BotPerception.IsOwnedByRealPlayer(MakeMonster(MakePlayer(false))), "击杀权在真人手上 → 假人必须放过");

// 4) 击杀权挂在已下线的人身上（Connection 被清空）
PlayerObject offline = MakePlayer(false);
offline.Connection = null!;
Check(!BotPerception.IsOwnedByRealPlayer(MakeMonster(offline)), "持有者已下线 → 当作没人打");

// 5) 空怪物引用不能炸（防御性）
Check(!BotPerception.IsOwnedByRealPlayer(null!), "null 怪物 → 返回 false 不抛");

Console.WriteLine();
Console.WriteLine("聊天语料解析与过滤（P2-1）：");

// 写一份带各种坏行的 chat.txt，验证「坏行跳过、好行留下」。
Directory.CreateDirectory(BotData.Root);

File.WriteAllLines(BotData.ChatPath, new[]
{
    "# 注释行要忽略",
    "",
    "问候||1|你好啊",                    // 有效
    "感慨|比奇城|10|这城的药真贵",        // 有效，限地图 + 限等级
    "问候|1",                            // 坏：字段不足
    "聊天||1|类别不认识",                 // 坏：类别不认识
    "感慨||abc|等级不是数字",             // 坏：等级下限非数字
    new string('长', 61) + "||1|x",       // 坏：文本超 60 字
});

List<BotChatLine> lines = BotChat.Lines();

Check(lines.Count == 2, $"只留下 2 条有效语料（实际 {lines.Count}）");
Check(BotChat.Count == 2, "Count 与 Lines().Count 一致");
Check(lines.Any(x => x.Kind == BotChatKind.Greeting && x.MinLevel == 1), "问候语解析正确");
Check(lines.Any(x => x.Kind == BotChatKind.Feeling && x.Map == "比奇城" && x.MinLevel == 10), "限地图 + 限等级的感慨解析正确");

// Filter 是私有的，用反射验证（不想为了测试把内部实现暴露成公开 API）。
MethodInfo filter = typeof(BotChat).GetMethod("Filter", BindingFlags.NonPublic | BindingFlags.Static)!;

int Count(object? kind, string? mapName, int level) =>
    ((List<BotChatLine>)filter.Invoke(null, new[] { lines, kind, mapName, level })!).Count;

Check(Count(BotChatKind.Greeting, "比奇城", 1) == 1, "只要问候 → 1 条");
Check(Count(null, "比奇城", 1) == 1, "1 级在比奇城 → 只剩不限等级的 1 条");
Check(Count(null, "比奇城", 10) == 2, "10 级在比奇城 → 两条都能说");
Check(Count(null, "潘夜村落", 10) == 1, "10 级在别的地图 → 限地图那条不能说");
Check(Count(null, "比奇城", 9) == 1, "9 级在比奇城 → 等级下限挡住感慨");

Console.WriteLine();
Console.WriteLine("夜间时段判定（P2-6）：");

Config.BotNightMinHour = 1;
Config.BotNightMaxHour = 7;

Check(BotRoster.IsNightHour(new DateTime(2026, 11, 12, 1, 0, 0)), "1 点算夜间");
Check(BotRoster.IsNightHour(new DateTime(2026, 11, 12, 6, 59, 0)), "6:59 算夜间");
Check(!BotRoster.IsNightHour(new DateTime(2026, 11, 12, 7, 0, 0)), "7 点不算夜间（右开区间）");
Check(!BotRoster.IsNightHour(new DateTime(2026, 11, 12, 0, 30, 0)), "0:30 不算夜间");

// 跨零点：22 点 → 次日 7 点
Config.BotNightMinHour = 22;
Config.BotNightMaxHour = 7;

Check(BotRoster.IsNightHour(new DateTime(2026, 11, 12, 23, 0, 0)), "跨零点：23 点算夜间");
Check(BotRoster.IsNightHour(new DateTime(2026, 11, 12, 3, 0, 0)), "跨零点：3 点算夜间");
Check(!BotRoster.IsNightHour(new DateTime(2026, 11, 12, 12, 0, 0)), "跨零点：中午不算夜间");

// 起止相同 = 不启用
Config.BotNightMinHour = 5;
Config.BotNightMaxHour = 5;

Check(!BotRoster.IsNightHour(new DateTime(2026, 11, 12, 5, 0, 0)), "起止相同 → 视为不启用");

Console.WriteLine();
Console.WriteLine("榜首降速（P4-4）：");

// 手工搭一条排行榜链表：按等级从高到低。
// 不用 SEnvir.RankingSort（那个依赖完整的 DB 会话与里程碑写入）。
var ranked = new List<CharacterInfo>();

for (int i = 0; i < 5; i++)
{
    var ch = (CharacterInfo)RuntimeHelpers.GetUninitializedObject(typeof(CharacterInfo));
    ch.Level = 50 - i;
    ranked.Add(ch);
}

var list = new LinkedList<CharacterInfo>();

foreach (CharacterInfo ch in ranked)
    ch.RankingNode = list.AddLast(ch);

SEnvir.Rankings = list;

Check(BotPerception.IsTopRanked(ranked[0], 3), "第 1 名在前 3 里");
Check(BotPerception.IsTopRanked(ranked[2], 3), "第 3 名在前 3 里");
Check(!BotPerception.IsTopRanked(ranked[3], 3), "第 4 名不在前 3 里");
Check(BotPerception.IsTopRanked(ranked[4], 5), "第 5 名在前 5 里");
Check(!BotPerception.IsTopRanked(ranked[0], 0), "top=0 → 永远 false");

// RankingNode 为 null（刚建号 / 还没进榜）不能炸
var unranked = (CharacterInfo)RuntimeHelpers.GetUninitializedObject(typeof(CharacterInfo));
Check(!BotPerception.IsTopRanked(unranked, 3), "没进榜的角色 → false 不抛");
Check(!BotPerception.IsTopRanked(null!, 3), "null 角色 → false 不抛");

// 排行榜为空
SEnvir.Rankings = new LinkedList<CharacterInfo>();
Check(!BotPerception.IsTopRanked(ranked[0], 3), "排行榜为空 → false 不抛");

Console.WriteLine();
Console.WriteLine(failures == 0
    ? $"全部通过：{checks} 项"
    : $"失败 {failures} / {checks} 项");

return failures == 0 ? 0 : 1;



