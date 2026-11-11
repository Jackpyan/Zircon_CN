using Library.SystemModels;
using MirDB;
using Server.Envir;
using System;

namespace Server.DBModels
{
    [UserObject]
    public sealed class GameStoreSale : DBObject
    {
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

        public DateTime Date
        {
            get { return _Date; }
            set
            {
                if (_Date == value) return;

                var oldValue = _Date;
                _Date = value;

                OnChanged(oldValue, value, "Date");
            }
        }
        private DateTime _Date;

        public int Price
        {
            get { return _Price; }
            set
            {
                if (_Price == value) return;

                var oldValue = _Price;
                _Price = value;

                OnChanged(oldValue, value, "Price");
            }
        }
        private int _Price;

        public long Count
        {
            get { return _Count; }
            set
            {
                if (_Count == value) return;

                var oldValue = _Count;
                _Count = value;

                OnChanged(oldValue, value, "Count");
            }
        }
        private long _Count;

        /// <summary>
        /// 购买角色（2026-11-11 起商城记录从账号下沉到角色）。
        /// </summary>
        [Association("StoreSales")]
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

        public bool HuntGold
        {
            get { return _HuntGold; }
            set
            {
                if (_HuntGold == value) return;

                var oldValue = _HuntGold;
                _HuntGold = value;

                OnChanged(oldValue, value, "HuntGold");
            }
        }
        private bool _HuntGold;

        protected override void OnDeleted()
        {
            Character = null;
            Item = null;

            base.OnDeleted();
        }

        protected override void OnCreated()
        {
            base.OnCreated();

            Date = SEnvir.Now;
        }



    }
}
