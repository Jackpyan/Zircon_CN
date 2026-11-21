using Library;
using System;
using System.Collections.Generic;

namespace Server.Bots
{
    /// <summary>
    /// 技能的**命中形状**（2026-11-20，K1）。
    /// <para>
    /// 由引擎 <c>Models/Magics/**</c> 的 <c>MagicCast</c> 实现归纳而来，共 8 类：
    /// <list type="bullet">
    /// <item><see cref="Single"/>：单点（<c>Attacked()</c> / <c>MagicAttack()</c>）</item>
    /// <item><see cref="Line"/>：直线穿透 1~8 格（**正交 3 格宽、斜向 1 格宽**）</item>
    /// <item><see cref="MeleeLine"/>：近身直线 1~7 格（十方斩）</item>
    /// <item><see cref="Fan"/>：前方扇形 3 向（半月弯刀 / 龙升）</item>
    /// <item><see cref="Cross"/>：目标点十字 5 格（火墙）</item>
    /// <item><see cref="AoE"/>：目标点半径 R（<c>GetCells(location, 0, R)</c>）</item>
    /// <item><see cref="SelfAoE"/>：自身周围半径 R（毒云）</item>
    /// <item><see cref="MultiRandom"/>：随机多目标（流星火雨）</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>为什么单独成表而不是散落在判断里</b>：以后加新技能只需在
    /// <see cref="BotMagicShapes"/> 里加一行；漏登记时按 <see cref="Single"/> 处理
    /// （等价于当前行为，绝不会变差），并由审计日志提示补登记。
    /// </para>
    /// </summary>
    public enum MagicShapeKind
    {
        Single = 0,
        Line = 1,
        MeleeLine = 2,
        Fan = 3,
        Cross = 4,
        AoE = 5,
        SelfAoE = 6,
        MultiRandom = 7,
    }

    /// <summary>一条形状登记：形状 + 作用半径/距离。</summary>
    public readonly struct MagicShape
    {
        public readonly MagicShapeKind Kind;

        /// <summary>Line/MeleeLine 的射程格数；AoE/SelfAoE 的半径；其它为 0。</summary>
        public readonly int Range;

        public MagicShape(MagicShapeKind kind, int range = 0)
        {
            Kind = kind;
            Range = range;
        }

        /// <summary>是否「方向型」技能 —— 这类技能的命中区由**施法方向**决定，
        /// 目标选择必须优先挑「该方向上怪最多」的那只。</summary>
        public bool IsDirectional =>
            Kind == MagicShapeKind.Line || Kind == MagicShapeKind.MeleeLine || Kind == MagicShapeKind.Fan;
    }

    /// <summary>
    /// 技能命中形状登记表（2026-11-20，K1）。
    /// <para>
    /// <b>维护规则</b>：
    /// ① 新技能先在 <c>Models/Magics/&lt;职业&gt;/&lt;技能&gt;.cs</c> 的 <c>MagicCast</c> 里看它怎么写命中区；
    /// ② 在本表加一行；
    /// ③ 漏了也不会有事 —— 未登记按 <see cref="MagicShapeKind.Single"/> 处理，
    ///    启动时 <see cref="BotSkills"/> 会打一条「未登记命中形状」的审计日志提醒补上。
    /// </para>
    /// </summary>
    public static class BotMagicShapes
    {
        private static readonly Dictionary<MagicType, MagicShape> Shapes = new Dictionary<MagicType, MagicShape>
        {
            // ── 法师 ─────────────────────────────────────────────
            // 直线穿透（for i=1..8 + Move(CurrentLocation, direction, i)）
            { MagicType.BlowEarth, new MagicShape(MagicShapeKind.Line, 8) },
            { MagicType.FrozenEarth, new MagicShape(MagicShapeKind.Line, 8) },
            { MagicType.LightningBeam, new MagicShape(MagicShapeKind.Line, 8) },
            { MagicType.ScortchedEarth, new MagicShape(MagicShapeKind.Line, 8) },

            // 目标点 AoE
            { MagicType.Asteroid, new MagicShape(MagicShapeKind.AoE, 3) },
            { MagicType.IceRain, new MagicShape(MagicShapeKind.AoE, 3) },
            { MagicType.ChainLightning, new MagicShape(MagicShapeKind.AoE, 1) },
            { MagicType.DragonTornado, new MagicShape(MagicShapeKind.AoE, 1) },
            { MagicType.FireStorm, new MagicShape(MagicShapeKind.AoE, 1) },
            { MagicType.IceStorm, new MagicShape(MagicShapeKind.AoE, 1) },
            { MagicType.LightningWave, new MagicShape(MagicShapeKind.AoE, 1) },

            // 自身周围 AoE
            { MagicType.FrozenDragon, new MagicShape(MagicShapeKind.SelfAoE, 2) },
            { MagicType.IceBreaker, new MagicShape(MagicShapeKind.SelfAoE, 2) },

            // 随机多目标
            { MagicType.MeteorShower, new MagicShape(MagicShapeKind.MultiRandom, 8) },

            // 十字（火墙：目标点 + 上下左右，共 5 格）
            { MagicType.FireWall, new MagicShape(MagicShapeKind.Cross) },

            // 单点技能：显式登记，避免 K6 审计把它们当「未登记」反复报警。
            // 这些技能本来就只打一只，登记后审计只剩「真正的新技能」。
            { MagicType.FireBall, new MagicShape(MagicShapeKind.Single) },
            { MagicType.AdamantineFireBall, new MagicShape(MagicShapeKind.Single) },
            { MagicType.GreaterFrozenEarth, new MagicShape(MagicShapeKind.Single) },
            { MagicType.FireBounce, new MagicShape(MagicShapeKind.Single) },
            { MagicType.ThunderBolt, new MagicShape(MagicShapeKind.Single) },
            { MagicType.LightningBall, new MagicShape(MagicShapeKind.Single) },
            { MagicType.LightningStrike, new MagicShape(MagicShapeKind.Single) },
            { MagicType.ThunderStrike, new MagicShape(MagicShapeKind.Single) },
            { MagicType.IceBolt, new MagicShape(MagicShapeKind.Single) },
            { MagicType.IceBlades, new MagicShape(MagicShapeKind.Single) },
            { MagicType.IceDragon, new MagicShape(MagicShapeKind.Single) },
            { MagicType.GustBlast, new MagicShape(MagicShapeKind.Single) },
            { MagicType.Cyclone, new MagicShape(MagicShapeKind.Single) },
            { MagicType.ElementalHurricane, new MagicShape(MagicShapeKind.Single) },

            // ── 战士 ─────────────────────────────────────────────
            { MagicType.DestructiveSurge, new MagicShape(MagicShapeKind.MeleeLine, 7) },
            { MagicType.DragonRise, new MagicShape(MagicShapeKind.Fan, 2) },
            { MagicType.HalfMoon, new MagicShape(MagicShapeKind.Fan, 2) },
            { MagicType.Fetter, new MagicShape(MagicShapeKind.SelfAoE, 2) },
            { MagicType.FireSword, new MagicShape(MagicShapeKind.SelfAoE, 2) },
            { MagicType.TaecheonSword, new MagicShape(MagicShapeKind.SelfAoE, 2) },

            // ── 道士 ─────────────────────────────────────────────
            { MagicType.LifeSteal, new MagicShape(MagicShapeKind.AoE, 3) },
            { MagicType.BloodLust, new MagicShape(MagicShapeKind.AoE, 3) },
            { MagicType.CorpseExploder, new MagicShape(MagicShapeKind.AoE, 1) },
            { MagicType.PoisonCloud, new MagicShape(MagicShapeKind.SelfAoE, 2) },
            { MagicType.HeavenlySky, new MagicShape(MagicShapeKind.SelfAoE, 2) },
            { MagicType.Parasite, new MagicShape(MagicShapeKind.SelfAoE, 1) },

            { MagicType.ElementalSwords, new MagicShape(MagicShapeKind.Single) },
            { MagicType.HundredFist, new MagicShape(MagicShapeKind.Single) },
            { MagicType.Thrusting, new MagicShape(MagicShapeKind.MeleeLine, 2) },

            // ── 道士 ─────────────────────────────────────────────
            { MagicType.EvilSlayer, new MagicShape(MagicShapeKind.Single) },
            { MagicType.GreaterEvilSlayer, new MagicShape(MagicShapeKind.Single) },
            { MagicType.ExplosiveTalisman, new MagicShape(MagicShapeKind.Single) },
            { MagicType.ImprovedExplosiveTalisman, new MagicShape(MagicShapeKind.Single) },
            { MagicType.BindingTalisman, new MagicShape(MagicShapeKind.Single) },
            { MagicType.BrainStorm, new MagicShape(MagicShapeKind.Single) },
            { MagicType.SearingLight, new MagicShape(MagicShapeKind.Single) },
            { MagicType.PoisonDust, new MagicShape(MagicShapeKind.Single) },


            // ── 刺客 ─────────────────────────────────────────────
            { MagicType.HellFire, new MagicShape(MagicShapeKind.Single) },
            { MagicType.Rake, new MagicShape(MagicShapeKind.Single) },
            { MagicType.Shredding, new MagicShape(MagicShapeKind.Single) },
            { MagicType.ChainOfFire, new MagicShape(MagicShapeKind.Single) },
            { MagicType.DragonRepulse, new MagicShape(MagicShapeKind.Single) },
            { MagicType.FlamingDaggers, new MagicShape(MagicShapeKind.Single) },
            { MagicType.FlashOfLight, new MagicShape(MagicShapeKind.Single) },
            { MagicType.Hemorrhage, new MagicShape(MagicShapeKind.Single) },
            { MagicType.BurningFire, new MagicShape(MagicShapeKind.AoE, 1) },
            { MagicType.CrescentMoon, new MagicShape(MagicShapeKind.SelfAoE, 3) },
            { MagicType.FourWheels, new MagicShape(MagicShapeKind.SelfAoE, 2) },
            { MagicType.FlameSplash, new MagicShape(MagicShapeKind.SelfAoE, 2) },
            { MagicType.FullBloom, new MagicShape(MagicShapeKind.SelfAoE, 2) },
            { MagicType.Karma, new MagicShape(MagicShapeKind.SelfAoE, 2) },
            { MagicType.RedLotus, new MagicShape(MagicShapeKind.SelfAoE, 2) },
            { MagicType.SweetBrier, new MagicShape(MagicShapeKind.SelfAoE, 2) },
            { MagicType.WhiteLotus, new MagicShape(MagicShapeKind.SelfAoE, 2) },
        };

        /// <summary>
        /// 查形状。**未登记一律按 <see cref="MagicShapeKind.Single"/> 返回**（K5 兜底）：
        /// 这样以后加新技能，最坏情况是「和现在一样只打一只」，不会更差。
        /// </summary>
        public static MagicShape Of(MagicType type)
        {
            return Shapes.TryGetValue(type, out MagicShape shape)
                ? shape
                : new MagicShape(MagicShapeKind.Single);
        }

        /// <summary>该技能是否已登记形状（审计用，K6）。</summary>
        public static bool IsRegistered(MagicType type) => Shapes.ContainsKey(type);

        /// <summary>登记项总数（审计日志用）。</summary>
        public static int Count => Shapes.Count;
    }
}
