using Library;
using Library.SystemModels;
using MirDB;
using Server.DBModels;
using Server.Envir;
using Server.Models;
using System.Reflection;

// 阶段 C（2026-11-11 账号级数据下沉到角色级）隔离性验证：
// 同账号 2 个角色，各自持有 仓库 / 坐骑 / 交互开关 / 宠物名册 / 宠物解锁 /
// 邮件 / 寄售 / 任务 / 屏蔽名单 / 商城记录 / Buff，
// 断言「每个角色的集合里只有自己的东西」，并验证存库重载后依然隔离、删角色会级联清理。
// 用的是生产模型与生产 Session，数据库是一次性临时目录，绝不要指向正式库。

string root = Path.Combine(Path.GetTempPath(), "ZirconPhaseCChecks-" + Guid.NewGuid().ToString("N"));
var session = new Session(SessionMode.System | SessionMode.Users, root + Path.DirectorySeparatorChar);
session.Initialize(typeof(ItemInfo).Assembly, typeof(UserItem).Assembly);
foreach (FieldInfo field in typeof(SEnvir).GetFields(BindingFlags.Public | BindingFlags.Static))
    if (field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition() == typeof(DBCollection<>))
        field.SetValue(null, session.GetCollection(field.FieldType.GetGenericArguments()[0]));
SEnvir.Session = session;
SEnvir.Random = new Random(20261111);
Globals.CurrencyInfoList = SEnvir.CurrencyInfoList;

int checks = 0;

void Check(bool condition, string message)
{
    checks++;
    if (!condition) throw new Exception("断言失败：" + message);
}

// ---------- 准备系统数据 ----------
foreach (CurrencyType type in new[] { CurrencyType.GameGold, CurrencyType.Gold, CurrencyType.HuntGold })
{
    var currency = SEnvir.CurrencyInfoList.CreateNewObject();
    currency.Type = type;
}

var potion = SEnvir.ItemInfoList.CreateNewObject();
potion.ItemName = "阶段C药水";
potion.ItemType = ItemType.Consumable;
potion.RequiredType = RequiredType.Level;
potion.RequiredAmount = 1;
potion.StackSize = 10;
potion.Weight = 1;

var companionInfo = SEnvir.CompanionInfoList.CreateNewObject();
companionInfo.MonsterInfo = SEnvir.MonsterInfoList.CreateNewObject();
companionInfo.MonsterInfo.MonsterName = "阶段C宠物";
companionInfo.Available = true;

var questInfo = SEnvir.QuestInfoList.CreateNewObject();
questInfo.QuestName = "阶段C任务";
questInfo.QuestType = QuestType.Account;

var storeInfo = SEnvir.StoreInfoList.CreateNewObject();
storeInfo.Item = potion;

// ---------- 同账号 2 个角色 ----------
var account = SEnvir.AccountInfoList.CreateNewObject();
account.EMailAddress = "phasec@mir3.local";
account.Password = Array.Empty<byte>();

var alpha = SEnvir.CharacterInfoList.CreateNewObject();
alpha.CharacterName = "甲角色";
alpha.Account = account;

var beta = SEnvir.CharacterInfoList.CreateNewObject();
beta.CharacterName = "乙角色";
beta.Account = account;

Check(account.Characters.Count == 2, "账号下有 2 个角色");

// ---------- C1 仓库 ----------
UserItem AlphaItem = SEnvir.UserItemList.CreateNewObject();
AlphaItem.Info = potion;
AlphaItem.Count = 3;
AlphaItem.Slot = 0;
AlphaItem.StorageOwner = alpha;

UserItem BetaItem = SEnvir.UserItemList.CreateNewObject();
BetaItem.Info = potion;
BetaItem.Count = 7;
BetaItem.Slot = 1;
BetaItem.StorageOwner = beta;

alpha.StorageSize = 40;
beta.StorageSize = 60;

Check(alpha.Storage.Count == 1 && alpha.Storage[0] == AlphaItem, "甲角色的仓库只有自己的物品");
Check(beta.Storage.Count == 1 && beta.Storage[0] == BetaItem, "乙角色的仓库只有自己的物品");
Check(alpha.Storage[0].StorageOwner == alpha && beta.Storage[0].StorageOwner == beta, "仓库物品的持有者正确");

// ---------- C2 坐骑 + 交互开关 ----------
alpha.Horse = HorseType.Red;
beta.Horse = HorseType.WhiteUnicorn;
alpha.AllowGroup = true;
alpha.AllowTrade = false;
beta.AllowGroup = false;
beta.AllowTrade = true;

Check(alpha.Horse == HorseType.Red && beta.Horse == HorseType.WhiteUnicorn, "坐骑按角色隔离");
Check(alpha.AllowGroup && !alpha.AllowTrade && !beta.AllowGroup && beta.AllowTrade, "交互开关按角色隔离");

// ---------- C3 宠物名册 + 解锁 ----------
var alphaCompanion = SEnvir.UserCompanionList.CreateNewObject();
alphaCompanion.Owner = alpha;
alphaCompanion.Info = companionInfo;
alphaCompanion.Name = "甲的宠";
alphaCompanion.Level = 5;

var betaCompanion = SEnvir.UserCompanionList.CreateNewObject();
betaCompanion.Owner = beta;
betaCompanion.Info = companionInfo;
betaCompanion.Name = "乙的宠";
betaCompanion.Level = 9;

var alphaUnlock = SEnvir.UserCompanionUnlockList.CreateNewObject();
alphaUnlock.Character = alpha;
alphaUnlock.CompanionInfo = companionInfo;

Check(alpha.Companions.Count == 1 && alpha.Companions[0] == alphaCompanion, "甲角色的宠物名册只有自己的宠物");
Check(beta.Companions.Count == 1 && beta.Companions[0] == betaCompanion, "乙角色的宠物名册只有自己的宠物");
Check(alpha.CompanionUnlocks.Count == 1 && beta.CompanionUnlocks.Count == 0, "宠物解锁按角色隔离");
Check(alphaCompanion.Owner == alpha && betaCompanion.Owner == beta, "宠物名册归属正确");

// ---------- C4 邮件 + 寄售 ----------
var alphaMail = SEnvir.MailInfoList.CreateNewObject();
alphaMail.Character = alpha;
alphaMail.Subject = "给甲的信";
alphaMail.Sender = "系统";

var betaMail = SEnvir.MailInfoList.CreateNewObject();
betaMail.Character = beta;
betaMail.Subject = "给乙的信";
betaMail.Sender = "系统";

var alphaAuction = SEnvir.AuctionInfoList.CreateNewObject();
alphaAuction.Character = alpha;
alphaAuction.Price = 100;

var betaAuction = SEnvir.AuctionInfoList.CreateNewObject();
betaAuction.Character = beta;
betaAuction.Price = 200;

Check(alpha.Mail.Count == 1 && alpha.Mail[0] == alphaMail, "甲角色的信箱只有自己的信");
Check(beta.Mail.Count == 1 && beta.Mail[0] == betaMail, "乙角色的信箱只有自己的信");
Check(alpha.Auctions.Count == 1 && alpha.Auctions[0] == alphaAuction, "甲角色的寄售只有自己的挂单");
Check(beta.Auctions.Count == 1 && beta.Auctions[0] == betaAuction, "乙角色的寄售只有自己的挂单");

// ---------- C5 任务（账号任务也下沉到角色） ----------
var alphaQuest = SEnvir.UserQuestList.CreateNewObject();
alphaQuest.Character = alpha;
alphaQuest.QuestInfo = questInfo;

var betaQuest = SEnvir.UserQuestList.CreateNewObject();
betaQuest.Character = beta;
betaQuest.QuestInfo = questInfo;

Check(alpha.Quests.Count == 1 && alpha.Quests[0] == alphaQuest, "甲角色的任务只有自己的");
Check(beta.Quests.Count == 1 && beta.Quests[0] == betaQuest, "乙角色的任务只有自己的");

// ---------- C6 屏蔽名单 + 商城记录 + Buff ----------
var block = SEnvir.BlockInfoList.CreateNewObject();
block.Character = alpha;
block.BlockedCharacter = beta;
block.BlockedName = beta.CharacterName;

Check(alpha.BlockingList.Count == 1 && alpha.BlockingList[0] == block, "甲角色的屏蔽名单只有自己拉的");
Check(beta.BlockingList.Count == 0, "乙角色没有屏蔽任何人");
Check(beta.BlockedByList.Count == 1 && beta.BlockedByList[0] == block, "乙角色的「被屏蔽」名单里有甲那条");

var alphaSale = SEnvir.GameStoreSaleList.CreateNewObject();
alphaSale.Character = alpha;
alphaSale.Item = potion;
alphaSale.Price = 10;
alphaSale.Count = 1;

var alphaFav = SEnvir.GameStoreFavouriteList.CreateNewObject();
alphaFav.Character = alpha;
alphaFav.StoreInfo = storeInfo;

Check(alpha.StoreSales.Count == 1 && beta.StoreSales.Count == 0, "商城购买记录按角色隔离");
Check(alpha.StoreFavourites.Count == 1 && beta.StoreFavourites.Count == 0, "商城收藏按角色隔离");

BuffInfo alphaBuff = alpha.Buffs.FirstOrDefault(x => x.Type == BuffType.HuntGold);
BuffInfo betaBuff = beta.Buffs.FirstOrDefault(x => x.Type == BuffType.HuntGold);
Check(alphaBuff != null && betaBuff != null && alphaBuff != betaBuff, "每个角色各有自己的狩猎币上限 Buff");

// ---------- 存库重载 ----------
session.Save(true);
var reload = new Session(SessionMode.System | SessionMode.Users, root + Path.DirectorySeparatorChar);
reload.Initialize(typeof(ItemInfo).Assembly, typeof(UserItem).Assembly);

var reloadedAccount = reload.GetCollection<AccountInfo>().Binding.Single(x => x.EMailAddress == "phasec@mir3.local");
var reloadedAlpha = reloadedAccount.Characters.Single(x => x.CharacterName == "甲角色");
var reloadedBeta = reloadedAccount.Characters.Single(x => x.CharacterName == "乙角色");

Check(reloadedAlpha.Storage.Count == 1 && reloadedBeta.Storage.Count == 1, "重载后两个角色各有 1 件仓库物品");
Check(reloadedAlpha.Storage[0].Count == 3 && reloadedBeta.Storage[0].Count == 7, "重载后仓库物品数量正确");
Check(reloadedAlpha.Storage[0].StorageOwner == reloadedAlpha, "重载后仓库物品仍归属正确");
Check(reloadedAlpha.StorageSize == 40 && reloadedBeta.StorageSize == 60, "重载后仓库格数按角色隔离");
Check(reloadedAlpha.Horse == HorseType.Red && reloadedBeta.Horse == HorseType.WhiteUnicorn, "重载后坐骑按角色隔离");
Check(reloadedAlpha.AllowGroup && !reloadedBeta.AllowGroup, "重载后交互开关按角色隔离");
Check(reloadedAlpha.Companions.Count == 1 && reloadedBeta.Companions.Count == 1, "重载后宠物名册按角色隔离");
Check(reloadedAlpha.Companions[0].Name == "甲的宠" && reloadedBeta.Companions[0].Name == "乙的宠", "重载后宠物归属正确");
Check(reloadedAlpha.CompanionUnlocks.Count == 1 && reloadedBeta.CompanionUnlocks.Count == 0, "重载后宠物解锁按角色隔离");
Check(reloadedAlpha.Mail.Count == 1 && reloadedBeta.Mail.Count == 1, "重载后邮件按角色隔离");
Check(reloadedAlpha.Mail[0].Subject == "给甲的信" && reloadedBeta.Mail[0].Subject == "给乙的信", "重载后邮件归属正确");
Check(reloadedAlpha.Auctions.Count == 1 && reloadedBeta.Auctions.Count == 1, "重载后寄售按角色隔离");
Check(reloadedAlpha.Quests.Count == 1 && reloadedBeta.Quests.Count == 1, "重载后任务按角色隔离");
Check(reloadedAlpha.BlockingList.Count == 1 && reloadedBeta.BlockingList.Count == 0, "重载后屏蔽名单按角色隔离");
Check(reloadedBeta.BlockedByList.Count == 1, "重载后「被屏蔽」名单正确");
Check(reloadedAlpha.StoreSales.Count == 1 && reloadedBeta.StoreSales.Count == 0, "重载后商城记录按角色隔离");
Check(reloadedAlpha.StoreFavourites.Count == 1 && reloadedBeta.StoreFavourites.Count == 0, "重载后商城收藏按角色隔离");
Check(reloadedAlpha.Buffs.Any(x => x.Type == BuffType.HuntGold) && reloadedBeta.Buffs.Any(x => x.Type == BuffType.HuntGold),
    "重载后每个角色都有狩猎币上限 Buff");

// ---------- 删除角色 → 级联清理 ----------
reload.GetCollection<CharacterInfo>().Binding.Single(x => x.CharacterName == "甲角色").Delete();
reload.Save(true);

var afterDelete = new Session(SessionMode.System | SessionMode.Users, root + Path.DirectorySeparatorChar);
afterDelete.Initialize(typeof(ItemInfo).Assembly, typeof(UserItem).Assembly);

var afterAccount = afterDelete.GetCollection<AccountInfo>().Binding.Single(x => x.EMailAddress == "phasec@mir3.local");
var afterBeta = afterAccount.Characters.Single(x => x.CharacterName == "乙角色");

Check(afterBeta.Storage.Count == 1, "删掉甲角色后，乙角色的仓库不受影响");
Check(afterBeta.Companions.Count == 1, "删掉甲角色后，乙角色的宠物不受影响");
Check(afterBeta.Mail.Count == 1, "删掉甲角色后，乙角色的邮件不受影响");
Check(afterBeta.Auctions.Count == 1, "删掉甲角色后，乙角色的寄售不受影响");
Check(afterBeta.Quests.Count == 1, "删掉甲角色后，乙角色的任务不受影响");
Check(afterBeta.BlockingList.Count == 0, "删掉甲角色后，乙角色的屏蔽名单仍为空");
Check(afterBeta.BlockedByList.Count == 0, "甲角色被删后，「乙被甲屏蔽」这条记录随之清理");
Check(afterDelete.GetCollection<UserItem>().Binding.All(x => x.StorageOwner == null || x.StorageOwner.CharacterName == "乙角色"),
    "甲角色的仓库物品已随之删除");
Check(afterDelete.GetCollection<MailInfo>().Binding.All(x => x.Character == null || x.Character.CharacterName == "乙角色"),
    "甲角色的邮件已随之删除");

Console.WriteLine($"Passed {checks} phase C character-isolation checks. Disposable database: {root}");
