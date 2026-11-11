using Library;
using Library.SystemModels;
using MirDB;
using Server.Envir;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Server.DBModels
{
    [UserObject]
    public sealed class UserCurrency : DBObject
    {
        public CurrencyInfo Info
        {
            get { return _Info; }
            set
            {
                if (_Info == value) return;

                var oldValue = _Info;
                _Info = value;

                OnChanged(oldValue, value, "Info");
            }
        }
        private CurrencyInfo _Info;

        public long Amount
        {
            get { return _Amount; }
            set
            {
                if (_Amount == value) return;

                var oldValue = _Amount;
                _Amount = value;

                OnChanged(oldValue, value, "Amount");
            }
        }
        private long _Amount;

        [Association("Currencies")]
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

        /// <summary>
        /// 角色级货币的宿主（2026-11-11 起金币 / 狩猎币下沉到角色）。
        /// <para>
        /// 充值元宝 <see cref="Library.CurrencyType.GameGold"/> 仍挂 <see cref="Account"/>，
        /// 其余币种挂本属性。两者共用 identity "Currencies"，靠父类型不同区分。
        /// </para>
        /// </summary>
        [Association("Currencies")]
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

        protected override void OnDeleted()
        {
            Info = null;

            Account = null;
            Character = null;

            base.OnDeleted();
        }

        /// <summary>
        /// 老数据迁移（2026-11-11）：金币 / 狩猎币 / 声望点 / 贡献点原本挂在**账号**下，
        /// 现在下沉到**角色**。老库里这些记录的 <see cref="Character"/> 为空、
        /// <see cref="Account"/> 有值 —— 这里给该账号下的每个角色各发一份原余额。
        /// </summary>
        /// <remarks>
        /// 迁移规则由用户拍板：**每个角色各发一份原余额**（老账号总资产因此翻倍，
        /// 但玩家不会觉得钱丢了）。
        /// <para>
        /// 几个必须注意的点：
        /// </para>
        /// <list type="bullet">
        /// <item>迁移必须在 Session 加载完成之后、MirDB 自动重存之前发生 → 放在 <c>OnLoaded</c>。</item>
        /// <item>各集合 <c>OnLoaded</c> 的执行顺序不确定 → 迁移只依赖 <c>Account.Characters</c>
        /// （此时父子链已由 <c>ConsumeKeys</c> 建好），且对「角色已有空壳货币」做**累加**而非新建，
        /// 所以无论谁先跑都不会重复计数。</item>
        /// <item>不能在 <c>OnLoaded</c> 里调 <c>Delete()</c>：集合正在 <c>foreach</c> 遍历，
        /// 删元素会抛 <c>InvalidOperationException</c>。用 <c>FastDelete()</c>，
        /// 它只打「临时」标记，保存时自然跳过（<c>DBCollection.SaveObjects</c> 里 <c>IsTemporary</c> 会被 continue）。</item>
        /// </list>
        /// </remarks>
        protected override void OnLoaded()
        {
            base.OnLoaded();

            // 已经是角色级的，或充值元宝（账号级），或孤儿记录 —— 都不用管。
            if (Character != null) return;
            if (Account == null) return;
            if (Info == null) return;
            if (Info.Type == CurrencyType.GameGold) return;

            try
            {
                foreach (CharacterInfo character in MigrationTargets())
                {
                    if (character == null) continue;

                    UserCurrency target = character.Currencies.FirstOrDefault(x => x.Info == Info);

                    if (target == null)
                    {
                        target = Session.GetCollection<UserCurrency>().CreateNewObject();
                        target.Character = character;
                        target.Info = Info;
                        target.Amount = Amount;
                    }
                    else
                    {
                        // 角色已经补了空壳（CharacterInfo.OnLoaded 先跑的情况）→ 累加。
                        target.Amount += Amount;
                    }
                }

                // 账号下不再保留金币 / 狩猎币，原记录退役。
                FastDelete();
            }
            catch (Exception ex)
            {
                // 单个账号迁移失败不能拖垮整库加载。
                string mail = Account?.EMailAddress ?? "（账号已释放）";
                string name = Info?.Name ?? "（币种已释放）";

                SEnvir.Log($"[货币迁移] 账号 {mail} 的 {name} 迁移失败：{ex.Message}");
            }
        }

        /// <summary>迁移目标：该账号下所有未删除的角色；一个都没有时退回等级最高的那个（哪怕已删除）。</summary>
        private List<CharacterInfo> MigrationTargets()
        {
            List<CharacterInfo> targets = Account.Characters.Where(x => x != null && !x.Deleted).ToList();

            if (targets.Count > 0) return targets;

            CharacterInfo fallback = Account.Characters
                .Where(x => x != null)
                .OrderByDescending(x => x.Level)
                .ThenBy(x => x.Index)
                .FirstOrDefault();

            if (fallback != null) targets.Add(fallback);

            return targets;
        }

        public ClientUserCurrency ToClientInfo(bool hideAmount = false)
        {
            return new ClientUserCurrency
            {
                CurrencyIndex = Info.Index,
                Amount = hideAmount ? 0 : Amount,
            };
        }
    }
}
