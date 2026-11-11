using Library.SystemModels;
using MirDB;

namespace Server.DBModels
{
    [UserObject]
    public sealed class GameStoreFavourite : DBObject
    {
        /// <summary>
        /// 收藏角色（2026-11-11 起商城收藏从账号下沉到角色）。
        /// </summary>
        [Association("StoreFavourites")]
        public CharacterInfo Character
        {
            get => _Character;
            set
            {
                if (_Character == value) return;
                CharacterInfo oldValue = _Character;
                _Character = value;
                OnChanged(oldValue, value, "Character");
            }
        }
        private CharacterInfo _Character;

        public StoreInfo StoreInfo
        {
            get => _StoreInfo;
            set
            {
                if (_StoreInfo == value) return;
                StoreInfo oldValue = _StoreInfo;
                _StoreInfo = value;
                OnChanged(oldValue, value, "StoreInfo");
            }
        }
        private StoreInfo _StoreInfo;

        protected override void OnDeleted()
        {
            Character = null;
            StoreInfo = null;
            base.OnDeleted();
        }
    }
}
