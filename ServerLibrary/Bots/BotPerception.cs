using Library;
using Library.SystemModels;
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

        /// <summary>找一件地上的掉落物（就近）。</summary>
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
    }
}
