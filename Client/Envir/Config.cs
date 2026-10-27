using Shared.Rendering;
using Library;
using System;
using System.Collections.Generic;
using System.Drawing;

namespace Client.Envir
{
    [ConfigPath(@".\Zircon.ini")]
    public static class Config
    {
        public static readonly Size IntroSceneSize = new Size(1024, 768);

        public const string DefaultIPAddress = "127.0.0.1";
        public const int DefaultPort = 7000;

        [ConfigSection("Network")]
        public static bool UseNetworkConfig { get; set; } = false;
        public static string IPAddress { get; set; } = DefaultIPAddress;
        public static int Port { get; set; } = DefaultPort;
        public static TimeSpan TimeOutDuration { get; set; } = TimeSpan.FromSeconds(15);

        [ConfigSection("Audit")]
        public static bool SentryEnabled { get; set; } = false;
        public static string SentryDSN { get; set; } = "";


        [ConfigSection("Graphics")]
        public static bool FullScreen { get; set; } = true;
        public static bool VSync { get; set; }
        public static bool LimitFPS { get; set; }
        public static bool ExtendedLogin { get; set; }
        public static Size GameSize { get; set; } = IntroSceneSize;
        public static string DefaultMonitor { get; set; } = string.Empty;
        public static string RenderingPipeline { get; set; } = RenderingPipelineIds.SilkDXD3D11;
        public static bool UseZlAtlasPages { get; set; } = true;
        public static bool UseD3D11SpriteBatch { get; set; } = false;
        public static TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(30);
        public static string FontName { get; set; } = "MS Sans Serif";
        public static string MapPath { get; set; } = @".\Map\";
        public static bool ClipMouse { get; set; } = false;
        public static bool DebugLabel { get; set; } = false;
        public static float FontSizeMod { get; set; } = 0.0F;
        public static string Language { get; set; } = "English";
        public static bool Borderless { get; set; } = false;
        public static bool SmoothMove { get; set; } = false;
        public static float ShadowOpacity { get; set; } = 0.5F;


        [ConfigSection("Sound")]
        public static bool SoundInBackground { get; set; } = true;
        public static int SoundOverLap { get; set; } = 5;
        public static int SystemVolume { get; set; } = 25;
        public static bool SystemVolumeMuted { get; set; } = false;
        public static int MusicVolume { get; set; } = 25;
        public static bool MusicVolumeMuted { get; set; } = false;
        public static int PlayerVolume { get; set; } = 25;
        public static bool PlayerVolumeMuted { get; set; } = false;
        public static int MonsterVolume { get; set; } = 25;
        public static bool MonsterVolumeMuted { get; set; } = false;
        public static int MagicVolume { get; set; } = 25;
        public static bool MagicVolumeMuted { get; set; } = false;

        [ConfigSection("Login")]
        public static bool RememberDetails { get; set; } = false;
        public static string RememberedEMail { get; set; } = string.Empty;
        public static string RememberedPassword { get; set; } = string.Empty;

        [ConfigSection("Game")]
        public static bool DrawEffects { get; set; } = true;
        public static bool DrawParticles { get; set; } = false;
        public static bool DrawWeather { get; set; } = true;
        public static bool ShowTargetOutline { get; set; } = true;
        public static bool ShowItemNames { get; set; } = true;
        public static bool ShowMonsterNames { get; set; } = true;
        public static bool ShowPlayerNames { get; set; } = true;
        public static bool ShowNPCNames { get; set; } = true;
        public static bool ShowUserHealth { get; set; } = true;
        public static bool ShowMonsterHealth { get; set; } = true;
        public static bool ShowDamageNumbers { get; set; } = true;
        public static bool EscapeCloseAll { get; set; } = false;
        public static bool ShiftOpenChat { get; set; } = true;
        public static bool SpecialRepair { get; set; } = true;
        public static bool RightClickDeTarget { get; set; } = true;
        public static bool HideChatBar { get; set; } = true;
        public static bool ShowMagicBarFrames { get; set; } = true;

        public static bool MonsterBoxExpanded { get; set; } = true;
        public static bool MonsterBoxVisible { get; set; } = true;
        public static bool QuestTrackerVisible { get; set; } = true;

        public static bool LogChat { get; set; } = true;

        public static int RankingClass { get; set; } = (int)RequiredClass.All;
        public static bool RankingOnline { get; set; } = true;
        public static string HighlightedItems { get; set; } = string.Empty;

        [ConfigSection("Colours")]
        public static Color LocalTextForeColour { get; set; } = Color.White;
        public static Color GMWhisperInTextForeColour { get; set; } = Color.Red;
        public static Color WhisperInTextForeColour { get; set; } = Color.Cyan;
        public static Color WhisperOutTextForeColour { get; set; } = Color.Aquamarine;
        public static Color GroupTextForeColour { get; set; } = Color.Plum;
        public static Color GuildTextForeColour { get; set; } = Color.LightPink;
        public static Color ShoutTextForeColour { get; set; } = Color.Yellow;
        public static Color GlobalTextForeColour { get; set; } = Color.Lime;
        public static Color ObserverTextForeColour { get; set; } = Color.Silver;
        public static Color HintTextForeColour { get; set; } = Color.AntiqueWhite;
        public static Color SystemTextForeColour { get; set; } = Color.Red;
        public static Color GainsTextForeColour { get; set; } = Color.GreenYellow;
        public static Color AnnouncementTextForeColour { get; set; } = Color.DarkBlue;

        public static Color LocalTextBackColour { get; set; } = Color.FromArgb(0, 0, 0, 0);
        public static Color GMWhisperInTextBackColour { get; set; } = Color.FromArgb(200, 255, 255, 255);
        public static Color WhisperInTextBackColour { get; set; } = Color.FromArgb(0, 0, 0, 0);
        public static Color WhisperOutTextBackColour { get; set; } = Color.FromArgb(0, 0, 0, 0);
        public static Color GroupTextBackColour { get; set; } = Color.FromArgb(0, 0, 0, 0);
        public static Color GuildTextBackColour { get; set; } = Color.FromArgb(0, 0, 0, 0);
        public static Color ShoutTextBackColour { get; set; } = Color.FromArgb(0, 0, 0, 0);
        public static Color GlobalTextBackColour { get; set; } = Color.FromArgb(0, 0, 0, 0);
        public static Color ObserverTextBackColour { get; set; } = Color.FromArgb(0, 0, 0, 0);
        public static Color HintTextBackColour { get; set; } = Color.FromArgb(0, 0, 0, 0);
        public static Color SystemTextBackColour { get; set; } = Color.FromArgb(200, 255, 255, 255);
        public static Color GainsTextBackColour { get; set; } = Color.FromArgb(0, 0, 0, 0);
        public static Color AnnouncementTextBackColour { get; set; } = Color.FromArgb(200, 255, 255, 255);

        [ConfigSection("Target Colours")]
        public static Color TargetMonsterLowLevelColour { get; set; } = Color.LimeGreen;
        public static Color TargetMonsterSameLevelColour { get; set; } = Color.Yellow;
        public static Color TargetMonsterHighLevelColour { get; set; } = Color.Red;
        public static Color TargetMonsterFriendlyColour { get; set; } = Color.Cyan;
        public static Color TargetPlayerFriendlyColour { get; set; } = Color.Cyan;
        public static Color TargetPlayerEnemyColour { get; set; } = Color.Red;
        public static Color TargetNPCColour { get; set; } = Color.Cyan;

        #region 辅助功能（BigPatch）

        // 说明：本段移植自 A端 Client\Envir\Config.cs 的 [ConfigSection("BigPatch")]。
        // 属性名即 Zircon.ini 中的键名，中文键可直接使用（ConfigReader 用 property.Name 作 key）。
        // 分阶段添加，阶段 2 只包含「常用」标签页需要的字段。

        [ConfigSection("BigPatch")]
        // 常用
        public static bool 免蜡烛 { get; set; } = false;
        public static bool 免助跑 { get; set; } = false;
        public static bool 免SHIFT { get; set; } = false;
        public static bool 跑不停 { get; set; } = false;
        public static bool 数字显血 { get; set; } = false;
        public static bool 在安全处有效 { get; set; } = false;
        public static bool 组队数字显血 { get; set; } = false;
        public static bool Boss提示 { get; set; } = false;
        public static bool 自动关组 { get; set; } = false;
        public static bool 清理尸体 { get; set; } = false;
        public static bool 攻击锁定目标 { get; set; } = false;
        public static bool 关闭经验提示 { get; set; } = false;
        public static bool 锁怪效果 { get; set; } = false;
        public static bool 稳如泰山 { get; set; } = false;
        public static bool 转生残影 { get; set; } = false;
        public static bool 死亡红屏 { get; set; } = false;
        public static bool 快速自动拾取 { get; set; } = false;
        public static bool Tab捡取 { get; set; } = false;
        public static bool 自动学习技能书 { get; set; } = false;
        public static bool 启用Ctrl数字控制宠物 { get; set; } = false;

        // 天气
        public static int 天气效果 { get; set; } = 0;

        // 自动练技能
        public static bool 是否开启自动练技能 { get; set; } = false;
        public static int 自动练F几技能 { get; set; } = 0;
        public static long 隔多少秒自动练技能 { get; set; } = 10;

        // 鼠标中键
        public static bool 是否开启鼠标中间按钮自动使用坐骑 { get; set; } = false;
        public static bool 是否开启鼠标中间按钮自动使用技能 { get; set; } = false;
        public static int 鼠标中间按钮使用F几的技能 { get; set; } = 0;

        // ===== 以下为阶段 4「辅助」页新增 =====

        // 自动施法 - 战士
        public static bool 自动烈火 { get; set; } = false;
        public static bool 自动翔空 { get; set; } = false;
        public static bool 自动莲月 { get; set; } = false;
        public static bool 自动快刀斩马 { get; set; } = false;
        public static bool 自动天雷锤 { get; set; } = false;
        public static bool 自动铁布衫 { get; set; } = false;
        public static bool 自动破血 { get; set; } = false;
        public static bool 自动移花接木 { get; set; } = false;
        public static bool 自动金刚之躯 { get; set; } = false;
        public static bool 自动半月弯刀 { get; set; } = false;
        public static bool 自动十方斩 { get; set; } = false;

        // 自动施法 - 法师
        public static bool 自动魔法盾 { get; set; } = false;
        public static bool 自动凝血 { get; set; } = false;
        public static bool 自动天打雷劈 { get; set; } = false;
        public static bool 自动法师连续技能 { get; set; } = true;

        // 自动施法 - 道士
        public static bool 自动道士连续技能 { get; set; } = true;
        public static bool 自动换毒 { get; set; } = false;
        public static bool 自动换符 { get; set; } = false;
        public static bool 自动阴阳盾 { get; set; } = false;
        public static bool 有宠物时自动移花接玉 { get; set; } = true;
        public static bool 自动吸星大法 { get; set; } = false;
        public static bool 自动施放神圣战甲术 { get; set; } = false;
        public static bool 自动施放幽灵盾 { get; set; } = false;
        public static bool 自动给宠物施放猛虎强势 { get; set; } = false;
        public static bool 自动强魔震法 { get; set; } = false;
        public static bool 自动施展秒影恢复血量 { get; set; } = false;

        // 自动施法 - 刺客
        public static bool 自动四花 { get; set; } = false;
        public static bool 自动风之闪避 { get; set; } = false;
        public static bool 自动风之守护 { get; set; } = false;

        // 自动技能 1 / 2
        public static long 自动技能1多长时间使用一次 { get; set; } = 0;
        public static MagicType 自动技能1 { get; set; } = MagicType.None;
        public static bool 是否开启自动技能1 { get; set; } = false;
        public static long 自动技能2多长时间使用一次 { get; set; } = 0;
        public static MagicType 自动技能2 { get; set; } = MagicType.None;
        public static bool 是否开启自动技能2 { get; set; } = false;
        public static bool 暂停辅助功能 { get; set; } = false;

        // 挂机
        public static bool 开始挂机 { get; set; } = false;
        public static bool 自动上毒 { get; set; } = false;
        public static bool 自动躲避 { get; set; } = false;
        public static bool 死亡回城 { get; set; } = false;
        public static bool 是否远战挂机 { get; set; } = false;
        public static bool 远战挂机是否使用技能 { get; set; } = false;
        public static MagicType 挂机自动技能 { get; set; } = MagicType.None;
        public static Point 范围挂机坐标 { get; set; } = Point.Empty;
        public static long 范围距离 { get; set; } = 0;
        public static bool 范围挂机 { get; set; } = false;

        // 挂机保护
        public static long 血量剩下百分之多少时自动回城 { get; set; } = 0;
        public static bool 是否开启回城保护 { get; set; } = false;
        public static long 血量剩下百分之多少时自动随机 { get; set; } = 0;
        public static bool 是否开启随机保护 { get; set; } = false;
        public static long 隔多少秒自动随机一次 { get; set; } = 0;
        public static bool 是否开启每间隔自动随机 { get; set; } = false;
        public static long 多少秒无经验或者未杀死目标自动随机 { get; set; } = 0;
        public static bool 是否开启指定时间无经验或者未杀死目标自动随机 { get; set; } = false;

        /// <summary>
        /// 技能辅助配置表（自动换符/换毒、施法目标限制）。
        /// 移植自 A 端 Config.cs:265。由 GameScene.GetMagicHelpper 按 MagicType 查找。
        /// </summary>
        public static List<MagicHelper> magics { get; set; } = new List<MagicHelper>();

        #endregion
    }
}
