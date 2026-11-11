using Library;
using MirDB;
using System;

namespace Server.DBModels
{
    [UserObject]
    public sealed class BuffInfo : DBObject
    {
        [Association("Buffs")]
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

        public BuffType Type
        {
            get { return _Type; }
            set
            {
                if (_Type == value) return;

                var oldValue = _Type;
                _Type = value;

                OnChanged(oldValue, value, "Type");
            }
        }
        private BuffType _Type;

        public Stats Stats
        {
            get { return _Stats; }
            set
            {
                if (_Stats == value) return;

                var oldValue = _Stats;
                _Stats = value;

                OnChanged(oldValue, value, "Stats");
            }
        }
        private Stats _Stats;

        public TimeSpan RemainingTime
        {
            get { return _RemainingTime; }
            set
            {
                if (_RemainingTime == value) return;

                var oldValue = _RemainingTime;
                _RemainingTime = value;

                OnChanged(oldValue, value, "RemainingTime");
            }
        }
        private TimeSpan _RemainingTime;

        public TimeSpan TickFrequency
        {
            get { return _TickFrequency; }
            set
            {
                if (_TickFrequency == value) return;

                var oldValue = _TickFrequency;
                _TickFrequency = value;

                OnChanged(oldValue, value, "TickFrequency");
            }
        }
        private TimeSpan _TickFrequency;

        public TimeSpan TickTime
        {
            get { return _TickTime; }
            set
            {
                if (_TickTime == value) return;

                var oldValue = _TickTime;
                _TickTime = value;

                OnChanged(oldValue, value, "TickTime");
            }
        }
        private TimeSpan _TickTime;

        public int ItemIndex
        {
            get { return _ItemIndex; }
            set
            {
                if (_ItemIndex == value) return;

                var oldValue = _ItemIndex;
                _ItemIndex = value;

                OnChanged(oldValue, value, "ItemIndex");
            }
        }
        private int _ItemIndex;


        public bool Visible
        {
            get { return _Visible; }
            set
            {
                if (_Visible == value) return;

                var oldValue = _Visible;
                _Visible = value;

                OnChanged(oldValue, value, "Visible");
            }
        }
        private bool _Visible;

        public bool Pause
        {
            get { return _Pause; }
            set
            {
                if (_Pause == value) return;

                var oldValue = _Pause;
                _Pause = value;

                OnChanged(oldValue, value, "Pause");
            }
        }
        private bool _Pause;

        public bool Hidden
        {
            get { return _Hidden; }
            set
            {
                if (_Hidden == value) return;

                var oldValue = _Hidden;
                _Hidden = value;

                OnChanged(oldValue, value, "Hidden");
            }
        }
        private bool _Hidden;

        public int Extra
        {
            get { return _Extra; }
            set
            {
                if (_Extra == value) return;

                var oldValue = _Extra;
                _Extra = value;

                OnChanged(oldValue, value, "Extra");
            }
        }
        private int _Extra;

        protected override void OnDeleted()
        {
            Character = null;

            base.OnDeleted();
        }


        protected override void OnChanged(object oldValue, object newValue, string propertyName)
        {
            base.OnChanged(oldValue, newValue, propertyName);

            // Buff 现在只挂在角色上（2026-11-11 起从账号下沉到角色），
            // 原来这里是「角色变了要解绑账号 / 账号变了要解绑角色」的交叉解绑，
            // 账号侧字段已删除后没有交叉对象可解绑，只保留基类调用。
        }


        public ClientBuffInfo ToClientInfo()
        {
            return new ClientBuffInfo
            {
                Index = Index,
                RemainingTime = RemainingTime,
                TickFrequency = TickFrequency,
                Type = Type,
                Pause = Pause,
                Stats = Stats,
                ItemIndex = ItemIndex,
                Extra = Extra
            };
        }
    }
}
