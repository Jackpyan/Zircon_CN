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

        /// <summary>全库最低药价缓存（按恢复属性）。静态数据，一次算好终身有效。</summary>
        private static readonly Dictionary<Stat, int> FloorPotionPriceCache = new Dictionary<Stat, int>();

        /// <summary>
        /// 全库（<see cref="SEnvir.ItemInfoList"/>）里这类药的最低单价。
        /// <para>
        /// 兜底用：背包里没有同类药时（红药正好喝完是常态）取不到背包价，
        /// 上层会把「不知道多少钱」当成「缺钱」—— 结果有 4000 金币的假人也在卖货，永远不买药。
        /// </para>
        /// </summary>
        public static int FloorPotionPrice(Stat restoreStat)
        {
            if (FloorPotionPriceCache.TryGetValue(restoreStat, out int cached)) return cached;

            int best = 0;
            var list = SEnvir.ItemInfoList?.Binding;

            if (list != null)
            {
                int min = int.MaxValue;

                foreach (ItemInfo info in list)
                {
                    if (info == null) continue;
                    if (info.ItemType != ItemType.Consumable) continue;
                    if (info.Stats == null || info.Stats[restoreStat] <= 0) continue;
                    if (info.Price <= 0) continue;

                    if (info.Price < min) min = info.Price;
                }

                best = min == int.MaxValue ? 0 : min;
            }

            // 只缓存有效价：0 表示「全库都没有」，可能是数据还没加载完，
            // 缓存下来会让后续永远拿 0（假人一直以为买药不要钱/或永远判缺钱）。
            if (best > 0) FloorPotionPriceCache[restoreStat] = best;

            return best;
        }

        /// <summary>
        /// 背包里这类药的**最低单价**（按 <see cref="ItemInfo.Price"/>）。
        /// <para>
        /// 用于判断「钱包够不够买下一轮药」—— 买不起就该去卖货赚钱。
        /// 背包里没有同类药时回退到 <see cref="FloorPotionPrice"/>（全库最低价）。
        /// </para>
        /// </summary>
        public static int CheapestPotionPrice(PlayerObject player, Stat restoreStat)
        {
            if (player?.Inventory == null) return FloorPotionPrice(restoreStat);

            int best = int.MaxValue;

            for (int i = 0; i < player.Inventory.Length; i++)
            {
                UserItem item = player.Inventory[i];

                if (item?.Info == null) continue;
                if (item.Info.ItemType != ItemType.Consumable) continue;
                if (item.Info.Stats[restoreStat] <= 0) continue;

                int price = item.Info.Price;

                if (price > 0 && price < best) best = price;
            }

            return best == int.MaxValue ? FloorPotionPrice(restoreStat) : best;
        }

        #region 回城卷（Town Scroll）

        /// <summary>
        /// 这件物品是不是「回城卷」。
        /// <para>
        /// 判定依据是引擎的实现，不是物品名或 Index：引擎的
        /// <c>PlayerObject.ItemUse</c> 里 <c>ItemType.Consumable</c> 分支按
        /// <c>item.Info.Shape</c> 分派，<c>Shape == 2</c> 才是 Town Teleport
        /// （<c>Shape == 3</c> 是随机传送、<c>Shape == 0</c> 是药）。
        /// 库里 <c>回城卷</c>（Index 156 / 随机传送卷 Index 155）就是按这个规则标的。
        /// </para>
        /// <para>
        /// **不能靠物品名判断**：不同库的回城卷 Index 不一样，靠名字会漏。
        /// 也不能靠 <c>Stats[Health] &gt; 0</c> 判断 —— 回城卷不回血（实测 Health/Mana 都是 0），
        /// 靠回血判断会把「回城卷」和「药」混为一谈。
        /// </para>
        /// </summary>
        public static bool IsTownScroll(UserItem item)
        {
            return IsTownScroll(item?.Info);
        }

        /// <inheritdoc cref="IsTownScroll(UserItem)"/>
        public static bool IsTownScroll(ItemInfo info)
        {
            if (info == null) return false;
            if (info.ItemType != ItemType.Consumable) return false;

            return info.Shape == 2;
        }

        /// <summary>背包里第一张回城卷的格号，没有返回 -1。</summary>
        public static int FindTownScrollSlot(PlayerObject player)
        {
            if (player?.Inventory == null) return -1;

            for (int i = 0; i < player.Inventory.Length; i++)
            {
                UserItem item = player.Inventory[i];

                if (item == null) continue;
                if (!IsTownScroll(item)) continue;

                return i;
            }

            return -1;
        }

        /// <summary>背包里的回城卷总张数（按堆叠累加，不是格数）。</summary>
        public static int CountTownScroll(PlayerObject player)
        {
            if (player?.Inventory == null) return 0;

            long total = 0;

            for (int i = 0; i < player.Inventory.Length; i++)
            {
                UserItem item = player.Inventory[i];

                if (!IsTownScroll(item)) continue;

                total += item.Count;
            }

            return (int)Math.Min(total, int.MaxValue);
        }

        /// <summary>
        /// 用一张回城卷瞬移回绑定主城。
        /// <para>
        /// 走真人同一条 <c>ItemUse</c> 路径（引擎的 <c>Shape == 2</c> 分支），
        /// 不做任何特权传送 —— 引擎会自己校验：
        /// <list type="bullet">
        /// <item>所在地图 <c>AllowTT</c>（全服只有幽灵船 4 层不允许）；</item>
        /// <item>副本 <c>Instance.AllowTeleport</c>；</item>
        /// <item>物品冷却（<c>UseItemTime</c>，回城卷 500ms）；</item>
        /// <item>角色有绑定点且绑定点有合法落点。</item>
        /// </list>
        /// </para>
        /// <para>
        /// 返回值是**引擎的真实结果**：卷被消耗掉（数量减少）才算成功。
        /// 引擎在冷却期内会直接 return 不消耗 —— 这时必须返回 false，
        /// 否则假人会以为已经回城，然后站在野外反复「回城」却永远没动。
        /// </para>
        /// </summary>
        public static bool TryUseTownScroll(PlayerObject player)
        {
            if (player == null || player.Dead) return false;

            // 引擎的 case 2 直接解引用 Character.BindPoint —— 没有绑定点会 NRE。
            // 正常假人一定有（BotFactory 建号时就绑了安全区），这里只是兜底。
            if (player.Character?.BindPoint?.BindRegion?.Map == null) return false;

            if (player.Character.BindPoint.ValidBindPoints.Count == 0) return false;

            int slot = FindTownScrollSlot(player);

            if (slot < 0) return false;

            UserItem scroll = player.Inventory[slot];

            long before = scroll.Count;

            player.ItemUse(new CellLinkInfo
            {
                GridType = GridType.Inventory,
                Slot = slot,
                Count = 1,
            });

            // 引擎用完会把整叠清空（RemoveItem + 格子置空）或把数量减 1。
            // 两种都算成功。
            if (player.Inventory[slot] == null) return true;

            UserItem after = player.Inventory[slot];

            return after == null || after.Count < before;
        }

        /// <summary>
        /// 当前地图能不能用回城卷。
        /// <para>
        /// 引擎的判定是「地图 <c>AllowTT</c> + 副本 <c>AllowTeleport</c>」，
        /// 这里提前算好，省得发包了才发现传不了。
        /// </para>
        /// </summary>
        public static bool CanUseTownScrollHere(PlayerObject player)
        {
            Map map = player?.CurrentMap;

            if (map?.Info == null) return false;
            if (!map.Info.AllowTT) return false;

            if (map.Instance != null && !map.Instance.AllowTeleport) return false;

            return true;
        }

        /// <summary>
        /// 物品使用冷却是否已过。
        /// <para>
        /// 引擎的 <c>ItemUse</c> 在 <c>Consumable</c> 分支开头有一句
        /// <c>if (SEnvir.Now &lt; UseItemTime) return;</c> ——
        /// 冷却期内发包**不消耗物品、也不生效**。
        /// </para>
        /// <para>
        /// 为什么要有这个：药水冷却 2000ms、回城卷 500ms，两者共用同一个
        /// <c>UseItemTime</c>。假人「喝了药 → 立刻用卷回城」会被引擎静默拒绝，
        /// 看起来就像「卷坏了」。这里提前判断，让调用方知道该等一等。
        /// </para>
        /// </summary>
        public static bool ItemUseCooldownReady(PlayerObject player)
        {
            if (player == null) return false;

            return SEnvir.Now >= player.UseItemTime;
        }

        #endregion

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

        /// <summary>
        /// 在当前 NPC 页里挑一个回城卷商品：**买得起 + 是回城卷**。
        /// <para>
        /// 与 <see cref="FindPotionGood"/> 分开写，因为两者的判定维度不同：
        /// 药按「恢复量」挑档位，回城卷只有一种（<c>Shape == 2</c>），只按「买得起」筛。
        /// </para>
        /// <para>
        /// 杂货商（伦纳德 / 科里 / 赛文…）与药店（大卫 / 海莉…）是**两个不同的 NPC** ——
        /// 实测 12 个主城里药店只卖药、杂货商只卖卷，只有炼狱岛的拉瓦尔两个都有。
        /// 所以「买药」和「买回城卷」要分别跑一趟。
        /// </para>
        /// </summary>
        public static NPCGood FindTownScrollGood(PlayerObject player, int budget)
        {
            if (player?.NPCPage?.Goods == null) return null;

            NPCGood best = null;

            foreach (NPCGood good in player.NPCPage.Goods)
            {
                if (good?.Item == null) continue;
                if (!IsTownScroll(good.Item)) continue;

                if (good.Cost > budget) continue;

                if (best == null || good.Cost < best.Cost)
                    best = good;
            }

            return best;
        }

        #endregion
    }
}
