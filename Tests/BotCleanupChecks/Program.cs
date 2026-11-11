using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Library;
using Library.Network;
using Library.SystemModels;
using MirDB;
using Server.Bots;
using Server.DBModels;
using Server.Envir;
using Server.Envir.Translations;

// 验证 2026-11-11 的「清理假人」：
//   1) 预览只统计假人，真人不计入
//   2) 清理后假人账号 / 角色 / 货币 / 物品全部消失
//   3) 真人账号 / 角色 / 货币 / 物品一个都不受影响
//   4) 库里不留孤儿行（没有角色的物品 / 货币）
//
// 跑在一次性临时库上，不碰正式 Users.db。
string root = Path.Combine(Path.GetTempPath(), "ZirconBotCleanup-" + Guid.NewGuid().ToString("N"));

Session session = new(SessionMode.System | SessionMode.Users, root + Path.DirectorySeparatorChar);
session.Initialize(typeof(ItemInfo).Assembly, typeof(UserItem).Assembly);

foreach (FieldInfo field in typeof(SEnvir).GetFields(BindingFlags.Public | BindingFlags.Static))
    if (field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition() == typeof(DBCollection<>))
        field.SetValue(null, session.GetCollection(field.FieldType.GetGenericArguments()[0]));

SEnvir.Session = session;
SEnvir.Random = new Random(20261112);
Globals.CurrencyInfoList = SEnvir.CurrencyInfoList;

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

Console.WriteLine("清理假人（只删假人，不碰真人）：");

// ---------- 准备系统数据 ----------

ItemInfo goldItem = SEnvir.ItemInfoList.CreateNewObject();
goldItem.ItemType = ItemType.Nothing;
goldItem.StackSize = 1;

ItemInfo sword = SEnvir.ItemInfoList.CreateNewObject();
sword.ItemType = ItemType.Weapon;
sword.StackSize = 1;

CurrencyInfo goldCurrency = SEnvir.CurrencyInfoList.CreateNewObject();
goldCurrency.Type = CurrencyType.Gold;
goldCurrency.DropItem = goldItem;

// ---------- 造一个带角色 / 货币 / 物品的账号 ----------

CharacterInfo MakeCharacter(AccountInfo account, string name, MirClass cls, out UserItem item, out UserCurrency currency)
{
    CharacterInfo character = SEnvir.CharacterInfoList.CreateNewObject();

    character.Account = account;
    character.CharacterName = name;
    character.Class = cls;
    character.Gender = MirGender.Male;
    character.HairType = 0;
    character.HairColour = System.Drawing.Color.FromArgb(0);
    character.ArmourColour = System.Drawing.Color.FromArgb(255, 255, 255, 255);
    character.CreationIP = "test";
    character.CreationDate = SEnvir.Now;
    character.Level = 12;
    character.BotKills = 345;
    character.BotOnlineSeconds = 6789;

    item = SEnvir.UserItemList.CreateNewObject();
    item.Info = sword;
    item.Character = character;
    item.Count = 3;

    currency = SEnvir.UserCurrencyList.CreateNewObject();
    currency.Character = character;
    currency.Info = goldCurrency;
    currency.Amount = 5000;

    return character;
}

AccountInfo MakeAccount(string email, bool isBot)
{
    AccountInfo account = SEnvir.AccountInfoList.CreateNewObject();

    account.EMailAddress = email;
    account.Password = SEnvir.CreateHash("Pass123456");
    account.BirthDate = new DateTime(1990, 1, 1);
    account.CreationIP = "test";
    account.CreationDate = SEnvir.Now;
    account.Activated = true;
    account.ActivationTime = SEnvir.Now;
    account.IsBot = isBot;

    return account;
}

AccountInfo botAccount = MakeAccount("hnt01@mir3.local", true);
AccountInfo realAccount = MakeAccount("player01@mir3.local", false);

CharacterInfo botCharacter = MakeCharacter(botAccount, "夜行人", MirClass.Wizard, out UserItem botItem, out UserCurrency botCurrency);
CharacterInfo realCharacter = MakeCharacter(realAccount, "张三", MirClass.Warrior, out UserItem realItem, out UserCurrency realCurrency);

int totalAccounts = SEnvir.AccountInfoList.Count;
int totalCharacters = SEnvir.CharacterInfoList.Count;
int totalItems = SEnvir.UserItemList.Count;
int totalCurrencies = SEnvir.UserCurrencyList.Count;

// 角色级货币是自动补齐的（CharacterInfo.OnCreated 会按系统货币表建一份），
// 所以用实际数量做断言，不写死 1。
int botCurrencyCount = botCharacter.Currencies.Count;
int realCurrencyCount = realCharacter.Currencies.Count;

Check(totalAccounts == 2 && totalCharacters == 2 && totalItems == 2,
    $"准备就绪：{totalAccounts} 账号 / {totalCharacters} 角色 / {totalItems} 物品 / {totalCurrencies} 货币");
Check(botCurrencyCount >= 1 && realCurrencyCount >= 1,
    $"每个角色都自动带上了角色级货币（假人 {botCurrencyCount} 条 / 真人 {realCurrencyCount} 条）");

// ---------- 1) 预览只统计假人 ----------

BotCleanupStats preview = AccountCleanup.PreviewBots();

Check(preview.Accounts == 1, $"预览只统计到 1 个假人账号（实际 {preview.Accounts}）");
Check(preview.Characters == 1, $"预览统计到 1 个假人角色（实际 {preview.Characters}）");
Check(preview.Items == 1, $"预览统计到 1 件假人物品（实际 {preview.Items}）");
Check(preview.Currencies == botCurrencyCount, $"预览统计到 {botCurrencyCount} 条假人货币（实际 {preview.Currencies}）");

// 预览不能动数据
Check(SEnvir.AccountInfoList.Count == totalAccounts && SEnvir.UserItemList.Count == totalItems,
    "预览是只读的，没有删任何东西");

// ---------- 2) 清理 ----------

BotCleanupStats result = AccountCleanup.DeleteBots();

Check(result.Accounts == 1, $"清理了 1 个假人账号（实际 {result.Accounts}）");
Check(result.Characters == 1, $"清理了 1 个假人角色（实际 {result.Characters}）");

// ---------- 3) 假人数据全部消失 ----------

Check(!SEnvir.AccountInfoList.Binding.Any(x => x.IsBot), "假人账号已全部删除");
Check(!SEnvir.CharacterInfoList.Binding.Any(x => x.CharacterName == "夜行人"), "假人角色已删除");
Check(!SEnvir.UserItemList.Binding.Any(x => x.Index == botItem.Index), "假人的物品已删除");
Check(!SEnvir.UserCurrencyList.Binding.Any(x => x.Index == botCurrency.Index), "假人的角色级货币已删除");

// ---------- 4) 真人数据一个都没动 ----------

Check(SEnvir.AccountInfoList.Binding.Any(x => x.EMailAddress == "player01@mir3.local"), "真人账号还在");
Check(SEnvir.CharacterInfoList.Binding.Any(x => x.CharacterName == "张三"), "真人角色还在");
Check(SEnvir.UserItemList.Binding.Any(x => x.Index == realItem.Index), "真人的物品还在");
Check(SEnvir.UserCurrencyList.Binding.Any(x => x.Index == realCurrency.Index), "真人的角色级货币还在");

Check(realCharacter.Level == 12 && realCharacter.BotKills == 345 && realCharacter.BotOnlineSeconds == 6789,
    "真人角色的字段完全没被改动");

// ---------- 5) 不留孤儿行 ----------

Check(!SEnvir.UserItemList.Binding.Any(x => x.Character == null && x.Guild == null && x.Mail == null),
    "没有留下无主的物品（孤儿行）");

Check(SEnvir.UserItemList.Count == totalItems - 1, "物品总数只少了假人的那一件");
Check(SEnvir.UserCurrencyList.Count == totalCurrencies - botCurrencyCount,
    $"货币总数只少了假人角色的 {botCurrencyCount} 条");
Check(SEnvir.AccountInfoList.Count == totalAccounts - 1, "账号总数只少了假人的那一个");
Check(SEnvir.CharacterInfoList.Count == totalCharacters - 1, "角色总数只少了假人的那一个");

// ---------- 6) 在线假人不会被误删 ----------

AccountInfo onlineBot = MakeAccount("hnt02@mir3.local", true);
CharacterInfo onlineBotCharacter = MakeCharacter(onlineBot, "青衫客", MirClass.Taoist, out UserItem onlineItem, out UserCurrency onlineCurrency);

// 造一条「在线」连接：真人连接构造需要 socket，这里直接用假人连接挂上去即可（Connection 非空即视为在线）。
SConnection online = new("bot:02", true);
onlineBot.Connection = online;

BotCleanupStats onlinePreview = AccountCleanup.PreviewBots();

Check(onlinePreview.Online == 1, $"预览把在线假人单独计数（实际 {onlinePreview.Online}）");
Check(onlinePreview.Accounts == 0, "在线假人不计入可清理的账号数");

BotCleanupStats onlineResult = AccountCleanup.DeleteBots();

Check(onlineResult.Accounts == 0, "在线假人没有被删");
Check(SEnvir.AccountInfoList.Binding.Any(x => x.EMailAddress == "hnt02@mir3.local"), "在线假人账号还在");
Check(SEnvir.CharacterInfoList.Binding.Any(x => x.CharacterName == "青衫客"), "在线假人角色还在");

Console.WriteLine();

if (failures == 0)
{
    Console.WriteLine($"Passed {checks} bot-cleanup checks.");
    return;
}

Console.WriteLine($"FAILED {failures} of {checks} bot-cleanup checks.");
Environment.Exit(1);
