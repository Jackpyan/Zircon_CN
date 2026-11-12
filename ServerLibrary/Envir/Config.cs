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

        /// <summary>
        /// 真人优先：已经被真人打过的怪（引擎的 <c>MonsterObject.EXPOwner</c> = 谁先打谁拥有）假人不再抢。
        /// <para>
        /// 不开这个，假人会围着真人正在打的怪一起砍 —— 这是真人最容易察觉、也最容易反感的一条。
        /// </para>
        /// </summary>
        public static bool BotRespectRealPlayerTags { get; set; } = true;

        /// <summary>BOSS 留给真人：假人跳过 <c>MonsterInfo.IsBoss</c> 的怪（抢掉落 + 容易被围死）。</summary>
        public static bool BotSkipBoss { get; set; } = true;

        /// <summary>
        /// 假人说话总开关（默认关）。
        /// <para>
        /// 关着的时候假人一句不说；开着才按 <c>Bots/chat.txt</c> 的语料说话。
        /// 默认关是因为它会往全服聊天频道和 <c>Logs.txt</c> 里写字，属于「上线前要人确认」的行为。
        /// </para>
        /// </summary>
        public static bool BotChatEnabled { get; set; } = false;

        /// <summary>同一个假人两次说话的最小间隔（秒）。默认 15 分钟。</summary>
        public static int BotChatIntervalMinSeconds { get; set; } = 900;

        /// <summary>同一个假人两次说话的最大间隔（秒）。默认 40 分钟。</summary>
        public static int BotChatIntervalMaxSeconds { get; set; } = 2400;

        /// <summary>是否允许上世界频道（<c>!@</c>）。关掉就只在本图普通频道说话。</summary>
        public static bool BotChatWorldEnabled { get; set; } = true;

        /// <summary>世界频道的最低等级。引擎硬要求 33 级，配低了也发不出去。</summary>
        public static int BotChatWorldMinLevel { get; set; } = 33;

        /// <summary>说世界频道的概率（0~100）。默认 35%，其余走本图普通频道。</summary>
        public static int BotChatWorldPercent { get; set; } = 35;

        /// <summary>全服两次世界频道说话的最小间隔（秒）。防止几个假人同时刷世界。</summary>
        public static int BotChatWorldCooldownSeconds { get; set; } = 40;

        /// <summary>
        /// 走路停顿（P2-2）：每走 5~15 步随机站 0.4~1.2 秒，消除「机器人匀速巡逻」的观感。
        /// 打架 / 掉血 / 目标贴脸时会自动打断。
        /// </summary>
        public static bool BotWalkPauseEnabled { get; set; } = true;

        /// <summary>城里闲逛（P2-3）：补给/卖货办完后按概率在主城多待 1~3 分钟，让城里有人。</summary>
        public static bool BotTownStrollEnabled { get; set; } = true;

        /// <summary>闲逛概率（0~100），默认 30%。</summary>
        public static int BotTownStrollPercent { get; set; } = 30;

        /// <summary>
        /// 误操作（P2-5）：极小概率走岔一步，破「完美脚本」感。
        /// <para>默认开启但概率极低（0.5%）、每小时最多 2 次、血 &lt; 30% 与安全区不触发。</para>
        /// </summary>
        public static bool BotMistakeEnabled { get; set; } = true;

        /// <summary>每个假人每小时最多犯几次错。硬上限，别调太高。</summary>
        public static int BotMistakePerHour { get; set; } = 2;

        /// <summary>
        /// 作息（P2-6）：夜间时段把在线目标按比例缩水。
        /// 缩水是「一个一个下线」（受 <see cref="BotMaxChangePerTick"/> 约束），不是整点集体消失。
        /// </summary>
        public static bool BotNightEnabled { get; set; } = true;

        /// <summary>夜间时段起点（0~23）。默认 1 点。</summary>
        public static int BotNightMinHour { get; set; } = 1;

        /// <summary>夜间时段终点（0~23，不含）。默认 7 点。与起点相等 = 不启用。</summary>
        public static int BotNightMaxHour { get; set; } = 7;

        /// <summary>夜间目标百分比（默认 50 = 减半）。</summary>
        public static int BotNightScalePercent { get; set; } = 50;

        /// <summary>
        /// 换图流动（P2-4）：在同一张图待满 25~45 分钟后，按等级段随机换一张练级图。
        /// 目的不是效率，是让地图上的人「在流动」。复用 90 秒换图冷却 + 5 分钟防折返。
        /// </summary>
        public static bool BotMapRotateEnabled { get; set; } = true;

        /// <summary>
        /// 社交总开关（P3）。关着的时候行会 / 组队 / 交易 / PK 全部不动作。
        /// <para>
        /// 三条硬禁令写在 <see cref="Server.Bots.BotSocial"/> 里：不邀请真人、不私聊真人、不向真人发起交易。
        /// </para>
        /// </summary>
        public static bool BotSocialEnabled { get; set; } = false;

        /// <summary>假人行会（P3-1）。需要 <see cref="BotSocialEnabled"/> 一起打开。</summary>
        public static bool BotGuildEnabled { get; set; } = true;

        /// <summary>要建几个假人行会（不超过 <see cref="BotGuildNames"/> 的条数）。</summary>
        public static int BotGuildCount { get; set; } = 2;

        /// <summary>
        /// 行会名，逗号分隔。
        /// <para>
        /// <b>只能是字母数字</b>：<c>Globals.GuildNameRegex</c> 是 <c>^[A-Za-z0-9]{n,m}$</c>，中文名建不出来。
        /// </para>
        /// </summary>
        public static string BotGuildNames { get; set; } = "MoonLight,OldTimes";

        /// <summary>建会资格等级（真人客户端也是 30 级才能建会）。</summary>
        public static int BotGuildMinLevel { get; set; } = 30;

        /// <summary>
        /// 建会时额外买多少「成员格」（每格 100 万金币）。
        /// <para>成员上限 = 10 + 这个值。默认 5 → 15 个位置，两个会能装下 30 个假人。</para>
        /// </summary>
        public static int BotGuildMembers { get; set; } = 5;

        /// <summary>
        /// 建会注资上限（金币）。0 = 不注资（假人只能靠自己攒，基本建不起来）。
        /// <para>
        /// 建会要 750 万，假人靠打怪攒不现实，所以这里允许一次性补差额。
        /// <b>这是往经济里注水</b>：两个会约 2500 万，开之前先想清楚。
        /// </para>
        /// </summary>
        public static long BotGuildGrantGold { get; set; } = 13000000;

        /// <summary>
        /// 红名 PK（P3-4）。<b>会主动攻击真人红名</b>，是整个假人系统里唯一这么做的功能。
        /// 只打红名、只吓唬（对方血 &lt; 30% 收手）、有分钟级间隔。
        /// </summary>
        public static bool BotPkEnabled { get; set; } = true;

        /// <summary>同一个假人两次主动 PK 的最小间隔（分钟）。默认 30。</summary>
        public static int BotPkIntervalMinutes { get; set; } = 30;

        /// <summary>发现红名的半径（格）。默认 8。</summary>
        public static int BotPkRadius { get; set; } = 8;

        /// <summary>
        /// 假人互组（P3-2）：同图、等级接近、都没队的假人互相组队。
        /// 只影响观感（城里/野外有队伍），不影响各自练级路线。
        /// </summary>
        public static bool BotGroupEnabled { get; set; } = true;

        /// <summary>组队的最低等级（太低级的号组队没意义，也容易被当成脚本）。</summary>
        public static int BotGroupMinLevel { get; set; } = 5;

        /// <summary>组队的最大等级差。超过这个差距引擎分经验也不划算。</summary>
        public static int BotGroupLevelGap { get; set; } = 5;

        /// <summary>
        /// 榜首降速（P4-4）：假人连续占住排行榜前几名就暂停练级，把位置让给真人。
        /// <para>
        /// 这是「不设等级封顶」的配套措施 —— 封顶会让等级停成一条直线，比占榜还假；
        /// 降速是「跑一阵、歇两小时」，看起来像玩家自己下线了。
        /// </para>
        /// </summary>
        public static bool BotTopSlowEnabled { get; set; } = true;

        /// <summary>「前几名」算占榜。默认 3。</summary>
        public static int BotTopRankLimit { get; set; } = 3;

        /// <summary>连续占榜多少分钟后开始降速。默认 30 分钟。</summary>
        public static int BotTopHoldMinutes { get; set; } = 30;

        /// <summary>降速持续多少分钟。默认 120 分钟（2 小时）。</summary>
        public static int BotTopSlowMinutes { get; set; } = 120;


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
