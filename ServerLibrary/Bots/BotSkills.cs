using Library;
using Library.SystemModels;
using Server.DBModels;
using Server.Envir;
using Server.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using C = Library.Network.ClientPackets;

namespace Server.Bots
{
    /// <summary>
    /// 假人的技能本。三件事：
    /// 1. <b>补技能</b>：法师/道士按引擎等级门槛直接建 <see cref="UserMagic"/>，不走「打书 → 用书」；
    /// 2. <b>选技能</b>：只挑<b>攻击型</b>（伤害）与<b>治疗型</b>；盾/隐身这些 P0 不放，
    ///    召唤不进伤害候选（无直接伤害，选出来会空放），但进学习清单并在战斗前/宠物死亡后补召
    ///    （见 <see cref="TrySummonPet"/>）；
    ///    选攻击技能时按<b>目标元素抗性</b>打分（免疫的绝不选，见 <see cref="ChooseBest"/>）；
    /// 3. <b>施法</b>：走真人同一条 <see cref="PlayerObject.Magic"/>（校验距离、蓝量、冷却），不做特权。
    /// <para>
    /// 数据事实（2026-11-10 用 tools/magiclist 查本服 System.db）：
    /// 法师第一个攻击魔法是 <b>火球术 NeedLevel1 = 7</b>，道士第一个攻击魔法是 <b>灵魂火符 NeedLevel1 = 13</b>
    /// （7 级只有治愈术）。所以 1~6 级两道职业都只能平砍 —— 与真人一致，不放宽门槛。
    /// </para>
    /// <para>所有方法都要求**在游戏线程内调用**。</para>
    /// </summary>
    public static class BotSkills
    {
        /// <summary>法师的伤害技能白名单（按 NeedLevel1 自动筛，这里只负责「哪些算攻击」）。</summary>
        private static readonly MagicType[] WizardAttack =
        {
            MagicType.FireBall, MagicType.LightningBall, MagicType.IceBolt, MagicType.GustBlast,
            MagicType.AdamantineFireBall, MagicType.ThunderBolt, MagicType.IceBlades, MagicType.Cyclone,
            MagicType.ScortchedEarth, MagicType.LightningBeam, MagicType.FrozenEarth, MagicType.BlowEarth,
            MagicType.FireWall, MagicType.FireStorm, MagicType.LightningWave, MagicType.IceStorm,
            MagicType.DragonTornado, MagicType.GreaterFrozenEarth, MagicType.ChainLightning,
            MagicType.MeteorShower, MagicType.Tempest, MagicType.JudgementOfHeaven,
            MagicType.ThunderStrike, MagicType.FireBounce, MagicType.ElementalHurricane,
            MagicType.Asteroid, MagicType.IceRain, MagicType.FrostBite, MagicType.LightningStrike,
        };

        /// <summary>道士：攻击 + 治疗（召唤兽走 <see cref="TrySummonPet"/> 补召，不进伤害表）。</summary>
        /// <para>
        /// <b>施毒术（PoisonDust）不在这个表里</b>（2026-11-13 用户实测报的）：
        /// 它是<b>纯 debuff</b> —— <c>PoisonDust.MagicCast</c> 只 <c>ApplyPoison</c>，一点直接伤害都没有。
        /// 放进攻击表就会被 <see cref="PickBest"/> 当成「伤害技能」选出来，
        /// 假人站在 10 格外一直放毒、永远不贴脸砍，怪掉血极慢甚至不掉血。
        /// 现在它只在 <see cref="TryApplyPoison"/> 里**战斗中循环补毒**（见 2026-11-14 道士施毒术逻辑）。
        /// </para>
        private static readonly MagicType[] TaoistAttack =
        {
            MagicType.ExplosiveTalisman, MagicType.EvilSlayer,
            MagicType.GreaterEvilSlayer, MagicType.CelestialLight, MagicType.LifeSteal,
            MagicType.ImprovedExplosiveTalisman, MagicType.SearingLight, MagicType.SoulResonance,
        };

        /// <summary>
        /// 每个技能要消耗的「弹药」：<see cref="EquipmentSlot"/> + 每次数量。
        /// <para>
        /// <b>这是必须挡的一道门</b>：这些魔法在 <c>MagicCast</c> 里第一件事就是
        /// <c>Player.UseAmulet(...)</c> / <c>UsePoison(...)</c>，失败就 <c>break</c> ——
        /// 结果是「动画照播、蓝照扣、一个目标都没有」，假人站着空放，永远打不死怪。
        /// 道士升到 12 级学会施毒术、13 级学会灵魂火符之后，如果身上没有毒药粉/护身符，
        /// 就会永久卡在这个循环里（实测症状：后台「最近技能」一直显示同一个技能、击杀数不涨）。
        /// </para>
        /// </summary>
        private static readonly Dictionary<MagicType, (EquipmentSlot Slot, int Count)> AmmoCost = new()
        {
            [MagicType.PoisonDust] = (EquipmentSlot.Poison, 1),
            [MagicType.ExplosiveTalisman] = (EquipmentSlot.Amulet, 1),
            [MagicType.ImprovedExplosiveTalisman] = (EquipmentSlot.Amulet, 1),
            [MagicType.EvilSlayer] = (EquipmentSlot.Amulet, 1),
            [MagicType.GreaterEvilSlayer] = (EquipmentSlot.Amulet, 1),
            [MagicType.CelestialLight] = (EquipmentSlot.Amulet, 20),
            [MagicType.LifeSteal] = (EquipmentSlot.Amulet, 2),
            // 召唤技能也耗护身符（2026-11-17 补）：召唤骷髅 1 个、召唤神兽 5 个。
            // 不登记的话 HasAmmo 返回 false 挡不住空放——引擎 UseAmulet 失败时 Cast=false
            // 但蓝照扣（MagicConsume 在 cast 判断前调用），假人会在没符时反复空放烧蓝。
            [MagicType.SummonSkeleton] = (EquipmentSlot.Amulet, 1),
            [MagicType.SummonShinsu] = (EquipmentSlot.Amulet, 5),
        };

        /// <summary>
        /// 这个玩家**已经学会、且需要弹药**的技能清单（补给时据此决定买哪种弹药）。
        /// 只返回已学的：没学的技能现在用不上，买回来只是压负重。
        /// </summary>
        public static IEnumerable<(EquipmentSlot Slot, MagicType Type)> AmmoSkills(PlayerObject player)
        {
            if (player?.Character == null) yield break;

            foreach (KeyValuePair<MagicType, (EquipmentSlot Slot, int Count)> pair in AmmoCost)
            {
                if (!player.GetMagic(pair.Key, out MagicObject _)) continue;

                yield return (pair.Value.Slot, pair.Key);
            }
        }

        /// <summary>攻击技能表（供自检用：确认纯 debuff 没混进去）。</summary>
        public static IEnumerable<MagicType> AttackSkillsOf(MirClass cls)
        {
            switch (cls)
            {
                case MirClass.Wizard:
                    return WizardAttack;
                case MirClass.Taoist:
                    return TaoistAttack;
                case MirClass.Warrior:
                    return WarriorAttack;
                case MirClass.Assassin:
                    // 只返回 MagicCast 型（AssassinMagic）—— 这些能走 player.Magic() 施放，
                    // 供 PickAttack → TryCastAttack 的魔法射程分支选技能。
                    // AttackCast 型（AssassinAttack）不能走 Magic()，在 BotBrain 贴脸分支单独处理。
                    return AssassinMagic;
                default:
                    return Array.Empty<MagicType>();
            }
        }

        /// <summary>登记在弹药表里的全部技能（供自检用）。</summary>
        public static IEnumerable<MagicType> AmmoSkillTypes => AmmoCost.Keys;

        /// <summary>这个技能是否需要弹药。</summary>
        public static bool RequiresAmmo(MagicType type) => AmmoCost.ContainsKey(type);

        /// <summary>这个技能吃哪个槽位的弹药（没登记返回 <see cref="EquipmentSlot.Weapon"/>，仅自检用）。</summary>
        public static EquipmentSlot AmmoSlotOf(MagicType type)
        {
            return AmmoCost.TryGetValue(type, out (EquipmentSlot Slot, int Count) cost)
                ? cost.Slot
                : EquipmentSlot.Weapon;
        }

        /// <summary>身上这类弹药够不够放一次（没登记的技能视为不需要弹药）。</summary>
        public static bool HasAmmo(PlayerObject player, MagicType type)
        {
            if (!AmmoCost.TryGetValue(type, out (EquipmentSlot Slot, int Count) cost)) return true;

            UserItem item = player?.Equipment?[(int)cost.Slot];

            // 类型也要对：`UseAmulet` / `UsePoison` 都会校验 ItemType，
            // 槽位里塞了别的东西（理论上不会，但别赌）同样算没有。
            ItemType expect = cost.Slot == EquipmentSlot.Poison ? ItemType.Poison : ItemType.Amulet;

            if (item?.Info == null || item.Info.ItemType != expect) return false;

            return item.Count >= cost.Count;
        }

        /// <summary>
        /// 数一数某个装备槽里的数量（弹药类物品 <c>Count</c> 就是「还剩几次」）。
        /// 槽位空 / 类型不对一律算 0。
        /// </summary>
        public static int CountEquip(PlayerObject player, EquipmentSlot slot)
        {
            UserItem item = player?.Equipment?[(int)slot];

            if (item == null) return 0;

            return (int)Math.Max(0, item.Count);
        }

        /// <summary>
        /// 是否**快断弹**了（步骤 3-3，2026-11-16）：任一**已学会**技能所需的弹药
        /// （护身符 / 毒药粉）低于 <paramref name="threshold"/> 就返回 true。
        /// <para>
        /// 供补给触发判据用（每帧调用，内部不建对象、不拆字符串）。
        /// 只数已学技能 —— 没学的用不上弹药，不必为它跑一趟补给。
        /// 与 <see cref="HasAmmo"/> 同构（槽位 / ItemType 校验一致），
        /// 区别是阈值可自定义（<see cref="HasAmmo"/> 用「够放一次」的门槛）。
        /// </para>
        /// </summary>
        public static bool HasAmmoBelow(PlayerObject player, int threshold)
        {
            if (player?.Character == null) return false;

            foreach (KeyValuePair<MagicType, (EquipmentSlot Slot, int Count)> pair in AmmoCost)
            {
                if (!player.GetMagic(pair.Key, out MagicObject _)) continue;

                UserItem item = player.Equipment?[(int)pair.Value.Slot];

                ItemType expect = pair.Value.Slot == EquipmentSlot.Poison ? ItemType.Poison : ItemType.Amulet;

                if (item?.Info == null || item.Info.ItemType != expect) continue;

                if (item.Count < threshold) return true;
            }

            return false;
        }

        /// <summary>
        /// 法师的**群攻**技能 —— 一次能打多个目标。
        /// <para>
        /// 判定依据是引擎实现，不是猜的：这些魔法的 <c>MagicCast</c> 里都在调
        /// <c>GetCells(location, 0, 半径)</c> 遍历一片格子
        /// （2026-11-13 逐个 grep `ServerLibrary/Models/Magics/**` 确认，半径写在注释里）。
        /// </para>
        /// <para>
        /// 火墙（FireWall）没进来：它的实现里没有 GetCells，是「在地上留一片持续伤害区域」，
        /// 命中逻辑和普通 AOE 不同，先不放，避免出现「放了火墙但怪没掉血」的观感问题。
        /// </para>
        /// </summary>
        private static readonly MagicType[] WizardGroup =
        {
            MagicType.FireStorm,       // GetCells(location, 0, 1)
            MagicType.IceStorm,        // GetCells(location, 0, 1)
            MagicType.DragonTornado,   // GetCells(location, 0, 1)
            MagicType.Tempest,         // GetCells(location, 0, 1)
            MagicType.ChainLightning,  // GetCells(location, 0, 1)
            MagicType.LightningWave,   // GetCells(location, 0, 1)
            MagicType.IceRain,         // GetCells(location, 0, 3, true)
            MagicType.Asteroid,        // GetCells(location, 0, 3)
            MagicType.MeteorShower,    // 随机打 6 + 技能等级 个目标（不是 GetCells，单独确认过）
        };

        /// <summary>
        /// 道士的群攻技能。
        /// <para>
        /// 本服道士真正 AOE 的只有 <c>LifeSteal</c>（GetCells 半径 3）。
        /// 群体施毒（GreaterPoisonDust）没有范围判定、毒云（PoisonCloud）又不在
        /// <see cref="Wanted"/> 的补技能名单里，所以现阶段道士的群攻基本用不上 ——
        /// 这与真人低级道士只能单体火符的体验一致，不放宽。
        /// </para>
        /// </summary>
        private static readonly MagicType[] TaoistGroup =
        {
            MagicType.LifeSteal,       // GetCells(location, 0, 3)
        };

        private static readonly MagicType[] TaoistHeal =
        {
            MagicType.Heal, MagicType.MassHeal,
        };

        /// <summary>
        /// 道士的 debuff（学，但不进「伤害技能」候选）。
        /// 目前只有施毒术：<see cref="TryApplyPoison"/> 会在战斗中循环补毒。
        /// </summary>
        private static readonly MagicType[] TaoistDebuff =
        {
            MagicType.PoisonDust,
        };

        /// <summary>
        /// 道士的召唤技能（3-4，2026-11-17 反转实施）：召唤骷髅 / 召唤神兽。
        /// <para>
        /// <b>进 <see cref="Wanted"/> 学习清单，不进攻击表/群攻表</b> —— 召唤是辅助不是伤害，
        /// 被 <see cref="PickBest"/> 当伤害技能选出来会空放（<c>MagicCast</c> 只召唤宠物，无直接伤害）。
        /// 战斗前/宠物死亡后的补召走 <see cref="TrySummonPet"/>，不经过伤害技能选择。
        /// </para>
        /// <para>
        /// 数据事实（2026-11-16 用 tools/magicq 查本服 System.db，2026-11-17 复核）：
        /// 召唤骷髅 <c>SummonSkeleton</c> Lv17，蓝耗 10/15，消耗护身符 1 个；
        /// 召唤神兽 <c>SummonShinsu</c> Lv30，蓝耗 15/15，消耗护身符 5 个。两者 Delay=0（无冷却）。
        /// 引擎实现核对（<c>ServerLibrary/Models/Magics/Taoist/**</c>）：
        /// <c>MagicCast</c> 只 <c>UseAmulet</c> + 延迟 500ms 在目标格召唤宠物，
        /// 对目标**零直接伤害**，放进伤害表只会空放白扣蓝耗符。
        /// </para>
        /// <para>
        /// 更高阶召唤（金甲骷髅 Lv33、魔兽 Lv50、亡灵 Lv95）本阶段不纳入，只补骷髅+神兽。
        /// </para>
        /// </summary>
        private static readonly MagicType[] TaoistSummon =
        {
            MagicType.SummonSkeleton,
            MagicType.SummonShinsu,
        };

        /// <summary>
        /// 战士的攻击技能（均为 Toggle 型，与法师/道士的施法型不同）。
        /// <para>
        /// <b>Toggle 型技能的触发方式</b>：必须先 <c>magic.Toggle(true)</c> 开启
        /// （设 <c>Character.CanXxx = true</c> 并通知客户端），再显式
        /// <c>player.Attack(dir, MagicType.Xxx)</c> —— 传 <see cref="MagicType.None"/>
        /// 是纯平砍，<c>AttackCast</c> 检查 <c>attackType != Type</c> 直接返回 Cast=false，
        /// 技能不会触发（这是假人战士此前只平砍不用技能的根因）。
        /// </para>
        /// <para>数据事实（2026-11-16 用 tools/magicq 查本服 System.db）：</para>
        /// <list type="bullet">
        /// <item>刺杀剑术 Thrusting = 103，19 级，2 格正前方直线（隔位刺杀）；</item>
        /// <item>半月弯刀 HalfMoon = 104，24 级，左前/正前/右前三向群攻；</item>
        /// <item>十方斩 DestructiveSurge = 109，40 级，正前方 1~7 格直线群攻。</item>
        /// </list>
        /// </summary>
        private static readonly MagicType[] WarriorAttack =
        {
            MagicType.Thrusting,
            MagicType.HalfMoon,
            MagicType.DestructiveSurge,
        };

        /// <summary>
        /// 战士的群攻技能：半月弯刀（三向近身）与十方斩（正前方 1~7 格直线）。
        /// 周围怪 ≥2 只时优先使用（与法师/道士的 preferGroup 逻辑同构）。
        /// </summary>
        private static readonly MagicType[] WarriorGroup =
        {
            MagicType.HalfMoon,
            MagicType.DestructiveSurge,
        };

        /// <summary>
        /// 刺客的 AttackCast 型攻击技能（贴脸显式触发，走 <c>player.Attack(dir, type)</c>）。
        /// <para>
        /// <b>为什么是这几个、而血之花/致命一击/满月劫等不放</b>（2026-11-16 逐个读
        /// <c>ServerLibrary/Models/Magics/Assassin/**</c> 确认）：
        /// <list type="bullet">
        /// <item><b>可显式触发</b>：<c>AttackCast</c> 在条件满足时设 <c>Cast=true</c>，
        /// 假人 <c>Attack(dir, type)</c> 能真正打出技能。</item>
        /// <item><b>排除被动挂载型</b>：血之花(426)/致命一击(442)/修身(403)/藤蔓舞(402)/
        /// 双武器精通(448) 的 <c>AttackCast</c> 只 <c>Magics.Add(Type)</c> 不设 <c>Cast=true</c>，
        /// 伤害经被动 <c>ModifyPowerAdditionner</c>/<c>GetPassiveStats</c> 挂到平砍 ——
        /// 显式 <c>Attack(dir, type)</c> 会打引擎错误日志且不触发技能，放进来只会空放。</item>
        /// <item><b>排除依赖隐身的</b>：满月劫(408)/残月(418)/因果报应(416)/裂伤(413)/龙血(441)
        /// 的 <c>AttackCast</c>/<c>MagicCast</c> 要求 <c>BuffType.Cloak</c>（隐身）或内部
        /// <c>CanAttack</c> 状态，假人不学隐身（P0 不放），条件恒不满足，放进去同样是空放+错误日志。</item>
        /// </list>
        /// </para>
        /// <para>
        /// <b>烈焰飞溅(425)是 Toggle 型</b>（检查 <c>Character.CanFlameSplash</c>），
        /// 与战士刺杀剑术同构：<see cref="Sync"/> 里 <see cref="EnableAssassinToggles"/> 显式开启。
        /// </para>
        /// </summary>
        private static readonly MagicType[] AssassinAttack =
        {
            MagicType.FullBloom,      // 花开富贵：AttackCast 检查 attackType/冷却/蓝耗，Cast=true
            MagicType.WhiteLotus,     // 白莲：同上
            MagicType.RedLotus,       // 红莲：同上
            MagicType.SweetBrier,     // 刺蔷薇：同上
            MagicType.FlameSplash,    // 烈焰飞溅：Toggle 型，检查 CanFlameSplash + 蓝耗
        };

        /// <summary>
        /// 刺客的 MagicCast 型攻击技能（走 <c>player.Magic()</c> 触发，与法师/道士同路径）。
        /// <para>
        /// 这些技能的 <c>MagicCast</c> 校验 <c>CanAttackTarget</c> 后经 <c>DelayMagic</c> →
        /// <c>MagicComplete</c> → <c>Player.MagicAttack</c> 直接对目标/区域伤害，引擎不检查直线路径
        /// （与法师/道士同结论，D13 遮挡判断前提不成立，无需改动）。
        /// </para>
        /// <para>
        /// <b>排除纯 buff/控制/召唤</b>：狂风(437)/新生(427) 是纯 buff，深渊(433)/锁链(446)/
        /// 怨灵束缚(409) 是纯 debuff（只 ApplyPoison 无直接伤害），毒云(404)/地狱烈焰(452) 是
        /// 地面持续伤害区域（类似火墙，命中逻辑不同先不放），燕舞(428) 会瞬移玩家位置（复杂先不放），
        /// 龙之震击(430) CheckCost 自定义扣血扣蓝（假人血量可能不够，先不放）。
        /// </para>
        /// </summary>
        private static readonly MagicType[] AssassinMagic =
        {
            MagicType.Hemorrhage,      // 出血：MagicCast → MagicAttack 直接伤害
            MagicType.FlamingDaggers,  // 烈焰飞刀：同上
            MagicType.Shredding,       // 撕裂：同上
            MagicType.HellFire,        // 地狱火：MagicAttack + 地狱火毒
            MagicType.FlashOfLight,    // 闪光术：前方 2 格 MagicAttack
        };

        /// <summary>
        /// 刺客的群攻技能（一次打多个目标，MagicCast 型，走 <c>player.Magic()</c>）。
        /// <para>判定依据：<c>MagicCast</c> 里 <c>GetCells(location, 0, 半径)</c> 遍历一片格子。</para>
        /// </summary>
        private static readonly MagicType[] AssassinGroup =
        {
            MagicType.FourWheels,      // GetCells(当前位置, 0, 2)
            MagicType.CrescentMoon,    // GetCells(当前位置, 0, 3)
        };

        /// <summary>该职业要补的全部技能（攻击 + 治疗 + debuff）。战士返回攻击技能（Toggle 型，学完需开启）。</summary>
        public static IEnumerable<MagicType> Wanted(MirClass cls)
        {
            switch (cls)
            {
                case MirClass.Wizard:
                    return WizardAttack;
                case MirClass.Taoist:
                    // 攻击 + 治疗 + debuff + 召唤：召唤只学不用（见 TaoistSummon 注释）。
                    return TaoistAttack.Concat(TaoistHeal).Concat(TaoistDebuff).Concat(TaoistSummon);
                case MirClass.Warrior:
                    return WarriorAttack;
                case MirClass.Assassin:
                    // AttackCast 型 + MagicCast 型 + 群攻，全部要补（Sync 按等级门槛自动学）。
                    return AssassinAttack.Concat(AssassinMagic).Concat(AssassinGroup);
                default:
                    return Array.Empty<MagicType>();
            }
        }

        /// <summary>该职业的群攻技能（没有就是空数组）。</summary>
        public static IEnumerable<MagicType> GroupWanted(MirClass cls)
        {
            switch (cls)
            {
                case MirClass.Wizard:
                    return WizardGroup;
                case MirClass.Taoist:
                    return TaoistGroup;
                case MirClass.Warrior:
                    return WarriorGroup;
                case MirClass.Assassin:
                    return AssassinGroup;
                default:
                    return Array.Empty<MagicType>();
            }
        }

        /// <summary>
        /// 按当前等级补齐技能，返回本轮新增数量。幂等：已学的跳过，所以可以每次上线/升级都调。
        /// </summary>
        public static int Sync(PlayerObject player)
        {
            if (player?.Character == null) return 0;
            if (!Config.BotAutoSkill) return 0;

            int added = 0;

            foreach (MagicType type in Wanted(player.Class))
            {
                if (player.GetMagic(type, out MagicObject _)) continue;

                MagicInfo info = FindInfo(type, player.Class);

                if (info == null) continue;

                // 引擎门槛：等级不够就等下次升级，不打折。
                if (player.Level < info.NeedLevel1) continue;

                UserMagic magic = SEnvir.UserMagicList.CreateNewObject();

                magic.Character = player.Character;
                magic.Info = info;

                // SetupMagic 才会把 MagicObject 塞进 MagicObjects，不调这个等于学了不会用。
                player.SetupMagic(magic);

                added++;
            }

            if (added > 0)
                BotLog.Info(player.Bot?.Profile?.Seq ?? 0,
                    string.Format("补齐技能 {0} 个（{1} 级，{2}）", added, player.Level, player.Class));

            // 战士的 Toggle 型技能（刺杀剑术/半月弯刀/十方斩）：学完必须显式开启，
            // 否则 CanXxx 默认 false，AttackCast 检查不过，Attack(dir, MagicType.Xxx) 不会触发技能。
            if (player.Class == MirClass.Warrior)
                EnableWarriorToggles(player);

            // 刺客的 Toggle 型技能（烈焰飞溅）：同上，AttackCast 检查 CanFlameSplash，不开启不触发。
            if (player.Class == MirClass.Assassin)
                EnableAssassinToggles(player);

            return added;
        }

        /// <summary>
        /// 开启刺客的 Toggle 型攻击技能（烈焰飞溅）。
        /// <para>
        /// 烈焰飞溅是开关型：<c>AttackCast</c> 检查 <c>attackType != Type || !CanFlameSplash</c>，
        /// 不开启（CanFlameSplash=false）时即使显式 <c>Attack(dir, MagicType.FlameSplash)</c> 也返回 Cast=false，
        /// 技能不会触发。学完（Sync）后必须调 <c>Toggle(true)</c> 开启，并通知客户端。
        /// </para>
        /// <para>幂等：已开启的跳过，避免每次 Sync 重复发包。</para>
        /// </summary>
        private static void EnableAssassinToggles(PlayerObject player)
        {
            if (player?.Character == null) return;

            foreach (MagicType type in AssassinAttack)
            {
                if (!player.GetMagic(type, out MagicObject magic)) continue;

                bool alreadyOn = type switch
                {
                    MagicType.FlameSplash => player.Character.CanFlameSplash,
                    _ => true,
                };

                if (alreadyOn) continue;

                magic.Toggle(true);
            }
        }

        /// <summary>
        /// 从刺客的 AttackCast 型技能里挑一个当前能放的（贴脸平砍触发用）。
        /// <para>
        /// 与 <see cref="PickAttack"/> 同构：已学 / 蓝够（<see cref="MagicObject.CheckCost"/>）/
        /// 冷却 / 弹药 / 按目标抗性打分（免疫的不选），但候选是 <see cref="AssassinAttack"/>
        /// （AttackCast 型，走 <c>player.Attack(dir, type)</c>，不是 <c>player.Magic()</c>）。
        /// </para>
        /// <para>
        /// 返回 <see cref="MagicType.None"/> 表示没有可用技能，调用方退回平砍。
        /// Toggle 型技能（烈焰飞溅）的开启状态由 <see cref="TryAssassinSkillAttack"/> 最终把关，
        /// 这里只按蓝耗/冷却/抗性粗选。
        /// </para>
        /// </summary>
        public static MagicType PickAssassinMeleeAttack(PlayerObject player, MonsterObject target)
        {
            return PickBest(player, AssassinAttack, target, null);
        }

        /// <summary>
        /// 开启战士的 Toggle 型攻击技能（刺杀剑术/半月弯刀/十方斩）。
        /// <para>
        /// 这些技能是开关型：<c>AttackCast</c> 检查 <c>attackType != Type || !CanXxx</c>，
        /// 不开启（CanXxx=false）时即使显式 <c>Attack(dir, MagicType.Xxx)</c> 也返回 Cast=false，
        /// 技能不会触发。学完（Sync）后必须调 <c>Toggle(true)</c> 开启，并通知客户端。
        /// </para>
        /// <para>幂等：已开启的跳过，避免每次 Sync 重复发包。</para>
        /// </summary>
        private static void EnableWarriorToggles(PlayerObject player)
        {
            if (player?.Character == null) return;

            foreach (MagicType type in WarriorAttack)
            {
                if (!player.GetMagic(type, out MagicObject magic)) continue;

                bool alreadyOn = type switch
                {
                    MagicType.Thrusting => player.Character.CanThrusting,
                    MagicType.HalfMoon => player.Character.CanHalfMoon,
                    MagicType.DestructiveSurge => player.Character.CanDestructiveSurge,
                    _ => true,
                };

                if (alreadyOn) continue;

                magic.Toggle(true);
            }
        }

        /// <summary>按 MagicType + 职业找定义。同名技能在库里只有一条，这里多带一个职业判断防脏数据。</summary>
        private static MagicInfo FindInfo(MagicType type, MirClass cls)
        {
            foreach (MagicInfo info in SEnvir.MagicInfoList.Binding)
            {
                if (info.Magic != type) continue;
                if (!info.MatchesClass(cls)) continue;

                return info;
            }

            return null;
        }

        /// <summary>
        /// 挑一个当前能放的攻击魔法（等级够、蓝够、冷却好），取**实际伤害期望最高**的那个。
        /// 返回 <see cref="MagicType.None"/> 表示这一刀只能用平的。
        /// <para>
        /// <paramref name="preferGroup"/> = true 时先挑**群攻**（周围怪 ≥2 只），
        /// 群攻全部冷却中/蓝不够/被抗性吃掉再退回单体 —— 这条「群攻不可用就退回单体，别干等」
        /// 是照客户端辅助挂机的 <c>MapControl.PickAutoSkillType</c> 抄的。
        /// </para>
        /// <para>
        /// <b>候选只取 <see cref="AttackSkillsOf"/>（纯伤害表）</b>，不能取 <see cref="Wanted"/> ——
        /// 后者是「要学什么」的清单，含治疗与施毒术。以前这里传的是 <c>Wanted</c>，
        /// 于是 12 级道士（还没有灵魂火符）会把施毒术当攻击技能选出来，
        /// 站在 10 格外一直放毒、永远不贴脸砍（2026-11-13 用户报的那个 bug 的另一半）。
        /// </para>
        /// </summary>
        public static MagicType PickAttack(PlayerObject player, MonsterObject target, bool preferGroup = false)
        {
            return PickAttack(player, target, preferGroup, out _);
        }

        /// <summary>
        /// 同上，额外带出「为什么没选更强的那个」的说明（抗性跳过），供日志用。
        /// </summary>
        public static MagicType PickAttack(PlayerObject player, MonsterObject target, bool preferGroup, out string note)
        {
            note = null;

            if (player?.Character == null) return MagicType.None;

            List<string> notes = new List<string>();

            MagicType best = MagicType.None;

            if (preferGroup)
                best = PickBest(player, GroupWanted(player.Class), target, notes);

            if (best == MagicType.None)
                best = PickBest(player, AttackSkillsOf(player.Class), target, notes);

            if (notes.Count > 0) note = string.Join("；", notes);

            return best;
        }

        /// <summary>
        /// 抗性 → 魔法伤害系数，口径与引擎 <c>PlayerObject.MagicAttack</c> 完全一致：
        /// 每个元素分支都是 <c>power -= power * res / 10</c>（含负抗，负抗就是加成）。
        /// <para>
        /// 所以 <b>res = 10 是免疫（系数 0）</b>、<b>res = 5 是减半</b>、
        /// <b>res = -3 是弱点（系数 1.3，多受 30%）</b>。
        /// </para>
        /// <para>
        /// ⚠️ 别照抄 <c>MonsterObject</c> 里那两行：那边（怪物打人 / 玩家平砍）负抗是
        /// <c>res / 5</c>（多受 60%），<b>魔法这条路径只有 /10</b>。
        /// 这个差异是 2026-11-13 对着源码逐个 case 核过的，抄错了只会让排序偏一点、不会崩，
        /// 所以特别容易一直错下去。
        /// </para>
        /// </summary>
        public static double ResistanceMultiplier(int res)
        {
            if (res >= 10) return 0;

            return 1 - res / 10.0;
        }

        /// <summary>
        /// 从候选里挑「期望伤害」最高的一个：评分 = 抗性系数 × (等级门槛 + 10)。
        /// <para>
        /// <b>为什么不是直接取门槛最高的</b>：本库 309 只怪里 204 只有非零抗性，
        /// 最高到 10（完全免疫）。冰抗 +7 的怪被冰系技能打只剩 30% 伤害，
        /// 这时低两阶但吃满伤害的技能反而更疼。等级门槛当「基础伤害」的代理
        /// （本服技能强度基本随门槛单调上升），乘上抗性系数就是期望伤害的粗排。
        /// </para>
        /// <para>系数 ≤ 0（免疫）的候选**永远不选**；全部免疫时返回 <see cref="MagicType.None"/>，退回平砍。</para>
        /// </summary>
        public static MagicType ChooseBest(IEnumerable<(MagicType Type, int Need, double Mult)> candidates)
        {
            MagicType best = MagicType.None;
            double bestScore = 0;
            int bestNeed = -1;

            foreach ((MagicType type, int need, double mult) in candidates)
            {
                if (mult <= 0) continue;

                double score = mult * (need + 10);

                // 分高者胜；同分取门槛高的（同一档里更高级的那个通常更强）
                if (score > bestScore + 1e-6 || (Math.Abs(score - bestScore) <= 1e-6 && need > bestNeed))
                {
                    best = type;
                    bestScore = score;
                    bestNeed = need;
                }
            }

            return best;
        }

        /// <summary>从候选里挑期望伤害最高的那个（抗性系数来自目标身上真实的抗性）。</summary>
        private static MagicType PickBest(PlayerObject player, IEnumerable<MagicType> candidates, MonsterObject target, List<string> notes)
        {
            List<(MagicType Type, int Need, double Mult)> list = new List<(MagicType, int, double)>();

            foreach (MagicType type in candidates)
            {
                if (!player.GetMagic(type, out MagicObject magic)) continue;

                if (!magic.CanUseMagic()) continue;
                if (!magic.CheckCost()) continue;
                if (SEnvir.Now < magic.Magic.Cooldown) continue;

                // 治疗技能不能当攻击用（MagicCast 会返回 Cast=false，白费一次动作）。
                if (TaoistHeal.Contains(type)) continue;

                // 弹药不够别选 —— 选了也是空放（见 AmmoCost 的注释）。
                if (!HasAmmo(player, type)) continue;

                Element element = magic.GetElement(Element.None);
                int res = target?.Stats == null ? 0 : target.Stats.GetResistanceValue(element);
                double mult = ResistanceMultiplier(res);

                if (mult <= 0)
                {
                    notes?.Add(string.Format("跳过 {0}（{1}，目标{1}抗 {2}，完全免疫）",
                        BotText.Magic(type), ElementName(element), res));

                    continue;
                }

                list.Add((type, magic.Magic.Info.NeedLevel1, mult));
            }

            return ChooseBest(list);
        }

        /// <summary>技能的元素名（中文，日志用）。</summary>
        public static string ElementName(Element element)
        {
            switch (element)
            {
                case Element.Fire: return "火";
                case Element.Ice: return "冰";
                case Element.Lightning: return "雷";
                case Element.Wind: return "风";
                case Element.Holy: return "神圣";
                case Element.Dark: return "暗黑";
                case Element.Phantom: return "幻影";
                default: return "物理";
            }
        }

        /// <summary>
        /// 给后台「最近动作」用的一句话：这个技能对这个怪的抗性关系。
        /// 例：<c>火·目标火抗-3 弱点 +60%</c> / <c>冰·目标冰抗+5 减伤 50%</c>。
        /// </summary>
        public static string ElementNote(PlayerObject player, MonsterObject target, MagicType type)
        {
            if (player == null || target?.Stats == null || type == MagicType.None) return null;
            if (!player.GetMagic(type, out MagicObject magic)) return null;

            Element element = magic.GetElement(Element.None);
            int res = target.Stats.GetResistanceValue(element);

            if (res == 0) return string.Format("{0}·目标无{0}抗", ElementName(element));

            if (res < 0)
                return string.Format("{0}·目标{0}抗{1} 弱点 +{2:P0}", ElementName(element), res, -res / 10.0);

            return string.Format("{0}·目标{0}抗+{1} 减伤 {2:P0}", ElementName(element), res, res / 10.0);
        }


        /// <summary>
        /// 对怪放一个攻击魔法。距离由引擎校验（<c>Globals.MagicRange = 10</c>），
        /// 超出范围返回 false，调用方退回走近砍。
        /// <para>会按目标元素抗性挑技能：被免疫的绝不放（放了也是 0 伤害 + 白扣蓝）。</para>
        /// </summary>
        public static bool TryCastAttack(PlayerObject player, MonsterObject monster, bool preferGroup = false)
        {
            if (player == null || monster == null) return false;
            if (player.Dead || monster.Dead || monster.Node == null) return false;

            MagicType type = PickAttack(player, monster, preferGroup);

            if (type == MagicType.None) return false;

            int distance = BotNavigator.Distance(player.CurrentLocation, monster.CurrentLocation);

            if (distance > Globals.MagicRange) return false;

            if (!player.CanCast) return false;

            player.Magic(new C.Magic
            {
                Type = type,
                Target = monster.ObjectID,
                Location = monster.CurrentLocation,
                Direction = Functions.DirectionFromPoint(player.CurrentLocation, monster.CurrentLocation),
            });

            return true;
        }

        /// <summary>
        /// 给目标补毒：只要绿毒 / 红毒缺一种就放一次施毒术，返回本次是否施放。
        /// <para>
        /// <b>为什么每次只放一次、要调用方每帧调</b>：引擎实现里
        /// <c>PoisonDust.MagicCast → Player.UsePoison(1, out shape)</c>，
        /// <c>shape == 0 ? PoisonType.Green : PoisonType.Red</c> ——
        /// <b>一次施毒随机只给一种毒</b>。「上 2 种毒」必须连续多次施毒直到绿红都齐。
        /// 而 <c>MagicCast</c> 会立刻写 <c>magic.Magic.Cooldown</c>，
        /// 同一帧内连放第二次必然被冷却挡住，所以「连续施毒」天然只能跨帧进行 ——
        /// 本方法每次调用放一次，<see cref="ProcessFight"/> 每帧调用，直到两种都齐。
        /// </para>
        /// <para>
        /// 三道门缺一不可：<b>有毒药粉</b>（否则 <c>UsePoison</c> 失败 = 空放）、
        /// <b>目标还缺毒</b>（两种都齐了就不浪费）、<b>冷却/蓝够</b>。
        /// </para>
        /// </summary>
        public static bool TryApplyPoison(PlayerObject player, MonsterObject monster)
        {
            if (player?.Character == null || monster == null) return false;

            if (player.Class != MirClass.Taoist) return false;
            if (player.Dead || monster.Dead || monster.Node == null) return false;

            if (!player.GetMagic(MagicType.PoisonDust, out MagicObject magic)) return false;

            if (!magic.CanUseMagic()) return false;
            if (!magic.CheckCost()) return false;
            if (SEnvir.Now < magic.Magic.Cooldown) return false;

            if (!HasAmmo(player, MagicType.PoisonDust)) return false;

            // 两种毒都齐了，不用补。
            if (HasBothPoisons(monster)) return false;

            int distance = BotNavigator.Distance(player.CurrentLocation, monster.CurrentLocation);

            if (distance > Globals.MagicRange) return false;
            if (!player.CanCast) return false;

            player.Magic(new C.Magic
            {
                Type = MagicType.PoisonDust,
                Target = monster.ObjectID,
                Location = monster.CurrentLocation,
                Direction = Functions.DirectionFromPoint(player.CurrentLocation, monster.CurrentLocation),
            });

            return true;
        }

        /// <summary>
        /// 道士补召宠物（3-4，2026-11-17 反转实施）：宠物缺位就施放召唤技能，返回本次是否施放。
        /// <para>
        /// <b>两个触发场景</b>（都在 <c>ProcessFight</c> 里节流调用）：
        /// <list type="bullet">
        /// <item><b>战斗前补召</b>：进入战斗时宠物位空着，开打前先召一只；</item>
        /// <item><b>死亡补召</b>：宠物战死（<c>Die()</c> 自动从 <c>Player.Pets</c> 移除）后补召。</item>
        /// </list>
        /// 两个场景共用本方法，判据都是「宠物位没满」——宠物活着占位就不会重复施放。
        /// </para>
        /// <para>
        /// <b>四道门缺一不可</b>：
        /// <list type="bullet">
        /// <item><b>守卫区不召</b>：安全区（<see cref="BotPerception.InSafeZone"/>，即守卫所在的城市区域）
        /// 里不召唤——城里没有战斗需求，召了只是浪费蓝和护身符，且宠物在城里可能卡位。</item>
        /// <item><b>宠物位未满</b>：引擎上限 2 只（<c>Player.Pets.Count &gt;= 2</c> 时
        /// <c>MagicComplete</c> 直接放弃），满了不召。</item>
        /// <item><b>蓝耗门</b>：召唤骷髅 10 蓝 / 召唤神兽 15 蓝（Lv1 口径），
        /// 走引擎 <see cref="MagicObject.CheckCost"/>（含超级模式豁免）。</item>
        /// <item><b>弹药门</b>：召唤骷髅耗符 1 个 / 神兽 5 个，走 <see cref="HasAmmo"/>。
        /// 引擎 <c>UseAmulet</c> 失败时 <c>Cast=false</c> 但<b>蓝照扣</b>
        /// （<c>MagicConsume</c> 在 cast 判断前调用），没符时不施放就是白扣蓝，必须先查弹药。</item>
        /// </list>
        /// 另过 <see cref="MagicObject.CanUseMagic"/>（等级门槛）与 <c>magic.Magic.Cooldown</c>（冷却，
        /// 本服召唤 Delay=0，但写法与其它技能一致，将来调冷却不用改这里）。
        /// </para>
        /// <para>
        /// <b>技能选择</b>：已学且可用的前提下，优先召唤神兽（Lv30，更强），没学/没蓝/没符退召唤骷髅（Lv17）。
        /// 施放走真人同一条 <c>player.Magic()</c>，方向取玩家当前朝向（引擎在朝向前方一格召唤）。
        /// </para>
        /// <para>
        /// <b>节流由调用方控制</b>：引擎召唤有 500ms 延迟（<c>DelayedAction</c>），
        /// 且施放失败不写冷却，不节流会每帧重试空放烧蓝——<c>ProcessFight</c> 里 1 秒调一次。
        /// </para>
        /// <returns>本次施放的技能（<see cref="MagicType.None"/> 表示没施放）。</returns>
        /// </summary>
        public static MagicType TrySummonPet(PlayerObject player)
        {
            if (player?.Character == null) return MagicType.None;
            if (player.Class != MirClass.Taoist) return MagicType.None;
            if (player.Dead) return MagicType.None;

            // 守卫区不召：安全区（城市/守卫区域）没有战斗需求，不浪费蓝和护身符。
            if (BotPerception.InSafeZone(player)) return MagicType.None;

            // 宠物位未满：引擎上限 2 只，满了不召（宠物活着占位 = 不需要补召）。
            if (player.Pets != null && player.Pets.Count >= 2) return MagicType.None;

            // 技能选择：优先神兽，不可用退骷髅。
            MagicType type = MagicType.None;

            if (player.GetMagic(MagicType.SummonShinsu, out MagicObject shinsu) && SummonReady(player, shinsu))
                type = MagicType.SummonShinsu;
            else if (player.GetMagic(MagicType.SummonSkeleton, out MagicObject skeleton) && SummonReady(player, skeleton))
                type = MagicType.SummonSkeleton;

            if (type == MagicType.None) return MagicType.None;

            if (!player.CanCast) return MagicType.None;

            player.Magic(new C.Magic
            {
                Type = type,
                Target = 0,
                Location = player.CurrentLocation,
                Direction = player.Direction,
            });

            return type;
        }

        /// <summary>召唤技能的三重校验：已学门槛 + 蓝耗 + 弹药（护身符），与攻击技能同构。</summary>
        private static bool SummonReady(PlayerObject player, MagicObject magic)
        {
            if (!magic.CanUseMagic()) return false;
            if (!magic.CheckCost()) return false;
            if (SEnvir.Now < magic.Magic.Cooldown) return false;
            return HasAmmo(player, magic.Type);
        }

        /// <summary>目标身上是否已经有绿毒。</summary>
        public static bool HasGreenPoison(MonsterObject monster)
        {
            if (monster?.PoisonList == null) return false;

            foreach (Poison poison in monster.PoisonList)
            {
                if (poison.Type == PoisonType.Green) return true;
            }

            return false;
        }

        /// <summary>目标身上是否已经有红毒。</summary>
        public static bool HasRedPoison(MonsterObject monster)
        {
            if (monster?.PoisonList == null) return false;

            foreach (Poison poison in monster.PoisonList)
            {
                if (poison.Type == PoisonType.Red) return true;
            }

            return false;
        }

        /// <summary>目标身上绿毒 + 红毒是否都齐了（施毒术的目标状态）。</summary>
        public static bool HasBothPoisons(MonsterObject monster)
        {
            return HasGreenPoison(monster) && HasRedPoison(monster);
        }

        /// <summary>道士给自己治疗（血低于 60% 才用）。战士/法师没有治疗技能，直接返回 false。</summary>
        public static bool TrySelfHeal(PlayerObject player)
        {
            if (player?.Character == null) return false;
            if (player.Class != MirClass.Taoist) return false;
            if (player.Dead || !player.CanCast) return false;

            if (BotPerception.HealthPercent(player) > 60) return false;

            foreach (MagicType type in TaoistHeal)
            {
                if (!player.GetMagic(type, out MagicObject magic)) continue;

                if (!magic.CanUseMagic()) continue;
                if (!magic.CheckCost()) continue;
                if (SEnvir.Now < magic.Magic.Cooldown) continue;

                player.Magic(new C.Magic
                {
                    Type = type,
                    Target = player.ObjectID,
                    Location = player.CurrentLocation,
                    Direction = player.Direction,
                });

                return true;
            }

            return false;
        }

        /// <summary>已学技能数（后台展示用）。</summary>
        public static int KnownCount(PlayerObject player)
        {
            if (player?.Character?.Magics == null) return 0;

            return player.Character.Magics.Count(x => x?.Info != null && x.Info.School != MagicSchool.None);
        }
    }
}
