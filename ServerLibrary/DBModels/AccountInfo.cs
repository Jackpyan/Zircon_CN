using Library;
using Library.SystemModels;
using MirDB;
using Server.Envir;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Server.DBModels
{
    [UserObject]
    public sealed class AccountInfo : DBObject
    {
        public string EMailAddress
        {
            get { return _EMailAddress; }
            set
            {
                if (_EMailAddress == value) return;

                var oldValue = _EMailAddress;
                _EMailAddress = value;

                OnChanged(oldValue, value, "EMailAddress");
            }
        }
        private string _EMailAddress;

        public byte[] Password
        {
            get { return _Password; }
            set
            {
                if (_Password == value) return;

                var oldValue = _Password;
                _Password = value;

                OnChanged(oldValue, value, "Password");
            }
        }
        private byte[] _Password;

        public string RealName
        {
            get { return _RealName; }
            set
            {
                if (_RealName == value) return;

                var oldValue = _RealName;
                _RealName = value;

                OnChanged(oldValue, value, "RealName");
            }
        }
        private string _RealName;

        public DateTime BirthDate
        {
            get { return _BirthDate; }
            set
            {
                if (_BirthDate == value) return;

                var oldValue = _BirthDate;
                _BirthDate = value;

                OnChanged(oldValue, value, "BirthDate");
            }
        }
        private DateTime _BirthDate;

        public string Question
        {
            get { return _Question; }
            set
            {
                if (_Question == value) return;

                var oldValue = _Question;
                _Question = value;

                OnChanged(oldValue, value, "Question");
            }
        }
        private string _Question;

        public string Answer
        {
            get { return _Answer; }
            set
            {
                if (_Answer == value) return;

                var oldValue = _Answer;
                _Answer = value;

                OnChanged(oldValue, value, "Answer");
            }
        }
        private string _Answer;

        [Association("Referrals")]
        public AccountInfo Referral
        {
            get { return _Referral; }
            set
            {
                if (_Referral == value) return;

                var oldValue = _Referral;
                _Referral = value;

                OnChanged(oldValue, value, "Referral");
            }
        }
        private AccountInfo _Referral;

        public string CreationIP
        {
            get { return _CreationIP; }
            set
            {
                if (_CreationIP == value) return;

                var oldValue = _CreationIP;
                _CreationIP = value;

                OnChanged(oldValue, value, "CreationIP");
            }
        }
        private string _CreationIP;

        public DateTime CreationDate
        {
            get { return _CreationDate; }
            set
            {
                if (_CreationDate == value) return;

                var oldValue = _CreationDate;
                _CreationDate = value;

                OnChanged(oldValue, value, "CreationDate");
            }
        }
        private DateTime _CreationDate;

        public string LastIP
        {
            get { return _LastIP; }
            set
            {
                if (_LastIP == value) return;

                var oldValue = _LastIP;
                _LastIP = value;

                OnChanged(oldValue, value, "LastIP");
            }
        }
        private string _LastIP;

        public DateTime LastLogin
        {
            get { return _LastLogin; }
            set
            {
                if (_LastLogin == value) return;

                var oldValue = _LastLogin;
                _LastLogin = value;

                OnChanged(oldValue, value, "LastLogin");
            }
        }
        private DateTime _LastLogin;

        public string ActivationKey
        {
            get { return _ActivationKey; }
            set
            {
                if (_ActivationKey == value) return;

                var oldValue = _ActivationKey;
                _ActivationKey = value;

                OnChanged(oldValue, value, "ActivationKey");
            }
        }
        private string _ActivationKey;

        public DateTime ActivationTime
        {
            get { return _ActivationTime; }
            set
            {
                if (_ActivationTime == value) return;

                var oldValue = _ActivationTime;
                _ActivationTime = value;

                OnChanged(oldValue, value, "ActivationTime");
            }
        }
        private DateTime _ActivationTime;

        public bool Activated
        {
            get { return _Activated; }
            set
            {
                if (_Activated == value) return;

                var oldValue = _Activated;
                _Activated = value;

                OnChanged(oldValue, value, "Activated");
            }
        }
        private bool _Activated;

        public string ResetKey
        {
            get { return _ResetKey; }
            set
            {
                if (_ResetKey == value) return;

                var oldValue = _ResetKey;
                _ResetKey = value;

                OnChanged(oldValue, value, "ResetKey");
            }
        }
        private string _ResetKey;

        public DateTime ResetTime
        {
            get { return _ResetTime; }
            set
            {
                if (_ResetTime == value) return;

                var oldValue = _ResetTime;
                _ResetTime = value;

                OnChanged(oldValue, value, "ResetTime");
            }
        }
        private DateTime _ResetTime;

        public DateTime PasswordTime
        {
            get { return _PasswordTime; }
            set
            {
                if (_PasswordTime == value) return;

                var oldValue = _PasswordTime;
                _PasswordTime = value;

                OnChanged(oldValue, value, "PasswordTime");
            }
        }
        private DateTime _PasswordTime;

        public DateTime ChatBanExpiry
        {
            get { return _ChatBanExpiry; }
            set
            {
                if (_ChatBanExpiry == value) return;

                var oldValue = _ChatBanExpiry;
                _ChatBanExpiry = value;

                OnChanged(oldValue, value, "ChatBanExpiry");
            }
        }
        private DateTime _ChatBanExpiry;

        public bool Banned
        {
            get { return _Banned; }
            set
            {
                if (_Banned == value) return;

                var oldValue = _Banned;
                _Banned = value;

                OnChanged(oldValue, value, "Banned");
            }
        }
        private bool _Banned;

        public DateTime BanExpiry
        {
            get { return _ExpiryDate; }
            set
            {
                if (_ExpiryDate == value) return;

                var oldValue = _ExpiryDate;
                _ExpiryDate = value;

                OnChanged(oldValue, value, "ExpiryDate");
            }
        }
        private DateTime _ExpiryDate;

        public string BanReason
        {
            get { return _BanReason; }
            set
            {
                if (_BanReason == value) return;

                var oldValue = _BanReason;
                _BanReason = value;

                OnChanged(oldValue, value, "BanReason");
            }
        }
        private string _BanReason;

        /// <summary>
        /// 充值元宝（真钱）。**账号级**，同账号的所有角色共同拥有（用户 2026-11-11 决策）。
        /// </summary>
        /// <remarks>
        /// 金币 / 狩猎币 / 声望点 / 贡献点已于 2026-11-11 下沉到角色，改用 <see cref="CharacterInfo.Gold"/> 等。
        /// 这里刻意**不再**提供 <c>Gold</c> / <c>HuntGold</c> 便捷属性 —— 让编译器把全部旧调用点暴露出来，
        /// 避免漏改成账号级读取。
        /// </remarks>
        [IgnoreProperty]
        public UserCurrency GameGold => Currencies.FirstOrDefault(x => x.Info?.Type == CurrencyType.GameGold);

        /// <summary>
        /// 账号上挂账的狩猎币（2026-11-11 起狩猎币下沉到角色）。
        /// <para>
        /// 推荐奖励这类「在角色建好之前就要发钱」的场景先挂在这里，等该账号的**第一个角色**上线时转入。
        /// </para>
        /// </summary>
        public long PendingHuntGold
        {
            get { return _PendingHuntGold; }
            set
            {
                if (_PendingHuntGold == value) return;

                var oldValue = _PendingHuntGold;
                _PendingHuntGold = value;

                OnChanged(oldValue, value, "PendingHuntGold");
            }
        }
        private long _PendingHuntGold;

        /// <summary>
        /// 给账号下的某个角色发狩猎币。
        /// <para>
        /// 角色级货币下沉后，「给账号发钱」必须落到具体角色上。规则：优先发给**最后登录的角色**，
        /// 其次第一个未删除的角色；账号下还没有角色时先挂账（<see cref="PendingHuntGold"/>），
        /// 等第一个角色上线时补发。
        /// </para>
        /// </summary>
        /// <returns>实际入账的货币对象；走挂账时返回 null。</returns>
        public UserCurrency GrantHuntGold(long amount)
        {
            if (amount <= 0) return null;

            CharacterInfo target = LastCharacter != null && !LastCharacter.Deleted
                ? LastCharacter
                : Characters.FirstOrDefault(x => x != null && !x.Deleted);

            if (target == null)
            {
                PendingHuntGold += amount;
                return null;
            }

            UserCurrency currency = target.HuntGold;

            if (currency == null)
            {
                currency = Session.GetCollection<UserCurrency>().CreateNewObject();
                currency.Character = target;
                currency.Info = Session.GetCollection<CurrencyInfo>().Binding.FirstOrDefault(x => x.Type == CurrencyType.HuntGold);
            }

            if (currency == null || currency.Info == null) return null;

            currency.Amount += amount;

            return currency;
        }



        public DateTime GlobalShoutExpiry
        {
            get { return _GlobalTime; }
            set
            {
                if (_GlobalTime == value) return;

                var oldValue = _GlobalTime;
                _GlobalTime = value;

                OnChanged(oldValue, value, "GlobalTime");
            }
        }
        private DateTime _GlobalTime;

        public bool Admin
        {
            get { return _Admin; }
            set
            {
                if (_Admin == value) return;

                var oldValue = _Admin;
                _Admin = value;

                OnChanged(oldValue, value, "Admin");
            }
        }
        private bool _Admin;

        public string LastSum
        {
            get { return _LastSum; }
            set
            {
                if (_LastSum == value) return;

                var oldValue = _LastSum;
                _LastSum = value;

                OnChanged(oldValue, value, "LastSum");
            }
        }
        private string _LastSum;

        public bool GoldBot
        {
            get { return _GoldBot; }
            set
            {
                if (_GoldBot == value) return;

                var oldValue = _GoldBot;
                _GoldBot = value;

                OnChanged(oldValue, value, "GoldBot");
            }
        }
        private bool _GoldBot;

        public bool ItemBot
        {
            get { return _ItemBot; }
            set
            {
                if (_ItemBot == value) return;

                var oldValue = _ItemBot;
                _ItemBot = value;

                OnChanged(oldValue, value, "ItemBot");
            }
        }
        private bool _ItemBot;


        public bool Observer
        {
            get { return _Observer; }
            set
            {
                if (_Observer == value) return;

                var oldValue = _Observer;
                _Observer = value;

                OnChanged(oldValue, value, "Observer");
            }
        }
        private bool _Observer;

        public bool TempAdmin;

        [Association("Currencies")]
        public DBBindingList<UserCurrency> Currencies { get; set; }

        [Association("Referrals")]
        public DBBindingList<AccountInfo> Referrals { get; set; }

        [Association("Characters")]
        public DBBindingList<CharacterInfo> Characters { get; set; }

        [Association("Payments")]
        public DBBindingList<GameGoldPayment> Payments { get; set; }

        public CharacterInfo LastCharacter
        {
            get { return _LastCharacter; }
            set
            {
                if (_LastCharacter == value) return;

                var oldValue = _LastCharacter;
                _LastCharacter = value;

                OnChanged(oldValue, value, "LastCharacter");
            }
        }
        private CharacterInfo _LastCharacter;


        public int WrongPasswordCount;
        public SConnection Connection;
        public string Key;

        protected override void OnCreated()
        {
            base.OnCreated();

            AddDefaultCurrencies();
        }
        protected override void OnLoaded()
        {
            base.OnLoaded();

            AddDefaultCurrencies();
            RemoveDeletedCurrencies();
        }

        /// <summary>
        /// 补齐账号级货币。2026-11-11 起**只有充值元宝 GameGold 留在账号**，
        /// 金币 / 狩猎币 / 声望点 / 贡献点全部下沉到角色（见 <see cref="CharacterInfo.Currencies"/>）。
        /// </summary>
        private void AddDefaultCurrencies()
        {
            foreach (var currency in Session.GetCollection<CurrencyInfo>().Binding)
            {
                // 只有充值元宝是账号级的，其余币种在角色上。
                if (currency.Type != CurrencyType.GameGold) continue;

                var userCurrency = Currencies.FirstOrDefault(x => x.Info == currency);

                if (userCurrency == null)
                {
                    userCurrency = Session.GetCollection<UserCurrency>().CreateNewObject();
                    userCurrency.Account = this;
                    userCurrency.Info = currency;
                }
            }
        }

        private void RemoveDeletedCurrencies()
        {
            for (int i = Currencies.Count - 1; i >= 0; i--)
            {
                var currency = Currencies[i];

                if (currency.Info == null)
                {
                    Currencies.RemoveAt(i);
                }
            }
        }

        public bool IsAdmin(bool includeTemp = false)
        {
            return Admin || (includeTemp && TempAdmin);
        }

        public int HighestLevel()
        {
            int count = 0;

            foreach (CharacterInfo character in Characters)
                if (character.Level > count)
                    count = character.Level;

            return count;
        }

        public List<SelectInfo> GetSelectInfo()
        {
            List<SelectInfo> characters = new List<SelectInfo>();

            foreach (CharacterInfo character in Characters)
            {
                if (character.Deleted) continue;

                characters.Add(character.ToSelectInfo());
            }

            return characters;
        }

        /// <summary>
        /// 是否允许该账号登录 Web 管理后台（与游戏内 GM 的 Admin 字段相互独立）。
        /// 注意：DB 属性按声明顺序序列化，新增字段必须加在类的最后一个属性位置。
        /// </summary>
        public bool WebAdmin
        {
            get { return _WebAdmin; }
            set
            {
                if (_WebAdmin == value) return;

                var oldValue = _WebAdmin;
                _WebAdmin = value;

                OnChanged(oldValue, value, "WebAdmin");
            }
        }
        private bool _WebAdmin;

        /// <summary>
        /// 管理身份等级（对齐 A 端 AccountIdentity，DB 属性按声明顺序序列化，
        /// 必须保持在类的最后一个属性位置）。判定一律走 <see cref="GetEffectiveIdentity"/>，
        /// 它包含旧数据（Admin bool）的兼容读。
        /// </summary>
        public AccountIdentity Identity
        {
            get { return _Identity; }
            set
            {
                if (_Identity == value) return;

                var oldValue = _Identity;
                _Identity = value;

                OnChanged(oldValue, value, "Identity");
            }
        }
        private AccountIdentity _Identity = AccountIdentity.Normal;

        /// <summary>
        /// 账号类型（后台显示用，2026-11-11）。
        /// <para>
        /// 只读 + <see cref="IgnoreProperty"/>：不参与持久化，也不改变 <see cref="IsBot"/> 作为最后一个 DB 属性的位置。
        /// 后台账号列表用它显示「真人 / 假人」，避免运营把假人账号当真人账号处理。
        /// </para>
        /// </summary>
        [IgnoreProperty]
        public string AccountType => IsBot ? "假人" : "真人";

        /// <summary>
        /// 是否为服务器内置假人账号（AI 假人系统）。
        /// 用途：假人账号不参与后台账号统计、不允许玩家登录（后端加校验）、
        /// 上线人数与水位调度靠它区分真假人。
        /// 注意：DB 属性按声明顺序序列化，新增字段必须加在类的最后一个属性位置。
        /// </summary>
        public bool IsBot
        {
            get { return _IsBot; }
            set
            {
                if (_IsBot == value) return;

                var oldValue = _IsBot;
                _IsBot = value;

                OnChanged(oldValue, value, "IsBot");
            }
        }
        private bool _IsBot;

        /// <summary>
        /// 实际生效的管理身份。
        /// TempAdmin（主密码登录）视为 Admin（最高档，2026-11-03 决策）；
        /// Identity 字段未迁移时兼容读旧 Admin bool。
        /// </summary>
        public AccountIdentity GetEffectiveIdentity(bool includeTemp = false)
        {
            if (includeTemp && TempAdmin) return AccountIdentity.Admin;

            if (Identity != AccountIdentity.Normal) return Identity;

            // 兼容旧数据：Admin bool 已置位但 Identity 尚未迁移
            if (Admin) return AccountIdentity.Admin;

            return AccountIdentity.Normal;
        }

        public override string ToString()
        {
            return EMailAddress;
        }
    }
}
