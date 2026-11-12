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
        private static List<string> GuildNames()
        {
            return (Config.BotGuildNames ?? string.Empty)
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .ToList();
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
        }

        /// <summary>行会数量不够就建一个（每轮最多建一个，避免同一帧连着建）。</summary>
        private static void EnsureGuilds()
        {
            List<string> names = GuildNames();

            if (names.Count == 0) return;

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

                BotLog.Warn(session.Profile.Seq, string.Format(
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
                BotLog.Warn(session.Profile.Seq, string.Format("行会已创建：{0}（成员上限 {1}）", name, 10 + members));
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
                    BotLog.Warn(session.Profile.Seq, string.Format("已加入行会：{0}", bot.Character.GuildMember.Guild.GuildName));
                else
                    BotLog.Warn(session.Profile.Seq, string.Format("入会失败：{0}（邀请未生效或被拒）", bot.Name));

                // 一次只处理一个：同一帧连续 30 次入会会让公会包广播把主循环顶住。
                return;
            }
        }

        private static GuildInfo PickJoinableGuild()
        {
            List<string> names = GuildNames();

            foreach (string name in names)
            {
                GuildInfo guild = FindGuild(name);

                if (guild == null) continue;
                if (guild.Members.Count >= guild.MemberLimit) continue;

                return guild;
            }

            return null;
        }

        #endregion
    }
}
