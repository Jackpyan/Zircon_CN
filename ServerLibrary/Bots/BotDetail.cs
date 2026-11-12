using Library;
using Library.SystemModels;
using Server.DBModels;
using Server.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Server.Bots
{
    /// <summary>详情弹窗里的一行「属性：值」（物品卡的基础属性、角色的属性 tab 共用）。</summary>
    public sealed class BotDetailLine
    {
        public string Title { get; init; } = string.Empty;
        public string Value { get; init; } = string.Empty;
    }

    /// <summary>
    /// 纸娃娃上的一个装备槽。
    /// <para>
    /// <b>坐标来自客户端</b>：<c>Client/Scenes/Views/CharacterDialog.cs</c> 里每个 <c>DXItemCell</c> 的
    /// <c>Location</c> 原样搬过来（28×28 的格子，x∈[10,311]、y∈[90,302]）。
    /// 这样后台的纸娃娃和玩家在游戏里看到的角色窗口**是同一个排布**，运营不用重新学一套。
    /// </para>
    /// </summary>
    public sealed class BotDetailSlot
    {
        /// <summary><see cref="EquipmentSlot"/> 的数值（点槽位时回传它）。</summary>
        public int Index { get; init; }

        /// <summary>枚举名（<c>Weapon</c>/<c>Armour</c>…）。中文由 Web 层翻，别在服务端再维护一份。</summary>
        public string SlotName { get; init; } = string.Empty;

        /// <summary>客户端角色窗口里的格子坐标（左上角）。</summary>
        public int X { get; init; }
        public int Y { get; init; }

        /// <summary>这个槽位上有没有装备。</summary>
        public bool HasItem { get; init; }

        /// <summary>图标序号（<c>ItemInfo.Image</c>，对应 <c>wwwroot/icons/items/{Image}.png</c>）。</summary>
        public int Image { get; init; }

        public string ItemName { get; init; } = string.Empty;

        /// <summary>物品类型的中文名（如「武器」「衣服」）。</summary>
        public string TypeName { get; init; } = string.Empty;

        /// <summary>强化等级（<c>UserItem.Level</c>）。</summary>
        public int EnhanceLevel { get; init; }

        public int CurrentDurability { get; init; }
        public int MaxDurability { get; init; }

        /// <summary>剩余持久百分比（0~100，与后台表格、<see cref="BotItemRules.DurabilityPercent"/> 同一口径）。</summary>
        public int DurabilityPercent { get; init; }

        /// <summary>这件装备有没有「持久」这个概念（戒指/项链没有，卡片上就不画持久条）。</summary>
        public bool HasDurability { get; init; }

        public int Weight { get; init; }
        public int Price { get; init; }

        /// <summary>需求等级（<c>ItemInfo.RequiredAmount</c>）。</summary>
        public int RequiredLevel { get; init; }

        /// <summary>「绑定 / 不可交易」这类提示，空串表示没有。</summary>
        public string FlagsText { get; init; } = string.Empty;

        /// <summary>基础属性（破坏力 3-7、自然魔力 0-1 …）。</summary>
        public IReadOnlyList<BotDetailLine> Stats { get; init; } = Array.Empty<BotDetailLine>();
    }

    /// <summary>
    /// 角色详情（P5-3）。<b>只能在游戏线程构造</b> —— 它直接读 <c>PlayerObject</c>。
    /// </summary>
    public sealed class BotDetail
    {
        public int Seq { get; init; }
        public bool Online { get; init; }

        public string CharacterName { get; init; } = string.Empty;
        public string ClassName { get; init; } = string.Empty;
        public int Level { get; init; }
        public int ExpPercent { get; init; }

        public string MapName { get; init; } = string.Empty;
        public int MapIndex { get; init; }
        public int X { get; init; }
        public int Y { get; init; }

        public int CurrentHealth { get; init; }
        public int MaxHealth { get; init; }
        public int CurrentMana { get; init; }
        public int MaxMana { get; init; }

        public long Gold { get; init; }
        public int BagWeight { get; init; }
        public int MaxBagWeight { get; init; }
        public int UsedSlots { get; init; }
        public int SlotCount { get; init; }

        public int Kills { get; init; }
        public int Deaths { get; init; }
        public string State { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;

        /// <summary>纸娃娃的 17 个槽位（空格子也返回，页面才能把「空槽」画出来）。</summary>
        public IReadOnlyList<BotDetailSlot> Slots { get; init; } = Array.Empty<BotDetailSlot>();

        /// <summary>属性 tab 的行（只放有中文名的条目，顺序按 <see cref="Stat"/> 枚举）。</summary>
        public IReadOnlyList<BotDetailLine> Attributes { get; init; } = Array.Empty<BotDetailLine>();

        /// <summary>取详情时的本地时间（页面显示「更新 hh:mm:ss」）。</summary>
        public DateTime SnapshotAt { get; init; }

        #region 纸娃娃布局

        /// <summary>
        /// 17 个槽位在纸娃娃画布上的坐标（单位：一个「格子」= 28px，页面再乘缩放系数）。
        /// <para>
        /// <b>槽位集合</b>取自客户端 <c>Client/Scenes/Views/CharacterDialog.cs</c>（它建了哪 17 个
        /// <c>DXItemCell</c>，这里就有哪 17 个），**但排布是重新设计的**：
        /// 客户端那套坐标是围着**实时渲染的角色精灵**摆的（盾牌就压在胸口上、衣服贴着脑袋），
        /// 网页上我们渲染不出精灵，照搬会变成「几个方框叠在一张轮廓图上」，很难看也不好点。
        /// 所以这里改成「上排 4 / 左右各 4 / 下排 5」的对称网格 —— 和参照图一致，
        /// 运营一眼能认，也不依赖精灵。
        /// </para>
        /// <para>
        /// 画布 302×272（格子 28），身体轮廓画在 x∈[81,221]、y∈[36,220] 的中缝里。
        /// </para>
        /// </summary>
        public static readonly (EquipmentSlot Slot, int X, int Y)[] DollLayout =
        {
            // 上排：武器 / 护身符 / 盾牌 / 头盔
            (EquipmentSlot.Weapon, 92, 0),
            (EquipmentSlot.Amulet, 122, 0),
            (EquipmentSlot.Shield, 152, 0),
            (EquipmentSlot.Helmet, 182, 0),

            // 左列：时装 / 项链 / 火把 / 鞋子
            (EquipmentSlot.Costume, 8, 42),
            (EquipmentSlot.Necklace, 8, 74),
            (EquipmentSlot.Torch, 8, 106),
            (EquipmentSlot.Shoes, 8, 138),

            // 右列：衣服 / 徽章 / 马甲 / 花
            (EquipmentSlot.Armour, 266, 42),
            (EquipmentSlot.Emblem, 266, 74),
            (EquipmentSlot.HorseArmour, 266, 106),
            (EquipmentSlot.Flower, 266, 138),

            // 下排：左手镯 / 右手镯 / 左戒指 / 右戒指 / 毒药
            (EquipmentSlot.BraceletL, 77, 232),
            (EquipmentSlot.BraceletR, 107, 232),
            (EquipmentSlot.RingL, 137, 232),
            (EquipmentSlot.RingR, 167, 232),
            (EquipmentSlot.Poison, 197, 232),
        };

        /// <summary>纸娃娃画布的宽（格子单位）。页面乘缩放系数后就是像素宽。</summary>
        public const int DollWidth = 302;

        /// <summary>纸娃娃画布的高（格子单位）。</summary>
        public const int DollHeight = 272;

        /// <summary>纸娃娃坐标系里 y 的基准（当前布局从 0 开始，留着是为了以后微调不用改页面）。</summary>
        public const int DollTop = 0;

        #endregion

        /// <summary>
        /// 在**游戏线程**里把一个在线假人的详情读出来。
        /// <para>离线 / 还没拿到 <c>PlayerObject</c> 时返回 <c>null</c>，调用方显示「该假人不在线」。</para>
        /// </summary>
        public static BotDetail Build(BotSession session, BotSnapshot snapshot)
        {
            if (session == null) return null;

            PlayerObject player = session.Connection?.Player;

            if (player == null) return null;

            List<BotDetailSlot> slots = new List<BotDetailSlot>(DollLayout.Length);

            foreach ((EquipmentSlot slot, int x, int y) in DollLayout)
            {
                UserItem item = player.Equipment[(int)slot];

                slots.Add(ToSlot(slot, x, y, item));
            }

            return new BotDetail
            {
                Seq = session.Profile.Seq,
                Online = true,
                CharacterName = snapshot?.CharacterName ?? player.Name ?? string.Empty,
                ClassName = snapshot?.ClassName ?? player.Class.ToString(),
                Level = player.Level,
                ExpPercent = snapshot?.ExpPercent ?? 0,
                MapName = player.CurrentMap?.Info?.Description ?? "-",
                MapIndex = player.CurrentMap?.Info?.Index ?? 0,
                X = player.CurrentLocation.X,
                Y = player.CurrentLocation.Y,
                CurrentHealth = player.CurrentHP,
                MaxHealth = player.Stats[Stat.Health],
                CurrentMana = player.CurrentMP,
                MaxMana = player.Stats[Stat.Mana],
                Gold = player.Character?.Gold?.Amount ?? 0,
                BagWeight = player.BagWeight,
                MaxBagWeight = player.Stats[Stat.BagWeight],
                UsedSlots = BotPerception.UsedInventorySlots(player),
                SlotCount = player.Inventory?.Length ?? 0,
                Kills = snapshot?.Kills ?? 0,
                Deaths = snapshot?.Deaths ?? 0,
                State = snapshot?.State ?? string.Empty,
                Detail = snapshot?.Detail ?? string.Empty,
                Slots = slots,
                Attributes = BuildAttributes(player),
                SnapshotAt = Time.LocalNow,
            };
        }

        private static BotDetailSlot ToSlot(EquipmentSlot slot, int x, int y, UserItem item)
        {
            if (item?.Info == null)
            {
                return new BotDetailSlot
                {
                    Index = (int)slot,
                    SlotName = slot.ToString(),
                    X = x,
                    Y = y,
                };
            }

            ItemInfo info = item.Info;

            int max = item.MaxDurability;
            bool hasDurability = info.Durability > 0 && max > 0;

            return new BotDetailSlot
            {
                Index = (int)slot,
                SlotName = slot.ToString(),
                X = x,
                Y = y,
                HasItem = true,
                Image = info.Image,
                ItemName = info.ItemName ?? string.Empty,
                TypeName = BotText.Item(info.ItemType),
                EnhanceLevel = item.Level,
                CurrentDurability = item.CurrentDurability,
                MaxDurability = max,
                DurabilityPercent = hasDurability ? BotItemRules.DurabilityPercent(item.CurrentDurability, max) : 100,
                HasDurability = hasDurability,
                Weight = info.Weight,
                Price = info.Price,
                RequiredLevel = info.RequiredAmount,
                FlagsText = FlagsText(item.Flags),
                Stats = BuildItemStats(info),
            };
        }

        /// <summary>物品自身的基础属性（<c>ItemInfo.Stats</c> 已经是「累加完 ItemStats」的结果）。</summary>
        private static List<BotDetailLine> BuildItemStats(ItemInfo info)
        {
            List<BotDetailLine> lines = new List<BotDetailLine>();

            if (info.Stats == null) return lines;

            foreach (Stat stat in info.Stats.Values.Keys)
            {
                BotDetailLine line = ToLine(info.Stats, stat);

                if (line != null) lines.Add(line);
            }

            return lines;
        }

        /// <summary>角色属性 tab：把 <c>PlayerObject.Stats</c> 里「有中文名」的条目全部列出来。</summary>
        private static List<BotDetailLine> BuildAttributes(PlayerObject player)
        {
            List<BotDetailLine> lines = new List<BotDetailLine>();

            Stats stats = player.Stats;

            if (stats?.Values == null) return lines;

            foreach (Stat stat in stats.Values.Keys)
            {
                BotDetailLine line = ToLine(stats, stat);

                if (line != null) lines.Add(line);
            }

            return lines;
        }

        /// <summary>
        /// 把一条 stat 转成「标题 + 值」。
        /// <para>
        /// <c>Stats.GetDisplay()</c> 返回的是「破坏力: 3-7」这种**带标题的整串**，
        /// 而页面要的是左右两列，所以这里把标题前缀切掉。切不掉（说明格式特殊）就整串当值。
        /// </para>
        /// </summary>
        private static BotDetailLine ToLine(Stats stats, Stat stat)
        {
            string title = stats.GetTitle(stat);

            if (string.IsNullOrEmpty(title)) return null;

            string display = stats.GetDisplay(stat);

            if (string.IsNullOrEmpty(display)) return null;

            string value = display;

            if (value.StartsWith(title!, StringComparison.Ordinal))
                value = value.Substring(title!.Length).TrimStart(':', '：', ' ');

            return new BotDetailLine
            {
                Title = title!,
                Value = value.Trim(),
            };
        }

        /// <summary>装备上值得提示的标记。卖不掉 / 绑定这类是运营最常问的，所以只挑这几个。</summary>
        private static string FlagsText(UserItemFlags flags)
        {
            List<string> parts = new List<string>();

            if ((flags & UserItemFlags.Bound) == UserItemFlags.Bound) parts.Add("绑定");
            if ((flags & UserItemFlags.Locked) == UserItemFlags.Locked) parts.Add("锁定");
            if ((flags & UserItemFlags.Worthless) == UserItemFlags.Worthless) parts.Add("无价值");
            if ((flags & UserItemFlags.Marriage) == UserItemFlags.Marriage) parts.Add("结婚戒指");

            return string.Join(" · ", parts);
        }
    }
}
