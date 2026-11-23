using Library;
using Library.Network.ClientPackets;
using C = Library.Network.ClientPackets;
using Library.SystemModels;
using Server.DBModels;
using Server.Envir;
using Server.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Server.Bots
{
    /// <summary>
    /// 假人社交（P3）：行会、组队、交易、红名 PK。
    /// <para>
    /// <b>三条硬禁令</b>（写在这里防止以后被"顺手优化"掉）：
    /// ① 不邀请真人入会/入队；② 不私聊真人；③ 不向真人发起交易。
    /// 所有社交只发生在假人之间，或由真人主动发起后假人被动接受。
    /// </para>
    /// <para>
    /// 全部方法都必须在<b>游戏线程</b>内调用（由 <see cref="BotRoster.Process"/> 每轮驱动）。
    /// </para>
    /// </summary>
    public static class BotSocial
    {
        private static DateTime _nextGuildSweep = DateTime.MinValue;

        /// <summary>
        /// 下一次允许「退会」的时间（2026-11-22）。
        /// <para>
        /// 用户要求：换会**不要一次全执行**，前一人退会后下一人随机间隔 10~60 分钟再退。
        /// 退会会向全会成员广播 <c>GuildKick</c> + <c>GuildChanged</c>，
        /// 100 个假人同时退会会把主循环顶住，也会在客户端刷出一片「XX 离开了行会」。
        /// </para>
        /// </summary>
        private static DateTime _nextGuildLeave = DateTime.MinValue;

        /// <summary>行会职位名（与引擎的 Rank 字符串一致，客户端直接显示）。</summary>
        private const string RankLeader = "会长";

        private const string RankViceLeader = "副会长";

        private const string RankTreasurer = "财政部长";

        private const string RankMember = "成员";
        private static DateTime _nextGroupSweep = DateTime.MinValue;

        /// <summary>每轮社交扫描的入口（由 BotRoster 调用）。</summary>
        public static void Process()
        {
            if (!Config.BotSocialEnabled) return;

            ProcessGuilds();
            ProcessGroups();
        }

        #region 组队（P3-2）

        /// <summary>
        /// 假人互组：同图、等级接近、都没队。
        /// <para>
        /// 纯观感功能 —— 组队不会让假人改变练级路线（各自还是去自己的刷怪点），
        /// 走散了队伍自然解散，跟真人一样。真要有"团队感"得另外做跟随逻辑，不在本期范围。
        /// </para>
        /// </summary>
        private static void ProcessGroups()
        {
            if (!Config.BotGroupEnabled) return;
            if (SEnvir.Now < _nextGroupSweep) return;

            _nextGroupSweep = SEnvir.Now.AddSeconds(45);

            foreach (BotSession session in BotRoster.AllSessions)
            {
                if (!session.IsOnline) continue;

                PlayerObject leader = session.Connection?.Player;

                if (leader?.Character == null) continue;
                if (leader.GroupMembers != null) continue;
                if (leader.Level < Config.BotGroupMinLevel) continue;

                // 上一轮发过邀请没接受，先接受（否则会一直挂着 GroupInvitation 被拒）。
                if (leader.GroupInvitation != null)
                {
                    leader.GroupJoin();
                    return;
                }

                PlayerObject mate = FindGroupMate(leader);

                if (mate == null) continue;

                leader.GroupInvite(mate.Name);

                // GroupInvite 只挂邀请 + 发包，不会自动入队 —— 假人必须自己再调一次。
                if (mate.GroupInvitation != null)
                    mate.GroupJoin();

                BotLog.Debug(session.Profile.Seq, string.Format("与 {0} 组队", mate.Name));

                // 一次只组一队，别在同一帧里连着组十几队。
                return;
            }
        }

        private static PlayerObject FindGroupMate(PlayerObject leader)
        {
            foreach (BotSession session in BotRoster.AllSessions)
            {
                if (!session.IsOnline) continue;

                PlayerObject mate = session.Connection?.Player;

                if (mate == null || mate == leader) continue;
                if (mate.Character == null) continue;
                if (mate.GroupMembers != null) continue;
                if (mate.GroupInvitation != null) continue;
                if (mate.CurrentMap != leader.CurrentMap) continue;
                if (Math.Abs(mate.Level - leader.Level) > Config.BotGroupLevelGap) continue;

                // 安全阀：只和假人组队，绝不邀请真人。
                if (!mate.Connection.IsBot) continue;

                return mate;
            }

            return null;
        }

        #endregion

        #region 行会（P3-1）

        /// <summary>配置里的行会名列表（逗号分隔）。</summary>
        /// <summary>
        /// 假人行会名清单，**唯一来源是 <c>Bots/guilds.txt</c>**（2026-11-22 二次调整）。
        /// <para>
        /// 文件不存在 → 返回空列表并打一条告警，调用方全部提前返回：
        /// 建会 / 入会 / 换会 / 退会**全部暂停**。
        /// 这是安全阀 —— 否则误删文件会被当成「清单为空」，所有假人所在会都算「不在清单」，
        /// 一次误删就把行会系统清空。
        /// </para>
        /// <para>
        /// 文件存在时按内容走，最多取前 5 个（见 <see cref="EffectiveGuildNames"/>）。
        /// </para>
        /// </summary>
        private static List<string> GuildNames()
        {
            List<string> names = BotData.Guilds();

            if (names.Count == 0)
            {
                BotData.WarnGuildsMissing();
                return names;
            }

            return names;
        }

        /// <summary>
        /// 实际要建的行会清单（2026-11-22）：**最多 5 个**，超出只取前 5 个。
        /// <para>
        /// 名单里只有 3 个就建 3 个；有 8 个也只建前 5 个 —— 避免运维往里塞一堆名字
        /// 把 <c>BotGuildCount</c> 的语义搞乱。
        /// </para>
        /// </summary>
        private static List<string> EffectiveGuildNames()
        {
            const int MaxGuilds = 5;

            List<string> names = GuildNames();

            if (names.Count <= MaxGuilds) return names;

            return names.GetRange(0, MaxGuilds);
        }

        /// <summary>
        /// 建会 + 入会。
        /// <para>
        /// 建会成本很高（<c>Globals.GuildCreationCost</c> 750 万 + 每格成员 100 万），
        /// 假人靠打怪攒到是不现实的，所以允许一次性注资（<see cref="Config.BotGuildGrantGold"/>）。
        /// 注资是<b>显式的</b>：配置为 0 就不注资，假人只会用自己攒的钱建会（大概率永远建不起来）。
        /// </para>
        /// </summary>
        private static void ProcessGuilds()
        {
            if (!Config.BotGuildEnabled) return;
            if (SEnvir.Now < _nextGuildSweep) return;

            _nextGuildSweep = SEnvir.Now.AddSeconds(20);

            EnsureGuilds();
            JoinGuilds();

            // 2026-11-22 追加三步（顺序有讲究）：
            //   ① 先给在会的人排职位（新入会的人才有 Rank）
            //   ② 再把「会名不在清单里」的假人逐个退会（10~60 分钟节流）
            //   ③ 最后清理空掉的旧会，避免新假人又被塞进废弃行会
            AssignRanks();
            MigrateGuilds();
            CleanupStaleGuilds();
        }

        /// <summary>行会数量不够就建一个（每轮最多建一个，避免同一帧连着建）。</summary>
        private static void EnsureGuilds()
        {
            List<string> names = EffectiveGuildNames();

            if (names.Count == 0) return;

            // 名单有 3 个就建 3 个；名单有 8 个也只建前 5 个（EffectiveGuildNames 已截断）。
            int want = Math.Clamp(Config.BotGuildCount, 0, names.Count);

            for (int i = 0; i < want; i++)
            {
                string name = names[i];

                if (FindGuild(name) != null) continue;

                BotSession founder = PickFounder();

                if (founder == null) return;

                CreateGuild(founder, name);

                return;
            }
        }

        private static GuildInfo FindGuild(string name)
        {
            return SEnvir.GuildInfoList.Binding.FirstOrDefault(
                x => string.Compare(x.GuildName, name, StringComparison.OrdinalIgnoreCase) == 0);
        }

        /// <summary>找建会的人：在线、没行会、等级够。</summary>
        private static BotSession PickFounder()
        {
            foreach (BotSession session in BotRoster.AllSessions)
            {
                if (!session.IsOnline) continue;

                PlayerObject player = session.Connection?.Player;

                if (player?.Character == null) continue;
                if (player.Character.GuildMember != null) continue;
                if (player.Level < Config.BotGuildMinLevel) continue;

                return session;
            }

            return null;
        }

        private static void CreateGuild(BotSession session, string name)
        {
            PlayerObject player = session.Connection?.Player;

            if (player?.Character == null) return;

            int members = Math.Max(0, Config.BotGuildMembers);

            long cost = members * Globals.GuildMemberCost + Globals.GuildCreationCost;

            // 钱不够就注资（只在允许注资时）。这里**不改**已有金币，只补差额，
            // 免得把一个已经攒了钱的假人金币抹平。
            if (player.Gold.Amount < cost && Config.BotGuildGrantGold > 0)
            {
                long grant = cost - player.Gold.Amount;

                player.Gold.Amount += grant;
                player.GoldChanged();

                BotLog.Info(session.Profile.Seq, string.Format(
                    "行会注资：{0} 金币（建会需要 {1}），用于创建「{2}」", grant, cost, name));
            }

            if (player.Gold.Amount < cost)
            {
                BotLog.Warn(session.Profile.Seq, string.Format(
                    "建会放弃：金币 {0} 不够 {1}（可调高 BotGuildGrantGold）", player.Gold.Amount, cost));

                return;
            }

            int before = SEnvir.GuildInfoList.Binding.Count;

            player.GuildCreate(new C.GuildCreate
            {
                Name = name,
                UseGold = true,
                Members = members,
                Storage = 0,
            });

            if (SEnvir.GuildInfoList.Binding.Count > before)
                BotLog.Info(session.Profile.Seq, string.Format("行会已创建：{0}（成员上限 {1}）", name, 10 + members));
            else
                BotLog.Warn(session.Profile.Seq, string.Format("行会创建失败：{0}（名字重复或金币不足）", name));
        }

        /// <summary>没有行会的假人，逐个塞进假人行会（邀请 → 自己接受）。</summary>
        private static void JoinGuilds()
        {
            foreach (BotSession session in BotRoster.AllSessions)
            {
                if (!session.IsOnline) continue;

                PlayerObject bot = session.Connection?.Player;

                if (bot?.Character == null) continue;
                if (bot.Character.GuildMember != null) continue;

                // 2026-11-22：**退会冷却内不要邀请**。
                // 引擎规定退会后 GuildTime = 现在 + 1 天，冷却内 GuildJoin 一定失败；
                // 不跳过的话，换会的假人会在这一整天里每 20 秒刷一条「入会失败」，
                // 而且邀请对象会一直挂在 GuildInvitation 上。
                if (bot.Character.GuildTime > SEnvir.Now) continue;

                // 上一轮发过邀请但没接受（比如中间被打断），先把它接受掉。
                if (bot.GuildInvitation != null)
                {
                    bot.GuildJoin();
                    return;
                }

                GuildInfo guild = PickJoinableGuild();

                if (guild == null) return;

                PlayerObject leader = guild.Members
                    .Where(x => x.OnlinePlayer != null)
                    .Select(x => x.OnlinePlayer)
                    .FirstOrDefault(x => (x.Character.GuildMember?.Permission & GuildPermission.AddMember) == GuildPermission.AddMember);

                if (leader == null) continue;

                leader.GuildInviteMember(new C.GuildInviteMember { Name = bot.Name });

                // GuildInviteMember 只挂 GuildInvitation 并发包，不会自动入会 —— 必须自己再调一次。
                if (bot.GuildInvitation != null)
                    bot.GuildJoin();

                if (bot.Character.GuildMember != null)
                    BotLog.Info(session.Profile.Seq, string.Format("已加入行会：{0}", bot.Character.GuildMember.Guild.GuildName));
                else
                    BotLog.Warn(session.Profile.Seq, string.Format("入会失败：{0}（邀请未生效或被拒）", bot.Name));

                // 一次只处理一个：同一帧连续 30 次入会会让公会包广播把主循环顶住。
                return;
            }
        }

        /// <summary>
        /// 给假人行会排职位（2026-11-22）：会长 1 / 副会长 1 / 财政部长 1 / 其余成员。
        /// <para>
        /// <b>每轮只处理一个行会</b>：每次 <c>GuildEditMember</c> 都会向全会广播一次
        /// <c>GuildUpdate</c>，20 人的会改一遍就是 20 次广播；5 个会一起改会把主循环顶住。
        /// </para>
        /// <para>
        /// 引擎的两个细节：① 只有会长能调 <c>GuildEditMember</c>；
        /// ② 改自己时引擎会跳过 Permission、但 <c>Rank</c> 照样生效 —— 所以会长自己的
        /// 「会长」头衔也设得上。
        /// </para>
        /// </summary>
        private static void AssignRanks()
        {
            List<string> names = GuildNames();

            if (names.Count == 0) return;

            foreach (string name in names)
            {
                GuildInfo guild = FindGuild(name);

                if (guild == null) continue;

                GuildMemberInfo leader = guild.Members.FirstOrDefault(
                    x => (x.Permission & GuildPermission.Leader) == GuildPermission.Leader);

                PlayerObject leaderPlayer = leader?.OnlinePlayer;

                // 会长不在线就改不了（引擎要求由会长本人发指令），下轮再说。
                if (leaderPlayer == null) continue;

                // 稳定排序：按成员索引排，保证「副会长 / 财政部长」不会每轮换人。
                List<GuildMemberInfo> ordered = guild.Members
                    .Where(x => x.Character != null)
                    .OrderBy(x => x.Index)
                    .ToList();

                if (ordered.Count == 0) continue;

                bool changed = false;

                for (int i = 0; i < ordered.Count; i++)
                {
                    GuildMemberInfo member = ordered[i];

                    string rank = i == 0 ? RankLeader
                        : i == 1 ? RankViceLeader
                        : i == 2 ? RankTreasurer
                        : RankMember;

                    GuildPermission permission = i == 0 ? GuildPermission.Leader
                        : i == 1 ? GuildPermission.AddMember | GuildPermission.RemoveMember
                                   | GuildPermission.EditNotice | GuildPermission.StartWar
                        : i == 2 ? GuildPermission.Storage | GuildPermission.FundsRepair
                                   | GuildPermission.FundsMerchant | GuildPermission.FundsMarket
                        : GuildPermission.None;

                    if (string.Equals(member.Rank, rank, StringComparison.Ordinal)) continue;

                    leaderPlayer.GuildEditMember(new C.GuildEditMember
                    {
                        Index = member.Index,
                        Rank = rank,
                        Permission = permission,
                    });

                    changed = true;
                }

                // 一轮只动一个会，改完就返回，剩下的下一轮（20 秒后）继续。
                if (changed) return;
            }
        }

        /// <summary>
        /// 换会（2026-11-22）：假人所在行会名**不在清单里** → 退会，之后由
        /// <see cref="JoinGuilds"/> 按「加入未满的会」或「新建清单里的会」重新安置。
        /// <para>
        /// <b>节流</b>：全局一次只退一个，前一人退会后随机等 10~60 分钟再退下一个
        /// （<c>BotGuildLeaveMinMinutes</c> / <c>BotGuildLeaveMaxMinutes</c>）。
        /// </para>
        /// <para>
        /// <b>会长最后退</b>：引擎 <c>GuildLeave</c> 只在「会内 >1 人 且 没有其他会长」时拒绝，
        /// 所以先退普通成员，等会里只剩会长一人时他就能退了（空会交给
        /// <see cref="CleanupStaleGuilds"/> 删掉）。
        /// </para>
        /// <para>
        /// ⚠ 引擎规定退会后 <c>Character.GuildTime = 现在 + 1 天</c>，这一天内加入不了新会
        /// （<c>GuildJoin</c> 会拒绝）。用户已确认接受这个冷却，所以这里**不清**它。
        /// </para>
        /// </summary>
        private static void MigrateGuilds()
        {
            if (SEnvir.Now < _nextGuildLeave) return;

            List<string> names = GuildNames();

            if (names.Count == 0) return;

            foreach (BotSession session in BotRoster.AllSessions)
            {
                if (!session.IsOnline) continue;

                PlayerObject player = session.Connection?.Player;

                GuildMemberInfo member = player?.Character?.GuildMember;

                if (member?.Guild == null) continue;

                // 会名还在清单里 → 不用动。
                if (names.Exists(x => string.Compare(x, member.Guild.GuildName, StringComparison.OrdinalIgnoreCase) == 0))
                    continue;

                bool isLeader = (member.Permission & GuildPermission.Leader) == GuildPermission.Leader;

                // 会长必须最后走：会里还有别人时先退成员。
                if (isLeader && member.Guild.Members.Count > 1) continue;

                string guildName = member.Guild.GuildName;
                int left = member.Guild.Members.Count - 1;

                player.GuildLeave();

                if (player.Character.GuildMember != null)
                {
                    BotLog.Warn(session.Profile.Seq, string.Format("退会失败：{0}", guildName));
                    continue;
                }

                int minMinutes = Math.Max(1, Config.BotGuildLeaveMinMinutes);
                int maxMinutes = Math.Max(minMinutes, Config.BotGuildLeaveMaxMinutes);

                _nextGuildLeave = SEnvir.Now.AddMinutes(SEnvir.Random.Next(minMinutes, maxMinutes + 1));

                BotLog.Info(session.Profile.Seq, string.Format(
                    "已退出行会「{0}」（会内还剩 {1} 人，{2} 分钟后才允许下一个人退会）",
                    guildName, left, (int)(_nextGuildLeave - SEnvir.Now).TotalMinutes));

                return;
            }
        }

        /// <summary>
        /// 清理废弃行会（2026-11-22）：**空会 且 名字不在清单里** → 删除。
        /// <para>
        /// 不删的话，空会会一直挂在 <c>GuildInfoList</c> 里占着名字，
        /// 而 <see cref="JoinGuilds"/> 只看「有没有空位」，会把新假人又塞进这个废弃行会。
        /// </para>
        /// <para>
        /// 三道保险：① 有成员的会不删；② 清单里的会不删（等着被重新填满）；
        /// ③ 有城堡/有仓库物品的会不删（删除会破坏关联数据）。
        /// </para>
        /// </summary>
        private static void CleanupStaleGuilds()
        {
            List<string> names = GuildNames();

            if (names.Count == 0) return;

            List<GuildInfo> stale = null;

            foreach (GuildInfo guild in SEnvir.GuildInfoList.Binding)
            {
                if (guild == null || guild.StarterGuild) continue;
                if (guild.Members.Count > 0) continue;
                if (guild.Conquest != null) continue;
                if (guild.Items.Count > 0) continue;

                if (names.Exists(x => string.Compare(x, guild.GuildName, StringComparison.OrdinalIgnoreCase) == 0))
                    continue;

                (stale ??= new List<GuildInfo>()).Add(guild);
            }

            if (stale == null) return;

            foreach (GuildInfo guild in stale)
            {
                BotLog.Info(0, string.Format("清理废弃行会：{0}（空会且不在清单里）", guild.GuildName));
                guild.Delete();
            }
        }

        private static GuildInfo PickJoinableGuild()
        {
            List<string> names = GuildNames();

            if (names.Count == 0) return null;

            HashSet<string> botNames = BotNameSet();

            foreach (string name in names)
            {
                GuildInfo guild = FindGuild(name);

                if (guild == null) continue;
                if (guild.Members.Count >= guild.MemberLimit) continue;

                // 2026-11-22：**每会只放 BotGuildBotLimit 个假人**，剩下的位置留给真人。
                // 引擎的 MemberLimit 是「10 + 成员格」= 30，但假人不能把 30 个位置占满，
                // 否则真人玩家永远进不了会。
                if (CountBotsInGuild(guild, botNames) >= Config.BotGuildBotLimit) continue;

                return guild;
            }

            return null;
        }

        /// <summary>当前所有假人的角色名集合（用于区分「会里哪些成员是假人」）。</summary>
        private static HashSet<string> BotNameSet()
        {
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (BotSession session in BotRoster.AllSessions)
            {
                string name = session.Character?.CharacterName ?? session.Profile?.CharacterName;

                if (!string.IsNullOrEmpty(name)) names.Add(name);
            }

            return names;
        }

        /// <summary>
        /// 会里的假人数量（离线成员也算 —— 它们仍然是行会成员，占着位置）。
        /// </summary>
        private static int CountBotsInGuild(GuildInfo guild, HashSet<string> botNames)
        {
            int count = 0;

            foreach (GuildMemberInfo member in guild.Members)
            {
                string name = member.Character?.CharacterName;

                if (name != null && botNames.Contains(name)) count++;
            }

            return count;
        }

        #endregion
    }
}
