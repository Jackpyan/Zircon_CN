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
        private static readonly MagicType[] TaoistAttack =
        {
            MagicType.PoisonDust, MagicType.ExplosiveTalisman, MagicType.EvilSlayer,
            MagicType.GreaterEvilSlayer, MagicType.CelestialLight, MagicType.LifeSteal,
            MagicType.ImprovedExplosiveTalisman, MagicType.SearingLight, MagicType.SoulResonance,
        };

        private static readonly MagicType[] TaoistHeal =
        {
            MagicType.Heal, MagicType.MassHeal,
        };

        /// <summary>该职业要补的全部技能（攻击 + 治疗）。战士返回空 —— 平砍就是输出。</summary>
        public static IEnumerable<MagicType> Wanted(MirClass cls)
        {
            switch (cls)
            {
                case MirClass.Wizard:
                    return WizardAttack;
                case MirClass.Taoist:
                    return TaoistAttack.Concat(TaoistHeal);
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
        /// 挑一个当前能放的攻击魔法（等级够、蓝够、冷却好），取<b>门槛最高</b>的那个 —— 伤害最大。
        /// 返回 <see cref="MagicType.None"/> 表示这一刀只能用平的。
        /// </summary>
        public static MagicType PickAttack(PlayerObject player)
        {
            if (player?.Character == null) return MagicType.None;

            MagicType best = MagicType.None;
            int bestLevel = -1;

            foreach (MagicType type in Wanted(player.Class))
            {
                if (!player.GetMagic(type, out MagicObject magic)) continue;

                if (!magic.CanUseMagic()) continue;
                if (!magic.CheckCost()) continue;
                if (SEnvir.Now < magic.Magic.Cooldown) continue;

                // 治疗技能不能当攻击用（MagicCast 会返回 Cast=false，白费一次动作）。
                if (TaoistHeal.Contains(type)) continue;

                int need = magic.Magic.Info.NeedLevel1;

                if (need <= bestLevel) continue;

                best = type;
                bestLevel = need;
            }

            return best;
        }

        /// <summary>
        /// 对怪放一个攻击魔法。距离由引擎校验（<c>Globals.MagicRange = 10</c>），
        /// 超出范围返回 false，调用方退回走近砍。
        /// </summary>
        public static bool TryCastAttack(PlayerObject player, MonsterObject monster)
        {
            if (player == null || monster == null) return false;
            if (player.Dead || monster.Dead || monster.Node == null) return false;

            MagicType type = PickAttack(player);

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
