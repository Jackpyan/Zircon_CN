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

// 2026-11-12：转生数据保留核对 + 两跳传送规划 + 刷怪价值判定。
//
// 为什么要有这个测试：
//   1) 「转生后装备 / 技能 / 仓库会不会丢」只能靠人肉盯 60 小时 —— 做成自动核对 + 断言。
//   2) 两跳传送（本图无直达标图 → 中转图 → 目标图）是纯规划逻辑，最适合离线验证。
//   3) 「本图还有怪就别急着跨图」依赖 HasHuntValue，判错了会让假人在图之间来回折返。
//
// 跑在一次性临时库上，不碰正式 Users.db。
string root = Path.Combine(Path.GetTempPath(), "ZirconRebirth-" + Guid.NewGuid().ToString("N"));

Session session = new(SessionMode.System | SessionMode.Users, root + Path.DirectorySeparatorChar);
session.Initialize(typeof(ItemInfo).Assembly, typeof(UserItem).Assembly);

foreach (FieldInfo field in typeof(SEnvir).GetFields(BindingFlags.Public | BindingFlags.Static))
    if (field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition() == typeof(DBCollection<>))
        field.SetValue(null, session.GetCollection(field.FieldType.GetGenericArguments()[0]));

SEnvir.Session = session;
SEnvir.Random = new Random(20261112);
SEnvir.Now = DateTime.Now;

Globals.CurrencyInfoList = SEnvir.CurrencyInfoList;

ConfigReader.ConfigObjects[typeof(ChineseMessages)] = new ChineseMessages();
ConfigReader.ConfigObjects[typeof(EnglishMessages)] = new EnglishMessages();

int checks = 0;
int failures = 0;

void Check(bool condition, string name, string? detail = null)
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

CharacterInfo MakeCharacter(AccountInfo account, string name, int level)
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
    character.Level = level;

    return character;
}

ItemInfo MakeItem(ItemType type, string name, int requiredAmount = 1)
{
    ItemInfo info = SEnvir.ItemInfoList.CreateNewObject();

    info.ItemName = name;
    info.ItemType = type;
    info.RequiredAmount = requiredAmount;
    info.RequiredClass = RequiredClass.All;
    info.RequiredGender = RequiredGender.None;
    info.Durability = 10000;

    return info;
}

MagicInfo MakeMagic(string name, int index)
{
    MagicInfo info = SEnvir.MagicInfoList.CreateNewObject();

    info.Magic = (MagicType)index;
    info.NeedLevel1 = 1;

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

    player.SeenByPlayers = new List<PlayerObject>();
    player.DataSeenByPlayers = new List<PlayerObject>();

    player.Stats[Stat.HandWeight] = 1000;
    player.Stats[Stat.WearWeight] = 1000;

    con.Player = player;

    return player;
}

UserItem MakeUserItem(CharacterInfo character, ItemInfo info, int slot, int count = 1)
{
    UserItem item = SEnvir.UserItemList.CreateNewObject();

    item.Info = info;
    item.Count = count;
    item.Slot = slot;
    item.CurrentDurability = info.Durability;
    item.MaxDurability = info.Durability;
    item.Character = character;

    return item;
}

// =====================================================================
// 1) 转生数据保留
// =====================================================================

Console.WriteLine();
Console.WriteLine("转生数据保留（装备 / 背包 / 仓库 / 技能 / 货币 / Buff）：");

AccountInfo account = MakeAccount("rebirth@test.local");
CharacterInfo character = MakeCharacter(account, "转生甲", 75);

ItemInfo weaponInfo = MakeItem(ItemType.Weapon, "炼狱", 40);
ItemInfo armourInfo = MakeItem(ItemType.Armour, "重盔甲", 40);
ItemInfo potionInfo = MakeItem(ItemType.Consumable, "金创药", 1);
ItemInfo oreInfo = MakeItem(ItemType.Ore, "黑铁矿石", 1);

PlayerObject player = MakePlayer(character);

// 装备 2 件
player.Equipment[(int)EquipmentSlot.Weapon] =
    MakeUserItem(character, weaponInfo, Globals.EquipmentOffSet + (int)EquipmentSlot.Weapon);
player.Equipment[(int)EquipmentSlot.Armour] =
    MakeUserItem(character, armourInfo, Globals.EquipmentOffSet + (int)EquipmentSlot.Armour);

// 背包 3 格（其中一格叠 20 瓶）
player.Inventory[0] = MakeUserItem(character, potionInfo, 0, 20);
player.Inventory[1] = MakeUserItem(character, potionInfo, 1, 20);
player.Inventory[2] = MakeUserItem(character, oreInfo, 2, 5);

// 仓库 2 格 + 材料仓 1 格
player.Storage[0] = MakeUserItem(character, oreInfo, Globals.PartsStorageOffset - 100);
player.Storage[1] = MakeUserItem(character, potionInfo, Globals.PartsStorageOffset - 99);
player.PartsStorage[0] = MakeUserItem(character, oreInfo, Globals.PartsStorageOffset);

// 技能 3 个
foreach (int index in new[] { 1, 2, 3 })
{
    UserMagic magic = session.GetCollection<UserMagic>().CreateNewObject();
    magic.Character = character;
    magic.Info = MakeMagic("技能" + index, index);
}

// 货币：金币 12345
CurrencyInfo? goldInfo = SEnvir.CurrencyInfoList.Binding.FirstOrDefault(x => x.Type == CurrencyType.Gold);

if (goldInfo == null)
{
    goldInfo = SEnvir.CurrencyInfoList.CreateNewObject();
    goldInfo.Type = CurrencyType.Gold;
    goldInfo.Name = "金币";
}

UserCurrency gold = session.GetCollection<UserCurrency>().CreateNewObject();
gold.Character = character;
gold.Info = goldInfo;
gold.Amount = 12345;

// Buff 1 个
BuffInfo buff = session.GetCollection<BuffInfo>().CreateNewObject();
buff.Character = character;
buff.Type = BuffType.Developer;
buff.RemainingTime = TimeSpan.FromMinutes(10);

// 属性点 + 隐士点（这两项**应该**被转生清零）
character.SpentPoints = 7;
character.HermitStats[Stat.MinDC] = 5;
character.HermitStats[Stat.MaxDC] = 5;

player.Level = 75;
player.Experience = 5000000m;

BotRebirthSnapshot before = BotRebirthAudit.Capture(player);

Check(before.Equipment == 2, $"转生前装备 2 件（{before.Equipment}）");
Check(before.Inventory == 3, $"转生前背包 3 格（{before.Inventory}）");
Check(before.InventoryStacks == 45, $"转生前背包共 45 件（{before.InventoryStacks}）");
Check(before.Storage == 2, $"转生前仓库 2 格（{before.Storage}）");
Check(before.PartsStorage == 1, $"转生前材料仓 1 格（{before.PartsStorage}）");
Check(before.Magics == 3, $"转生前技能 3 个（{before.Magics}）");
Check(before.Currencies == 1 && before.CurrencyTotal == 12345, $"转生前货币 1 种 / 12345（{before.CurrencyTotal}）");
// 注意：CharacterInfo 自带一个 HuntGold Buff，所以这里不写死 1，按实际条数断言。
int buffCount = character.Buffs.Count;

Check(before.Buffs == buffCount, $"转生前 Buff {buffCount} 个（{before.Buffs}）");
Check(before.SpentPoints == 7 && before.HermitStats == 2,
    $"转生前属性点 7 / 隐士点 2 项（{before.SpentPoints}/{before.HermitStats}）");

// 真正跑引擎的转生逻辑（不是模拟）
player.AdjustRebirth(1);

BotRebirthSnapshot after = BotRebirthAudit.Capture(player);

Console.WriteLine();
Console.WriteLine("转生后（引擎 AdjustRebirth 实跑）：");

Check(player.Level == 1, $"等级回到 1（{player.Level}）");
Check(player.Experience == 25000m, $"经验变成原来的 1/200（5000000 → {player.Experience}）");
Check(character.Rebirth == 1, $"转生次数 +1（{character.Rebirth}）");
Check(character.SpentPoints == 0, $"属性点清零（{character.SpentPoints}）");
Check(character.HermitStats.Values.Count == 0, $"隐士点清空（{character.HermitStats.Values.Count} 项）");

Check(after.Equipment == 2, $"装备一件没少（{after.Equipment}）");
Check(after.EquipmentSignature == before.EquipmentSignature, "装备内容指纹不变（没被换成别的）");
Check(player.Equipment[(int)EquipmentSlot.Weapon]?.Info == weaponInfo, "武器槽仍是原来那把");
Check(player.Equipment[(int)EquipmentSlot.Armour]?.Info == armourInfo, "衣服槽仍是原来那件");

Check(after.Inventory == 3, $"背包格数不变（{after.Inventory}）");
Check(after.InventoryStacks == 45, $"背包件数不变（{after.InventoryStacks}）");
Check(after.Storage == 2, $"仓库不变（{after.Storage}）");
Check(after.PartsStorage == 1, $"材料仓不变（{after.PartsStorage}）");

Check(after.Magics == 3, $"技能一个没少（{after.Magics}）");
Check(after.MagicSignature == before.MagicSignature, "技能内容指纹不变");

Check(after.Currencies == 1 && after.CurrencyTotal == 12345, $"金币一分没少（{after.CurrencyTotal}）");
Check(after.Buffs == buffCount, $"Buff 不变（{after.Buffs}）");

string diff = BotRebirthAudit.DescribeDiff(before, after);

Console.WriteLine($"       {diff}");

Check(BotRebirthAudit.IsDataPreserved(before, after), "核对结论：数据完整保留");
Check(diff.Contains("【正常】"), "核对文案含【正常】标记");

// 反向验证：核对器真的能发现丢失（否则「永远是正常」等于没核对）
Console.WriteLine();
Console.WriteLine("核对器自检（必须能发现丢失）：");

BotRebirthSnapshot lossBase = BotRebirthAudit.Capture(player);

player.Equipment[(int)EquipmentSlot.Weapon] = null;

string lossDiff = BotRebirthAudit.DescribeDiff(lossBase, BotRebirthAudit.Capture(player));

Check(!BotRebirthAudit.IsDataPreserved(lossBase, BotRebirthAudit.Capture(player)), "少一件装备时判为异常");
Check(lossDiff.Contains("装备 2→1"), $"异常文案点明装备数量（{lossDiff}）");

// 恢复
player.Equipment[(int)EquipmentSlot.Weapon] =
    MakeUserItem(character, weaponInfo, Globals.EquipmentOffSet + (int)EquipmentSlot.Weapon);

Check(BotRebirthAudit.IsDataPreserved(lossBase, BotRebirthAudit.Capture(player)), "补回装备后恢复为正常");

// =====================================================================
// 2) 两跳传送规划
// =====================================================================

Console.WriteLine();
Console.WriteLine("两跳传送规划（本图无直达标图 → 中转图 → 目标图）：");

MapInfo MakeMap(string description)
{
    MapInfo info = SEnvir.MapInfoList.CreateNewObject();

    // Index 由 DBCollection 分配，不能手写；这里只是给个可读的描述。
    info.Description = description;
    info.MinimumLevel = 1;

    return info;
}

MapRegion MakeRegion(MapInfo map, string description)
{
    MapRegion region = SEnvir.MapRegionList.CreateNewObject();

    region.Map = map;
    region.Description = description;
    region.PointList = new List<System.Drawing.Point> { new System.Drawing.Point(10, 10) };

    return region;
}

NPCPage MakePage(string description)
{
    NPCPage page = session.GetCollection<NPCPage>().CreateNewObject();

    page.Description = description;
    page.DialogType = NPCDialogType.None;

    return page;
}

// 造一颗「能直达 dest」的六合石
NPCInfo MakeStone(MapRegion region, string name, MapInfo dest, int cost)
{
    NPCInfo npc = SEnvir.NPCInfoList.CreateNewObject();

    npc.Region = region;
    npc.NPCName = name;

    NPCPage entry = MakePage(name + " 入口");
    NPCPage destPage = MakePage(name + " → " + dest.Description);

    NPCAction teleport = session.GetCollection<NPCAction>().CreateNewObject();
    teleport.Page = destPage;
    teleport.ActionType = NPCActionType.Teleport;
    teleport.MapParameter1 = dest;

    if (cost > 0)
    {
        NPCAction takeGold = session.GetCollection<NPCAction>().CreateNewObject();
        takeGold.Page = destPage;
        takeGold.ActionType = NPCActionType.TakeGold;
        takeGold.IntParameter1 = cost;
    }

    NPCButton button = session.GetCollection<NPCButton>().CreateNewObject();
    button.Page = entry;
    button.ButtonID = 1;
    button.DestinationPage = destPage;

    npc.EntryPage = entry;

    return npc;
}

MapInfo mapDesert = MakeMap("沙漠土城");
MapInfo mapNuoma = MakeMap("诺玛村落");
MapInfo mapPanye = MakeMap("潘夜村");
MapInfo mapParadise = MakeMap("失乐园");
MapInfo mapIsland = MakeMap("孤岛");

MapRegion regionDesert = MakeRegion(mapDesert, "沙漠土城");
MapRegion regionNuoma = MakeRegion(mapNuoma, "诺玛村落");
MapRegion regionPanye = MakeRegion(mapPanye, "潘夜村");
MapRegion regionParadise = MakeRegion(mapParadise, "失乐园");
MapRegion regionIsland = MakeRegion(mapIsland, "孤岛");

// 沙漠土城：只能到失乐园 / 诺玛村落（**没有潘夜村**，与实测一致）
MakeStone(regionDesert, "荒漠外传送员", mapParadise, 10000);
MakeStone(regionDesert, "荒漠外传送员2", mapNuoma, 3000);

// 诺玛村落：能到潘夜村
MakeStone(regionNuoma, "诺玛传送员", mapPanye, 3000);

// 潘夜村 / 失乐园 / 孤岛各自有石头（避免影响判定）
MakeStone(regionPanye, "潘夜传送员", mapParadise, 3000);
MakeStone(regionParadise, "失乐园传送员", mapDesert, 3000);
MakeStone(regionIsland, "孤岛传送员", mapIsland, 0);

var desertTargets = BotPerception.TeleportTargetsFrom(mapDesert);

Check(desertTargets.Contains(mapParadise.Index), "沙漠土城能直达失乐园");
Check(desertTargets.Contains(mapNuoma.Index), "沙漠土城能直达诺玛村落");
Check(!desertTargets.Contains(mapPanye.Index), "沙漠土城**不能**直达潘夜村（实测如此）");
Check(desertTargets.Count == 2, $"沙漠土城一共 2 个直达目标（{desertTargets.Count}）");

bool hasTwoHop = BotPerception.TryFindTwoHopRoute(mapDesert, mapPanye.Index, out int viaIndex);

Check(hasTwoHop, "沙漠土城 → 潘夜村 能规划出两跳");
Check(viaIndex == mapNuoma.Index, $"中转图是诺玛村落（{BotPerception.GetMapInfo(viaIndex)?.Description}）");

// 已经能直达 → 不该再给两跳
Check(!BotPerception.TryFindTwoHopRoute(mapDesert, mapParadise.Index, out _), "能直达时不返回两跳");

// 完全走不通 → 没有两跳
Check(!BotPerception.TryFindTwoHopRoute(mapIsland, mapPanye.Index, out _), "孤岛 → 潘夜村 无解（石头只到自己）");

// 中转图本身要能直达目标：诺玛村落确实能到潘夜村
Check(BotPerception.TeleportTargetsFrom(mapNuoma).Contains(mapPanye.Index), "诺玛村落能直达潘夜村");

// 票价：沙漠→诺玛 3000 + 诺玛→潘夜 3000
int cost1 = BotPerception.CheapestTeleportCostFrom(mapDesert, mapNuoma.Index);
int cost2 = BotPerception.CheapestTeleportCostFrom(mapNuoma, mapPanye.Index);

Check(cost1 == 3000, $"沙漠→诺玛 票价 3000（{cost1}）");
Check(cost2 == 3000, $"诺玛→潘夜 票价 3000（{cost2}）");
Check(BotPerception.CheapestTeleportCostFrom(mapDesert, mapPanye.Index) < 0, "沙漠→潘夜 没有直达票价（-1）");

// =====================================================================
// 3) 刷怪价值判定（本图还有怪就别急着跨图）
// =====================================================================

Console.WriteLine();
Console.WriteLine("刷怪价值判定：");

MonsterInfo MakeMonster(string name, int level)
{
    MonsterInfo info = SEnvir.MonsterInfoList.CreateNewObject();

    info.MonsterName = name;
    info.Level = level;

    return info;
}

RespawnInfo MakeRespawn(MapRegion region, MonsterInfo monster)
{
    RespawnInfo info = SEnvir.RespawnInfoList.CreateNewObject();

    info.Region = region;
    info.Monster = monster;

    return info;
}

MakeRespawn(regionDesert, MakeMonster("沙漠蝎子", 30));

Check(BotPerception.HasHuntValue(mapDesert, 30), "30 级假人在沙漠土城有怪可打");
Check(BotPerception.HasHuntValue(mapDesert, 34), "34 级仍在窗口内（怪 ≤ 自己+4）");
Check(!BotPerception.HasHuntValue(mapDesert, 40), "40 级时 30 级怪已低于自己-8，判为没价值");
Check(!BotPerception.HasHuntValue(mapNuoma, 30), "没有刷怪区的图判为没价值");
Check(!BotPerception.HasHuntValue(null, 30), "空地图不抛异常，返回 false");

// =====================================================================
// 4) 低等级换装（换装阈值不能写死 +50）
// =====================================================================

Console.WriteLine();
Console.WriteLine("低等级换装（botpeek 实测：转生 3 次的号仍穿木剑，背包里躺着学徒手刃）：");

ItemInfo MakeWeapon(string name, int requiredAmount, int minDC, int maxDC)
{
    ItemInfo info = MakeItem(ItemType.Weapon, name, requiredAmount);

    info.Stats[Stat.MinDC] = minDC;
    info.Stats[Stat.MaxDC] = maxDC;
    info.Weight = 5;

    return info;
}

// 评分 = Σ(属性 × 10) + RequiredAmount
ItemInfo woodSword = MakeWeapon("木剑", 1, 0, 2);      // 21
ItemInfo apprentice = MakeWeapon("学徒手刃", 3, 1, 4);  // 53
ItemInfo ironSword = MakeWeapon("铁剑", 2, 0, 2);       // 22（只高 1 分）

CharacterInfo gearChar = MakeCharacter(account, "换装甲", 12);
PlayerObject gearPlayer = MakePlayer(gearChar);

gearPlayer.Level = 12;

UserItem woodSwordItem =
    MakeUserItem(gearChar, woodSword, Globals.EquipmentOffSet + (int)EquipmentSlot.Weapon);
UserItem apprenticeItem = MakeUserItem(gearChar, apprentice, 0);
UserItem ironSwordItem = MakeUserItem(gearChar, ironSword, 1);

gearPlayer.Equipment[(int)EquipmentSlot.Weapon] = woodSwordItem;
gearPlayer.Inventory[0] = apprenticeItem;
gearPlayer.Inventory[1] = ironSwordItem;

int apprenticeScore = BotBrain.EquipmentScore(apprenticeItem);
int woodScore = BotBrain.EquipmentScore(woodSwordItem);

Check(apprenticeScore > woodScore && apprenticeScore <= woodScore + 50,
    $"学徒手刃比木剑高 {apprenticeScore - woodScore} 分 —— 旧写死的 +50 阈值正好把它挡在门外");

int upgradeSlot = BotBrain.FindBetterEquipment(gearPlayer);

Check(upgradeSlot == 0, $"捡到明显更好的武器会换上（挑中背包第 {upgradeSlot} 格）");

gearPlayer.Inventory[0] = null;

Check(BotBrain.FindBetterEquipment(gearPlayer) == -1, "只略好一点（+1 分）不换，避免来回折腾");

// 上面这条同时说明了旧实现的死区：学徒手刃高出 32 分，但旧阈值要 +50，
// 于是低等级假人永远换不了武器。新阈值是 max(5, 当前分/20)，低等级取 5。

// =====================================================================
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

// RefreshStats 在测试里被跳过：它要读全服 BaseStat / 地图 / 客户端连接，
// 与本测试的目标（「转生有没有动物品集合」）无关；
// 生产环境的真实 RefreshStats 由假人转生时的运行期核对日志覆盖。
sealed class CheckPlayer : PlayerObject
{
    private CheckPlayer() : base(null, null) { }
    public override void RefreshStats() { }
}
