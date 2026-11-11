using Library;
using MirDB;

namespace Server.DBModels
{
    [UserObject]
    public sealed class BlockInfo : DBObject
    {
        /// <summary>
        /// 发起屏蔽的角色（2026-11-11 起屏蔽从账号下沉到角色）。
        /// </summary>
        [Association("BlockingList")]
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

        /// <summary>
        /// 被屏蔽的角色（2026-11-11 起屏蔽从账号下沉到角色）。
        /// </summary>
        [Association("BlockedByList")]
        public CharacterInfo BlockedCharacter
        {
            get { return _BlockedCharacter; }
            set
            {
                if (_BlockedCharacter == value) return;

                var oldValue = _BlockedCharacter;
                _BlockedCharacter = value;

                OnChanged(oldValue, value, "BlockedCharacter");
            }
        }
        private CharacterInfo _BlockedCharacter;

        public string BlockedName
        {
            get { return _BlockedName; }
            set
            {
                if (_BlockedName == value) return;

                var oldValue = _BlockedName;
                _BlockedName = value;

                OnChanged(oldValue, value, "BlockedName");
            }
        }
        private string _BlockedName;

        protected override void OnDeleted()
        {
            Character = null;
            BlockedCharacter = null;

            base.OnDeleted();
        }

        public ClientBlockInfo ToClientInfo()
        {
            return new ClientBlockInfo
            {
                Index = Index,
                Name = BlockedName
            };
        }
    }
}
