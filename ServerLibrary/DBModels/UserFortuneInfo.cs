using Library;
using Library.SystemModels;
using MirDB;
using Server.Envir;
using System;
using System.Text.Json.Serialization;

namespace Server.DBModels
{
    [UserObject]
    public class UserFortuneInfo : DBObject
    {
        [Association("Fortunes")]
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

        public ItemInfo Item
        {
            get { return _Item; }
            set
            {
                if (_Item == value) return;

                var oldValue = _Item;
                _Item = value;

                OnChanged(oldValue, value, "Item");
            }
        }
        private ItemInfo _Item;


        public long DropCount
        {
            get { return _DropCount; }
            set
            {
                if (_DropCount == value) return;

                var oldValue = _DropCount;
                _DropCount = value;

                OnChanged(oldValue, value, "DropCount");
            }
        }
        private long _DropCount;

        public decimal DropProgress
        {
            get { return _DropProgress; }
            set
            {
                if (_DropProgress == value) return;

                var oldValue = _DropProgress;
                _DropProgress = value;

                OnChanged(oldValue, value, "DropProgress");
            }
        }
        private decimal _DropProgress;

        public DateTime CheckTime
        {
            get { return _CheckTime; }
            set
            {
                if (_CheckTime == value) return;

                var oldValue = _CheckTime;
                _CheckTime = value;

                OnChanged(oldValue, value, "CheckTime");
            }
        }
        private DateTime _CheckTime;

        /// <summary>
        /// 旧库遗留的账号索引（当年幸运查探记录挂在账号上）。这一列不参与新结构，
        /// 只在首次加载旧库时被 MirDB 读进来，由 <see cref="LegacyDropMigration"/> 换算成角色。
        /// </summary>
        [JsonIgnore]
        [MigrationProperty("Account")]
        public int LegacyAccountIndex
        {
            get { return _LegacyAccountIndex; }
            set { _LegacyAccountIndex = value; }
        }
        private int _LegacyAccountIndex;

        /// <summary>
        /// 迁移必须在这里做：Session 完成加载后、按新结构重存前（重存会丢掉旧列）。
        /// </summary>
        protected override void OnLoaded()
        {
            base.OnLoaded();

            if (Character != null || LegacyAccountIndex == 0) return;

            Character = LegacyDropMigration.ResolveCharacter(Session, LegacyAccountIndex);
        }

        public ClientFortuneInfo ToClientInfo()
        {
            return new ClientFortuneInfo
            {
                ItemIndex = Item.Index,
                CheckTime = SEnvir.Now - CheckTime,
                DropCount = DropCount,
                Progress = DropProgress,
            };
        }
    }
}
