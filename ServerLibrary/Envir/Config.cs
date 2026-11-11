using Library;
using System;
using System.IO;
using System.Security.Cryptography;

namespace Server.Envir
{
    [ConfigPath(@".\Server.ini")]
    public static class Config
    {
        [ConfigSection("Network")]
        public static string IPAddress { get; set; } = "127.0.0.1";
        public static ushort Port { get; set; } = 7000;
        public static TimeSpan TimeOut { get; set; } = TimeSpan.FromSeconds(20);
        public static TimeSpan PingDelay { get; set; } = TimeSpan.FromSeconds(2);
        public static ushort UserCountPort { get; set; } = 3000;
        public static int MaxPacket { get; set; } = 50;
        public static TimeSpan PacketBanTime { get; set; } = TimeSpan.FromMinutes(5);
        public static string SyncRemotePreffix { get; set; } = "http://127.0.0.1:80/Command/";

        [ConfigSection("System")]
        public static bool CheckVersion { get; set; } = true;
        public static string VersionPath { get; set; } = @".\Zircon.dll";

        public static string MapPath { get; set; } = @".\Map\";
        public static byte[] ClientHash;
        public static string MasterPassword { get; set; } = @"REDACTED";
        public static string SyncKey { get; set; } = "REDACTED";
        public static string ClientPath { get; set; }
        public static DateTime ReleaseDate { get; set; } = new DateTime(2017, 12, 22, 18, 00, 00, DateTimeKind.Utc);
        public static bool TestServer { get; set; } = false;
        public static string StarterGuildName { get; set; } = "Starter Guild";
        public static bool LazyLoadMaps { get; set; } = true;
        public static DateTime EasterEventEnd { get; set; } = new DateTime(2018, 04, 09, 00, 00, 00, DateTimeKind.Utc);
        public static DateTime HalloweenEventEnd { get; set; } = new DateTime(2018, 11, 07, 00, 00, 00, DateTimeKind.Utc);
        public static DateTime ChristmasEventEnd { get; set; } = new DateTime(2019, 01, 03, 00, 00, 00, DateTimeKind.Utc);
        public static TimeSpan DBSaveDelay { get; set; } = TimeSpan.FromMinutes(5);
        public static bool EncryptionEnabled { get; set; } = false;
        public static string EncryptionKey { get; set; } = string.Empty;

        [ConfigSection("Control")]
        public static bool AllowLogin { get; set; } = true;
        public static bool AllowNewAccount { get; set; } = true;
        public static bool AllowChangePassword { get; set; } = true;

        public static bool AllowRequestPasswordReset { get; set; } = true;
        public static bool AllowWebResetPassword { get; set; } = true;
        public static bool AllowManualResetPassword { get; set; } = true;

        public static bool AllowDeleteAccount { get; set; } = true;

        public static bool AllowManualActivation { get; set; } = true;
        public static bool AllowWebActivation { get; set; } = true;
        public static bool AllowRequestActivation { get; set; } = true;
        public static bool AllowSystemDBSync { get; set; } = false;

        public static bool AllowNewCharacter { get; set; } = true;
        public static bool AllowDeleteCharacter { get; set; } = true;
        public static bool AllowStartGame { get; set; }
        public static TimeSpan RelogDelay { get; set; } = TimeSpan.FromSeconds(10);
        public static bool AllowWarrior { get; set; } = true;
        public static bool AllowWizard { get; set; } = true;
        public static bool AllowTaoist { get; set; } = true;
        public static bool AllowAssassin { get; set; } = true;

        [ConfigSection("Mail")]
        public static string MailServer { get; set; } = @"smtp.gmail.com";
        public static int MailPort { get; set; } = 587;
        public static bool MailUseSSL { get; set; } = true;
        public static string MailAccount { get; set; } = @"admin@zirconserver.com";
        public static string MailPassword { get; set; } = @"REDACTED";
        public static string MailFrom { get; set; } = "admin@zirconserver.com";
        public static string MailDisplayName { get; set; } = "Admin";

        [ConfigSection("WebServer")]
        public static bool EnableWebServer { get; set; } = false;
        public static string WebPrefix { get; set; } = @"http://*:80/Command/";
        public static string WebCommandLink { get; set; } = @"https://www.zirconserver.com/Command";

        public static string ActivationSuccessLink { get; set; } = @"https://www.zirconserver.com/activation-successful/";
        public static string ActivationFailLink { get; set; } = @"https://www.zirconserver.com/activation-unsuccessful/";
        public static string ResetSuccessLink { get; set; } = @"https://www.zirconserver.com/password-reset-successful/";
        public static string ResetFailLink { get; set; } = @"https://www.zirconserver.com/password-reset-unsuccessful/";
        public static string DeleteSuccessLink { get; set; } = @"https://www.zirconserver.com/account-deletetion-successful/";
        public static string DeleteFailLink { get; set; } = @"https://www.zirconserver.com/account-deletetion-unsuccessful/";

        public static string BuyPrefix { get; set; } = @"http://*:80/BuyGameGold/";
        public static string BuyAddress { get; set; } = @"http://145.239.204.13/BuyGameGold";
        public static string IPNPrefix { get; set; } = @"http://*:80/IPN/";
        public static string ReceiverEMail { get; set; } = @"REDACTED";
        public static bool ProcessGameGold { get; set; } = true;
        public static bool AllowBuyGameGold { get; set; } = true;


        [ConfigSection("Players")]
        public static int MaxViewRange { get; set; } = 18;
        public static TimeSpan ShoutDelay { get; set; } = TimeSpan.FromSeconds(10);
        public static TimeSpan GlobalDelay { get; set; } = TimeSpan.FromSeconds(60);
        /// <summary>
        /// 全服等级上限（<c>[System] MaxLevel</c>）。**真人和假人走同一条判定**
        /// （<c>PlayerObject.GainExperience</c>），不是假人专属。
        /// <para>
        /// 2026-11-10：默认从 10 放开到 75（用户决策，先放开、后续再看内容影响）。
        /// 经验表 <c>Config/ExperienceList.txt</c> 有 100 级数据，所以 75 级以内不会缺经验曲线。
        /// </para>
        /// </summary>
        public static int MaxLevel { get; set; } = 75;
        public static int DayCycleCount { get; set; } = 3;
        public static int SkillExp { get; set; } = 3;
        public static bool AllowObservation { get; set; } = true;
        public static bool AllowWaypoints { get; set; } = true;
        public static int MaxWaypoints { get; set; } = 5;
        public static TimeSpan BrownDuration { get; set; } = TimeSpan.FromSeconds(60);
        public static int PKPointRate { get; set; } = 50;
        public static TimeSpan PKPointTickRate { get; set; } = TimeSpan.FromSeconds(60);
        public static int RedPoint { get; set; } = 200;
        public static TimeSpan PvPCurseDuration { get; set; } = TimeSpan.FromMinutes(60);
        public static int PvPCurseRate { get; set; } = 4;
        public static TimeSpan AutoReviveDelay { get; set; } = TimeSpan.FromMinutes(10);
        public static TimeSpan RankChangeResetDelay { get; set; } = TimeSpan.FromHours(24);
        public static bool EnableStruck { get; set; } = false;
        public static bool EnableHermit { get; set; } = false;
        public static bool EnableGroupLoot { get; set; } = false;

        [ConfigSection("Monsters")]
        public static TimeSpan DeadDuration { get; set; } = TimeSpan.FromMinutes(1);
        public static TimeSpan HarvestDuration { get; set; } = TimeSpan.FromMinutes(5);
        public static int MysteryShipRegionIndex { get; set; } = 0;
        public static int LairRegionIndex { get; set; } = 0;

        [ConfigSection("Items")]
        public static TimeSpan DropDuration { get; set; } = TimeSpan.FromMinutes(60);
        public static int DropDistance { get; set; } = 5;
        public static int DropLayers { get; set; } = 5;
        public static int TorchRate { get; set; } = 10;
        public static int MaxGemPurity { get; set; } = 100;
        public static TimeSpan SpecialRepairDelay { get; set; } = TimeSpan.FromHours(8);
        public static int MaxLuck { get; set; } = 7;
        public static int MaxCurse { get; set; } = -10;
        public static int CurseRate { get; set; } = 20;
        public static int LuckRate { get; set; } = 10;
        public static int MaxStrength { get; set; } = 5;
        public static int StrengthAddRate { get; set; } = 10;
        public static int StrengthLossRate { get; set; } = 20;
        public static bool DropVisibleOtherPlayers { get; set; } = false;
        public static bool EnableFortune { get; set; } = true;
        public static bool AdminStartInGamemasterMode { get; set; } = true;
        public static bool AdminStartInObserverMode { get; set; } = true;
        public static bool AdminStartInSupermanMode { get; set; } = true;

        [ConfigSection("Rates")]
        public static int ExperienceRate { get; set; } = 0;
        public static int DropRate { get; set; } = 0;
        public static int GoldRate { get; set; } = 0;
        public static int SkillRate { get; set; } = 0;
        public static int CompanionRate { get; set; } = 0;

        /// <summary>寄售上架费率：按挂单总价（单价×数量）的百分比收取，0.01 = 1%。</summary>
        public static decimal MarketPlaceFeeRate { get; set; } = 0.01M;
        /// <summary>寄售上架费最低值。</summary>
        public static int MarketPlaceFeeMinimum { get; set; } = 100;
        /// <summary>寄售成交税率：从货款中扣除，0.07 = 7%。</summary>
        public static decimal MarketPlaceTaxRate { get; set; } = 0.07M;

        [ConfigSection("Fishing")]
        public static bool FishEnablePerfectCatch { get; set; } = true;
        public static int FishNibbleChanceBase { get; set; } = 10;
        public static int FishPointsRequired { get; set; } = 50;
        public static int FishPointSuccessRewardMin { get; set; } = 2;
        public static int FishPointSuccessRewardMax { get; set; } = 5;
        public static int FishPointFailureRewardMin { get; set; } = 0;
        public static int FishPointFailureRewardMax { get; set; } = 5;

        [ConfigSection("AdminWeb")]
        public static bool AdminWebEnabled { get; set; } = true;
        public static string AdminWebHost { get; set; } = "127.0.0.1";
        public static ushort AdminWebPort { get; set; } = 8080;
        public static string AdminUserName { get; set; } = "admin";
        public static bool AdminAutoStartGameServer { get; set; } = false;

        /// <summary>
        /// 是否允许用「AdminUserName + MasterPassword」登录 Web 后台（应急入口，仅限内网）。
        /// 关闭后只能使用带 WebAdmin 权限的玩家账号登录。
        /// </summary>
        public static bool WebAllowMasterLogin { get; set; } = true;

        /// <summary>
        /// 主密码登录是否要求 TOTP 二次验证（默认关闭）。
        /// 关闭时 TOTP 功能整体失效（已绑定的也不校验），可用于手机丢失时的应急放行；
        /// 开启后若尚未绑定，登录会强制进入绑定页。
        /// </summary>
        public static bool AdminWebRequireTotp { get; set; } = false;

        /// <summary>
        /// 后台会话空闲超时（分钟）。0 = 不启用空闲超时。
        /// 浏览器每分钟心跳一次；超过该时长无任何鼠标/键盘操作 → 自动登出并跳登录页。
        /// </summary>
        public static int AdminWebIdleTimeoutMinutes { get; set; } = 30;

        /// <summary>
        /// 后台单点登录：同一账号只保留最新一次登录，后登录的会把先登录的踢下线（默认关闭，避免多设备运维被误踢）。
        /// </summary>
        public static bool AdminWebSingleSession { get; set; } = false;

        /// <summary>
        /// AI 假人系统开关组。默认全部关闭，由管理员在后台手动开启。
        /// 改动即时生效（每轮调度都重读这些静态属性），无需重启。
        /// </summary>
        [ConfigSection("Bots")]
        public static bool BotEnabled { get; set; } = false;
        /// <summary>目标在线人数（真人在线 + 假人在线）。水位 = 目标 - 真人数，夹在 Min/Max 之间。</summary>
        public static int BotTargetOnline { get; set; } = 30;
        public static int BotMinOnline { get; set; } = 0;
        public static int BotMaxOnline { get; set; } = 30;
        /// <summary>每轮调度最多新增/下线几个假人（避免同一帧 30 个登录把主循环卡死）。</summary>
        public static int BotMaxChangePerTick { get; set; } = 1;
        /// <summary>水位重算间隔（秒）。稳态补水/撤人按这个节奏。</summary>
        public static int BotRosterIntervalSeconds { get; set; } = 5;
        /// <summary>
        /// 冷启动铺量间隔（秒）：服务器起来后第一次把人铺满时，每放一个等这么久，之后转回
        /// <see cref="BotRosterIntervalSeconds"/>。默认 45 秒——D2 实测假人 45 秒已走出出生点 20 多格，
        /// 足够错开、又不至于像 5 分钟那样两小时才有人气。
        /// </summary>
        public static int BotStartupRampSeconds { get; set; } = 45;
        /// <summary>上线分步的最小/最大间隔（毫秒），连接、登录、进图每一步之间随机等待。</summary>
        public static int BotStepDelayMinMs { get; set; } = 300;
        public static int BotStepDelayMaxMs { get; set; } = 1200;
        /// <summary>新号一次性启动金币（0 = 不发放）。只在首次建角色时发，之后不补。</summary>
        public static int BotNewbieGold { get; set; } = 5000;
        /// <summary>新号一次性启动药水数量（红蓝各这么多，0 = 不发放）。</summary>
        public static int BotNewbiePotionCount { get; set; } = 20;
        /// <summary>是否按等级自动补技能（法师/道士不补会一直平砍，一眼假）。</summary>
        public static bool BotAutoSkill { get; set; } = true;
        /// <summary>假人等级上限，0 = 不封顶（已决策不封顶，留这个开关只为应急）。</summary>
        public static int BotLevelCap { get; set; } = 0;
        /// <summary>允许练级的 MapInfo.Index，逗号分隔；留空 = 不限制（P0 建议填 3~5 张低级图）。</summary>
        public static string BotHuntMaps { get; set; } = string.Empty;
        /// <summary>日志级别：0=Debug 1=Info 2=Warn 3=Error，默认只记 Warn 以上。</summary>
        public static int BotLogLevel { get; set; } = 2;
        /// <summary>连续上线失败多少次后标记 Broken，不再自动拉起（后台可手动重置）。</summary>
        public static int BotMaxFailures { get; set; } = 3;

        /// <summary>同一张地图上最多放几个假人练级，超了就跨图分流（防止一堆人抢一片刷怪区）。</summary>
        public static int BotMapCapacity { get; set; } = 8;
        /// <summary>红药低于这个瓶数就回城补给（0 = 不自动补给）。</summary>
        public static int BotRestockPotionLow { get; set; } = 5;
        /// <summary>每次补给买多少瓶红药。</summary>
        public static int BotRestockBuyRed { get; set; } = 20;
        /// <summary>每次补给买多少瓶蓝药（法师/道士用，战士用不上会剩下）。</summary>
        public static int BotRestockBuyBlue { get; set; } = 10;
        /// <summary>
        /// 每次补给顺路买多少张回城卷（0 = 不买）。
        /// <para>
        /// 回城卷（ItemInfo 156，价 500）是「断药后一秒回主城」的道具：
        /// 断药时最费时间的不是买药，是走回主城（实测 60 秒以上）。
        /// 假人用它省下这段路，用来在城外多打一会儿怪。
        /// </para>
        /// <para>
        /// 回城卷在**杂货商**处卖（药店只卖药，是两个不同的 NPC），
        /// 所以一次回城要跑两趟：先药店买药，再杂货商买卷。
        /// </para>
        /// </summary>
        public static int BotRestockBuyTownScroll { get; set; } = 2;

        public static void LoadVersion()
        {
            try
            {
                string versionPath = PlatformPath.Resolve(VersionPath);
                if (File.Exists(versionPath))
                    using (FileStream stream = File.OpenRead(versionPath))
                    using (SHA256 sha256 = SHA256.Create())
                    {
                        ClientHash = sha256.ComputeHash(stream);
                    }
                else ClientHash = null;
            }
            catch (Exception ex)
            {
                SEnvir.Log(ex.ToString());
            }
        }
    }
}
