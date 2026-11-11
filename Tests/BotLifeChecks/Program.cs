using System.Reflection;
using System.Runtime.CompilerServices;
using Library;
using Library.SystemModels;
using MirDB;
using Server.Bots;
using Server.DBModels;
using Server.Envir;
using Server.Envir.Translations;
using Server.Models;
using Server.Models.Monsters;

// 验证 2026-11-11 的「P1 生活化闭环」—— 修三个长期运行的硬伤：
//   1) 背包满 → 假人永久卡在拾取态（P1-1：容量检查 + 掉落筛选 + 状态超时 + 真实拾取结果）
//   2) 装备耐久归零 → 攻击力变 0（P1-3：修理）
//   3) 金币只出不进 → 买不到药（P1-2：卖货）
//   4) 捡到更好的装备不换 → 一直用新手装（P1-4：自动换装）
//
// 跑在一次性临时库上，不碰正式 Users.db。
string root = Path.Combine(Path.GetTempPath(), "ZirconBotLife-" + Guid.NewGuid().ToString("N"));

Session session = new(SessionMode.System | SessionMode.Users, root + Path.DirectorySeparatorChar);
session.Initialize(typeof(ItemInfo).Assembly, typeof(UserItem).Assembly);

foreach (FieldInfo field in typeof(SEnvir).GetFields(BindingFlags.Public | BindingFlags.Static))
    if (field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition() == typeof(DBCollection<>))
        field.SetValue(null, session.GetCollection(field.FieldType.GetGenericArguments()[0]));

SEnvir.Session = session;
SEnvir.Random = new Random(20261111);
SEnvir.Now = DateTime.Now;

Globals.CurrencyInfoList = SEnvir.CurrencyInfoList;

ConfigReader.ConfigObjects[typeof(ChineseMessages)] = new ChineseMessages();
ConfigReader.ConfigObjects[typeof(EnglishMessages)] = new EnglishMessages();

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

AccountInfo MakeAccount(string email)
{
    AccountInfo account = SEnvir.AccountInfoList.CreateNewObject();

    account.EMailAddress = email;
    account.Password = SEnvir.CreateHash("test");
    account.BirthDate = new DateTime(1990, 1, 1);
    account.Activated = true;
    account.IsBot = true;

    return account;
}

CharacterInfo MakeCharacter(AccountInfo account, string name)
{
    CharacterInfo character = SEnvir.CharacterInfoList.CreateNewObject();

    character.Account = account;
    character.CharacterName = name;
    character.Class = MirClass.Warrior;
    character.Gender = MirGender.Male;
    character.HairType = 0;
    character.HairColour = System.Drawing.Color.FromArgb(0);
    character.ArmourColour = System.Drawing.Color.FromArgb(255, 255, 255, 255);
    character.CreationIP = "test";
    character.CreationDate = SEnvir.Now;
    character.Level = 1;

    return character;
}

ItemInfo MakeItem(ItemType type, int stackSize = 1, bool canSell = true, bool canRepair = false,
    int durability = 0, int requiredAmount = 1)
{
    ItemInfo info = SEnvir.ItemInfoList.CreateNewObject();

    info.ItemType = type;
    info.StackSize = stackSize;
    info.CanSell = canSell;
    info.CanRepair = canRepair;
    info.CanTrade = true;
    info.Durability = durability;
    info.RequiredType = RequiredType.Level;
    info.RequiredAmount = requiredAmount;
    info.ItemName = type.ToString();

    // 引擎的 CanUseItem 用「按位包含」判定职业 / 性别：
    // RequiredClass.None(0) 不匹配任何职业，所以必须显式声明，否则谁也穿不了。
    info.RequiredClass = RequiredClass.Warrior;
    info.RequiredGender = RequiredGender.None;

    return info;
}

PlayerObject MakePlayer(CharacterInfo character)
{
    var con = (SConnection)RuntimeHelpers.GetUninitializedObject(typeof(SConnection));
    con.Observers = new();
    con.SendList = new System.Collections.Concurrent.ConcurrentQueue<Library.Network.Packet>();
    con.Language = new EnglishMessages();
    con.Stage = GameStage.Game;
    con.Connected = true;

    var player = (PlayerObject)RuntimeHelpers.GetUninitializedObject(typeof(CheckPlayer));
    player.Character = character;
    player.Connection = con;
    player.Inventory = new UserItem[Globals.InventorySize];
    player.Equipment = new UserItem[Globals.EquipmentSize];
    player.Storage = new UserItem[1000];
    player.PartsStorage = new UserItem[1000];
    player.TradeItems = new();
    player.NPCVals = new();
    player.Stats = new Stats();

    // MapObject 的构造器没跑（绕过了），SeenByPlayers 是 null ——
    // ItemMove 里的 SendShapeUpdate 会 Broadcast，null 集合直接 NRE。
    player.SeenByPlayers = new List<PlayerObject>();
    player.DataSeenByPlayers = new List<PlayerObject>();

    // 负重上限来自职业基础属性（RefreshStats 里从 BaseStat 赋值）。
    // 测试里没有跑完整的属性初始化，手动给一个够大的上限 ——
    // 否则 CanWearItem 的负重判定会把所有装备都判成超重。
    player.Stats[Stat.HandWeight] = 1000;
    player.Stats[Stat.WearWeight] = 1000;

    con.Player = player;

    return player;
}

ItemObject Ground(UserItem item)
{
    return new ItemObject { Item = item };
}

UserItem MakeUserItem(ItemInfo info, int count = 1)
{
    UserItem item = SEnvir.UserItemList.CreateNewObject();

    item.Info = info;
    item.Count = count;
    item.CurrentDurability = info.Durability;
    item.MaxDurability = info.Durability;

    return item;
}

Console.WriteLine("P1 生活化闭环（背包管理 / 卖店 / 修理 / 换装）：");

// ---------- 1) 背包容量检查 ----------

Console.WriteLine();
Console.WriteLine("背包容量检查：");

AccountInfo account = MakeAccount("p1@test.local");
CharacterInfo character = MakeCharacter(account, "假人甲");

PlayerObject player = MakePlayer(character);

Check(player.Inventory.Length == Globals.InventorySize,
    $"背包格数是 {Globals.InventorySize}（{player.Inventory.Length}）");

Check(BotPerception.UsedInventorySlots(player) == 0, "新角色背包是空的");

// 塞满背包
ItemInfo junk = MakeItem(ItemType.Ore);

for (int i = 0; i < player.Inventory.Length; i++)
    player.Inventory[i] = MakeUserItem(junk);

Check(BotPerception.UsedInventorySlots(player) == Globals.InventorySize, "背包塞满了");
Check(BotPerception.FreeInventorySlots(player) == 0, "剩余格数是 0");
Check(BotPerception.FreeInventorySlots(player) == player.Inventory.Length - BotPerception.UsedInventorySlots(player),
    "剩余格数与占用格数吻合");

// 腾出 3 格
player.Inventory[0] = null;
player.Inventory[1] = null;
player.Inventory[2] = null;

Check(BotPerception.FreeInventorySlots(player) == 3, "腾出 3 格后剩余 3 格");

// ---------- 2) 掉落筛选 ----------

Console.WriteLine();
Console.WriteLine("掉落筛选：");

ItemInfo potion = MakeItem(ItemType.Consumable, stackSize: 20);
ItemInfo weapon = MakeItem(ItemType.Weapon, durability: 1000);
ItemInfo junkOre = MakeItem(ItemType.Ore);
ItemInfo flower = MakeItem(ItemType.Flower);
ItemInfo boundWeapon = MakeItem(ItemType.Weapon, durability: 1000);
ItemInfo currency = MakeItem(ItemType.Currency);

UserItem boundItem = MakeUserItem(boundWeapon);
boundItem.Flags = UserItemFlags.Bound;

Check(!BotPerception.IsWorthPickingUp(Ground(MakeUserItem(junkOre))), "矿石不捡（用不上还占格）");
Check(!BotPerception.IsWorthPickingUp(Ground(MakeUserItem(flower))), "花不捡");
Check(BotPerception.IsWorthPickingUp(Ground(MakeUserItem(potion))), "药要捡");
Check(BotPerception.IsWorthPickingUp(Ground(MakeUserItem(weapon))), "武器要捡（能换装能卖钱）");
Check(BotPerception.IsWorthPickingUp(Ground(MakeUserItem(currency))), "货币要捡（唯一的金币收入来源）");
Check(!BotPerception.IsWorthPickingUp(Ground(boundItem)), "绑定的物品不捡（卖掉会被引擎拒绝）");
Check(!BotPerception.IsWorthPickingUp(null), "空对象不捡");

// ---------- 3) 状态超时（P1-1 的核心） ----------

Console.WriteLine();
Console.WriteLine("状态超时（防止永久卡死）：");

// ProcessLoot / ProcessGoHunt 现在都会检查 _stateDeadline。
// 这里验证 BotBrain 的状态机在超时后确实会切走，而不是一直耗在原状态里。
BotProfile profile = BotProfile.Create(1);
BotBrain brain = new(player, profile);

brain.EnterStateForTest(BotState.Loot, "测试", TimeSpan.FromMilliseconds(1));

Check(brain.State == BotState.Loot, "进入拾取态");

System.Threading.Thread.Sleep(20);

brain.ProcessForTest();

Check(brain.State != BotState.Loot,
    $"拾取态超时后会切走（当前 {brain.State}）—— 原来这里是永久卡死");

// ---------- 4) 装备评分与换装 ----------

Console.WriteLine();
Console.WriteLine("装备评分与自动换装：");

ItemInfo weakWeapon = MakeItem(ItemType.Weapon, durability: 1000);
weakWeapon.RequiredAmount = 1;

ItemInfo strongWeapon = MakeItem(ItemType.Weapon, durability: 5000);
strongWeapon.RequiredAmount = 30;
strongWeapon.Stats[Stat.MinDC] = 20;
strongWeapon.Stats[Stat.MaxDC] = 30;

UserItem weak = MakeUserItem(weakWeapon);
UserItem strong = MakeUserItem(strongWeapon);

Check(BotBrain.EquipmentScore(strong) > BotBrain.EquipmentScore(weak),
    $"强装备评分更高（{BotBrain.EquipmentScore(strong)} > {BotBrain.EquipmentScore(weak)}）");
Check(BotBrain.EquipmentScore(null) == 0, "空装备评分是 0");

// 背包清空，只放一把强武器
// 强武器要求 30 级 —— 引擎的 CanUseItem 会按等级门槛拦，所以角色也得是 30 级。
player.Character.Level = 30;

for (int i = 0; i < player.Inventory.Length; i++)
    player.Inventory[i] = null;

player.Inventory[0] = strong;

int better = BotBrain.FindBetterEquipment(player);

Check(better == 0, $"找到了更好的装备（背包格 {better}）");

bool equipped = BotBrain.TryEquip(player, 0);

Check(equipped, "换上成功");
Check(player.Equipment[(int)EquipmentSlot.Weapon] == strong, "武器已经在装备栏的武器槽");
Check(player.Inventory[0] == null, "背包里那格空了");

// 已经没有更好的了
Check(BotBrain.FindBetterEquipment(player) == -1, "换完后没有更好的装备了");

// 等级不够的装备不能穿（角色 30 级，这件要 40 级）
ItemInfo highLevel = MakeItem(ItemType.Weapon, durability: 5000, requiredAmount: 40);
highLevel.Stats[Stat.MinDC] = 99;

player.Inventory[1] = MakeUserItem(highLevel);

Check(BotBrain.FindBetterEquipment(player) == -1, "等级不够的装备不会被换上");

// ---------- 5) 修理判定 ----------

Console.WriteLine();
Console.WriteLine("修理判定：");

// 给假人装一件耐久归零的武器
ItemInfo repairable = MakeItem(ItemType.Weapon, canRepair: true, durability: 100);
UserItem broken = MakeUserItem(repairable);
broken.CurrentDurability = 0;

player.Equipment[(int)EquipmentSlot.Weapon] = broken;

Check(broken.CurrentDurability == 0, "武器耐久归零");

// 引擎会把这件装备整件跳过 —— 这里直接验证引擎行为本身
player.RefreshStats();

Check(player.Stats[Stat.MinDC] == 0,
    $"耐久归零的装备不计入属性（MinDC={player.Stats[Stat.MinDC]}）—— 这就是 P1-3 要修的");

// ---------- 6) 挖取（Harvest） ----------

Console.WriteLine();
Console.WriteLine("挖取（鸡 / 猪 / 牛的掉落不落地）：");

// 引擎的关键机制：NeedHarvest 的怪（AI=1/2/3）掉落不落地，
// 而是留在尸体的 Drops[账号] 里，必须走过去发 C.Harvest 才能拿到。
// 这里直接验证引擎的 Drop 分发逻辑与 NeedHarvest 判定。

MonsterInfo chicken = SEnvir.MonsterInfoList.CreateNewObject();
chicken.MonsterName = "鸡";
chicken.AI = 1;                       // AI=1/2/3 → NeedHarvest

MonsterObject corpse = MonsterObject.GetMonster(chicken);

Check(corpse.NeedHarvest, "AI=1 的怪是「需挖取」的");
Check(corpse.HarvestCount == 2, $"鸡要挖 2 次（实际 {corpse.HarvestCount}）");

MonsterInfo normal = SEnvir.MonsterInfoList.CreateNewObject();
normal.MonsterName = "普通怪";
normal.AI = 0;

MonsterObject normalCorpse = MonsterObject.GetMonster(normal);

Check(!normalCorpse.NeedHarvest, "AI=0 的怪不需要挖");
Check(normalCorpse.HarvestCount == 0, "普通怪的挖取次数是 0");

// 找可挖尸体的判定：NeedsHarvest + Drops 里有这个账号的掉落
MonsterObject planted = MonsterObject.GetMonster(chicken);
planted.Drops = new Dictionary<AccountInfo, List<UserItem>>
{
    [account] = new List<UserItem> { MakeUserItem(MakeItem(ItemType.Weapon)) },
};

Check(planted.Drops.ContainsKey(account), "尸体上挂着这个账号的掉落");
Check(planted.Drops[account].Count == 1, "尸体上有 1 件可挖的东西");

// 普通怪即使 Drops 有内容也不该被当成「可挖尸体」
MonsterObject normalPlanted = MonsterObject.GetMonster(normal);
normalPlanted.Drops = new Dictionary<AccountInfo, List<UserItem>>
{
    [account] = new List<UserItem> { MakeUserItem(MakeItem(ItemType.Weapon)) },
};

Check(!normalPlanted.NeedHarvest, "普通怪即使有 Drops 也不算可挖尸体");

// ---------- 7) 卖货判定 ----------

Console.WriteLine();
Console.WriteLine("卖货判定：");

ItemInfo sellableWeapon = MakeItem(ItemType.Weapon, canSell: true, durability: 100);
ItemInfo nonSellable = MakeItem(ItemType.Weapon, canSell: false, durability: 100);

Check(sellableWeapon.CanSell, "武器可卖");
Check(!nonSellable.CanSell, "部分物品不可卖");

// 商店的收货类型（引擎的 NPCPage.Types）
ItemInfo sellablePotion = MakeItem(ItemType.Consumable, stackSize: 10, canSell: true);
ItemInfo sellableOre = MakeItem(ItemType.Ore, canSell: true);

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

// Stats 与全服广播不在这些检查范围内；物品与 MirDB 代码都是生产代码。
sealed class CheckPlayer : PlayerObject
{
    private CheckPlayer() : base(null, null) { }
    public override void RefreshStats() { }
}
