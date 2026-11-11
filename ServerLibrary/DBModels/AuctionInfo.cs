using Library;
using MirDB;
using System;

namespace Server.DBModels
{
    [UserObject]
    public sealed class AuctionInfo : DBObject
    {
        [Association("Auction")]
        public UserItem Item
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
        private UserItem _Item;

        /// <summary>
        /// 上架角色（2026-11-11 起拍卖从账号下沉到角色）。
        /// </summary>
        [Association("Auctions")]
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

        public DateTime ConsignDate
        {
            get { return _ConsignDate; }
            set
            {
                if (_ConsignDate == value) return;

                var oldValue = _ConsignDate;
                _ConsignDate = value;

                OnChanged(oldValue, value, "ConsignDate");
            }
        }
        private DateTime _ConsignDate;

        public string Message
        {
            get { return _Message; }
            set
            {
                if (_Message == value) return;

                var oldValue = _Message;
                _Message = value;

                OnChanged(oldValue, value, "Message");
            }
        }
        private string _Message;

        protected override void OnDeleted()
        {
            Item = null;
            Character = null;

            base.OnDeleted();
        }


        public ClientMarketPlaceInfo ToClientInfo(CharacterInfo character)
        {
            return new ClientMarketPlaceInfo
            {
                Index = Index,

                Item = Item?.ToClientInfo(),

                Seller = Character?.CharacterName,

                Message = Message,

                IsOwner = character == Character,

                Price = Price,
                ConsignDate = ConsignDate,
            };
        }



        public override string ToString()
        {
            return Character?.Account?.ToString() ?? string.Empty;
        }
    }
}
