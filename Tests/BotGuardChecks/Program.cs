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
using C = Library.Network.ClientPackets;
using S = Library.Network.ServerPackets;

// 验证 2026-11-11 的「假人账号登录拦截 + 封禁自愈」：
//   1) 外部（真人）连接登录假人账号 → 被拒（AccountNotExists）
//   2) 外部（真人）连接登录真人账号 → 放行（回归：拦截不能误伤真人）
//   3) 假人自己的连接登录自己的假人账号 → 放行（回归：拦截不能误伤假人自己）
//   4) 被误封的假人账号 → EnsureAccount 自愈解封
//
// 跑在一次性临时库上，不碰正式 Users.db。
string root = Path.Combine(Path.GetTempPath(), "ZirconBotGuard-" + Guid.NewGuid().ToString("N"));

Session session = new(SessionMode.System | SessionMode.Users, root + Path.DirectorySeparatorChar);
session.Initialize(typeof(ItemInfo).Assembly, typeof(UserItem).Assembly);

foreach (FieldInfo field in typeof(SEnvir).GetFields(BindingFlags.Public | BindingFlags.Static))
    if (field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition() == typeof(DBCollection<>))
        field.SetValue(null, session.GetCollection(field.FieldType.GetGenericArguments()[0]));

SEnvir.Session = session;
SEnvir.Random = new Random(20261111);

// SEnvir.Now 由游戏主循环每帧刷新；测试里没有主循环，要自己喂时间，
// 否则 Now 停在 DateTime.MinValue，算在线时长会溢出。
SEnvir.Now = DateTime.Now;
Globals.CurrencyInfoList = SEnvir.CurrencyInfoList;

// SConnection 的构造器要从 ConfigReader 拿语言包，假人连接用的是中文。
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

Console.WriteLine("假人账号登录拦截与封禁自愈：");

// ---------- 造账号 ----------

AccountInfo RealAccount(string email, string password)
{
    AccountInfo account = SEnvir.AccountInfoList.CreateNewObject();

    account.EMailAddress = email;
    account.Password = SEnvir.CreateHash(password);
    account.BirthDate = new DateTime(1990, 1, 1);
    account.CreationIP = "test";
    account.CreationDate = SEnvir.Now;
    account.Activated = true;
    account.ActivationTime = SEnvir.Now;

    return account;
}

AccountInfo botAccount = RealAccount("hnt01@mir3.local", "Bot#01Locked");
botAccount.IsBot = true;

AccountInfo realAccount = RealAccount("player01@mir3.local", "RealPass123");
realAccount.IsBot = false;

// ---------- 造连接 ----------

SConnection RealConnection(string ip)
{
    SConnection con = (SConnection)RuntimeHelpers.GetUninitializedObject(typeof(SConnection));

    // IPAddress 是 get-only 自动属性，只能写 backing field。
    FieldInfo? backing = typeof(SConnection).GetField("<IPAddress>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
    backing?.SetValue(con, ip);

    con.SendList = new ConcurrentQueue<Packet>();
    con.Observers = new();
    con.Language = new EnglishMessages();
    con.Stage = GameStage.Login;
    con.Connected = true;

    return con;
}

SConnection BotConnection() => new("bot:01", true);

// 从连接的发包队列里取最后一个 S.Login 结果。
LoginResult LastLoginResult(SConnection con)
{
    S.Login? last = null;

    while (con.SendList.TryDequeue(out Packet? packet))
        if (packet is S.Login login) last = login;

    return last?.Result ?? (LoginResult)byte.MaxValue;
}

// ---------- 1) 外部连接登录假人账号 → 被拒 ----------

SConnection outsider = RealConnection("10.0.0.9");
SEnvir.Login(new C.Login { EMailAddress = botAccount.EMailAddress, Password = "Bot#01Locked", CheckSum = "test" }, outsider);

Check(LastLoginResult(outsider) == LoginResult.AccountNotExists,
    "外部连接登录假人账号被拒（AccountNotExists，不泄露账号存在）");

Check(botAccount.Connection == null,
    "被拒的登录没有建立连接（账号.Connection 仍为空）");

// ---------- 2) 外部连接登录真人账号 → 放行（回归） ----------

SConnection normal = RealConnection("10.0.0.10");
SEnvir.Login(new C.Login { EMailAddress = realAccount.EMailAddress, Password = "RealPass123", CheckSum = "test" }, normal);

LoginResult realResult = LastLoginResult(normal);

Check(realResult != LoginResult.AccountNotExists,
    $"外部连接登录真人账号没有被误伤（结果 {realResult}）");

Check(realResult == LoginResult.Success,
    $"真人账号登录成功（结果 {realResult}）");

// ---------- 3) 假人自己的连接登录自己的账号 → 放行（回归） ----------

SConnection bot = BotConnection();
SEnvir.Login(new C.Login { EMailAddress = botAccount.EMailAddress, Password = "Bot#01Locked", CheckSum = "bot" }, bot);

// 假人连接不走网络（SConnection.Enqueue 对 IsBot 直接 return），所以拿不到 S.Login 包，
// 判据只能是「账号有没有被绑定到这条连接」——没被拦下就说明登录流程走完了。
Check(botAccount.Connection == bot,
    "假人自己登录自己的账号没有被误伤（账号.Connection 已指向假人连接）");

// ---------- 4) 被误封的假人账号 → EnsureAccount 自愈 ----------

botAccount.Banned = true;
botAccount.BanReason = "登录失败次数过多";
botAccount.BanExpiry = SEnvir.Now.AddMinutes(10);
botAccount.WrongPasswordCount = 7;

BotProfile profile = new()
{
    Seq = 1,
    EMail = botAccount.EMailAddress,
    Password = "Bot#01Locked",
    CharacterName = "夜行人",
    Class = MirClass.Wizard,
    Gender = MirGender.Male,
    HairType = 0,
    HairColour = System.Drawing.Color.FromArgb(0),
    ArmourColour = System.Drawing.Color.FromArgb(255, 255, 255, 255),
    HomeMapIndex = 0,
};

AccountInfo healed = BotFactory.EnsureAccount(profile);

Check(healed == botAccount, "EnsureAccount 按邮箱查重命中已有账号（没有新建）");

Check(!healed.Banned, "被误封的假人账号已自动解封");

Check(healed.BanExpiry == DateTime.MinValue && string.IsNullOrEmpty(healed.BanReason),
    "解封后封禁原因与到期时间已清空");

Check(healed.WrongPasswordCount == 0, "密码错误计数已归零");

Check(healed.Activated && healed.IsBot, "激活标记与 IsBot 标记保持正确");

// ---------- 5) 真人账号不会被 EnsureAccount 碰到 ----------

AccountInfo untouched = RealAccount("player02@mir3.local", "RealPass123");
untouched.Banned = true;
untouched.BanExpiry = SEnvir.Now.AddMinutes(10);

BotProfile other = new()
{
    Seq = 2,
    EMail = "hnt02@mir3.local",
    Password = "Bot#02Locked",
    CharacterName = "青衫客",
    Class = MirClass.Warrior,
    Gender = MirGender.Male,
    HairType = 0,
    HairColour = System.Drawing.Color.FromArgb(0),
    ArmourColour = System.Drawing.Color.FromArgb(255, 255, 255, 255),
    HomeMapIndex = 0,
};

AccountInfo created = BotFactory.EnsureAccount(other);

Check(created != untouched, "真人账号没有被假人自愈逻辑影响（是另一个账号对象）");

Check(untouched.Banned, "真人账号的封禁状态保持不变（自愈只作用于假人账号）");

// ---------- 6) 累计在线时长（跨重启持久化） ----------

Console.WriteLine();
Console.WriteLine("累计在线时长：");

CharacterInfo timed = SEnvir.CharacterInfoList.CreateNewObject();
timed.Account = RealAccount("hnt09@mir3.local", "Bot#09Locked");
timed.CharacterName = "计时假人";
timed.Class = MirClass.Warrior;
timed.Gender = MirGender.Male;
timed.HairType = 0;
timed.HairColour = System.Drawing.Color.FromArgb(0);
timed.ArmourColour = System.Drawing.Color.FromArgb(255, 255, 255, 255);
timed.CreationIP = "test";
timed.CreationDate = SEnvir.Now;
timed.BotOnlineSeconds = 100;

BotSession.AccumulateOnlineTime(timed, SEnvir.Now.AddMinutes(-30));

Check(timed.BotOnlineSeconds >= 100 + 1799 && timed.BotOnlineSeconds <= 100 + 1801,
    $"30 分钟的在线被累计进角色（{timed.BotOnlineSeconds} 秒）");

BotSession.AccumulateOnlineTime(timed, SEnvir.Now.AddMinutes(-10));

Check(timed.BotOnlineSeconds >= 100 + 1799 + 599 && timed.BotOnlineSeconds <= 100 + 1801 + 601,
    $"第二段在线时长继续累加（{timed.BotOnlineSeconds} 秒）");

// 未上线过（OnlineSince 是 MinValue）不该累加
long before = timed.BotOnlineSeconds;
BotSession.AccumulateOnlineTime(timed, DateTime.MinValue);

Check(timed.BotOnlineSeconds == before, "没上线过（OnlineSince 为空）不累加");

// 空角色也不该炸
BotSession.AccumulateOnlineTime(null, SEnvir.Now.AddMinutes(-5));

Check(true, "传空角色不会抛异常");

Console.WriteLine();

if (failures == 0)
{
    Console.WriteLine($"Passed {checks} bot-guard checks.");
    return;
}

Console.WriteLine($"FAILED {failures} of {checks} bot-guard checks.");
Environment.Exit(1);
