using Library;
using Library.SystemModels;
using Server.DBModels;
using Server.Envir;
using Server.Models;
using System;
using System.Collections.Generic;
using System.Drawing;
using C = Library.Network.ClientPackets;

namespace Server.Bots
{
    /// <summary>
    /// 假人的手。全部直接调 <see cref="PlayerObject"/> 的公开方法 —— 这些就是真人
    /// <c>C.Attack</c> / <c>C.PickUp</c> 等服务端处理落到的同一批入口，
    /// 校验（距离、冷却、动作锁、背包空间）与真人完全一致，不做任何特权绕过。
    /// <para>要求**在游戏线程内调用**。</para>
    /// </summary>
    public static class BotActions
    {
        /// <summary>攻击目标。近战只能打相邻格（D1 实测距离 4 会挥空、不扣血），距离由调用方保证。</summary>
        public static bool TryAttack(PlayerObject player, MonsterObject target)
        {
            if (player == null || target == null) return false;
            if (player.Dead || !player.CanAttack) return false;
            if (target.Dead || target.Node == null) return false;

            if (BotNavigator.Distance(player.CurrentLocation, target.CurrentLocation) > 1) return false;

            player.Attack(Functions.DirectionFromPoint(player.CurrentLocation, target.CurrentLocation), MagicType.None);

            return true;
        }

        /// <summary>
        /// 拾取地面物品。距离由引擎校验，太远会自动走一步再捡（引擎内部逻辑）。
        /// <para>
        /// 返回值是**引擎的真实结果**，不是「我发了个请求」。
        /// 背包满时引擎的 <c>PickUpItem</c> 会返回 false、物品留在地上 ——
        /// 如果这里无条件返回 true，假人会以为捡到了，下一拍又找到同一个东西再捡，
        /// 陷入「捡了 48 格 → 背包满 → 永远重复捡同一个」的假死循环（P1 修的硬伤之一）。
        /// </para>
        /// </summary>
        public static bool TryPickUp(PlayerObject player, ItemObject item)
        {
            if (player == null || item == null) return false;
            if (player.Dead || item.Node == null) return false;

            player.PickUp(item.ObjectID, new Point(item.CurrentLocation.X, item.CurrentLocation.Y));

            // 引擎的 <c>TakeItem()</c> 成功时会把 <c>Item</c> 置空并 Despawn，
            // 所以「Item 变 null」就是真的捡走了 —— 背包格数没变不代表没捡到（货币直接进 UserCurrency）。
            return item.Item == null;
        }

        /// <summary>回城复活（死亡后调用）。</summary>
        public static bool TryTownRevive(PlayerObject player)
        {
            if (player == null || !player.Dead) return false;

            player.TownRevive();

            return true;
        }

        /// <summary>
        /// 喝一瓶恢复指定属性的药（红药传 <see cref="Stat.Health"/>，蓝药传 <see cref="Stat.Mana"/>）。
        /// 走 <c>ItemUse</c> 的同一入口，冷却/负重与真人一致。
        /// </summary>
        public static bool TryUsePotion(PlayerObject player, Stat restoreStat)
        {
            if (player == null || player.Dead) return false;

            int slot = FindPotionSlot(player, restoreStat);

            if (slot < 0) return false;

            player.ItemUse(new CellLinkInfo
            {
                GridType = GridType.Inventory,
                Slot = slot,
                Count = 1,
            });

            return true;
        }

        /// <summary>背包里找第一个「恢复指定属性」的消耗品格子，找不到返回 -1。</summary>
        public static int FindPotionSlot(PlayerObject player, Stat restoreStat)
        {
            if (player?.Inventory == null) return -1;

            for (int i = 0; i < player.Inventory.Length; i++)
            {
                UserItem item = player.Inventory[i];

                if (item?.Info == null) continue;
                if (item.Info.ItemType != ItemType.Consumable) continue;
                if (item.Info.Stats[restoreStat] <= 0) continue;

                return i;
            }

            return -1;
        }

        /// <summary>背包里指定恢复属性药水的总瓶数（按堆叠累加，不是格数）。</summary>
        public static int CountPotion(PlayerObject player, Stat restoreStat)
        {
            if (player?.Inventory == null) return 0;

            long total = 0;

            for (int i = 0; i < player.Inventory.Length; i++)
            {
                UserItem item = player.Inventory[i];

                if (item?.Info == null) continue;
                if (item.Info.ItemType != ItemType.Consumable) continue;
                if (item.Info.Stats[restoreStat] <= 0) continue;

                total += item.Count;
            }

            return (int)Math.Min(total, int.MaxValue);
        }

        #region NPC 买卖

        /// <summary>
        /// 打开 NPC 对话。等价于真人的 <c>C.NPCCall</c>：引擎会校验距离与死亡状态，
        /// 打开后 <see cref="PlayerObject.NPC"/> / <see cref="PlayerObject.NPCPage"/> 才有值。
        /// </summary>
        public static bool TryOpenNpc(PlayerObject player, NPCObject npc)
        {
            if (player == null || npc == null) return false;
            if (player.Dead || player.CurrentMap == null) return false;

            if (!Functions.InRange(npc.CurrentLocation, player.CurrentLocation, Config.MaxViewRange)) return false;

            player.NPCCall(npc.ObjectID);

            return player.NPC != null && player.NPCPage != null;
        }

        /// <summary>在已打开的 NPC 对话里点一个按钮（真人走 <c>C.NPCButton</c>）。</summary>
        public static bool TryPressButton(PlayerObject player, int buttonId)
        {
            if (player?.NPCPage == null) return false;

            player.NPCButton(buttonId);

            return player.NPCPage != null;
        }

        /// <summary>
        /// 在当前 NPC 页里买一件商品。<paramref name="goodIndex"/> 是 <see cref="NPCGood.Index"/>（不是 ItemInfo.Index），
        /// 对应真人 <c>C.NPCBuy</c> 里的 <c>Index</c>。
        /// </summary>
        public static bool TryBuy(PlayerObject player, int goodIndex, int amount)
        {
            if (player?.NPCPage == null || amount <= 0) return false;

            player.NPCBuy(new C.NPCBuy
            {
                Index = goodIndex,
                Amount = amount,
                GuildFunds = false,
            });

            return true;
        }

        /// <summary>
        /// 在当前 NPC 页里卖东西（真人走 <c>C.NPCSell</c>）。
        /// <para>
        /// 引擎会校验：物品必须可卖（<c>CanSell</c>）、不能是绑定/结婚/垃圾物，
        /// 而且 <c>NPCPage.Types</c> 里必须包含该物品的 <c>ItemType</c> ——
        /// 也就是说**卖武器得去姜先生、卖首饰得去艾米、卖药得去大卫**，
        /// 每个商店只收自己那几类。卖错地方会被引擎静默拒绝。
        /// </para>
        /// </summary>
        public static bool TrySell(PlayerObject player, List<CellLinkInfo> links)
        {
            if (player?.NPCPage == null || links == null || links.Count == 0) return false;

            player.NPCSell(links);

            return true;
        }

        /// <summary>
        /// 在当前 NPC 页里修理装备（真人走 <c>C.NPCRepair</c>）。
        /// <para>
        /// 引擎会校验 <c>NPCPage.DialogType == NPCDialogType.Repair</c>，
        /// 修理页按装备格逐件报价。
        /// </para>
        /// </summary>
        public static bool TryRepair(PlayerObject player, List<CellLinkInfo> links)
        {
            if (player?.NPCPage == null || links == null || links.Count == 0) return false;

            player.NPCRepair(new C.NPCRepair { Links = links });

            return true;
        }

        /// <summary>
        /// 在当前 NPC 页里挑一个药水商品：**等级够 + 买得起 + 单瓶恢复量不超过上限**，在这些里取恢复量最大的。
        /// <para>
        /// 恢复量上限很重要：金创药 I~V 都摆在同一页（80/200/500/1250/2500 金币），
        /// 只按"恢复量最大"选，1 级号会拿 5000 金币去买 4 瓶 V 档（回 250，血只有 52，喝一口溢出 4 倍），
        /// 钱瞬间花光还只买到 4 瓶 —— D3 第一次实测就是这么翻的。
        /// </para>
        /// </summary>
        public static NPCGood FindPotionGood(PlayerObject player, Stat restoreStat, int budget, int maxRestore)
        {
            if (player?.NPCPage?.Goods == null) return null;

            NPCGood best = null;

            foreach (NPCGood good in player.NPCPage.Goods)
            {
                if (good?.Item == null) continue;
                if (good.Item.ItemType != ItemType.Consumable) continue;

                int restore = good.Item.Stats[restoreStat];

                if (restore <= 0) continue;

                // 等级门槛（生命神水要 11 级才能喝）。
                if (good.Item.RequiredAmount > player.Level) continue;

                // 至少要买得起一瓶。
                if (good.Cost > budget) continue;

                // 恢复量超过自身承受上限的，喝了也是溢出，纯浪费钱。
                if (restore > maxRestore) continue;

                if (best == null ||
                    good.Item.Stats[restoreStat] > best.Item.Stats[restoreStat] ||
                    (good.Item.Stats[restoreStat] == best.Item.Stats[restoreStat] && good.Cost < best.Cost))
                {
                    best = good;
                }
            }

            return best;
        }

        /// <summary>关闭 NPC 对话。真人走 <c>C.NPCClose</c>，服务端就这两行，直接照做。</summary>
        public static void CloseNpc(PlayerObject player)
        {
            if (player == null) return;

            player.NPC = null;
            player.NPCPage = null;
        }

        #endregion
    }
}
