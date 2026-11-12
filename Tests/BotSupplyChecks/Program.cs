using Library;
using Server.Bots;

// 假人补给/卖货的纯逻辑测试（2026-11-12，修「假人普遍超重」时新建）。
//
// 背景：11 倍爆率下假人见药就捡，实测有人囤 90 瓶红药（负重 155/107，药水占 92%）。
// 药原来一律不许卖 → 负重降不下来 → 新战利品捡不进来 → 没东西可卖，死锁。
//
// 这里钉的是两条纯规则：拾取时的囤积上限、卖货时的保留额度。
// 全部不碰数据库、不起服务器。

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

Console.WriteLine("—— 拾取囤积上限（PotionStockLimit）——");
Console.WriteLine();

Check(BotPerception.PotionStockLimit(20) == 40, "补给目标 20 瓶 → 最多囤 40 瓶");
Check(BotPerception.PotionStockLimit(10) == 20, "补给目标 10 瓶 → 最多囤 20 瓶");
Check(BotPerception.PotionStockLimit(1) == 2, "目标 1 → 上限 2（不能退化成 0）");
Check(BotPerception.PotionStockLimit(0) == int.MaxValue, "目标 0（关掉自动补给）→ 不限");
Check(BotPerception.PotionStockLimit(-5) == int.MaxValue, "负数配置 → 不限，不是负上限");

Console.WriteLine();
Console.WriteLine("—— 卖货保留额度（BotPotionBudget）——");
Console.WriteLine();

BotPotionBudget unlimited = BotPotionBudget.Unlimited();
Check(unlimited.Allow(Stat.Health, 100) && unlimited.Allow(Stat.Health, 100) && unlimited.Allow(Stat.Mana, 100),
    "不限额度：怎么问都放行");

BotPotionBudget byCount = new BotPotionBudget(true, red: 2, blue: 1, weightLeft: 1000);
Check(byCount.Allow(Stat.Health, 1), "红 1/2 放行");
Check(byCount.Allow(Stat.Health, 1), "红 2/2 放行");
Check(!byCount.Allow(Stat.Health, 1), "红额度用完后第 3 瓶可卖");
Check(byCount.Allow(Stat.Mana, 1), "蓝 1/1 放行");
Check(!byCount.Allow(Stat.Mana, 1), "蓝额度用完后第 2 瓶可卖");

// 低等级角色负重上限只有 58：光按瓶数拦不住，重量维度必须也能兜住。
BotPotionBudget byWeight = new BotPotionBudget(true, red: 99, blue: 99, weightLeft: 15);
Check(byWeight.Allow(Stat.Health, 10), "重量 10 ≤ 余额 15 → 放行");
Check(!byWeight.Allow(Stat.Health, 10), "只剩 5 余额，再要 10 → 可卖");
Check(byWeight.Allow(Stat.Health, 5), "余额正好 5 → 放行");
Check(!byWeight.Allow(Stat.Health, 1), "余额归零 → 可卖");

Console.WriteLine();
Console.WriteLine("—— 额度计算（For：未超重就不卖药）——");
Console.WriteLine();

// 负重 100/200 = 50%，远没到 90% —— 一瓶都不许卖，保持原来的保守行为。
BotPotionBudget relaxed = BotPotionBudget.For(bagWeight: 100, capacity: 200, warrior: false, redTarget: 20, blueTarget: 10);
Check(relaxed.Allow(Stat.Health, 999) && relaxed.Allow(Stat.Health, 999) && relaxed.Allow(Stat.Mana, 999),
    "负重 50% → 不限额度（药一瓶不卖）");

// 刚好 90% 就要开始限量，和「负重快满了回城卖货」的触发线对齐。
BotPotionBudget tight = BotPotionBudget.For(bagWeight: 180, capacity: 200, warrior: false, redTarget: 20, blueTarget: 10);
int keptRed = 0;
while (tight.Allow(Stat.Health, 1) && keptRed < 100) keptRed++;
Check(keptRed == 20, $"负重 90% → 红药只留 20 瓶（实际 {keptRed}）");

BotPotionBudget tightBlue = BotPotionBudget.For(bagWeight: 180, capacity: 200, warrior: false, redTarget: 20, blueTarget: 10);
int keptBlue = 0;
while (tightBlue.Allow(Stat.Mana, 1) && keptBlue < 100) keptBlue++;
Check(keptBlue == 10, $"负重 90% → 蓝药只留 10 瓶（实际 {keptBlue}）");

BotPotionBudget warrior = BotPotionBudget.For(bagWeight: 180, capacity: 200, warrior: true, redTarget: 20, blueTarget: 10);
Check(!warrior.Allow(Stat.Mana, 1), "战士超重时蓝药一瓶不留（本来就不喝）");

// 两个维度取先耗尽的：上限 58 → 重量额度 23，但红药瓶数额度只有 20 → 瓶数先耗尽。
BotPotionBudget smallCapacity = BotPotionBudget.For(bagWeight: 58, capacity: 58, warrior: false, redTarget: 20, blueTarget: 10);
int smallKept = 0;
while (smallCapacity.Allow(Stat.Health, 1) && smallKept < 100) smallKept++;
Check(smallKept == 20, $"负重上限 58 → 瓶数额度先耗尽，红药留 20 瓶（实际 {smallKept}）");

// 上限压到 40 → 重量额度 16 < 瓶数额度 20，这时**重量**才是卡点（低等级角色的真实处境）。
BotPotionBudget tinyCapacity = BotPotionBudget.For(bagWeight: 40, capacity: 40, warrior: false, redTarget: 20, blueTarget: 10);
int tinyKept = 0;
while (tinyCapacity.Allow(Stat.Health, 1) && tinyKept < 100) tinyKept++;
Check(tinyKept == 16, $"负重上限 40 → 重量额度 16 先耗尽，红药留 16 瓶（实际 {tinyKept}）");

Check(BotPotionBudget.For(bagWeight: 10, capacity: 0, warrior: false, redTarget: 20, blueTarget: 10)
        .Allow(Stat.Health, 999),
    "负重上限取不到（capacity=0）→ 不限，别把药全卖了");

Console.WriteLine();
Console.WriteLine("—— 卖不掉的标记（BotItemRules）——");
Console.WriteLine();

Check(BotItemRules.IsPotion(ItemType.Consumable, health: 50, mana: 0), "消耗品 + 回血 = 药");
Check(BotItemRules.IsPotion(ItemType.Consumable, health: 0, mana: 30), "消耗品 + 回蓝 = 药");
Check(!BotItemRules.IsPotion(ItemType.Weapon, health: 0, mana: 0), "武器不是药");
Check(!BotItemRules.IsPotion(ItemType.Consumable, health: 0, mana: 0), "不回血不回蓝的消耗品（卷轴/食物）不是药");

Check(!BotItemRules.IsSellBlocked(UserItemFlags.None), "干净物品可以卖");
Check(BotItemRules.IsSellBlocked(UserItemFlags.Locked), "Locked 不能卖（引擎 NPCSell 直接拒收）");
Check(BotItemRules.IsSellBlocked(UserItemFlags.Worthless), "Worthless 不能卖（价格 0）");
Check(BotItemRules.IsSellBlocked(UserItemFlags.Bound), "Bound 不能卖（绑定物品不该流回商店）");
Check(BotItemRules.IsSellBlocked(UserItemFlags.Marriage), "结婚戒指不能卖");
Check(!BotItemRules.IsSellBlocked(UserItemFlags.NonRefinable), "NonRefinable 不挡卖货（只管精炼）");

Check(BotItemRules.RepairPotionFlags(UserItemFlags.Locked) == UserItemFlags.None, "修标记：清 Locked");
Check(BotItemRules.RepairPotionFlags(UserItemFlags.Bound | UserItemFlags.Worthless) == UserItemFlags.None,
    "修标记：清 Bound|Worthless（启动资源发的药就是这个组合）");
Check(BotItemRules.RepairPotionFlags(UserItemFlags.Locked | UserItemFlags.NonRefinable) == UserItemFlags.NonRefinable,
    "修标记：只清卖不掉的三个，别的位保留");

Check(BotItemRules.NeedsManaPotions(MirClass.Warrior) == false, "战士不带蓝药（一辈子喝不掉，又卖不掉）");
Check(BotItemRules.NeedsManaPotions(MirClass.Wizard), "法师要蓝药");
Check(BotItemRules.NeedsManaPotions(MirClass.Taoist), "道士要蓝药");

Console.WriteLine();
Console.WriteLine("—— 装备耐久（BotItemRules）——");
Console.WriteLine();

Check(BotItemRules.NeedsRepair(current: 0, max: 1000, thresholdPercent: 15), "耐久正好 0 → 去修");
Check(BotItemRules.NeedsRepair(current: 4, max: 1000, thresholdPercent: 15), "剩 0.4%（界面显示 0%）→ 也要去修");
Check(BotItemRules.NeedsRepair(current: 100, max: 1000, thresholdPercent: 15), "剩 10% → 去修");
Check(BotItemRules.NeedsRepair(current: 150, max: 1000, thresholdPercent: 15), "正好 15% → 去修（边界含等于）");
Check(!BotItemRules.NeedsRepair(current: 151, max: 1000, thresholdPercent: 15), "剩 15.1% → 还不用修");
Check(!BotItemRules.NeedsRepair(current: 1000, max: 1000, thresholdPercent: 15), "满耐久 → 不修");
Check(!BotItemRules.NeedsRepair(current: 0, max: 0, thresholdPercent: 15), "耐久上限为 0（不耗耐久的装备）→ 不修");

Check(BotItemRules.DurabilityPercent(current: 4, max: 1000) == 0, "千分比换算：剩 0.4% 显示 0%");
Check(BotItemRules.DurabilityPercent(current: 999, max: 1000) == 99, "千分比换算：剩 99.9% 显示 99%");

// 挑「最该修」的装备要比**比例**：武器上限 20000 剩 3000（15%），戒指上限 5000 剩 800（16%）
Check(BotItemRules.DurabilityPermille(current: 3000, max: 20000) <
      BotItemRules.DurabilityPermille(current: 800, max: 5000),
    "武器 15% 比戒指 16% 更该修（按比例，不按绝对值）");

Console.WriteLine();
Console.WriteLine(failures == 0
    ? $"全部通过：{checks} 项"
    : $"失败 {failures} / {checks} 项");

return failures == 0 ? 0 : 1;
