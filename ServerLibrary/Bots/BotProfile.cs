using Library;
using System;
using System.Collections.Generic;
using System.Drawing;

namespace Server.Bots
{
    /// <summary>
    /// 单个假人的静态画像：账号、角色名、职业、外观、活动地图。
    /// 由序号 <see cref="Seq"/> 决定的确定性数据，重启后同序号拿到同一套画像（幂等）。
    /// </summary>
    public sealed class BotProfile
    {
        public int Seq { get; init; }

        public string EMail { get; init; }
        public string Password { get; init; }
        public string CharacterName { get; init; }

        public MirClass Class { get; init; }
        public MirGender Gender { get; init; }
        public int HairType { get; init; }
        public Color HairColour { get; init; }
        public Color ArmourColour { get; init; }

        /// <summary>主城地图 Index（0 = 交给引擎按 BindPoint 自动选）。</summary>
        public int HomeMapIndex { get; init; }

        public string Tag => string.Format("BOT#{0:00}", Seq);

        /// <summary>
        /// 名字池。角色名有硬约束：<c>Globals.CharacterReg = ^[A-Za-z0-9]{3,15}$</c>（不支持中文），
        /// 所以这里只能放拼音或英文昵称，长度控制在 12 以内，留两位序号后缀。
        /// </summary>
        private static readonly string[] WuxiaNames =
        {
            "XiaoYao", "LingHu", "DuanYu", "QiaoFeng", "YangGuo",
            "GuoJing", "ZhangWuji", "HuangRong", "WeiXiaoBao", "LiXunHuan",
            "ChuLiuXiang", "LuXiaoFeng", "HuaManLou", "YeGuCheng", "FengQingYang",
            "RenWoXing", "TianBoGuang", "ShiPoTian", "SuXingHe", "BaiZiHua",
        };

        private static readonly string[] NetNames =
        {
            "NightWolf", "SilentRain", "MoonBlade", "IronFist", "DarkRider",
            "StormCaller", "LoneWalker", "SnowFox", "RedPhoenix", "GhostHand",
            "Thunder", "WildHunt", "FrostBite", "ShadowStep", "SwiftArrow",
            "BlueLotus", "CrimsonTide", "SteelHeart", "QuickSilver", "DawnBreak",
        };

        /// <summary>
        /// 职业轮转。**暂不放刺客**：刺客的绑定安全区（刺客藏身处）是独立小图，
        /// 0 级号从那里走不到比奇省的练级区，而跨图寻路 P0 不支持，会一直卡在城里。
        /// </summary>
        private static readonly MirClass[] ClassRotation =
        {
            MirClass.Warrior, MirClass.Wizard, MirClass.Taoist,
        };

        /// <summary>按序号生成画像。同一个 seq 永远得到同一套数据。</summary>
        public static BotProfile Create(int seq)
        {
            int index = Math.Max(seq, 1) - 1;

            // 优先用运行目录 Bots/names.txt（运营可改、热加载）；文件缺失或为空时回落到内置池。
            List<BotData.NameEntry> fileNames = BotData.Names();

            string baseName;
            MirClass cls;
            MirGender gender;

            if (fileNames.Count > 0)
            {
                BotData.NameEntry entry = fileNames[index % fileNames.Count];

                baseName = entry.Name;

                // 刺客不参与轮转：它的绑定安全区（刺客藏身处）是独立小图，0 级号走不出去。
                cls = entry.Class ?? ClassRotation[index % ClassRotation.Length];

                if (cls == MirClass.Assassin) cls = ClassRotation[index % ClassRotation.Length];

                gender = entry.Gender ?? (seq % 2 == 0 ? MirGender.Female : MirGender.Male);
            }
            else
            {
                // 内置兜底：一半武侠、一半网名，混在一起最像真的（方案 §14 已决策）。
                bool wuxia = seq % 2 == 1;
                string[] pool = wuxia ? WuxiaNames : NetNames;

                int poolIndex = (index / 2) % pool.Length;
                baseName = pool[poolIndex];

                // 名单文件只有 30 条、内置池要覆盖到 40 个序号，靠后缀拉开重名。
                string suffix = seq.ToString("00");

                baseName = CharacterNameRules.Truncate(baseName, suffix.Length) + suffix;

                cls = ClassRotation[index % ClassRotation.Length];
                gender = seq % 2 == 0 ? MirGender.Female : MirGender.Male;
            }

            string name = baseName;

            int maxLength = CharacterNameRules.MaxLength(name);

            if (name.Length > maxLength)
                name = name.Substring(0, maxLength);

            return new BotProfile
            {
                Seq = seq,
                EMail = string.Format("hnt{0:00}@mir3.local", seq),
                // 假人密码固定，仅内部使用；玩家无法用这些账号登录（BotLoginGuard 拦截）。
                Password = string.Format("Bot#{0:00}Locked", seq),
                CharacterName = name,
                Class = cls,
                Gender = gender,
                HairType = 0,
                HairColour = Color.FromArgb(0),
                // 与真人建号一致：Warrior/Wizard/Taoist 要求 ArmourColour.A == 255。
                // 刺客（暂不生成）要求 argb == 0，由 BotFactory 兜底处理。
                ArmourColour = Color.FromArgb(255, 255, 255, 255),
                HomeMapIndex = 0,
            };
        }
    }
}
