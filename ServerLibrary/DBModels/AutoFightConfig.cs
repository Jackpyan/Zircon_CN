using Library;
using MirDB;

namespace Server.DBModels
{
    /// <summary>
    /// 辅助功能（自动战斗）单项配置的持久化记录。
    /// 移植自 A 端 Server\DBModels\AutoFightConfig.cs。
    ///
    /// 注意：挂机总开关（<see cref="AutoSetConf.SetAutoOnHookBox"/>）**不写库**，
    /// 只存在于 <c>PlayerObject.setConfArr[16]</c>，避免重登后自动继续挂机。
    /// </summary>
    [UserObject]
    public class AutoFightConfig : DBObject
    {
        [Association("AutoFightLinks")]
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

        public AutoSetConf Slot
        {
            get { return _Slot; }
            set
            {
                if (_Slot == value) return;

                var oldValue = _Slot;
                _Slot = value;

                OnChanged(oldValue, value, "Slot");
            }
        }
        private AutoSetConf _Slot;

        public MagicType MagicIndex
        {
            get { return _MagicIndex; }
            set
            {
                if (_MagicIndex == value) return;

                var oldValue = _MagicIndex;
                _MagicIndex = value;

                OnChanged(oldValue, value, "MagicIndex");
            }
        }
        private MagicType _MagicIndex;

        public int TimeCount
        {
            get { return _TimeCount; }
            set
            {
                if (_TimeCount == value) return;

                var oldValue = _TimeCount;
                _TimeCount = value;

                OnChanged(oldValue, value, "TimeCount");
            }
        }
        private int _TimeCount;

        public bool Enabled
        {
            get { return _Enabled; }
            set
            {
                if (_Enabled == value) return;

                var oldValue = _Enabled;
                _Enabled = value;

                OnChanged(oldValue, value, "Enabled");
            }
        }
        private bool _Enabled;

        protected override void OnDeleted()
        {
            Character = null;

            base.OnDeleted();
        }

        public ClientAutoFightLink ToClientInfo()
        {
            return new ClientAutoFightLink
            {
                Slot = Slot,
                MagicIndex = MagicIndex,
                TimeCount = TimeCount,
                Enabled = Enabled,
            };
        }
    }
}
