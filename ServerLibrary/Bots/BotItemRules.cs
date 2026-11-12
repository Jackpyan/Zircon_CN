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
        /// <para><b>战士不要</b>：<c>BotBrain</c> 的喝药分支是「非战士且蓝低于 30% 才喝」，
        /// 战士一辈子喝不掉一瓶，而发下来的蓝药又卖不掉（见上），
        /// 于是每发一次就是几十点永久负重。实测两个战士各囤 40 瓶魔法药。</para>
        /// </summary>
        public static bool NeedsManaPotions(MirClass @class)
        {
            return @class != MirClass.Warrior;
        }
    }
}
