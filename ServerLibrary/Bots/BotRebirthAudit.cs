using Server.DBModels;
using Server.Models;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Server.Bots
{
    /// <summary>
    /// 转生前后的「角色数据」快照（只统计数量与内容指纹，不复制物品本体）。
    /// </summary>
    public readonly struct BotRebirthSnapshot
    {
        public int Level { get; init; }
        public int Rebirth { get; init; }
        public int SpentPoints { get; init; }
        public int HermitStats { get; init; }

        public int Equipment { get; init; }
        public long EquipmentSignature { get; init; }

        public int Inventory { get; init; }
        public long InventoryStacks { get; init; }
        public int Storage { get; init; }
        public int PartsStorage { get; init; }

        public int Magics { get; init; }
        public long MagicSignature { get; init; }

        public int Currencies { get; init; }
        public long CurrencyTotal { get; init; }

        public int Buffs { get; init; }
        public int Quests { get; init; }
        public int Companions { get; init; }
        public bool Discipline { get; init; }
    }

    /// <summary>
    /// 转生数据保留核对（2026-11-12）。
    /// <para>
    /// 背景：<see cref="PlayerObject.AdjustRebirth"/> 只改 6 项 ——
    /// <c>Level=1</c>、<c>Experience/=200</c>、<c>Character.Rebirth+1</c>、
    /// <c>SpentPoints=0</c>、<c>HermitStats.Clear()</c>、<c>Discipline.Delete()</c>，
    /// 之后调 <c>RefreshStats()</c>。装备 / 背包 / 仓库 / 技能 / 货币 / Buff / 任务**一行都没碰**，
    /// 但「代码没碰」不等于「实测没丢」—— 满级转生一次要 60 小时，
    /// 靠人肉盯不可能，所以做成「转生前拍快照 → 转生后比对 → 有差异就告警」的自动核对。
    /// </para>
    /// <para>
    /// 快照刻意**不统计会正常变化的量**（HP/MP、经验、属性点、等级、转生次数），
    /// 否则每次转生都会「有差异」，告警就没人看了。
    /// </para>
    /// </summary>
    public static class BotRebirthAudit
    {
        /// <summary>拍一张「不该被转生改动」的数据快照。</summary>
        public static BotRebirthSnapshot Capture(PlayerObject player)
        {
            CharacterInfo character = player?.Character;

            if (character == null) return default;

            int equipment = 0;
            long equipmentSignature = 0;

            if (player.Equipment != null)
            {
                for (int i = 0; i < player.Equipment.Length; i++)
                {
                    UserItem item = player.Equipment[i];

                    if (item?.Info == null) continue;

                    equipment++;
                    // 指纹 = Σ(物品 Index × 槽位权重)：换了装备 / 换了槽位都会变，数量不变也能发现。
                    equipmentSignature += (long)item.Info.Index * (i + 1);
                }
            }

            int inventory = 0;
            long inventoryStacks = 0;

            if (player.Inventory != null)
            {
                foreach (UserItem item in player.Inventory)
                {
                    if (item?.Info == null) continue;

                    inventory++;
                    inventoryStacks += item.Count;
                }
            }

            int storage = 0;

            if (player.Storage != null)
                foreach (UserItem item in player.Storage)
                    if (item?.Info != null) storage++;

            int partsStorage = 0;

            if (player.PartsStorage != null)
                foreach (UserItem item in player.PartsStorage)
                    if (item?.Info != null) partsStorage++;

            int magics = 0;
            long magicSignature = 0;

            if (character.Magics != null)
            {
                foreach (UserMagic magic in character.Magics)
                {
                    if (magic?.Info == null) continue;

                    magics++;
                    magicSignature += magic.Info.Index;
                }
            }

            int currencies = 0;
            long currencyTotal = 0;

            if (character.Currencies != null)
            {
                foreach (UserCurrency currency in character.Currencies)
                {
                    if (currency?.Info == null) continue;

                    currencies++;
                    currencyTotal += currency.Amount;
                }
            }

            return new BotRebirthSnapshot
            {
                Level = player.Level,
                Rebirth = character.Rebirth,
                SpentPoints = character.SpentPoints,
                // Stats.Count 是「各项数值绝对值之和」，不是条目数 —— 这里要的是条目数。
                HermitStats = character.HermitStats?.Values?.Count ?? 0,

                Equipment = equipment,
                EquipmentSignature = equipmentSignature,

                Inventory = inventory,
                InventoryStacks = inventoryStacks,
                Storage = storage,
                PartsStorage = partsStorage,

                Magics = magics,
                MagicSignature = magicSignature,

                Currencies = currencies,
                CurrencyTotal = currencyTotal,

                Buffs = character.Buffs?.Count ?? 0,
                Quests = character.Quests?.Count ?? 0,
                Companions = character.Companions?.Count ?? 0,
                Discipline = character.Discipline != null,
            };
        }

        /// <summary>
        /// 比对两张快照，返回人类可读的一行结论。
        /// <para>没有丢失时返回「…无丢失」；有丢失时逐项列出「项 前→后」。</para>
        /// </summary>
        public static string DescribeDiff(BotRebirthSnapshot before, BotRebirthSnapshot after)
        {
            List<string> losses = new List<string>();

            // 只有「变少」或「内容指纹变了」才算异常：
            // 转生后继续打怪会捡到新装备、升技能、涨钱，这些都是正常增加。
            if (after.Equipment < before.Equipment)
                losses.Add($"装备 {before.Equipment}→{after.Equipment}");
            else if (before.EquipmentSignature != after.EquipmentSignature && after.Equipment == before.Equipment)
                losses.Add($"装备被替换（指纹 {before.EquipmentSignature}→{after.EquipmentSignature}）");

            if (after.Inventory < before.Inventory)
                losses.Add($"背包 {before.Inventory}→{after.Inventory} 格");

            if (after.Storage < before.Storage)
                losses.Add($"仓库 {before.Storage}→{after.Storage} 格");

            if (after.PartsStorage < before.PartsStorage)
                losses.Add($"材料仓 {before.PartsStorage}→{after.PartsStorage} 格");

            if (after.Magics < before.Magics)
                losses.Add($"技能 {before.Magics}→{after.Magics} 个");
            else if (after.MagicSignature < before.MagicSignature)
                losses.Add($"技能被替换（指纹 {before.MagicSignature}→{after.MagicSignature}）");

            if (after.Currencies < before.Currencies)
                losses.Add($"货币种类 {before.Currencies}→{after.Currencies}");
            else if (after.CurrencyTotal < before.CurrencyTotal)
                losses.Add($"货币总额 {before.CurrencyTotal}→{after.CurrencyTotal}");

            if (after.Quests < before.Quests)
                losses.Add($"任务 {before.Quests}→{after.Quests}");

            if (after.Companions < before.Companions)
                losses.Add($"宠物 {before.Companions}→{after.Companions}");

            if (losses.Count > 0)
                return "转生数据核对【异常】：" + string.Join("，", losses);

            StringBuilder sb = new StringBuilder();

            sb.Append("转生数据核对【正常】：");
            sb.Append($"装备 {after.Equipment}、背包 {after.Inventory} 格/{after.InventoryStacks} 件、");
            sb.Append($"仓库 {after.Storage}、材料仓 {after.PartsStorage}、技能 {after.Magics}、");
            sb.Append($"货币 {after.Currencies} 种/{after.CurrencyTotal}、Buff {after.Buffs}、任务 {after.Quests}、宠物 {after.Companions}");
            sb.Append($"（等级 {before.Level}→{after.Level}，转生 {before.Rebirth}→{after.Rebirth}，属性点 {before.SpentPoints}→{after.SpentPoints}）");

            return sb.ToString();
        }

        /// <summary>数据是否完整保留（装备 / 背包 / 仓库 / 技能 / 货币 / 任务 / 宠物 一项都没少）。</summary>
        public static bool IsDataPreserved(BotRebirthSnapshot before, BotRebirthSnapshot after)
        {
            return DescribeDiff(before, after).Contains("【正常】");
        }
    }
}
