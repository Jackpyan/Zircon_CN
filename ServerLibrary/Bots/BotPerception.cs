using Library;
using Library.SystemModels;
using Server.DBModels;
using Server.Models;
using Server.Models.Monsters;
using System.Collections.Generic;
using System.Drawing;

namespace Server.Bots
{
    /// <summary>
    /// 假人的眼睛。所有方法都要求**在游戏线程内调用**（直接遍历 <c>Map.Objects</c>，不做快照）。
    /// </summary>
    public static class BotPerception
    {
        /// <summary>
        /// 找一只值得打的怪：优先残血/低血量，其次看距离。
        /// D1 实测 1 级号 52 点血打不动血厚的怪，按血量排序能显著提高存活率。
        /// <para>
        /// 两条硬过滤（实测踩过）：城里的 <see cref="MonsterAI.Guard"/> 是 1 血的守卫，
        /// 假人一路过就打，砍两分钟也不给经验、等级一直卡在 1 级；
        /// 高于自身等级 3 级以上的怪打不动，只会白白掉血。
        /// </para>
        /// </summary>
        public static MonsterObject FindHuntTarget(PlayerObject player, int radius)
        {
            Map map = player?.CurrentMap;

            if (map == null) return null;

            MonsterObject best = null;
            int bestScore = int.MaxValue;

            foreach (MapObject ob in map.Objects)
            {
                if (!(ob is MonsterObject monster)) continue;
                if (monster.Dead || monster.Node == null) continue;
                if (monster.Stats[Stat.Health] <= 0) continue;

                MonsterInfo info = monster.MonsterInfo;

                if (info == null) continue;

                // 守卫（MonsterInfo.AI == -1 生成 Guard）不产经验，假人一路过就打，
                // 实测砍两分钟等级还卡在 1 级 —— 永远不打。
                if (monster is Guard) continue;

                // 等级压制超过 4 级的先放过，0 级号硬扛只会送死。
                // 容差取 4 是有依据的：本服最低的怪就是 Lv5 的鸡，1 级号必须打得动它（D1 实测三只鸡可升 2 级）。
                if (info.Level > player.Level + 4) continue;

                // 比自己低 8 级以上的怪经验少得可怜，打了纯浪费时间 —— 这条也是「等级上浮」的推力：
                // 鸡是 Lv5，练到 13 级之后自动嫌弃，转去找 10~14 级的新图。
                if (info.Level < player.Level - 8) continue;

                int distance = BotNavigator.Distance(player.CurrentLocation, monster.CurrentLocation);

                if (distance > radius) continue;

                int score = monster.CurrentHP + distance * 20;

                if (score >= bestScore) continue;

                best = monster;
                bestScore = score;
            }

            return best;
        }

        /// <summary>
        /// 找一件地上的掉落物（就近）。
        /// <para>
        /// P1：加了 <see cref="IsWorthPickingUp"/> 筛选 —— 原先见什么捡什么，
        /// 会把垃圾塞满 48 格背包导致假人卡死在拾取态。
        /// </para>
        /// </summary>
        public static ItemObject FindNearestLoot(PlayerObject player, int radius)
        {
            Map map = player?.CurrentMap;

            if (map == null) return null;

            ItemObject best = null;
            int bestDistance = int.MaxValue;

            foreach (MapObject ob in map.Objects)
            {
                if (!(ob is ItemObject item)) continue;
                if (item.Node == null) continue;

                int distance = BotNavigator.Distance(player.CurrentLocation, item.CurrentLocation);

                if (distance > radius || distance >= bestDistance) continue;

                if (!IsWorthPickingUp(item)) continue;

                best = item;
                bestDistance = distance;
            }

            return best;
        }

        /// <summary>范围内所有活着的怪（用于「这里怪多不多，要不要换地方」的判断，D3 用）。</summary>
        public static List<MonsterObject> GetMonstersAround(PlayerObject player, int radius)
        {
            List<MonsterObject> result = new List<MonsterObject>();

            Map map = player?.CurrentMap;

            if (map == null) return result;

            foreach (MapObject ob in map.Objects)
            {
                if (!(ob is MonsterObject monster)) continue;
                if (monster.Dead || monster.Node == null) continue;

                if (BotNavigator.Distance(player.CurrentLocation, monster.CurrentLocation) > radius) continue;

                result.Add(monster);
            }

            return result;
        }

        /// <summary>背包里占用的格数。</summary>
        public static int UsedInventorySlots(PlayerObject player)
        {
            if (player?.Inventory == null) return 0;

            int count = 0;

            for (int i = 0; i < player.Inventory.Length; i++)
                if (player.Inventory[i] != null) count++;

            return count;
        }

        /// <summary>背包剩余空格子数。</summary>
        public static int FreeInventorySlots(PlayerObject player)
        {
            if (player?.Inventory == null) return 0;

            int free = 0;

            for (int i = 0; i < player.Inventory.Length; i++)
                if (player.Inventory[i] == null) free++;

            return free;
        }

        /// <summary>
        /// 判断一个掉落物值不值得捡。
        /// <para>
        /// P1 修的硬伤：原先 <c>FindNearestLoot</c> 不做任何筛选，见什么捡什么 ——
        /// 垃圾、任务物品、绑定物全都往 48 格背包里塞，塞满后假人就卡死在拾取态。
        /// 这里的规则是「能卖钱 / 能用 / 是药」才捡，其余留在地上（真人也捡不了，掉了也不心疼）。
        /// </para>
        /// <para>
        /// 货币（<see cref="ItemType.Currency"/>）必须捡：它不进背包、直接进
        /// <c>UserCurrency</c>，是假人唯一的金币收入来源。
        /// </para>
        /// </summary>
        public static bool IsWorthPickingUp(ItemObject item)
        {
            if (item?.Item?.Info == null) return false;

            UserItem userItem = item.Item;

            // 绑定的不能卖（卖掉会被引擎拒绝），捡了只是占格。
            if ((userItem.Flags & UserItemFlags.Bound) == UserItemFlags.Bound) return false;

            // 任务物品不捡。
            if ((userItem.Flags & UserItemFlags.QuestItem) == UserItemFlags.QuestItem) return false;

            switch (userItem.Info.ItemType)
            {
                // 货币直接进角色货币表，不占背包格 —— 必须捡。
                case ItemType.Currency:
                    return true;

                // 药要捡：回城买药要钱、要时间，路上捡到的先用着。
                case ItemType.Consumable:
                case ItemType.Meat:
                    return true;

                // 装备要捡：P1-4 会自动换上更好的，穿不上的还能卖钱。
                case ItemType.Weapon:
                case ItemType.Armour:
                case ItemType.Helmet:
                case ItemType.Necklace:
                case ItemType.Bracelet:
                case ItemType.Ring:
                case ItemType.Shoes:
                case ItemType.Shield:
                case ItemType.Costume:
                case ItemType.Emblem:
                case ItemType.SocketGem:
                    return true;

                default:
                    // 其余（矿石、书、卷轴、花、宠物食粮…）价值低或用不上，
                    // 捡了只占格子；假人不做炼矿、不做读书这类玩法。
                    return false;
            }
        }

        /// <summary>血量百分比（0~100）。</summary>
        public static int HealthPercent(PlayerObject player)
        {
            if (player == null) return 0;

            int max = player.Stats[Stat.Health];

            if (max <= 0) return 0;

            return player.CurrentHP * 100 / max;
        }

        /// <summary>当前位置是否在安全区内（安全区不发呆乱跑，也不该在这里找怪）。</summary>
        public static bool InSafeZone(PlayerObject player)
        {
            if (player?.CurrentMap == null) return false;

            Cell cell = player.CurrentMap.GetCell(player.CurrentLocation);

            return cell != null && cell.SafeZone != null;
        }

        /// <summary>把坐标包成 Point（避免调用方到处 new）。</summary>
        public static Point LocationOf(MapObject ob)
        {
            return ob?.CurrentLocation ?? Point.Empty;
        }

        #region 药店

        /// <summary>
        /// 「这个 NPC 卖不卖恢复类药水」的缓存。按 NPCInfo 引用做 key（Index 在建号前可能是 0）。
        /// 12 个药店是静态数据，一次扫描终身有效，不用每次遍历整页商品。
        /// </summary>
        private static readonly Dictionary<NPCInfo, bool> PharmacyCache = new Dictionary<NPCInfo, bool>();

        /// <summary>在当前地图上找卖红/蓝药的 NPC。</summary>
        public static NPCObject FindPharmacy(Map map)
        {
            if (map?.NPCs == null) return null;

            foreach (NPCObject npc in map.NPCs)
            {
                if (npc?.NPCInfo == null) continue;

                if (IsPharmacy(npc.NPCInfo)) return npc;
            }

            return null;
        }

        #region 六合圣石（传送 NPC）

        /// <summary>
        /// 「这个 NPC 是不是传送石」的缓存。125 个 NPC 是静态数据，一次扫描终身有效。
        /// </summary>
        private static readonly Dictionary<NPCInfo, bool> TeleportStoneCache = new Dictionary<NPCInfo, bool>();

        /// <summary>
        /// 在当前地图上找**传送 NPC**（本服叫「六合圣石」，每个主城一个）。
        /// <para>
        /// 为什么需要它：跨图用 A* 逐格走要穿过「北方通道」这类刷怪图，
        /// 低级假人一路被打死 → 复活 → 再跨图，形成活锁（D5 实测 死 77 / 杀 16）。
        /// 主城的传送石可以直接送到目标图，不用走路。
        /// </para>
        /// </summary>
        public static NPCObject FindTeleportStone(Map map)
        {
            if (map?.NPCs == null) return null;

            foreach (NPCObject npc in map.NPCs)
            {
                if (npc?.NPCInfo == null) continue;

                if (IsTeleportStone(npc.NPCInfo)) return npc;
            }

            return null;
        }

        /// <summary>
        /// 判断一个 NPC 是不是传送石：入口页的按钮目标页里，有指向别的地图的 <c>Teleport</c> 动作。
        /// <para>
        /// 引擎的传送实现在 <c>NPCObject.DoActions</c>：<c>NPCActionType.Teleport</c> 读
        /// <c>action.MapParameter1</c>（目标地图）与 <c>IntParameter1/2</c>（坐标，0,0 = 随机点）。
        /// 收费是同一页里的 <c>TakeGold</c> 动作 + <c>Gold</c> 检查，不是独立流程。
        /// </para>
        /// </summary>
        public static bool IsTeleportStone(NPCInfo npc)
        {
            if (npc == null) return false;

            if (TeleportStoneCache.TryGetValue(npc, out bool cached)) return cached;

            bool result = false;

            if (npc.EntryPage?.Buttons != null)
            {
                foreach (NPCButton button in npc.EntryPage.Buttons)
                {
                    NPCPage dest = button.DestinationPage;

                    if (dest?.Actions == null) continue;

                    foreach (NPCAction action in dest.Actions)
                    {
                        if (action.ActionType != NPCActionType.Teleport) continue;
                        if (action.MapParameter1 == null) continue;

                        result = true;
                        break;
                    }

                    if (result) break;
                }
            }

            TeleportStoneCache[npc] = result;

            return result;
        }

        /// <summary>
        /// 在传送石的入口页里找「通往 <paramref name="targetMapIndex"/> 的按钮」，返回 ButtonID；找不到返回 -1。
        /// <para>
        /// 只匹配**直达**按钮（目标页里的 Teleport 动作指向目标图）。
        /// 「自由传送员」这类二级页要先点一次父按钮再点子按钮，本版不处理 ——
        /// 高级图本来就有等级门槛，低级假人也去不了。
        /// </para>
        /// </summary>
        public static int FindTeleportButton(NPCInfo npc, int targetMapIndex)
        {
            if (npc?.EntryPage?.Buttons == null) return -1;
            if (targetMapIndex <= 0) return -1;

            foreach (NPCButton button in npc.EntryPage.Buttons)
            {
                NPCPage dest = button.DestinationPage;

                if (dest?.Actions == null) continue;

                foreach (NPCAction action in dest.Actions)
                {
                    if (action.ActionType != NPCActionType.Teleport) continue;
                    if (action.MapParameter1?.Index != targetMapIndex) continue;

                    return button.ButtonID;
                }
            }

            return -1;
        }

        /// <summary>
        /// 传送这颗石头的**最便宜一档**要多少钱（0 = 免费 / 未知）。
        /// 用来判断「钱够不够传送」，不够就走 A* 跨图。
        /// </summary>
        public static int CheapestTeleportCost(NPCInfo npc, int targetMapIndex)
        {
            if (npc?.EntryPage?.Buttons == null) return 0;

            foreach (NPCButton button in npc.EntryPage.Buttons)
            {
                NPCPage dest = button.DestinationPage;

                if (dest?.Actions == null) continue;

                bool hits = false;
                int cost = 0;

                foreach (NPCAction action in dest.Actions)
                {
                    if (action.ActionType == NPCActionType.Teleport && action.MapParameter1?.Index == targetMapIndex)
                        hits = true;
                    else if (action.ActionType == NPCActionType.TakeGold && action.IntParameter1 > 0)
                        cost = action.IntParameter1;
                }

                if (hits) return cost;
            }

            return 0;
        }

        #endregion

        /// <summary>
        /// 判断一个 NPC 是不是药店：入口页、或入口页一层的按钮目标页里，有恢复生命/魔法的消耗品。
        /// 实测 12 个药店都是「入口页 → 按钮(1) → 基础药品买卖－城堡」，所以最多看一层。
        /// </summary>
        public static bool IsPharmacy(NPCInfo npc)
        {
            if (npc == null) return false;

            if (PharmacyCache.TryGetValue(npc, out bool cached)) return cached;

            bool result = SellsPotion(npc.EntryPage);

            if (!result && npc.EntryPage?.Buttons != null)
            {
                foreach (NPCButton button in npc.EntryPage.Buttons)
                {
                    if (!SellsPotion(button.DestinationPage)) continue;

                    result = true;
                    break;
                }
            }

            PharmacyCache[npc] = result;

            return result;
        }

        private static bool SellsPotion(NPCPage page)
        {
            if (page?.Goods == null) return false;

            foreach (NPCGood good in page.Goods)
            {
                if (good?.Item == null) continue;
                if (good.Item.ItemType != ItemType.Consumable) continue;

                if (good.Item.Stats[Stat.Health] > 0 || good.Item.Stats[Stat.Mana] > 0) return true;
            }

            return false;
        }

        /// <summary>把 NPC 页里「能进买卖页」的按钮号找出来（实测固定是 1，但不写死）。</summary>
        public static int FindShopButton(NPCPage page)
        {
            if (page?.Buttons == null) return -1;

            foreach (NPCButton button in page.Buttons)
            {
                if (!SellsPotion(button.DestinationPage)) continue;

                return button.ButtonID;
            }

            return -1;
        }

        #endregion

        #region 挖取（Harvest）

        /// <summary>
        /// 找一具「可以挖」的尸体。
        /// <para>
        /// **关键机制**：鸡、猪、牛这类怪（<c>MonsterInfo.AI</c> = 1/2/3）的掉落
        /// **不会直接掉在地上**，而是留在尸体里 —— 引擎的 <c>Drop</c> 里有
        /// <c>if (!NeedHarvest) return;</c>，需挖的怪只把掉落记进
        /// <c>Drops[账号]</c>。玩家必须走过去对尸体发 <c>C.Harvest</c> 才能拿到。
        /// </para>
        /// <para>
        /// 所以假人光靠捡地上掉落物是拿不到鸡猪牛身上的东西的 ——
        /// 必须补上挖取，否则假人的背包永远是空的、卖货与换装两条链路都是死的。
        /// </para>
        /// </summary>
        public static MonsterObject FindHarvestableCorpse(PlayerObject player, int radius)
        {
            Map map = player?.CurrentMap;

            if (map == null) return null;

            MonsterObject best = null;
            int bestDistance = int.MaxValue;

            foreach (MapObject ob in map.Objects)
            {
                if (!(ob is MonsterObject monster)) continue;

                // 必须是「需挖取」的怪，且身上还挂着属于这个账号的掉落。
                if (!monster.NeedHarvest) continue;
                if (monster.Drops == null) continue;

                if (!monster.Drops.TryGetValue(player.Character?.Account, out List<UserItem> items)) continue;
                if (items == null || items.Count == 0) continue;

                int distance = BotNavigator.Distance(player.CurrentLocation, monster.CurrentLocation);

                if (distance > radius || distance >= bestDistance) continue;

                best = monster;
                bestDistance = distance;
            }

            return best;
        }

        #endregion

        #region 杂货商（回城卷卖家）

        /// <summary>
        /// 「这个 NPC 卖不卖回城卷」的缓存。按 NPCInfo 引用做 key（Index 在建号前可能是 0）。
        /// 12 个杂货商是静态数据，一次扫描终身有效。
        /// </summary>
        private static readonly Dictionary<NPCInfo, bool> ScrollShopCache = new Dictionary<NPCInfo, bool>();

        /// <summary>
        /// 在当前地图上找卖回城卷的 NPC（杂货商）。
        /// <para>
        /// ⚠ **杂货商与药店不是同一个 NPC** —— 实测 12 个主城里：
        /// 药店（大卫 / 海莉 / 凯茜…）只卖药，杂货商（伦纳德 / 科里 / 赛文…）只卖卷轴火把这类，
        /// 只有炼狱岛的拉瓦尔两个都有。所以「买药」和「买回城卷」要分别跑一趟。
        /// </para>
        /// </summary>
        public static NPCObject FindScrollShop(Map map)
        {
            if (map?.NPCs == null) return null;

            foreach (NPCObject npc in map.NPCs)
            {
                if (npc?.NPCInfo == null) continue;

                if (IsScrollShop(npc.NPCInfo)) return npc;
            }

            return null;
        }

        /// <summary>
        /// 判断一个 NPC 是不是杂货商：入口页、或入口页一层按钮的目标页里，有回城卷（Shape == 2）。
        /// </summary>
        public static bool IsScrollShop(NPCInfo npc)
        {
            if (npc == null) return false;

            if (ScrollShopCache.TryGetValue(npc, out bool cached)) return cached;

            bool result = SellsTownScroll(npc.EntryPage);

            if (!result && npc.EntryPage?.Buttons != null)
            {
                foreach (NPCButton button in npc.EntryPage.Buttons)
                {
                    if (!SellsTownScroll(button.DestinationPage)) continue;

                    result = true;
                    break;
                }
            }

            ScrollShopCache[npc] = result;

            return result;
        }

        private static bool SellsTownScroll(NPCPage page)
        {
            if (page?.Goods == null) return false;

            foreach (NPCGood good in page.Goods)
            {
                if (good?.Item == null) continue;
                if (!BotActions.IsTownScroll(good.Item)) continue;

                return true;
            }

            return false;
        }

        /// <summary>
        /// 在 NPC 的入口页里找出「通往卖回城卷的买卖页」的按钮号。
        /// 返回 -1 表示这个 NPC 不卖回城卷。
        /// </summary>
        public static int FindScrollButton(NPCInfo npc)
        {
            if (npc?.EntryPage?.Buttons == null) return -1;

            foreach (NPCButton button in npc.EntryPage.Buttons)
            {
                if (!SellsTownScroll(button.DestinationPage)) continue;

                return button.ButtonID;
            }

            return -1;
        }

        #endregion

        #region 商店与修理铺

        /// <summary>
        /// 「这个 NPC 收不收这类东西 / 修不修装备」的缓存。
        /// 按 NPCInfo 引用做 key（Index 在建号前可能是 0）。125 个 NPC 是静态数据，缓存终身有效。
        /// </summary>
        private static readonly Dictionary<NPCInfo, HashSet<ItemType>> TraderCache = new Dictionary<NPCInfo, HashSet<ItemType>>();
        private static readonly Dictionary<NPCInfo, bool> RepairCache = new Dictionary<NPCInfo, bool>();

        /// <summary>
        /// 在当前地图上找收 <paramref name="itemType"/> 这类东西的商人。
        /// <para>
        /// P1-2 卖货用。关键点：**每个商店只收自己那几类东西** ——
        /// 实测「姜先生」只收 Weapon/Ore、「艾米」只收 Necklace/Bracelet/Ring、
        /// 「琳达」只收 Armour/Helmet/Shoes、「伦纳德」只收 Torch/Poison/Amulet/Scroll/DarkStone。
        /// 卖错地方引擎会静默拒绝（<c>NPCPage.Types.Any(x => x.ItemType == item.Info.ItemType)</c>），
        /// 所以必须按类型找对应商店。
        /// </para>
        /// </summary>
        public static NPCObject FindTrader(Map map, ItemType itemType)
        {
            if (map?.NPCs == null) return null;

            foreach (NPCObject npc in map.NPCs)
            {
                if (npc?.NPCInfo == null) continue;

                if (AcceptsType(npc.NPCInfo, itemType)) return npc;
            }

            return null;
        }

        /// <summary>在当前地图上找修理铺。</summary>
        public static NPCObject FindRepairShop(Map map)
        {
            if (map?.NPCs == null) return null;

            foreach (NPCObject npc in map.NPCs)
            {
                if (npc?.NPCInfo == null) continue;

                if (HasRepairPage(npc.NPCInfo)) return npc;
            }

            return null;
        }

        /// <summary>这个 NPC 收不收这类东西（查它入口页一层按钮的目标页）。</summary>
        public static bool AcceptsType(NPCInfo npc, ItemType itemType)
        {
            if (npc == null) return false;

            if (!TraderCache.TryGetValue(npc, out HashSet<ItemType> types))
            {
                types = CollectAcceptedTypes(npc);
                TraderCache[npc] = types;
            }

            return types.Contains(itemType);
        }

        /// <summary>这个 NPC 有没有修理页。</summary>
        public static bool HasRepairPage(NPCInfo npc)
        {
            if (npc == null) return false;

            if (RepairCache.TryGetValue(npc, out bool cached)) return cached;

            bool result = FindRepairPage(npc.EntryPage) != null || HasRepairButton(npc.EntryPage);

            RepairCache[npc] = result;

            return result;
        }

        /// <summary>在当前页里找修理页（当前页本身就是修理页时返回它）。</summary>
        public static NPCPage FindRepairPage(NPCPage page)
        {
            if (page == null) return null;

            if (page.DialogType == NPCDialogType.Repair) return page;

            return null;
        }

        private static bool HasRepairButton(NPCPage page)
        {
            if (page?.Buttons == null) return false;

            foreach (NPCButton button in page.Buttons)
            {
                if (button.DestinationPage?.DialogType == NPCDialogType.Repair) return true;
            }

            return false;
        }

        /// <summary>找出这个 NPC 的所有买卖页收货类型（入口页 + 一层按钮的目标页）。</summary>
        private static HashSet<ItemType> CollectAcceptedTypes(NPCInfo npc)
        {
            HashSet<ItemType> types = new HashSet<ItemType>();

            void Collect(NPCPage page)
            {
                if (page == null) return;

                if (page.DialogType == NPCDialogType.BuySell && page.Types != null)
                {
                    foreach (NPCType type in page.Types)
                    {
                        if (type?.ItemType == null) continue;

                        types.Add(type.ItemType);
                    }
                }
            }

            Collect(npc.EntryPage);

            if (npc.EntryPage?.Buttons != null)
            {
                foreach (NPCButton button in npc.EntryPage.Buttons)
                    Collect(button.DestinationPage);
            }

            return types;
        }

        /// <summary>
        /// 在 NPC 的入口页里找出「通往收 <paramref name="itemType"/> 的买卖页」的按钮号。
        /// 返回 -1 表示这个 NPC 不收这类东西。
        /// </summary>
        public static int FindTradeButton(NPCInfo npc, ItemType itemType)
        {
            if (npc?.EntryPage?.Buttons == null) return -1;

            foreach (NPCButton button in npc.EntryPage.Buttons)
            {
                NPCPage target = button.DestinationPage;

                if (target?.DialogType != NPCDialogType.BuySell) continue;
                if (target.Types == null) continue;

                foreach (NPCType type in target.Types)
                {
                    if (type?.ItemType != itemType) continue;

                    return button.ButtonID;
                }
            }

            return -1;
        }

        /// <summary>在 NPC 的入口页里找出「通往修理页」的按钮号，返回 -1 表示没有。</summary>
        public static int FindRepairButton(NPCInfo npc)
        {
            if (npc?.EntryPage?.Buttons == null) return -1;

            foreach (NPCButton button in npc.EntryPage.Buttons)
            {
                if (button.DestinationPage?.DialogType == NPCDialogType.Repair) return button.ButtonID;
            }

            return -1;
        }

        #endregion
    }
}
