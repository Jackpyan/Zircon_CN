using Library.SystemModels;
using MirDB;

namespace Server.DBModels
{
    [UserObject]
    public sealed class UserCompanionUnlock : DBObject
    {
        /// <summary>
        /// 外观解锁的归属角色（2026-11-11 起宠物解锁从账号下沉到角色）。
        /// </summary>
        [Association("CompanionUnlocks")]
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

        public CompanionInfo CompanionInfo
        {
            get { return _CompanionInfo; }
            set
            {
                if (_CompanionInfo == value) return;

                var oldValue = _CompanionInfo;
                _CompanionInfo = value;

                OnChanged(oldValue, value, "CompanionInfo");
            }
        }
        private CompanionInfo _CompanionInfo;

        protected override void OnDeleted()
        {
            Character = null;
            CompanionInfo = null;

            base.OnDeleted();
        }

    }
}
