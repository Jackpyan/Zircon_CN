using Library;
using Library.SystemModels;
using MirDB;
using Server.Envir;
using Server.Models;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace Server.DBModels
{
    [UserObject]
    public sealed class CharacterInfo : DBObject
    {
        [Association("Characters")]
        public AccountInfo Account
        {
            get { return _Account; }
            set
            {
                if (_Account == value) return;

                var oldValue = _Account;
                _Account = value;

                OnChanged(oldValue, value, "Account");
            }
        }
        private AccountInfo _Account;

        public string CharacterName
        {
            get { return _CharacterName; }
            set
            {
                if (_CharacterName == value) return;

                var oldValue = _CharacterName;
                _CharacterName = value;

                OnChanged(oldValue, value, "CharacterName");
            }
        }
        private string _CharacterName;


        public MirClass Class
        {
            get { return _Class; }
            set
            {
                if (_Class == value) return;

                var oldValue = _Class;
                _Class = value;

                OnChanged(oldValue, value, "Class");
            }
        }
        private MirClass _Class;

        public MirGender Gender
        {
            get { return _Gender; }
            set
            {
                if (_Gender == value) return;

                var oldValue = _Gender;
                _Gender = value;

                OnChanged(oldValue, value, "Gender");
            }
        }
        private MirGender _Gender;

        public int Level
        {
            get { return _Level; }
            set
            {
                if (_Level == value) return;

                var oldValue = _Level;
                _Level = value;

                OnChanged(oldValue, value, "Level");
            }
        }
        private int _Level;

        public string Caption
        {
            get { return _Caption; }
            set
            {
                if (_Caption == value) return;

                var oldValue = _Caption;
                _Caption = value;

                OnChanged(oldValue, value, "Caption");
            }
        }
        private string _Caption;

        public int HairType
        {
            get { return _HairType; }
            set
            {
                if (_HairType == value) return;

                var oldValue = _HairType;
                _HairType = value;

                OnChanged(oldValue, value, "HairType");
            }
        }
        private int _HairType;

        public Color HairColour
        {
            get { return _HairColour; }
            set
            {
                if (_HairColour == value) return;

                var oldValue = _HairColour;
                _HairColour = value;

                OnChanged(oldValue, value, "HairColour");
            }
        }
        private Color _HairColour;

        public Color ArmourColour
        {
            get { return _ArmourColour; }
            set
            {
                if (_ArmourColour == value) return;

                var oldValue = _ArmourColour;
                _ArmourColour = value;

                OnChanged(oldValue, value, "ArmourColour");
            }
        }
        private Color _ArmourColour;

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

        public bool Deleted
        {
            get { return _Deleted; }
            set
            {
                if (_Deleted == value) return;

                var oldValue = _Deleted;
                _Deleted = value;

                OnChanged(oldValue, value, "Deleted");
            }
        }
        private bool _Deleted;

        /// <summary>
        /// 挂机剩余时长（秒），按**角色**持久化 —— 同账号下多个角色各自独立计时。
        /// 加时间药水（<see cref="ItemEffect.AutoTime"/>）累加；挂机中由服务端每秒回写。
        /// 角色被删除时由 SEnvir.DeleteCharacter 清零。
        /// </summary>
        public long AutoTime
        {
            get { return _AutoTime; }
            set
            {
                if (_AutoTime == value) return;

                var oldValue = _AutoTime;
                _AutoTime = value;

                OnChanged(oldValue, value, "AutoTime");
            }
        }
        private long _AutoTime;

        /// <summary>
        /// 上次做「挂机时长月度清零」的月份（yyyyMM），仅用于每月清零判定。
        /// 见 PlayerObject.ProcessAutoTimeMonthlyReset。
        /// </summary>
        public int AutoTimeMonth
        {
            get { return _AutoTimeMonth; }
            set
            {
                if (_AutoTimeMonth == value) return;

                var oldValue = _AutoTimeMonth;
                _AutoTimeMonth = value;

                OnChanged(oldValue, value, "AutoTimeMonth");
            }
        }
        private int _AutoTimeMonth;

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

        public Point CurrentLocation
        {
            get { return _CurrentLocation; }
            set
            {
                if (_CurrentLocation == value) return;

                var oldValue = _CurrentLocation;
                _CurrentLocation = value;

                OnChanged(oldValue, value, "CurrentLocation");
            }
        }
        private Point _CurrentLocation;

        public MapInfo CurrentMap
        {
            get { return _CurrentMap; }
            set
            {
                if (_CurrentMap == value) return;

                var oldValue = _CurrentMap;
                _CurrentMap = value;

                OnChanged(oldValue, value, "CurrentMap");
            }
        }
        private MapInfo _CurrentMap;

        public InstanceInfo CurrentInstance
        {
            get { return _CurrentInstance; }
            set
            {
                if (_CurrentInstance == value) return;

                var oldValue = _CurrentInstance;
                _CurrentInstance = value;

                OnChanged(oldValue, value, "CurrentInstance");
            }
        }
        private InstanceInfo _CurrentInstance;

        public MirDirection Direction
        {
            get { return _Direction; }
            set
            {
                if (_Direction == value) return;

                var oldValue = _Direction;
                _Direction = value;

                OnChanged(oldValue, value, "Direction");
            }
        }
        private MirDirection _Direction;

        public SafeZoneInfo BindPoint
        {
            get { return _BindPoint; }
            set
            {
                if (_BindPoint == value) return;

                var oldValue = _BindPoint;
                _BindPoint = value;

                OnChanged(oldValue, value, "BindPoint");
            }
        }
        private SafeZoneInfo _BindPoint;

        public int CurrentHP
        {
            get { return _CurrentHP; }
            set
            {
                if (_CurrentHP == value) return;

                var oldValue = _CurrentHP;
                _CurrentHP = value;

                OnChanged(oldValue, value, "CurrentHP");
            }
        }
        private int _CurrentHP;

        public int CurrentMP
        {
            get { return _CurrentMP; }
            set
            {
                if (_CurrentMP == value) return;

                var oldValue = _CurrentMP;
                _CurrentMP = value;

                OnChanged(oldValue, value, "CurrentMP");
            }
        }
        private int _CurrentMP;

        public decimal Experience
        {
            get { return _Experience; }
            set
            {
                if (_Experience == value) return;

                var oldValue = _Experience;
                _Experience = value;

                OnChanged(oldValue, value, "Experience");
            }
        }
        private decimal _Experience;

        public bool CanThrusting
        {
            get { return _canThrusting; }
            set
            {
                if (_canThrusting == value) return;

                var oldValue = _canThrusting;
                _canThrusting = value;

                OnChanged(oldValue, value, "CanThrusting");
            }
        }
        private bool _canThrusting;

        public bool CanHalfMoon
        {
            get { return _CanHalfMoon; }
            set
            {
                if (_CanHalfMoon == value) return;

                var oldValue = _CanHalfMoon;
                _CanHalfMoon = value;

                OnChanged(oldValue, value, "CanHalfMoon");
            }
        }
        private bool _CanHalfMoon;

        public bool CanDestructiveSurge
        {
            get { return _canDestructiveSurge; }
            set
            {
                if (_canDestructiveSurge == value) return;

                var oldValue = _canDestructiveSurge;
                _canDestructiveSurge = value;

                OnChanged(oldValue, value, "CanDestructiveSurge");
            }
        }
        private bool _canDestructiveSurge;

        public bool CanFlameSplash
        {
            get { return _CanFlameSplash; }
            set
            {
                if (_CanFlameSplash == value) return;

                var oldValue = _CanFlameSplash;
                _CanFlameSplash = value;

                OnChanged(oldValue, value, "CanFlameSplash");
            }
        }
        private bool _CanFlameSplash;

        public Stats LastStats
        {
            get { return _LastStats; }
            set
            {
                if (_LastStats == value) return;

                var oldValue = _LastStats;
                _LastStats = value;

                OnChanged(oldValue, value, "LastStats");
            }
        }
        private Stats _LastStats;

        public Stats HermitStats
        {
            get { return _HermitStats; }
            set
            {
                if (_HermitStats == value) return;

                var oldValue = _HermitStats;
                _HermitStats = value;

                OnChanged(oldValue, value, "HermitStats");
            }
        }
        private Stats _HermitStats;

        public int SpentPoints
        {
            get { return _SpentPoints; }
            set
            {
                if (_SpentPoints == value) return;

                var oldValue = _SpentPoints;
                _SpentPoints = value;

                OnChanged(oldValue, value, "SpentPoints");
            }
        }
        private int _SpentPoints;

        public AttackMode AttackMode
        {
            get { return _AttackMode; }
            set
            {
                if (_AttackMode == value) return;

                var oldValue = _AttackMode;
                _AttackMode = value;

                OnChanged(oldValue, value, "AttackMode");
            }
        }
        private AttackMode _AttackMode;

        public PetMode PetMode
        {
            get { return _PetMode; }
            set
            {
                if (_PetMode == value) return;

                var oldValue = _PetMode;
                _PetMode = value;

                OnChanged(oldValue, value, "PetMode");
            }
        }
        private PetMode _PetMode;

        public bool Observable
        {
            get { return _Observable; }
            set
            {
                if (_Observable == value) return;

                var oldValue = _Observable;
                _Observable = value;

                OnChanged(oldValue, value, "Observable");
            }
        }
        private bool _Observable;

        public DateTime ItemReviveTime
        {
            get { return _ItemReviveTime; }
            set
            {
                if (_ItemReviveTime == value) return;

                var oldValue = _ItemReviveTime;
                _ItemReviveTime = value;

                OnChanged(oldValue, value, "ItemReviveTime");
            }
        }
        private DateTime _ItemReviveTime;

        public DateTime ReincarnationPillTime
        {
            get { return _ReincarnationPillTime; }
            set
            {
                if (_ReincarnationPillTime == value) return;

                var oldValue = _ReincarnationPillTime;
                _ReincarnationPillTime = value;

                OnChanged(oldValue, value, "ReincarnationPillTime");
            }
        }
        private DateTime _ReincarnationPillTime;

        public DateTime MarriageTeleportTime
        {
            get { return _MarriageTeleportTime; }
            set
            {
                if (_MarriageTeleportTime == value) return;

                var oldValue = _MarriageTeleportTime;
                _MarriageTeleportTime = value;

                OnChanged(oldValue, value, "MarriageTeleportTime");
            }
        }
        private DateTime _MarriageTeleportTime;

        public DateTime GroupRecallTime
        {
            get { return _GroupRecallTime; }
            set
            {
                if (_GroupRecallTime == value) return;

                var oldValue = _GroupRecallTime;
                _GroupRecallTime = value;

                OnChanged(oldValue, value, "GroupRecallTime");
            }
        }
        private DateTime _GroupRecallTime;

        public bool HideHelmet
        {
            get { return _HideHelmet; }
            set
            {
                if (_HideHelmet == value) return;

                var oldValue = _HideHelmet;
                _HideHelmet = value;

                OnChanged(oldValue, value, "HideHelmet");
            }
        }
        private bool _HideHelmet;

        public bool CanDeathDrop
        {
            get { return _CanDeathDrop; }
            set
            {
                if (_CanDeathDrop == value) return;

                var oldValue = _CanDeathDrop;
                _CanDeathDrop = value;

                OnChanged(oldValue, value, "CanDeathDrop");
            }
        }
        private bool _CanDeathDrop;

        public int Rebirth
        {
            get => _Rebirth;
            set
            {
                if (_Rebirth == value) return;

                int oldValue = _Rebirth;
                _Rebirth = value;

                OnChanged(oldValue, value, "Rebirth");
            }
        }
        private int _Rebirth;

        public int Fame
        {
            get => _Fame;
            set
            {
                if (_Fame == value) return;

                int oldValue = _Fame;
                _Fame = value;

                OnChanged(oldValue, value, "Fame");
            }
        }
        private int _Fame;

        public int CraftingLevel
        {
            get => _CraftingLevel;
            set { if (_CraftingLevel == value) return; int oldValue = _CraftingLevel; _CraftingLevel = value; OnChanged(oldValue, value, "CraftingLevel"); }
        }
        private int _CraftingLevel = 1;

        public long CraftingExperience
        {
            get => _CraftingExperience;
            set { if (_CraftingExperience == value) return; long oldValue = _CraftingExperience; _CraftingExperience = value; OnChanged(oldValue, value, "CraftingExperience"); }
        }
        private long _CraftingExperience;

        public CraftingRecipeInfo FavouriteCraftingRecipe
        {
            get => _FavouriteCraftingRecipe;
            set { if (_FavouriteCraftingRecipe == value) return; CraftingRecipeInfo oldValue = _FavouriteCraftingRecipe; _FavouriteCraftingRecipe = value; OnChanged(oldValue, value, "FavouriteCraftingRecipe"); }
        }
        private CraftingRecipeInfo _FavouriteCraftingRecipe;

        public DateTime NextDeathDropChange
        {
            get { return _NextDeathDropChange; }
            set
            {
                if (_NextDeathDropChange == value) return;

                var oldValue = _NextDeathDropChange;
                _NextDeathDropChange = value;

                OnChanged(oldValue, value, "NextDeathDropChange");
            }
        }
        private DateTime _NextDeathDropChange;

        [Association("Companion")]
        public UserCompanion Companion
        {
            get { return _Companion; }
            set
            {
                if (_Companion == value) return;

                var oldValue = _Companion;
                _Companion = value;

                OnChanged(oldValue, value, "Companion");
            }
        }
        private UserCompanion _Companion;

        [Association("Discipline")]
        public UserDiscipline Discipline
        {
            get { return _Discipline; }
            set
            {
                if (_Discipline == value) return;

                var oldValue = _Discipline;
                _Discipline = value;

                OnChanged(oldValue, value, "Discipline");
            }
        }
        private UserDiscipline _Discipline;

        /// <summary>
        /// 角色所在的行会席位（2026-11-11 起行会 + 职位 + 贡献度下沉到角色）。
        /// <para>
        /// 之前席位挂在账号上，同账号的多个角色只能占一个席位；现在每个角色各有自己的席位。
        /// </para>
        /// </summary>
        [Association("Member")]
        public GuildMemberInfo GuildMember { get; set; }

        /// <summary>退会 / 被踢后的冷却截止时间（行会相关，跟着角色走）。</summary>
        public DateTime GuildTime
        {
            get { return _GuildTime; }
            set
            {
                if (_GuildTime == value) return;

                var oldValue = _GuildTime;
                _GuildTime = value;

                OnChanged(oldValue, value, "GuildTime");
            }
        }
        private DateTime _GuildTime;

        /// <summary>
        /// 角色级货币（2026-11-11 起金币 / 狩猎币从账号下沉到角色）。
        /// 充值元宝 <see cref="CurrencyType.GameGold"/> 仍在 <see cref="AccountInfo.Currencies"/>。
        /// </summary>
        [Association("Currencies")]
        public DBBindingList<UserCurrency> Currencies { get; set; }

        /// <summary>角色金币。没有时返回 null（迁移补齐前的老角色可能出现）。</summary>
        /// <remarks>
        /// 必须是 <see cref="IgnorePropertyAttribute"/>：这是只读表达式属性，
        /// MirDB 会把「DBObject 类型的属性」当成关联字段去持久化并在加载时反查赋值，
        /// 少了这个标记加载旧库会抛「Property set method not found」。
        /// </remarks>
        [IgnoreProperty]
        public UserCurrency Gold => Currencies?.FirstOrDefault(x => x.Info?.Type == CurrencyType.Gold);

        /// <summary>角色狩猎币。</summary>
        [IgnoreProperty]
        public UserCurrency HuntGold => Currencies?.FirstOrDefault(x => x.Info?.Type == CurrencyType.HuntGold);

        /// <summary>
        /// 角色坐骑（2026-11-11 起从账号下沉到角色）。
        /// <para>
        /// 之前坐骑挂在账号上，同账号 4 个角色共用一匹；现在每个角色各有一匹。
        /// </para>
        /// </summary>
        public HorseType Horse
        {
            get { return _Horse; }
            set
            {
                if (_Horse == value) return;

                var oldValue = _Horse;
                _Horse = value;

                OnChanged(oldValue, value, "Horse");
            }
        }
        private HorseType _Horse;

        /// <summary>是否允许被邀请组队（2026-11-11 起从账号下沉到角色）。</summary>
        public bool AllowGroup
        {
            get { return _AllowGroup; }
            set
            {
                if (_AllowGroup == value) return;

                var oldValue = _AllowGroup;
                _AllowGroup = value;

                OnChanged(oldValue, value, "AllowGroup");
            }
        }
        private bool _AllowGroup;

        /// <summary>是否允许被交易（2026-11-11 起从账号下沉到角色）。</summary>
        public bool AllowTrade
        {
            get { return _AllowTrade; }
            set
            {
                if (_AllowTrade == value) return;

                var oldValue = _AllowTrade;
                _AllowTrade = value;

                OnChanged(oldValue, value, "AllowTrade");
            }
        }
        private bool _AllowTrade;

        /// <summary>是否允许被邀请入行会（2026-11-11 起从账号下沉到角色）。</summary>
        public bool AllowGuild
        {
            get { return _AllowGuild; }
            set
            {
                if (_AllowGuild == value) return;

                var oldValue = _AllowGuild;
                _AllowGuild = value;

                OnChanged(oldValue, value, "AllowGuild");
            }
        }
        private bool _AllowGuild;

        /// <summary>是否允许被组召回（2026-11-11 起从账号下沉到角色）。</summary>
        public bool AllowGroupRecall
        {
            get { return _AllowGroupRecall; }
            set
            {
                if (_AllowGroupRecall == value) return;

                var oldValue = _AllowGroupRecall;
                _AllowGroupRecall = value;

                OnChanged(oldValue, value, "AllowGroupRecall");
            }
        }
        private bool _AllowGroupRecall;

        /// <summary>
        /// 角色的宠物名册（2026-11-11 起从账号下沉到角色）。
        /// </summary>
        [Association("Companions", true)]
        public DBBindingList<UserCompanion> Companions { get; set; }

        /// <summary>
        /// 角色的宠物外观解锁（2026-11-11 起从账号下沉到角色）。
        /// </summary>
        [Association("CompanionUnlocks", true)]
        public DBBindingList<UserCompanionUnlock> CompanionUnlocks { get; set; }

        /// <summary>
        /// 角色的邮件（2026-11-11 起从账号下沉到角色）。
        /// </summary>
        [Association("Mail", true)]
        public DBBindingList<MailInfo> Mail { get; set; }

        /// <summary>
        /// 角色的寄售（2026-11-11 起从账号下沉到角色）。
        /// </summary>
        [Association("Auctions", true)]
        public DBBindingList<AuctionInfo> Auctions { get; set; }

        /// <summary>
        /// 本角色屏蔽的角色名单（2026-11-11 起屏蔽从账号下沉到角色）。
        /// </summary>
        [Association("BlockingList", true)]
        public DBBindingList<BlockInfo> BlockingList { get; set; }

        /// <summary>
        /// 屏蔽本角色的角色名单（2026-11-11 起屏蔽从账号下沉到角色）。
        /// </summary>
        [Association("BlockedByList", true)]
        public DBBindingList<BlockInfo> BlockedByList { get; set; }

        /// <summary>
        /// 本角色的商城购买记录（2026-11-11 起商城记录从账号下沉到角色）。
        /// </summary>
        [Association("StoreSales", true)]
        public DBBindingList<GameStoreSale> StoreSales { get; set; }

        /// <summary>
        /// 本角色的商城收藏（2026-11-11 起商城记录从账号下沉到角色）。
        /// </summary>
        [Association("StoreFavourites", true)]
        public DBBindingList<GameStoreFavourite> StoreFavourites { get; set; }

        [Association("Items", true)]
        public DBBindingList<UserItem> Items { get; set; }

        /// <summary>
        /// 角色仓库（2026-11-11 起从账号下沉到角色）。
        /// <para>
        /// identity 用 <c>"Storage"</c> 而不是 <c>"Items"</c>：MirDB 里同一个子类型
        /// （<see cref="UserItem"/>）不能挂在同一个父类型的两个 identity 相同的集合下，
        /// 背包已经占了 <c>"Items"</c>。
        /// </para>
        /// </summary>
        [Association("Storage", true)]
        public DBBindingList<UserItem> Storage { get; set; }

        /// <summary>
        /// 仓库格数（2026-11-11 起从账号下沉到角色）。
        /// </summary>
        public int StorageSize
        {
            get { return _StorageSize; }
            set
            {
                if (_StorageSize == value) return;

                var oldValue = _StorageSize;
                _StorageSize = value;

                OnChanged(oldValue, value, "StorageSize");
            }
        }
        private int _StorageSize;

        [Association("BeltLinks", true)]
        public DBBindingList<CharacterBeltLink> BeltLinks { get; set; }

        [Association("AutoPotionLinks", true)]
        public DBBindingList<AutoPotionLink> AutoPotionLinks { get; set; }

        [Association("AutoFightLinks", true)]
        public DBBindingList<AutoFightConfig> AutoFightLinks { get; set; }

        [Association("Magics", true)]
        public DBBindingList<UserMagic> Magics { get; set; }

        [Association("Buffs", true)]
        public DBBindingList<BuffInfo> Buffs { get; set; }

        [Association("Refines", true)]
        public DBBindingList<RefineInfo> Refines { get; set; }

        [Association("Quests", true)]
        public DBBindingList<UserQuest> Quests { get; set; }

        [Association("Friends", true)]
        public DBBindingList<FriendInfo> Friends { get; set; }

        [Association("FriendedBy", true)]
        public DBBindingList<FriendInfo> FriendedBy { get; set; }

        [Association("MilestoneLogs", true)]
        public DBBindingList<UserMilestoneLog> MilestoneLogs { get; set; }

        [Association("Milestones", true)]
        public DBBindingList<UserMilestone> Milestones { get; set; }

        [Association("UserDrops", true)]
        public DBBindingList<UserDrop> UserDrops { get; set; }

        [Association("Fortunes", true)]
        public DBBindingList<UserFortuneInfo> Fortunes { get; set; }

        public OnlineState OnlineState
        {
            get { return _OnlineState; }
            set
            {
                if (_OnlineState == value) return;

                var oldValue = _OnlineState;
                _OnlineState = value;

                OnChanged(oldValue, value, "OnlineState");
            }
        }
        private OnlineState _OnlineState;

        [Association("Marriage")]
        public CharacterInfo Partner
        {
            get { return _Partner; }
            set
            {
                if (_Partner == value) return;

                var oldValue = _Partner;
                _Partner = value;

                OnChanged(oldValue, value, "Partner");
            }
        }
        private CharacterInfo _Partner;

        public GroupLootMode GroupLootMode
        {
            get { return _GroupLootMode; }
            set
            {
                if (_GroupLootMode == value) return;

                var oldValue = _GroupLootMode;
                _GroupLootMode = value;

                OnChanged(oldValue, value, "GroupLootMode");
            }
        }
        private GroupLootMode _GroupLootMode;

        public bool GroupLootBagEnabled
        {
            get { return _GroupLootBagEnabled; }
            set
            {
                if (_GroupLootBagEnabled == value) return;

                var oldValue = _GroupLootBagEnabled;
                _GroupLootBagEnabled = value;

                OnChanged(oldValue, value, "GroupLootBagEnabled");
            }
        }
        private bool _GroupLootBagEnabled = true;

        public bool GroupLootNeedRestrictions
        {
            get { return _GroupLootNeedRestrictions; }
            set
            {
                if (_GroupLootNeedRestrictions == value) return;

                var oldValue = _GroupLootNeedRestrictions;
                _GroupLootNeedRestrictions = value;

                OnChanged(oldValue, value, "GroupLootNeedRestrictions");
            }
        }
        private bool _GroupLootNeedRestrictions;

        public bool GroupLootAllowManualTaking
        {
            get { return _GroupLootAllowManualTaking; }
            set
            {
                if (_GroupLootAllowManualTaking == value) return;

                var oldValue = _GroupLootAllowManualTaking;
                _GroupLootAllowManualTaking = value;

                OnChanged(oldValue, value, "GroupLootAllowManualTaking");
            }
        }
        private bool _GroupLootAllowManualTaking = true;

        public string GroupLootRarities
        {
            get { return _GroupLootRarities; }
            set
            {
                if (_GroupLootRarities == value) return;

                var oldValue = _GroupLootRarities;
                _GroupLootRarities = value;

                OnChanged(oldValue, value, "GroupLootRarities");
            }
        }
        private string _GroupLootRarities = "";

        public string GroupLootItemTypes
        {
            get { return _GroupLootItemTypes; }
            set
            {
                if (_GroupLootItemTypes == value) return;

                var oldValue = _GroupLootItemTypes;
                _GroupLootItemTypes = value;

                OnChanged(oldValue, value, "GroupLootItemTypes");
            }
        }
        private string _GroupLootItemTypes = "";

        public string FiltersClass
        {
            get { return _FiltersClass; }
            set
            {
                if (_FiltersClass == value) return;

                var oldValue = _FiltersClass;
                _FiltersClass = value;

                OnChanged(oldValue, value, "FiltersClass");
            }
        }
        private string _FiltersClass = "";

        public string FiltersRarity
        {
            get { return _FiltersRarity; }
            set
            {
                if (_FiltersRarity == value) return;

                var oldValue = _FiltersRarity;
                _FiltersRarity = value;

                OnChanged(oldValue, value, "FiltersRarity");
            }
        }
        private string _FiltersRarity = "";

        public string FiltersItemType
        {
            get { return _FiltersItemType; }
            set
            {
                if (_FiltersItemType == value) return;

                var oldValue = _FiltersItemType;
                _FiltersItemType = value;

                OnChanged(oldValue, value, "FiltersItemType");
            }
        }
        private string _FiltersItemType = "";

        /// <summary>
        /// 宠物拾取白名单（物品 Index，逗号分隔）。
        /// 空字符串 = 什么都不捡（与拾取页「未勾选就不拾取」一致）。
        /// </summary>
        public string FiltersPickupItems
        {
            get { return _FiltersPickupItems; }
            set
            {
                if (_FiltersPickupItems == value) return;

                var oldValue = _FiltersPickupItems;
                _FiltersPickupItems = value;

                OnChanged(oldValue, value, "FiltersPickupItems");
            }
        }
        private string _FiltersPickupItems = "";

        /// <summary>宠物拾取是否放行非白色物品（碎片 / 带附加属性 / 非普通品质）。</summary>
        public bool FiltersPickupNonWhite
        {
            get { return _FiltersPickupNonWhite; }
            set
            {
                if (_FiltersPickupNonWhite == value) return;

                var oldValue = _FiltersPickupNonWhite;
                _FiltersPickupNonWhite = value;

                OnChanged(oldValue, value, "FiltersPickupNonWhite");
            }
        }
        private bool _FiltersPickupNonWhite;

        /// <summary>
        /// 假人累计击杀数（2026-11-11 新增，跨重启持久化）。
        /// <para>
        /// 真人角色永远是 0 —— 这个字段只给 AI 假人用，让后台「假人档案」能显示历史战绩，
        /// 而不是只看本次进程的快照（重启就归零）。
        /// </para>
        /// <para>
        /// ⚠ MirDB 按**声明顺序**序列化，新增字段必须放在类的最后一个属性位置 —— 这里就是最后一个。
        /// </para>
        /// </summary>
        public int BotKills
        {
            get { return _BotKills; }
            set
            {
                if (_BotKills == value) return;

                var oldValue = _BotKills;
                _BotKills = value;

                OnChanged(oldValue, value, "BotKills");
            }
        }
        private int _BotKills;

        /// <summary>
        /// 假人累计在线秒数（2026-11-11 新增，跨重启持久化）。
        /// <para>
        /// 每次下线时把本次在线时长累加进来；真人角色永远是 0。
        /// ⚠ 同 <see cref="BotKills"/>，必须放在类的最后一个属性位置。
        /// </para>
        /// </summary>
        public long BotOnlineSeconds
        {
            get { return _BotOnlineSeconds; }
            set
            {
                if (_BotOnlineSeconds == value) return;

                var oldValue = _BotOnlineSeconds;
                _BotOnlineSeconds = value;

                OnChanged(oldValue, value, "BotOnlineSeconds");
            }
        }
        private long _BotOnlineSeconds;

        /// <summary>
        /// 假人累计成功补给次数（2026-11-11 新增，跨重启持久化）。
        /// <para>
        /// 只在**真的买到东西**时 +1（见 <c>BotBrain.FinishRestock</c>）。
        /// D5 压测靠这个数量化「断药能回城买」这条闭环是否成立。
        /// ⚠ 同 <see cref="BotKills"/>，必须放在类的最后一个属性位置。
        /// </para>
        /// </summary>
        public int BotRestocks
        {
            get { return _BotRestocks; }
            set
            {
                if (_BotRestocks == value) return;

                var oldValue = _BotRestocks;
                _BotRestocks = value;

                OnChanged(oldValue, value, "BotRestocks");
            }
        }
        private int _BotRestocks;

        /// <summary>
        /// 假人累计补给失败次数（超时 / 没钱 / 找不到药店）。
        /// <para>
        /// 与 <see cref="BotRestocks"/> 配对看：失败率过高说明闭环有问题。
        /// ⚠ 同 <see cref="BotKills"/>，必须放在类的最后一个属性位置。
        /// </para>
        /// </summary>
        public int BotRestockFails
        {
            get { return _BotRestockFails; }
            set
            {
                if (_BotRestockFails == value) return;

                var oldValue = _BotRestockFails;
                _BotRestockFails = value;

                OnChanged(oldValue, value, "BotRestockFails");
            }
        }
        private int _BotRestockFails;

        /// <summary>
        /// 假人累计使用回城卷次数（衡量「瞬移回城」这条捷径的利用量）。
        /// <para>
        /// ⚠ 同 <see cref="BotKills"/>，必须放在类的最后一个属性位置。
        /// </para>
        /// </summary>
        public int BotTownScrollUses
        {
            get { return _BotTownScrollUses; }
            set
            {
                if (_BotTownScrollUses == value) return;

                var oldValue = _BotTownScrollUses;
                _BotTownScrollUses = value;

                OnChanged(oldValue, value, "BotTownScrollUses");
            }
        }
        private int _BotTownScrollUses;

        /// <summary>
        /// 假人累计买到回城卷的张数。
        /// <para>
        /// ⚠ 同 <see cref="BotKills"/>，必须放在类的最后一个属性位置。
        /// </para>
        /// </summary>
        public int BotTownScrollBuys
        {
            get { return _BotTownScrollBuys; }
            set
            {
                if (_BotTownScrollBuys == value) return;

                var oldValue = _BotTownScrollBuys;
                _BotTownScrollBuys = value;

                OnChanged(oldValue, value, "BotTownScrollBuys");
            }
        }
        private int _BotTownScrollBuys;

        /// <summary>
        /// 假人累计交易次数（卖货 / 修理各算一次）。
        /// <para>
        /// ⚠ 同 <see cref="BotKills"/>，必须放在类的最后一个属性位置。
        /// </para>
        /// </summary>
        public int BotTrades
        {
            get { return _BotTrades; }
            set
            {
                if (_BotTrades == value) return;

                var oldValue = _BotTrades;
                _BotTrades = value;

                OnChanged(oldValue, value, "BotTrades");
            }
        }
        private int _BotTrades;

        /// <summary>
        /// 假人累计挖取次数（鸡 / 猪 / 牛的掉落不落地，必须挖）。
        /// <para>
        /// ⚠ 同 <see cref="BotKills"/>，必须放在类的最后一个属性位置。
        /// </para>
        /// </summary>
        public int BotHarvests
        {
            get { return _BotHarvests; }
            set
            {
                if (_BotHarvests == value) return;

                var oldValue = _BotHarvests;
                _BotHarvests = value;

                OnChanged(oldValue, value, "BotHarvests");
            }
        }
        private int _BotHarvests;

        public Dictionary<RequiredClass, int> CurrentRank = new();
        public Dictionary<RequiredClass, int> RankChange = new();

        protected override void OnLoaded()
        {
            var removeList = new List<UserItem>();

            foreach (var item in Items)
            {
                if (item.Info == null)
                    removeList.Add(item);
            }

            foreach (var item in removeList)
                Items.Remove(item);

            // 仓库同样要清掉「物品模板已不存在」的脏数据（2026-11-11 仓库下沉到角色后接过来）。
            var removeStorageList = new List<UserItem>();

            foreach (var item in Storage)
            {
                if (item.Info == null)
                    removeStorageList.Add(item);
            }

            foreach (var item in removeStorageList)
                Storage.Remove(item);

            base.OnLoaded();

            // 老库里角色的仓库格数是 0（仓库格数之前挂在账号上），这里补齐默认值。
            if (StorageSize == 0)
                StorageSize = Globals.StorageSize;

            // 狩猎币上限 buff 之前挂在账号上，这里按角色补齐（老库迁移不到就重建一条）。
            BuffInfo huntGoldBuff = Buffs.FirstOrDefault(x => x.Type == BuffType.HuntGold);
            if (huntGoldBuff == null)
            {
                huntGoldBuff = SEnvir.BuffInfoList.CreateNewObject();
                huntGoldBuff.Character = this;
                huntGoldBuff.Type = BuffType.HuntGold;
                huntGoldBuff.TickFrequency = TimeSpan.FromMinutes(1);
                huntGoldBuff.RemainingTime = TimeSpan.MaxValue;
            }

            huntGoldBuff.Stats = new Stats { [Stat.AvailableHuntGoldCap] = 15 };

            // 老库里角色没有货币记录（2026-11-11 之前金币挂在账号上），这里补齐空壳。
            // 真正的余额搬运由 <see cref="UserCurrency"/> 的加载期迁移负责（两边做的是累加，不会重复计数）。
            AddDefaultCurrencies();
        }

        protected override void OnDeleted()
        {
            Account = null;
            Companion = null;
            Partner = null;

            base.OnDeleted();
        }

        public PlayerObject Player;
        public LinkedListNode<CharacterInfo> RankingNode;

        public SelectInfo ToSelectInfo()
        {
            return new SelectInfo
            {
                CharacterIndex = Index,
                CharacterName = CharacterName,
                Class = Class,
                Gender = Gender,
                Level = Level,
                Location = CurrentMap?.Index ?? 0,
                LastLogin = LastLogin,
            };
        }

        protected override void OnCreated()
        {
            base.OnCreated();

            LastStats = new Stats();
            HermitStats = new Stats();

            Observable = true;

            StorageSize = Globals.StorageSize;

            // 狩猎币上限 buff（2026-11-11 起从账号下沉到角色，与角色级狩猎币配套）。
            BuffInfo buff = SEnvir.BuffInfoList.CreateNewObject();

            buff.Character = this;
            buff.Type = BuffType.HuntGold;
            buff.TickFrequency = TimeSpan.FromMinutes(1);
            buff.Stats = new Stats { [Stat.AvailableHuntGoldCap] = 15 };
            buff.RemainingTime = TimeSpan.MaxValue;

            AddDefaultCurrencies();
        }

        /// <summary>
        /// 补齐角色级货币（金币 / 狩猎币 / 声望点 / 贡献点）。
        /// 充值元宝 GameGold 是账号级的，不在这里建。
        /// </summary>
        private void AddDefaultCurrencies()
        {
            if (Session == null) return;

            var currencyInfos = Session.GetCollection<CurrencyInfo>().Binding;

            if (currencyInfos == null || currencyInfos.Count == 0) return;

            foreach (var currency in currencyInfos)
            {
                if (currency.Type == CurrencyType.GameGold) continue;

                var userCurrency = Currencies.FirstOrDefault(x => x.Info == currency);

                if (userCurrency != null) continue;

                userCurrency = Session.GetCollection<UserCurrency>().CreateNewObject();
                userCurrency.Character = this;
                userCurrency.Info = currency;
            }
        }

        public override string ToString()
        {
            return CharacterName;
        }
    }
}
