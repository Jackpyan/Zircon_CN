using Library;
using System;

namespace Server.Bots
{
    /// <summary>
    /// 假人背包里「什么能卖、什么标记要修」的纯规则。
    ///
    /// <para><b>为什么单独抽出来</b>：同一条判断原来在 4 处各写一遍
    /// （<c>HasSellableItems</c> / <c>SellableTypeCounts</c> / <c>BuildSellLinks</c> / 解锁药水），
    /// 改一处漏三处 —— 实测就漏过一次：药水标记只解锁了 <c>Locked</c>，
    /// 而启动资源发的药带的是 <c>Bound | Worthless</c>，照样卖不掉，
    /// 于是 <c>三更雪</c>（战士）背包里 40 瓶魔法药 + 20 瓶绑定金创药 = 60 点负重永久占死。
    /// 抽成纯函数还有个好处：不碰 <c>PlayerObject</c> 就能写测试。</para>
    /// </summary>
    public static class BotItemRules
    {
        /// <summary>
        /// 这件东西算不算「药」（回血或回蓝的消耗品）。
        /// <para>口径与 <see cref="BotActions.CountPotion"/> 一致 —— 否则会出现
        /// 「数药时算它、卖货时又卖它」的自相矛盾。</para>
        /// </summary>
        public static bool IsPotion(ItemType type, long health, long mana)
        {
            return type == ItemType.Consumable && (health > 0 || mana > 0);
        }

        /// <summary>
        /// 卖货时要不要跳过它。
        ///
        /// <para>引擎自己只拒收 <c>Locked</c>（<c>PlayerObject.NPCSell</c>）和
        /// <c>Worthless</c>（同处，价格 0 干脆不卖）；<c>Bound</c> 引擎是放行的，
        /// 但假人这边一并跳过 —— 绑定物品（启动装备、活动奖励）不该流回商店换钱。</para>
        /// </summary>
        public static bool IsSellBlocked(UserItemFlags flags)
        {
            const UserItemFlags blocked =
                UserItemFlags.Bound | UserItemFlags.Locked | UserItemFlags.Worthless | UserItemFlags.Marriage;

            return (flags & blocked) != UserItemFlags.None;
        }

        /// <summary>
        /// 把「药」身上的卖不掉标记清掉。
        ///
        /// <para>只对药调用 —— 装备上的 <c>Locked</c> 不能动（还带着 <c>NonRefinable</c>，
        /// 解锁会破坏精炼与卖店的经济口径）。</para>
        /// </summary>
        public static UserItemFlags RepairPotionFlags(UserItemFlags flags)
        {
            return flags & ~(UserItemFlags.Bound | UserItemFlags.Locked | UserItemFlags.Worthless);
        }

        /// <summary>
        /// 这个职业要不要带蓝药。
        ///
        /// <para><b>战士默认不要</b>：<c>BotBrain</c> 的喝药分支是「非战士且蓝低于 30% 才喝」，
        /// 战士一辈子喝不掉一瓶，而发下来的蓝药又卖不掉（见上），
        /// 于是每发一次就是几十点永久负重。实测两个战士各囤 40 瓶魔法药。</para>
        /// <para>
        /// 2026-11-16（D1）补充：战士 <b>24 级起</b>需要蓝药（24 级起 BotBrain 会喝蓝），
        /// 见带等级的重载 <see cref="NeedsManaPotions(MirClass, int)"/>。
        /// 本单参版本保持原语义（战士不发），供不需要等级的场景与旧断言使用。
        /// </para>
        /// </summary>
        public static bool NeedsManaPotions(MirClass @class)
        {
            return @class != MirClass.Warrior;
        }

        /// <summary>
        /// 战士需要蓝药的起始等级（D1，2026-11-16）：24 级起战士有耗蓝技能、BotBrain 开始喝蓝，
        /// 发药/买药/捡药/喝蓝全部以此为统一判据，避免多处硬编码。
        /// </summary>
        public const int WarriorManaPotionLevel = 24;

        /// <summary>
        /// 这个职业在指定等级下要不要带蓝药（喝蓝/买蓝/发药/捡药的统一口径）。
        /// <para>法师/道士始终需要；战士 24 级起需要，24 级前不需要（无耗蓝技能、省负重）。</para>
        /// </summary>
        public static bool NeedsManaPotions(MirClass @class, int level)
        {
            return @class != MirClass.Warrior || level >= WarriorManaPotionLevel;
        }

        /// <summary>
        /// 剩余耐久的百分比（0~100，向下取整）—— 与后台页面显示的口径一致。
        /// </summary>
        public static int DurabilityPercent(long current, long max)
        {
            if (max <= 0) return 0;

            return (int)(current * 100 / max);
        }

        /// <summary>
        /// 这件装备该不该去修：剩余耐久 **&lt;= 阈值** 就修。
        ///
        /// <para><b>为什么不用「等于 0」</b>：后台按整数百分比显示，剩 0.4% 也显示 0%，
        /// 而旧判定要求 <c>CurrentDurability == 0</c>，于是那件装备一直挂在 0% 没人管
        /// （实测 2026-11-12：<c>老酒坛</c> 衣服 0%、<c>半盏茶</c> 衣服 10%）。</para>
        ///
        /// <para>调用方负责先过滤「不耗耐久」「不可修理」的装备。</para>
        /// </summary>
        public static bool NeedsRepair(long current, long max, int thresholdPercent)
        {
            if (max <= 0) return false;
            if (current >= max) return false;

            return current * 100 <= max * thresholdPercent;
        }

        /// <summary>
        /// 剩余耐久的千分比，用来在几件坏装备里挑「最该修」的那件。
        ///
        /// <para>不能比绝对值：武器耐久上限 2 万、戒指 5 千，
        /// 按绝对值会把「还剩 3000 的武器」判成比「还剩 800 的戒指」更该修。</para>
        /// </summary>
        public static long DurabilityPermille(long current, long max)
        {
            if (max <= 0) return 0;

            return current * 1000 / max;
        }
    }
}
