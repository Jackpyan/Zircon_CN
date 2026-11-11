using Library;
using MirDB;
using Server.Envir;
using System;
using System.Linq;

namespace Server.DBModels
{
    [UserObject]
    public sealed class MailInfo : DBObject
    {
        /// <summary>
        /// 收件角色（2026-11-11 起邮件从账号下沉到角色）。
        /// <para>
        /// 之前邮件挂在账号上，同账号 4 个角色都能收到同一封信；现在只有收件角色能收到。
        /// </para>
        /// </summary>
        [Association("Mail")]
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

        public string Sender
        {
            get { return _Sender; }
            set
            {
                if (_Sender == value) return;

                var oldValue = _Sender;
                _Sender = value;

                OnChanged(oldValue, value, "Sender");
            }
        }
        private string _Sender;


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

        public string Subject
        {
            get { return _Subject; }
            set
            {
                if (_Subject == value) return;

                var oldValue = _Subject;
                _Subject = value;

                OnChanged(oldValue, value, "Subject");
            }
        }
        private string _Subject;

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

        public bool Opened
        {
            get { return _Opened; }
            set
            {
                if (_Opened == value) return;

                var oldValue = _Opened;
                _Opened = value;

                OnChanged(oldValue, value, "Opened");
            }
        }
        private bool _Opened;

        public bool HasItem
        {
            get { return _HasItem; }
            set
            {
                if (_HasItem == value) return;

                var oldValue = _HasItem;
                _HasItem = value;

                OnChanged(oldValue, value, "HasItem");
            }
        }
        private bool _HasItem;


        [Association("Mail")]
        public DBBindingList<UserItem> Items { get; set; }

        protected override void OnDeleted()
        {
            Character = null;

            base.OnDeleted();
        }

        protected override void OnCreated()
        {
            base.OnCreated();

            Date = SEnvir.Now;
        }

        public ClientMailInfo ToClientInfo()
        {
            return new ClientMailInfo
            {
                Index = Index,
                Subject = Subject,
                Sender = Sender,
                Date = Date,
                Message = Message,
                HasItem = HasItem,
                Opened = Opened,
                Items = Items.Select(x => x.ToClientInfo()).ToList()
            };
        }

        public override string ToString()
        {
            return Character?.Account?.EMailAddress ?? string.Empty;
        }
    }
}
