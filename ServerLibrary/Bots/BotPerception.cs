using Library;
using Library.SystemModels;
using Server.DBModels;
using Server.Envir;
using Server.Models;
using Server.Models.Monsters;
using System;
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
        /// <summary>
        /// 找一只该打的怪。
        /// <para>
        /// <paramref name="respectRealPlayerTags"/>：真人已经打过的怪不抢。判据是引擎的
        /// <c>MonsterObject.EXPOwner</c>（「谁先打谁拥有」，怪物第一次被伤害时挂上），
        /// 它不是假人 → 说明真人在打，直接放过。这是真人最容易察觉的一条（一群假人围着你的怪砍）。
        /// </para>
        /// <para>
        /// <paramref name="skipBoss"/>：BOSS 留给真人。假人打 BOSS 会抢掉落，而且大概率被围死。
        /// </para>
        /// </summary>
        public static MonsterObject FindHuntTarget(PlayerObject player, int radius, bool respectRealPlayerTags = true, bool skipBoss = true)
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

                // BOSS 留给真人（P4-2）。
                if (skipBoss && info.IsBoss) continue;

                // 真人优先（P4-3）：这只怪的击杀权在真人手上，别去抢。
                if (respectRealPlayerTags && IsOwnedByRealPlayer(monster)) continue;

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
                if (IsPotionSurplus(player, item.Item)) continue;

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
        /// <summary>
        /// 药水囤积上限（瓶数）：补给目标 × 2。配置为 0/负数（关掉自动补给）= 不限。
        /// </summary>
        public static int PotionStockLimit(int restockTarget)
        {
            return restockTarget <= 0 ? int.MaxValue : Math.Max(1, restockTarget * 2);
        }

        /// <summary>
        /// 地上这瓶药还要不要捡？true = 已经够了，别捡。
        ///
        /// <para><b>为什么要拦</b>：11 倍爆率下地上到处是药，假人见什么捡什么 ——
        /// 实测有人囤了 90 瓶红药 + 46 瓶蓝药，背包总重 155 里药水占 142（92%），
        /// 而药又因为「自己要用」不许卖，于是负重降不下来、新战利品捡不进来，卡死。</para>
        ///
        /// <para><b>两道闸门，任意一条超了就不捡</b>：① 该属性药水已有 ≥ 目标×2 瓶；
        /// ② 药水总重已占负重上限的 40%。只按瓶数不够 —— 低等级角色负重上限才 58，
        /// 40 瓶药就占 69%，得让重量维度也能兜住。</para>
        /// </summary>
        public static bool IsPotionSurplus(PlayerObject player, UserItem item)
        {
            if (player?.Inventory == null || item?.Info == null) return false;
            if (item.Info.ItemType != ItemType.Consumable) return false;

            bool heals = item.Info.Stats[Stat.Health] > 0;
            bool restoresMana = item.Info.Stats[Stat.Mana] > 0;

            // 卷轴 / 食物之类不算药，走普通物品判定。
            if (!heals && !restoresMana) return false;

            Stat stat = heals ? Stat.Health : Stat.Mana;

            // 战士不喝蓝药，地上的也别捡。
            if (stat == Stat.Mana && player.Character?.Class == MirClass.Warrior) return true;

            int target = stat == Stat.Health ? Config.BotRestockBuyRed : Config.BotRestockBuyBlue;
            int limit = PotionStockLimit(target);

            if (limit != int.MaxValue && BotActions.CountPotion(player, stat) >= limit) return true;

            long capacity = player.Stats[Stat.BagWeight];

            if (capacity <= 0) return false;

            long potionWeight = 0;

            for (int i = 0; i < player.Inventory.Length; i++)
            {
                UserItem owned = player.Inventory[i];

                if (owned?.Info == null) continue;
                if (owned.Info.ItemType != ItemType.Consumable) continue;
                if (owned.Info.Stats[Stat.Health] <= 0 && owned.Info.Stats[Stat.Mana] <= 0) continue;

                potionWeight += (long)owned.Info.Weight * Math.Max(1, owned.Count);
            }

            return potionWeight >= capacity * 40 / 100;
        }

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

        /// <summary>
        /// 找一个红名目标（P3-4）。只返回「名字红到会被系统标红」的玩家，白名一律不碰。
        /// <para>
        /// 判据与 <c>PlayerObject.ProcessNameColour</c> 完全一致：<c>Stats[Stat.PKPoint] &gt;= Config.RedPoint</c>。
        /// 安全区里的红名也不打 —— 那是系统保护的休战区，打了等于当着所有人违规。
        /// </para>
        /// </summary>
        public static PlayerObject FindRedNameTarget(PlayerObject player, int radius, int redPoint)
        {
            Map map = player?.CurrentMap;

            if (map == null) return null;

            PlayerObject best = null;
            int bestDistance = int.MaxValue;

            foreach (PlayerObject other in map.Players)
            {
                if (other == null || other == player) continue;
                if (other.Dead || other.Node == null) continue;
                if (other.Connection == null) continue;
                if (other.Stats[Stat.PKPoint] < redPoint) continue;
                if (InSafeZone(other)) continue;

                int distance = BotNavigator.Distance(player.CurrentLocation, other.CurrentLocation);

                if (distance > radius || distance >= bestDistance) continue;

                best = other;
                bestDistance = distance;
            }

            return best;
        }

        /// <summary>
        /// 这个角色现在是否在排行榜前 <paramref name="top"/> 名（P4-4 榜首降速）。
        /// <para>
        /// 直接走 <c>SEnvir.Rankings</c> 链表的头几个节点，O(top) —— 不用
        /// <c>GetRankingPosition</c>（那个是 private，而且要从节点一路往前数，O(n)）。
        /// 30 个假人每几秒调一次也不会有性能问题。
        /// </para>
        /// <para>
        /// <c>RankingNode</c> 可能为 null（角色还没进排行榜 / 刚建号），一律当作「不在榜上」。
        /// </para>
        /// </summary>
        public static bool IsTopRanked(CharacterInfo character, int top)
        {
            if (character?.RankingNode == null) return false;
            if (SEnvir.Rankings == null || top <= 0) return false;

            LinkedListNode<CharacterInfo> node = SEnvir.Rankings.First;

            for (int i = 0; i < top && node != null; i++, node = node.Next)
            {
                if (ReferenceEquals(node.Value, character)) return true;
            }

            return false;
        }

        /// <summary>
        /// 这只怪的击杀权是否在真人手上（P4-3 真人优先）。
        /// <para>
        /// <c>EXPOwner</c> 是引擎的「谁先打谁拥有」标记：怪物第一次被伤害时由 <c>PlayerObject</c> 挂上，
        /// 死亡时清空。为 null = 还没人打过，假人可以打。
        /// </para>
        /// <para>
        /// 判据用 <c>Connection.IsBot</c> 而不是角色名 / 账号：假人是伪连接，这个标记在整个
        /// 生命周期里恒为 true，重启后重建的假人也是 true。
        /// </para>
        /// </summary>
        public static bool IsOwnedByRealPlayer(MonsterObject monster)
        {
            PlayerObject owner = monster?.EXPOwner;

            if (owner == null) return false;

            // Connection 为 null 说明这个人已经下线（击杀权还挂着）。这种情况当作「没人在打」，
            // 让假人接着打，免得怪烂在地上、刷怪点被一具「僵尸所有权」卡住。
            if (owner.Connection == null) return false;

            return !owner.Connection.IsBot;
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

        /// <summary>「带某种 NPC 动作」的 NPC 缓存（转生用）。null 是有效值（全服没有）。</summary>
        private static readonly Dictionary<NPCActionType, NPCInfo> ActionNpcCache = new Dictionary<NPCActionType, NPCInfo>();

        /// <summary>有六合圣石的地图集合（懒加载一次）。用来在挑练级图时优先选「能传送到的」。</summary>
        private static HashSet<MapInfo> _stoneMaps;

        /// <summary>地图 → 这张图上的六合石（只缓存命中，见 <see cref="FindTeleportStone"/>）。</summary>
        private static readonly Dictionary<Map, NPCObject> TeleportStoneMapCache = new Dictionary<Map, NPCObject>();

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

            // 找到了就记住：每个假人每次决策都要问一次，遍历整张图的 NPC 不便宜
            // （D5 有 30 个假人 × 每秒 2~5 次决策）。
            // **只缓存命中，不缓存 null**：地图的 NPC 列表可能是懒加载的，
            // 缓存了「没有石头」会让这张图永远找不到石头。
            if (TeleportStoneMapCache.TryGetValue(map, out NPCObject cached) && cached != null)
                return cached;

            foreach (NPCObject npc in map.NPCs)
            {
                if (npc?.NPCInfo == null) continue;

                if (!IsTeleportStone(npc.NPCInfo)) continue;

                TeleportStoneMapCache[map] = npc;

                return npc;
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

            bool result = HasTeleportTarget(npc.EntryPage, 0);

            if (!result && npc.EntryPage?.Buttons != null)
            {
                foreach (NPCButton button in npc.EntryPage.Buttons)
                {
                    if (!HasTeleportTarget(button.DestinationPage, 0)) continue;

                    result = true;
                    break;
                }
            }

            TeleportStoneCache[npc] = result;

            return result;
        }

        /// <summary>
        /// 从按钮的目标页开始，沿 <see cref="NPCPage.SuccessPage"/> 链往下走（最多 6 层）。
        /// <para>
        /// **为什么必须串链**：引擎的 <c>NPCObject.NPCCall</c> 是「执行本页动作 → Say 为空就跳
        /// SuccessPage 继续」，所以动作可以挂在链上任意一页。
        /// </para>
        /// <para>
        /// 六合圣石的真实结构就是两段：
        /// <c>入口页 → 按钮「失乐园 10K」→ 页[TakeGold 10000] → SuccessPage「传送至失乐园」[Teleport]</c>。
        /// 只看按钮直达页会得出「这颗石头不会传送」——D5 里 30 个假人因此一次都没传送过，
        /// 全走 A* 逐格跨图，被沿途的怪打成活锁（死 441 / 杀 599）。
        /// </para>
        /// </summary>
        private static IEnumerable<NPCPage> PageChain(NPCPage start)
        {
            for (int guard = 0; start != null && guard < 6; guard++)
            {
                yield return start;

                start = start.SuccessPage;
            }
        }

        /// <summary>
        /// 这条页链里有没有「传送到 <paramref name="targetMapIndex"/>」的动作。
        /// <paramref name="targetMapIndex"/> = 0 表示只要求是一个传送动作。
        /// </summary>
        private static bool HasTeleportTarget(NPCPage start, int targetMapIndex)
        {
            if (start == null) return false;

            foreach (NPCPage page in PageChain(start))
            {
                if (page.Actions == null) continue;

                foreach (NPCAction action in page.Actions)
                {
                    if (action.ActionType != NPCActionType.Teleport) continue;
                    if (action.MapParameter1 == null) continue;

                    if (targetMapIndex > 0 && action.MapParameter1.Index != targetMapIndex) continue;

                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 在传送石的入口页里找「通往 <paramref name="targetMapIndex"/> 的按钮」，返回 ButtonID；找不到返回 -1。
        /// <para>
        /// 只匹配**直达**按钮（按钮页 + 它的 SuccessPage 链里有指向目标图的 Teleport 动作）。
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
                if (!HasTeleportTarget(button.DestinationPage, targetMapIndex)) continue;

                return button.ButtonID;
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

                if (!HasTeleportTarget(dest, targetMapIndex)) continue;

                // 收费（TakeGold）在按钮页，传送（Teleport）在 SuccessPage ——
                // 所以钱要沿整条链累加，不能只看按钮页。
                int cost = 0;

                foreach (NPCPage page in PageChain(dest))
                {
                    if (page.Actions == null) continue;

                    foreach (NPCAction action in page.Actions)
                        if (action.ActionType == NPCActionType.TakeGold && action.IntParameter1 > 0)
                            cost += action.IntParameter1;
                }

                return cost;
            }

            return 0;
        }

        #endregion

        #region 刷怪价值

        /// <summary>
        /// 这张图上有没有「自己这个等级能打」的刷怪区（只看数据，不加载地图、不判可达性）。
        /// <para>
        /// 用途：决定「本图没怪了要不要跨图」。跨图一趟 1~3 分钟，没有六合石的洞穴图
        /// 还得烧回城卷回主城再传，来回折返比留在本图等刷新更亏。
        /// 只有本图**确实没有**这个等级的怪，才值得走。
        /// </para>
        /// <para>
        /// 等级窗口与 <c>BotBrain.TryPickHuntPoint</c> / <c>TryTravelToHuntMap</c> 保持一致：
        /// 怪等级 ≤ 自己 +4（打得动）、≥ 自己 -8（还有经验价值）。
        /// </para>
        /// </summary>
        public static bool HasHuntValue(MapInfo map, int level)
        {
            if (map == null) return false;

            foreach (RespawnInfo region in SEnvir.RespawnInfoList.Binding)
            {
                if (region?.Region?.Map != map) continue;

                MonsterInfo monster = region.Monster;

                if (monster == null) continue;

                if (monster.Level > level + 4) continue;
                if (monster.Level < level - 8) continue;

                return true;
            }

            return false;
        }

        #endregion

        #region 传送图（两跳规划）

        /// <summary>全服传送邻接表：出发图 → 能直达的地图索引集合。懒加载一次。</summary>
        private static Dictionary<MapInfo, HashSet<int>> _teleportGraph;

        /// <summary>地图索引 → <see cref="MapInfo"/>。懒加载一次。</summary>
        private static Dictionary<int, MapInfo> _mapInfoByIndex;

        /// <summary>「这张图没有直达按钮」时返回的空集合（避免每次 new）。</summary>
        private static readonly HashSet<int> EmptyTeleportTargets = new HashSet<int>();

        /// <summary>按索引取 <see cref="MapInfo"/>（找不到返回 null）。</summary>
        public static MapInfo GetMapInfo(int index)
        {
            if (index <= 0) return null;

            if (_mapInfoByIndex == null)
            {
                Dictionary<int, MapInfo> map = new Dictionary<int, MapInfo>();

                foreach (MapInfo info in SEnvir.MapInfoList.Binding)
                {
                    if (info == null) continue;

                    map[info.Index] = info;
                }

                // 空表说明数据还没加载完，别把空表缓存下来（否则整轮都以为全服没有地图）。
                if (map.Count == 0) return null;

                _mapInfoByIndex = map;
            }

            return _mapInfoByIndex.TryGetValue(index, out MapInfo found) ? found : null;
        }

        /// <summary>
        /// 从 <paramref name="from"/> 这张图的六合石出发，能**一跳直达**哪些地图（索引集合，可能为空）。
        /// <para>
        /// 只看 NPC 数据（<see cref="SEnvir.NPCInfoList"/>），不加载地图、不需要玩家站在那儿，
        /// 所以可以用来做「从 A 能不能经 B 到 C」的规划。
        /// </para>
        /// </summary>
        public static HashSet<int> TeleportTargetsFrom(MapInfo from)
        {
            if (from == null) return EmptyTeleportTargets;

            if (_teleportGraph == null)
            {
                Dictionary<MapInfo, HashSet<int>> graph = new Dictionary<MapInfo, HashSet<int>>();

                foreach (NPCInfo npc in SEnvir.NPCInfoList.Binding)
                {
                    MapInfo map = npc?.Region?.Map;

                    if (map == null) continue;
                    if (!IsTeleportStone(npc)) continue;

                    if (!graph.TryGetValue(map, out HashSet<int> targets))
                        graph[map] = targets = new HashSet<int>();

                    if (npc.EntryPage?.Buttons == null) continue;

                    foreach (NPCButton button in npc.EntryPage.Buttons)
                    {
                        foreach (NPCPage page in PageChain(button.DestinationPage))
                        {
                            if (page.Actions == null) continue;

                            foreach (NPCAction action in page.Actions)
                            {
                                if (action?.ActionType != NPCActionType.Teleport) continue;
                                if (action.MapParameter1 == null) continue;

                                targets.Add(action.MapParameter1.Index);
                            }
                        }
                    }
                }

                // 同 _stoneMaps：空表说明数据没加载完，别缓存。
                if (graph.Count == 0) return EmptyTeleportTargets;

                _teleportGraph = graph;
            }

            return _teleportGraph.TryGetValue(from, out HashSet<int> set) ? set : EmptyTeleportTargets;
        }

        /// <summary>
        /// <paramref name="from"/> 的六合石直达 <paramref name="target"/> 的最便宜票价（-1 = 没有直达）。
        /// 0 表示免费或未知（引擎里免费传送不带 TakeGold）。
        /// </summary>
        public static int CheapestTeleportCostFrom(MapInfo from, int target)
        {
            if (from == null || target <= 0) return -1;

            int best = -1;

            foreach (NPCInfo npc in SEnvir.NPCInfoList.Binding)
            {
                if (npc?.Region?.Map != from) continue;
                if (FindTeleportButton(npc, target) < 0) continue;

                int cost = CheapestTeleportCost(npc, target);

                if (best < 0 || cost < best) best = cost;
            }

            return best;
        }

        /// <summary>
        /// 两跳规划：<paramref name="from"/> 没有直达 <paramref name="target"/> 的按钮时，
        /// 找一个中转图 <paramref name="viaIndex"/>，使 from→via→target 两跳都成立。
        /// <para>
        /// **为什么需要**：本服的六合石按钮是「按城给」的，不是全连通。
        /// 实测沙漠土城只有「失乐园 / 沙巴克城 / 诺玛村庄」三个按钮，**没有潘夜村**；
        /// 而诺玛村落的石头能到潘夜村。没有两跳规划时，假人只能「烧一张回城卷回比奇城再传」——
        /// 白白多花一张卷 + 一次城内长途，还可能在路上被打死。
        /// </para>
        /// <para>
        /// 选路顺序：先挑「总票价最低」，同价再挑「目标图怪等级更贴近」的（由调用方决定）。
        /// 中转图不能是自己、也不能是目标图本身。
        /// </para>
        /// </summary>
        public static bool TryFindTwoHopRoute(MapInfo from, int target, out int viaIndex)
        {
            viaIndex = 0;

            if (from == null || target <= 0) return false;

            // 已经能直达就不用两跳。
            if (TeleportTargetsFrom(from).Contains(target)) return false;

            int bestCost = int.MaxValue;
            int bestVia = 0;

            foreach (int via in TeleportTargetsFrom(from))
            {
                if (via == target) continue;
                if (via == from.Index) continue;

                MapInfo viaInfo = GetMapInfo(via);

                if (viaInfo == null) continue;

                // 中转图本身得有石头，且能直达目标。
                if (!TeleportTargetsFrom(viaInfo).Contains(target)) continue;

                int cost1 = CheapestTeleportCostFrom(from, via);
                int cost2 = CheapestTeleportCostFrom(viaInfo, target);

                if (cost1 < 0 || cost2 < 0) continue;

                int total = cost1 + cost2;

                if (total >= bestCost) continue;

                bestCost = total;
                bestVia = via;
            }

            if (bestVia <= 0) return false;

            viaIndex = bestVia;

            return true;
        }

        #endregion

        #region 转生 / 特殊动作

        /// <summary>
        /// 这张地图上有没有六合圣石（只看 NPC 数据，不加载地图）。
        /// 挑练级图时优先挑有石头的 —— 传过去 1 秒，走 A* 过去要 1~3 分钟还容易被怪打死。
        /// </summary>
        public static bool HasTeleportStone(MapInfo info)
        {
            if (info == null) return false;

            if (_stoneMaps == null)
            {
                HashSet<MapInfo> set = new HashSet<MapInfo>();

                foreach (NPCInfo npc in SEnvir.NPCInfoList.Binding)
                {
                    if (npc?.Region?.Map == null) continue;
                    if (!IsTeleportStone(npc)) continue;

                    set.Add(npc.Region.Map);
                }

                _stoneMaps = set;
            }

            return _stoneMaps.Contains(info);
        }

        /// <summary>
        /// 找出「进哪个对话框能触发 <paramref name="type"/> 动作」的 NPC（全服唯一命中，带缓存）。
        /// 实测（mapq action:Rebirth）：全服只有 1 处 —— 市政厅[2] 的「管理员」，
        /// 路径是「入口页 按钮5 转生主页 → 按钮1 执行转生[Rebeath 动作]」。
        /// </summary>
        public static NPCInfo FindActionNpc(NPCActionType type)
        {
            if (ActionNpcCache.TryGetValue(type, out NPCInfo cached)) return cached;

            NPCInfo found = null;

            foreach (NPCInfo npc in SEnvir.NPCInfoList.Binding)
            {
                if (npc?.EntryPage == null) continue;

                if (FindButtonToward(npc.EntryPage, type) > 0 || PageHasAction(npc.EntryPage, type))
                {
                    found = npc;
                    break;
                }
            }

            ActionNpcCache[type] = found;

            return found;
        }

        /// <summary>这一页（含其 SuccessPage 链）里有没有该动作。</summary>
        public static bool PageHasAction(NPCPage page, NPCActionType type)
        {
            foreach (NPCPage p in PageChain(page))
            {
                if (p.Actions == null) continue;

                foreach (NPCAction action in p.Actions)
                    if (action?.ActionType == type)
                        return true;
            }

            return false;
        }

        /// <summary>
        /// 在当前页里找「点下去能走到带该动作的页」的按钮。返回 ButtonID，找不到返回 -1。
        /// 一次只走一层，调用方循环点，比一次性算整条路径更抗页面结构变化。
        /// </summary>
        public static int FindButtonToward(NPCPage page, NPCActionType type)
        {
            if (page?.Buttons == null) return -1;

            foreach (NPCButton button in page.Buttons)
            {
                if (button == null) continue;

                if (PageHasAction(button.DestinationPage, type))
                    return button.ButtonID;
            }

            // 再深一层（最多 3 层）：转生主页 → 执行转生 就是两层。
            foreach (NPCButton button in page.Buttons)
            {
                if (button?.DestinationPage?.Buttons == null) continue;

                foreach (NPCButton inner in button.DestinationPage.Buttons)
                    if (inner != null && PageHasAction(inner.DestinationPage, type))
                        return button.ButtonID;
            }

            return -1;
        }

        #endregion

        #region 传送（六合圣石）

        /// <summary>
        /// 在 <paramref name="map"/> 上找一颗**能直达 <paramref name="targetMapIndex"/>** 的六合石。
        /// <para>
        /// **为什么不能只取第一颗**：一个城里常常有好几颗石头，分工不同。
        /// 实测沙漠土城就有「荒漠土城内传送员」（只有「荒漠土城内城 1K」一个按钮，
        /// Teleport 指向沙漠土城自己）和「荒漠土城外传送员」（失乐园 10K / 自由传送员）。
        /// 取 map.NPCs 里第一颗会拿到**内传送员**，于是「去失乐园」永远判成「没有直达按钮」，
        /// 假人只能 A* 硬走 —— 2026-11-12 用诊断日志实测到的就是这一条。
        /// </para>
        /// </summary>
        public static NPCObject FindTeleportStoneFor(Map map, int targetMapIndex)
        {
            if (map?.NPCs == null) return null;
            if (targetMapIndex <= 0) return null;

            foreach (NPCObject npc in map.NPCs)
            {
                if (npc?.NPCInfo == null) continue;
                if (!IsTeleportStone(npc.NPCInfo)) continue;
                if (FindTeleportButton(npc.NPCInfo, targetMapIndex) < 0) continue;

                return npc;
            }

            return null;
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

        /// <summary>修理页收哪些类型（武器店 / 首饰店 / 服装店各不同，见 <see cref="FindRepairShop"/>）。</summary>
        private static readonly Dictionary<NPCInfo, HashSet<ItemType>> RepairTypeCache = new Dictionary<NPCInfo, HashSet<ItemType>>();

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

        /// <summary>
        /// 在当前地图上找修理铺。
        /// <para>
        /// <b>必须按类型找</b>：修理铺分三家 —— 武器店收 <c>Weapon,Shield</c>、
        /// 首饰店收 <c>Ring,Bracelet,Necklace</c>、服装店收 <c>Armour,Helmet,Shoes,Shield</c>
        /// （<c>npcq --repair</c> 实测 34 个带修理页的 NPC）。
        /// 原来只挑「第一个带修理页的 NPC」，而 <c>map.NPCs</c> 里武器店通常排在前面，
        /// 于是 <see cref="BotBrain"/> 的 <c>BuildRepairLinks</c> 按页收类型一过滤，
        /// 衣服 / 首饰全被丢掉 → 报「没有要修的装备」→ 10 分钟冷却 → **永远修不到**。
        /// 实测（2026-11-12）<c>老酒坛</c> 衣服 0%、<c>半盏茶</c> 10%、<c>落花生</c> 武器 12%。
        /// </para>
        /// <para><paramref name="itemType"/> 传 <c>Nothing</c> 时退化成「随便找一家」，只用于诊断。</para>
        /// </summary>
        public static NPCObject FindRepairShop(Map map, ItemType itemType = ItemType.Nothing)
        {
            if (map?.NPCs == null) return null;

            NPCObject fallback = null;

            foreach (NPCObject npc in map.NPCs)
            {
                if (npc?.NPCInfo == null) continue;
                if (!HasRepairPage(npc.NPCInfo)) continue;

                if (itemType == ItemType.Nothing) return npc;

                if (RepairAcceptsType(npc.NPCInfo, itemType)) return npc;

                if (fallback == null) fallback = npc;
            }

            // 这家图没有对口的铺子 —— 返回任意一家，让上层自己判断「修不了」，
            // 总比返回 null 让调用方误以为「这张图没有修理铺」好排查。
            return fallback;
        }

        /// <summary>
        /// 这个 NPC 的修理页收不收这类装备。
        /// <para>修理页既可能就在入口页上，也可能是入口页某个按钮的目标页，两处都要看。</para>
        /// </summary>
        public static bool RepairAcceptsType(NPCInfo npc, ItemType itemType)
        {
            if (npc == null) return false;

            if (!RepairTypeCache.TryGetValue(npc, out HashSet<ItemType> types))
            {
                types = CollectRepairTypes(npc);
                RepairTypeCache[npc] = types;
            }

            return types.Contains(itemType);
        }

        /// <summary>这个 NPC 的修理页收哪些类型（入口页 + 一层按钮的目标页）。</summary>
        private static HashSet<ItemType> CollectRepairTypes(NPCInfo npc)
        {
            HashSet<ItemType> types = new HashSet<ItemType>();

            void Collect(NPCPage page)
            {
                if (page?.DialogType != NPCDialogType.Repair || page.Types == null) return;

                foreach (NPCType type in page.Types)
                {
                    if (type == null) continue;

                    types.Add(type.ItemType);
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

        /// <summary>类型 → 「哪些地图上有收这一类的修理铺」。懒加载一次。</summary>
        private static Dictionary<ItemType, List<MapInfo>> _repairMapsByType;

        /// <summary>
        /// 全服范围内，哪些地图上有**收这一类装备**的修理铺。
        /// <para>
        /// <b>为什么不能只看当前图 / 绑定主城</b>：修理铺按店铺分家，而小村子往往缺一家 ——
        /// <c>npcq --repair</c> 实测潘夜村(2) 只有武器店（金先生）和首饰店（达科塔），
        /// **没有服装店**；假人老家正好是潘夜村，于是「衣服耐久见底」永远找不到铺子，
        /// 只能一遍遍 10 分钟冷却（实测 <c>老酒坛</c> 衣服 0%、<c>半盏茶</c> 10%）。
        /// </para>
        /// <para>
        /// 只读 NPC 数据（<see cref="SEnvir.NPCInfoList"/>）里的 <c>Region.Map</c>，
        /// **不加载地图**，所以不会把几十张图实例化进内存。
        /// </para>
        /// </summary>
        public static List<MapInfo> RepairMapsFor(ItemType itemType)
        {
            if (_repairMapsByType == null)
            {
                Dictionary<ItemType, List<MapInfo>> index = new Dictionary<ItemType, List<MapInfo>>();

                foreach (NPCInfo npc in SEnvir.NPCInfoList.Binding)
                {
                    if (npc?.Region?.Map == null) continue;
                    if (!HasRepairPage(npc)) continue;

                    foreach (ItemType accepted in CollectRepairTypes(npc))
                    {
                        if (!index.TryGetValue(accepted, out List<MapInfo> maps))
                            index[accepted] = maps = new List<MapInfo>();

                        if (!maps.Contains(npc.Region.Map)) maps.Add(npc.Region.Map);
                    }
                }

                // 空表说明数据还没加载完，别缓存（同 GetMapInfo）。
                if (index.Count == 0) return new List<MapInfo>();

                _repairMapsByType = index;
            }

            return _repairMapsByType.TryGetValue(itemType, out List<MapInfo> found) ? found : new List<MapInfo>();
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
