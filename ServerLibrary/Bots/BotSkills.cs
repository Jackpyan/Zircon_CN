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
    /// 2. <b>选技能</b>：只挑<b>攻击型</b>（伤害）与<b>治疗型</b>，召唤/盾/隐身这些 P0 不放，补了也没用；
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

        /// <summary>道士：攻击 + 治疗（召唤兽 P0 不放，缺了召唤道士偏弱但观感正常）。</summary>
        /// <para>
        /// <b>施毒术（PoisonDust）不在这个表里</b>（2026-11-13 用户实测报的）：
        /// 它是<b>纯 debuff</b> —— <c>PoisonDust.MagicCast</c> 只 <c>ApplyPoison</c>，一点直接伤害都没有。
        /// 放进攻击表就会被 <see cref="PickBest"/> 当成「伤害技能」选出来，
        /// 假人站在 10 格外一直放毒、永远不贴脸砍，怪掉血极慢甚至不掉血。
        /// 现在它只在 <see cref="TryOpenWithPoison"/> 里**开场放一次**。
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
        /// 目前只有施毒术：<see cref="TryOpenWithPoison"/> 会开场放一次。
        /// </summary>
        private static readonly MagicType[] TaoistDebuff =
        {
            MagicType.PoisonDust,
        };

        /// <summary>该职业要补的全部技能（攻击 + 治疗 + debuff）。战士返回空 —— 平砍就是输出。</summary>
        public static IEnumerable<MagicType> Wanted(MirClass cls)
        {
            switch (cls)
            {
                case MirClass.Wizard:
                    return WizardAttack;
                case MirClass.Taoist:
                    return TaoistAttack.Concat(TaoistHeal).Concat(TaoistDebuff);
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

            return added;
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
        /// 开场给目标上一次毒（<b>只在没中毒时放一次</b>），成功返回 true。
        /// <para>
        /// 施毒术是纯 debuff：绿毒持续掉血、红毒让目标多受 20% 伤害。
        /// 真人道士也是「先上毒再打」，而不是站在远处一直放毒。
        /// 所以它不进 <see cref="TaoistAttack"/>，只在这里当一次性开场。
        /// </para>
        /// <para>
        /// 三道门缺一不可：<b>有毒药粉</b>（否则 <c>UsePoison</c> 失败 = 空放）、
        /// <b>目标还没中毒</b>（重复放纯浪费）、<b>冷却/蓝够</b>。
        /// </para>
        /// </summary>
        public static bool TryOpenWithPoison(PlayerObject player, MonsterObject monster)
        {
            if (player?.Character == null || monster == null) return false;

            if (player.Class != MirClass.Taoist) return false;
            if (player.Dead || monster.Dead || monster.Node == null) return false;

            if (!player.GetMagic(MagicType.PoisonDust, out MagicObject magic)) return false;

            if (!magic.CanUseMagic()) return false;
            if (!magic.CheckCost()) return false;
            if (SEnvir.Now < magic.Magic.Cooldown) return false;

            if (!HasAmmo(player, MagicType.PoisonDust)) return false;

            if (IsPoisoned(monster)) return false;

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

        /// <summary>目标身上是否已经有绿毒 / 红毒（其他毒不算，那些不是施毒术给的）。</summary>
        private static bool IsPoisoned(MonsterObject monster)
        {
            if (monster.PoisonList == null) return false;

            foreach (Poison poison in monster.PoisonList)
            {
                if (poison.Type == PoisonType.Green || poison.Type == PoisonType.Red) return true;
            }

            return false;
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
