using Library;
using MirDB;
using Server.Envir;
using Server.Models;
using System;
using System.Linq;
using S = Library.Network.ServerPackets;

namespace Server.DBModels
{
    [UserObject]
    public sealed class GuildMemberInfo : DBObject
    {
        [Association("Members")]
        public GuildInfo Guild
        {
            get { return _Guild; }
            set
            {
                if (_Guild == value) return;

                var oldValue = _Guild;
                _Guild = value;

                OnChanged(oldValue, value, "Guild");
            }
        }
        private GuildInfo _Guild;

        public string Rank
        {
            get { return _Rank; }
            set
            {
                if (_Rank == value) return;

                var oldValue = _Rank;
                _Rank = value;

                OnChanged(oldValue, value, "Rank");
            }
        }
        private string _Rank;

        /// <summary>
        /// 行会席位归属的**角色**（2026-11-11 起行会 + 职位 + 贡献度下沉到角色）。
        /// <para>
        /// 之前这里挂的是账号，所以同账号下多个角色只能占一个席位、行会职位也只有一份。
        /// </para>
        /// </summary>
        [Association("Member")]
        public CharacterInfo Character
        {
            get { return _Character; }
            set
            {
                if (_Character == value) return;

                var oldValue = _Character;
                _Character = value;

                OnChanged(oldValue, value, "Character");
            }
        }
        private CharacterInfo _Character;

        /// <summary>席位所属账号（= 席位角色的账号）。只读便捷属性。</summary>
        /// <remarks>
        /// 必须标 <see cref="IgnorePropertyAttribute"/>：只读的 DBObject 类型属性若被 MirDB 当成
        /// 关联字段持久化，加载旧库时会抛「Property set method not found.」。
        /// </remarks>
        [IgnoreProperty]
        public AccountInfo Account => Character?.Account;

        /// <summary>该席位所属账号的连接（账号级；一个账号同一时刻只有一个连接）。</summary>
        [IgnoreProperty]
        public SConnection Connection => Character?.Account?.Connection;

        /// <summary>
        /// 该席位对应的**在线玩家**（角色级判定）。
        /// <para>
        /// 一个账号只有一个连接，而连接上的角色不一定是这个席位 —— 所以不能直接用
        /// <c>Account.Connection.Player</c>，否则同账号的其它角色会被误当成这个席位。
        /// </para>
        /// </summary>
        [IgnoreProperty]
        public PlayerObject OnlinePlayer
        {
            get
            {
                PlayerObject player = Character?.Account?.Connection?.Player;

                return player != null && player.Character == Character ? player : null;
            }
        }

        public DateTime JoinDate
        {
            get { return _JoinDate; }
            set
            {
                if (_JoinDate == value) return;

                var oldValue = _JoinDate;
                _JoinDate = value;

                OnChanged(oldValue, value, "JoinDate");
            }
        }
        private DateTime _JoinDate;

        public long TotalContribution
        {
            get { return _TotalContribution; }
            set
            {
                if (_TotalContribution == value) return;

                var oldValue = _TotalContribution;
                _TotalContribution = value;

                OnChanged(oldValue, value, "TotalContribution");
            }
        }
        private long _TotalContribution;

        public long DailyContribution
        {
            get { return _DailyContribution; }
            set
            {
                if (_DailyContribution == value) return;

                var oldValue = _DailyContribution;
                _DailyContribution = value;

                OnChanged(oldValue, value, "DailyContribution");
            }
        }
        private long _DailyContribution;

        public GuildPermission Permission
        {
            get { return _Permission; }
            set
            {
                if (_Permission == value) return;

                var oldValue = _Permission;
                _Permission = value;

                OnChanged(oldValue, value, "Permission");
            }
        }
        private GuildPermission _Permission;

        protected override void OnDeleted()
        {
            Character = null;
            Guild = null;

            base.OnDeleted();
        }


        public void Contribute(long amount)
        {
            if (amount <= 0) return;

            Guild.GuildFunds += amount;
            Guild.DailyGrowth += amount;

            DailyContribution += amount;
            TotalContribution += amount;

            DailyContribution += amount;
            TotalContribution += amount;

            foreach (GuildMemberInfo member in Guild.Members)
            {
                SConnection connection = member.Character?.Account?.Connection;

                if (connection?.Player == null) continue;

                connection.Enqueue(new S.GuildMemberContribution { Index = Index, Contribution = amount, ObserverPacket = false });
            }
        }

        public ClientGuildMemberInfo ToClientInfo()
        {
            // 席位本身就是角色（2026-11-11 起），所以名字直接用角色名 ——
            // 之前这里写的是 Account.LastCharacter.CharacterName，玩家切角色上线时
            // 行会名单里的名字会当场变掉。
            CharacterInfo character = Character;

            ClientGuildMemberInfo info = new ClientGuildMemberInfo
            {
                Index = Index,

                Name = character?.CharacterName ?? "（未知）",
                Rank = Rank,
                DailyContribution = DailyContribution,
                TotalContribution = TotalContribution,
                Permission = Permission,
            };

            // 在线判定也按角色：一个账号只有一个连接，连接上的角色不一定是这个席位。
            PlayerObject online = character?.Account?.Connection?.Player;

            if (online != null && online.Character == character)
            {
                info.Online = TimeSpan.MinValue;
                info.ObjectID = online.ObjectID;
            }
            else if (character != null)
                info.Online = SEnvir.Now - character.LastLogin;


            return info;
        }
    }
}
