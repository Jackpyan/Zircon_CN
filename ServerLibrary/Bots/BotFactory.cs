using Library;
using Library.SystemModels;
using Server.DBModels;
using Server.Envir;
using Server.Models;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace Server.Bots
{
    /// <summary>
    /// 假人账号与角色的生成器。三条铁律：
    /// 1. **幂等**：先查再建，重启不会产生重复账号/角色，Index 不会无限膨胀；
    /// 2. **等级留 0**：进图时引擎的 <c>if (Level == 0) NewCharacter()</c> 会做全套初始化
    ///    （置 1 级、发首装、绑主城安全区），所以这里一个字段都不要填；
    /// 3. **只碰 DB，不碰连接**：建号与上线解耦，后台也能单独跑一遍"补账号"。
    /// </summary>
    public static class BotFactory
    {
        /// <summary>按邮箱查重，没有才创建。已存在时顺手修好 Activated / IsBot 两个标记。</summary>
        public static AccountInfo EnsureAccount(BotProfile profile)
        {
            foreach (AccountInfo info in SEnvir.AccountInfoList.Binding)
            {
                if (!string.Equals(info.EMailAddress, profile.EMail, StringComparison.OrdinalIgnoreCase)) continue;

                if (!info.Activated) info.Activated = true;
                if (!info.IsBot) info.IsBot = true;

                // 假人账号不应该处于封禁状态，一律自愈解封（2026-11-11）。
                //
                // 背景：假人账号的密码是写死的固定值，历史上可能被真人乱试密码触发了封禁
                // （连错 5 次 → Banned），也可能在后台被误操作封过。假人账号不接受外部登录，
                // 封禁对它没有任何意义，而一旦被封，EnsureAccount 原本不会解封 → 该序号从此再也上不了线。
                if (info.Banned)
                {
                    info.Banned = false;
                    info.BanReason = string.Empty;
                    info.BanExpiry = DateTime.MinValue;

                    BotLog.System(string.Format("[假人自愈] 账号 {0} 处于封禁状态，已自动解封", info.EMailAddress));
                }

                // 密码错误计数同理清掉：假人不会输错密码，累计着只会让人误以为账号有问题。
                if (info.WrongPasswordCount > 0) info.WrongPasswordCount = 0;

                return info;
            }

            AccountInfo account = SEnvir.AccountInfoList.CreateNewObject();

            account.EMailAddress = profile.EMail;
            account.Password = SEnvir.CreateHash(profile.Password);
            account.BirthDate = new DateTime(1990, 1, 1);
            account.CreationIP = "bot";
            account.CreationDate = SEnvir.Now;
            account.Activated = true;
            account.ActivationTime = SEnvir.Now;
            account.IsBot = true;

            BotLog.Info(profile.Seq, string.Format("账号已创建：{0}", account.EMailAddress));

            return account;
        }

        /// <summary>
        /// 取该账号下第一个未删除的角色，没有就建一个。
        /// <paramref name="created"/> 为 true 表示这次是新造的，调用方据此决定要不要发启动资源。
        /// </summary>
        public static CharacterInfo EnsureCharacter(AccountInfo account, BotProfile profile, out bool created)
        {
            created = false;

            foreach (CharacterInfo existing in account.Characters)
            {
                if (existing.Deleted) continue;

                return existing;
            }

            string name = ResolveAvailableName(profile.CharacterName);

            CharacterInfo info = SEnvir.CharacterInfoList.CreateNewObject();

            info.CharacterName = name;
            info.Account = account;
            info.Class = profile.Class;
            info.Gender = profile.Gender;
            info.HairType = profile.HairType;
            info.HairColour = profile.HairColour;
            // 刺客建号时校验 ArmourColour.ToArgb() == 0，其它三职业要求 A == 255。
            info.ArmourColour = profile.Class == MirClass.Assassin
                ? Color.FromArgb(0)
                : Color.FromArgb(255, 255, 255, 255);
            info.CreationIP = "bot";
            info.CreationDate = SEnvir.Now;

            // 交互开关是角色级的（2026-11-11 起从账号下沉到角色），假人默认全开。
            info.AllowGroup = true;
            info.AllowTrade = true;
            info.AllowGuild = true;

            // 绑定点决定出生地。引擎默认按职业分（战士→比奇城、法师→潘夜村、道士→失乐园），
            // 但实测只有比奇城周边有 1 级能打的怪，潘夜村/失乐园最近的刷怪区全是 5 级以上，
            // 假人在那儿一步也练不了。所以统一绑到「有低等级怪的地图」的安全区。
            SafeZoneInfo starter = FindStarterSafeZone(profile.Seq);

            if (starter != null)
                info.BindPoint = starter;

            // 排行榜节点必须建，否则等级榜/后台排序会缺人。
            info.RankingNode = SEnvir.Rankings.AddLast(info);

            created = true;

            BotLog.Info(profile.Seq, string.Format("角色已创建：{0}（{1} {2}，出生点 {3}，等级留给引擎初始化）",
                name, profile.Class, profile.Gender,
                starter?.BindRegion?.ServerDescription ?? "引擎默认"));

            return info;
        }

        /// <summary>
        /// 所有适合 0 级号起步的安全区，两个条件：① 该地图上存在怪等级 ≤ 5 的刷怪区；
        /// ② 地图本身没有等级门槛（<c>MinimumLevel ≤ 1</c>）。
        /// 本服实测只有 3 个符合：比奇城 / 潘夜村 / 失乐园（各 1 片鸡区 + 1 个药店）。
        /// 比奇城堡城边也有鸡区，但 <c>MinimumLevel = 45</c>，0 级号连图都进不去。
        /// 按地图 Index 排序保证结果稳定，重启后同一个序号还是分到同一个城。
        /// </summary>
        public static List<SafeZoneInfo> FindStarterSafeZones()
        {
            List<SafeZoneInfo> result = new List<SafeZoneInfo>();

            HashSet<MapInfo> lowLevelMaps = new HashSet<MapInfo>();

            foreach (RespawnInfo respawn in SEnvir.RespawnInfoList.Binding)
            {
                if (respawn.Monster == null) continue;
                if (respawn.Monster.Level > 5) continue;

                MapInfo map = respawn.Region?.Map;

                if (map == null) continue;

                lowLevelMaps.Add(map);
            }

            if (lowLevelMaps.Count == 0) return result;

            foreach (SafeZoneInfo zone in SEnvir.SafeZoneInfoList.Binding)
            {
                MapInfo map = zone.BindRegion?.Map;

                if (map == null) continue;

                if (!lowLevelMaps.Contains(map)) continue;

                // 地图本身的等级门槛也要过：比奇城堡（#259）虽然城边有 Lv5 的鸡，
                // 但 MinimumLevel = 45，0 级号 StartGame 直接 Spawn 失败、卡在 Entering 直到超时。
                if (map.MinimumLevel > 1) continue;

                result.Add(zone);
            }

            result.Sort((a, b) => a.BindRegion.Map.Index.CompareTo(b.BindRegion.Map.Index));

            return result;
        }

        /// <summary>
        /// 按序号分配主城：<c>(seq - 1) % 主城数</c>。30 个假人铺到 4 个城 → 每城 7~8 个，
        /// 而不是全挤在比奇城抢同一片鸡区（D2 实测 5 个人一片鸡区，升级明显变慢）。
        /// </summary>
        public static SafeZoneInfo FindStarterSafeZone(int seq)
        {
            List<SafeZoneInfo> zones = FindStarterSafeZones();

            if (zones.Count == 0) return null;

            SafeZoneInfo zone = zones[(Math.Max(seq, 1) - 1) % zones.Count];

            // 懒加载模式下这张图的区域点还没生成，Ensure 会失败；
            // 不要因为这一次失败就跳过 —— 进图时 PlayerObject.SetBindPoint 会再试一次。
            SEnvir.EnsureSafeZoneBindPoints(zone);

            return zone;
        }

        /// <summary>角色名全服唯一，撞名就在后面加序号。</summary>
        private static string ResolveAvailableName(string preferred)
        {
            if (IsNameAvailable(preferred)) return preferred;

            for (int i = 2; i <= 99; i++)
            {
                string suffix = i.ToString("0");

                // 截断按「名字内容」算：含汉字 8 / 纯英文数字 15，后缀要占位置。
                string candidate = CharacterNameRules.Truncate(preferred, suffix.Length) + suffix;

                if (IsNameAvailable(candidate)) return candidate;
            }

            return preferred + SEnvir.Random.Next(100, 999);
        }

        private static bool IsNameAvailable(string name)
        {
            foreach (CharacterInfo info in SEnvir.CharacterInfoList.Binding)
            {
                if (info.Deleted) continue;
                if (string.Equals(info.CharacterName, name, StringComparison.OrdinalIgnoreCase)) return false;
            }

            return true;
        }

        /// <summary>
        /// 首次进图后发一次启动资源：金币 + 红蓝药水。
        /// 幂等判定用「1 级且金币为 0」：花光金币时等级早就超过 1，不会重复发；
        /// 反过来，如果建号后服务器在进图前崩了，重启也能补上。
        /// </summary>
        public static bool GrantNewbieKit(PlayerObject player, int seq)
        {
            if (player == null) return false;
            if (player.Level > 1) return false;

            // 金币已下沉到角色（2026-11-11）：假人每个独立账号，但金币在角色上。
            UserCurrency gold = player.Character.Gold;

            if (gold == null) return false;

            if (gold.Amount > 0) return false;

            int granted = 0;

            if (Config.BotNewbieGold > 0)
            {
                gold.Amount = Config.BotNewbieGold;
                granted++;
            }

            if (Config.BotNewbiePotionCount > 0)
                granted += GrantPotions(player, Config.BotNewbiePotionCount);

            if (granted == 0) return false;

            BotLog.Info(seq, string.Format("启动资源已发放：{0} 金币 + {1} 瓶药",
                Config.BotNewbieGold, Config.BotNewbiePotionCount));

            return true;
        }

        /// <summary>发红蓝药。库里找不到对应药水时静默跳过（不阻断上线）。</summary>
        private static int GrantPotions(PlayerObject player, int count)
        {
            int granted = 0;

            granted += GivePotion(player, Stat.Health, count);
            granted += GivePotion(player, Stat.Mana, count);

            return granted;
        }

        private static int GivePotion(PlayerObject player, Stat restoreStat, int count)
        {
            ItemInfo potion = FindPotion(restoreStat, player.Level);

            if (potion == null) return 0;

            ItemCheck check = new ItemCheck(potion, count, UserItemFlags.Bound | UserItemFlags.Worthless, TimeSpan.Zero);

            if (!player.CanGainItems(false, check)) return 0;

            UserItem item = SEnvir.CreateFreshItem(check);

            if (item == null) return 0;

            player.GainItem(item);

            return 1;
        }

        /// <summary>按恢复属性找一瓶「等级要求最低」的药水。</summary>
        private static ItemInfo FindPotion(Stat restoreStat, int level)
        {
            ItemInfo best = null;

            foreach (ItemInfo info in SEnvir.ItemInfoList.Binding)
            {
                if (info.ItemType != ItemType.Consumable) continue;

                if (info.Stats[restoreStat] <= 0) continue;

                if (info.RequiredAmount > 0 && info.RequiredAmount > level) continue;

                if (best == null || info.RequiredAmount < best.RequiredAmount)
                    best = info;
            }

            return best;
        }
    }
}
