using Library;
using System;

namespace Server.Bots
{
    /// <summary>
    /// 药水保留额度：一次背包遍历里「哪些药还能留着、哪些可以卖」。
    ///
    /// <para><b>为什么需要它</b>：药是「自己要用」的，原来一律不许卖。但 11 倍爆率下地上到处是药，
    /// 假人见什么捡什么 —— 实测 <c>一剑霜寒</c> 囤了 90 瓶红药 + 46 瓶蓝药，
    /// 背包总重 155，其中药水 142（92%），负重 155/107。
    /// 药卖不掉 → 负重降不下来 → 新战利品捡不进来 → 没东西可卖 → 死锁。</para>
    ///
    /// <para><b>规则</b>：没到「该卖货」的程度就<b>一瓶不卖</b>（<see cref="Unlimited"/>），
    /// 保持原来的保守行为；只有负重已经 ≥90% 时才限量，把超出的部分吐出来。
    /// 限量同时看两个维度 —— 瓶数（红/蓝各自的补给目标）与总重（负重上限的 40%），
    /// 取先耗尽的那个。只按瓶数不够：低等级角色负重上限才 58，40 瓶药就占 69%。</para>
    /// </summary>
    public sealed class BotPotionBudget
    {
        /// <summary>不限量：所有药都留着（未超重时的默认行为）。</summary>
        public static BotPotionBudget Unlimited() => new BotPotionBudget(false, 0, 0, 0);

        private readonly bool _limited;

        private int _red;
        private int _blue;
        private long _weightLeft;

        public BotPotionBudget(bool limited, int red, int blue, long weightLeft)
        {
            _limited = limited;
            _red = red;
            _blue = blue;
            _weightLeft = weightLeft;
        }

        /// <summary>
        /// 这一瓶药还留不留？返回 false = 可以卖掉。
        /// <para>注意它是<b>有状态</b>的：每放行一瓶就扣掉对应额度，所以要按背包顺序遍历。</para>
        /// </summary>
        public bool Allow(Stat stat, long weight)
        {
            if (!_limited) return true;

            if (weight < 0) weight = 0;

            bool health = stat == Stat.Health;

            if (health)
            {
                if (_red <= 0) return false;
            }
            else if (_blue <= 0)
            {
                return false;
            }

            if (_weightLeft < weight) return false;

            if (health) _red--;
            else _blue--;

            _weightLeft -= weight;

            return true;
        }

        /// <summary>按角色的负重与职业算一份额度。未到 90% 返回 <see cref="Unlimited"/>。</summary>
        public static BotPotionBudget For(long bagWeight, long capacity, bool warrior, int redTarget, int blueTarget)
        {
            if (capacity <= 0 || bagWeight < capacity * 9 / 10) return Unlimited();

            return new BotPotionBudget(
                true,
                Math.Max(1, redTarget),
                warrior ? 0 : Math.Max(0, blueTarget),
                capacity * 40 / 100);
        }
    }
}
