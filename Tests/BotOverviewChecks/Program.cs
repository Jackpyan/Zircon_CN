using Library;
using Server.Bots;

// P5-1 / P4-1 纯函数测试：后台总览聚合 + 金币采样。
//
// 这两块是「后台页面数字的来源」，一旦算错，页面会一本正经地显示错数，
// 而且没有任何报错可查 —— 所以单独钉死。全部不碰数据库、不起服务器。

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

BotSnapshot Bot(int seq, bool online, int level, int kills, int deaths, long gold,
    int bagWeight = 0, int maxBagWeight = 0, long goldEarned = 0, long goldPerHour = 0)
{
    return new BotSnapshot
    {
        Seq = seq,
        IsOnline = online,
        Level = level,
        Kills = kills,
        Deaths = deaths,
        Gold = gold,
        BagWeight = bagWeight,
        MaxBagWeight = maxBagWeight,
        GoldEarned = goldEarned,
        GoldPerHour = goldPerHour,
    };
}

Console.WriteLine("总览聚合（P5-1）：");

BotTotals empty = BotRoster.BuildTotals(Array.Empty<BotSnapshot>());

Check(empty.Online == 0 && empty.Total == 0, "空列表 → 在线 0 / 总数 0");
Check(empty.SurvivalRate == -1, "空列表 → 存活率 -1（页面显示 —）");
Check(empty.AverageLevel == 0, "空列表 → 平均等级 0，不除零");

var mixed = new[]
{
    Bot(1, true, 20, 100, 0, 1000),
    Bot(2, true, 21, 0, 100, 2000),
    Bot(3, false, 99, 500, 500, 9000),   // 离线：不参与等级/存活率
};

BotTotals overview = BotRoster.BuildTotals(mixed);

Check(overview.Total == 3, "总数含离线");
Check(overview.Online == 2, "在线只数 IsOnline");
Check(overview.AverageLevel == 20.5, "平均等级只算在线（20.5）");
Check(overview.TotalKills == 100, "击杀只算在线");
Check(overview.TotalDeaths == 100, "死亡只算在线");
Check(overview.SurvivalRate == 50, "存活率 = 100/(100+100) = 50%");

BotTotals noSample = BotRoster.BuildTotals(new[] { Bot(1, true, 10, 0, 0, 0) });

Check(noSample.SurvivalRate == -1, "没有击杀也没有死亡 → 存活率 -1，不是 0%");

BotTotals rounded = BotRoster.BuildTotals(new[]
{
    Bot(1, true, 10, 0, 0, 0),
    Bot(2, true, 11, 0, 0, 0),
    Bot(3, true, 11, 0, 0, 0),
});

Check(rounded.AverageLevel == 10.7, "平均等级四舍五入到 1 位（10.67 → 10.7）");

Console.WriteLine();
Console.WriteLine("负重阈值（P5-1）：");

BotTotals weight = BotRoster.BuildTotals(new[]
{
    Bot(1, true, 10, 0, 0, 0, bagWeight: 100, maxBagWeight: 100), // 100% → 超重
    Bot(2, true, 10, 0, 0, 0, bagWeight: 120, maxBagWeight: 100), // 120% → 超重
    Bot(3, true, 10, 0, 0, 0, bagWeight: 85, maxBagWeight: 100),  // 85%  → 临界
    Bot(4, true, 10, 0, 0, 0, bagWeight: 99, maxBagWeight: 100),  // 99%  → 临界
    Bot(5, true, 10, 0, 0, 0, bagWeight: 84, maxBagWeight: 100),  // 84%  → 正常
    Bot(6, true, 10, 0, 0, 0, bagWeight: 50, maxBagWeight: 0),    // 上限未知 → 不计
});

Check(weight.Overweight == 2, "负重 ≥100% 记 2 人");
Check(weight.NearOverweight == 2, "负重 85~99% 记 2 人");
Check(weight.Overweight + weight.NearOverweight == 4, "84% 与「上限未知」都不计入");

Check(Bot(1, true, 1, 0, 0, 0, bagWeight: 50, maxBagWeight: 0).BagWeightPercent == 0,
    "MaxBagWeight=0 → 百分比 0，不除零");

Console.WriteLine();
Console.WriteLine("经济观测（P4-1）：");

BotTotals economy = BotRoster.BuildTotals(new[]
{
    Bot(1, true, 10, 0, 0, 1000, goldEarned: 500, goldPerHour: 1800),
    Bot(2, true, 10, 0, 0, 2000, goldEarned: 300, goldPerHour: 900),
});

Check(economy.TotalGold == 3000, "总金币 = 各人存量之和");
Check(economy.GoldEarned == 800, "赚取合计 = 各人本次在线之和");
Check(economy.GoldPerHour == 2700, "金币/小时合计 = 各人之和");

var session = new BotSession(new BotProfile { Seq = 1 });

session.SampleGold(1000);
Check(session.GoldEarned == 0, "第一次采样只记基线，不累加");

session.SampleGold(1500);
Check(session.GoldEarned == 500, "涨了 500 → 记 500");

session.SampleGold(1200);
Check(session.GoldEarned == 500, "买药花了 300 → 不算负产出，仍 500");

session.SampleGold(1300);
Check(session.GoldEarned == 600, "再涨 100 → 600");

session.SampleGold(1300);
Check(session.GoldEarned == 600, "金币没变 → 不累加");

Console.WriteLine();
Console.WriteLine("—— 文本本地化（BotText）——");
Console.WriteLine();

// 后台页面曾经漏出 ExplosiveTalisman / FireBall / Meat 这种内部枚举名，
// 中文界面里混英文很难看，所以把翻译钉死。
Check(BotText.Item(ItemType.Meat) == "肉类", "ItemType.Meat → 肉类（读 [Description]）");
Check(BotText.Item(ItemType.Ore) == "矿石", "ItemType.Ore → 矿石");
Check(BotText.Item(ItemType.Nothing) == "无", "Nothing 没有 Description → 显示「无」而不是英文");

// 新增 ItemType 时忘了加 [Description] 会在这里炸出来 —— 比在界面上发现英文早得多。
List<ItemType> missingItemNames = Enum.GetValues<ItemType>()
    .Where(type => BotText.Item(type) == type.ToString())
    .ToList();
Check(missingItemNames.Count == 0,
    $"所有 ItemType 都有中文名（缺失：{(missingItemNames.Count == 0 ? "无" : string.Join("、", missingItemNames))}）");

Check(BotText.Magic(MagicType.None) == "无", "MagicType.None → 无");

// 测试进程没有 SEnvir.MagicInfoList（没起服），技能名查不到时要原样返回枚举名，
// 绝不能变成空串 —— 页面上留个英文名还能查，留空白就完全不知道发生了什么。
string fireBall = BotText.Magic(MagicType.FireBall);
Check(fireBall == "FireBall", $"查不到技能表时回退枚举名（得到 {fireBall}）");

Console.WriteLine();
Console.WriteLine(failures == 0
    ? $"全部通过：{checks} 项"
    : $"失败 {failures} / {checks} 项");

return failures == 0 ? 0 : 1;
