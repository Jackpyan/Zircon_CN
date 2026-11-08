using System.Linq;
using MirDB;

namespace Server.DBModels
{
    /// <summary>
    /// 保底账本（用户掉落 / 幸运查探）从「按账号」改为「按角色」的一次性迁移。
    /// 旧库里这两张表的宿主列叫 Account（账号索引），新结构改成 Character（角色索引）。
    /// MirDB 按新结构重存时会丢弃旧列，所以换算只能发生在 Session 加载完成之后、
    /// 自动重存之前，也就是实体的 OnLoaded 里。
    /// 归属规则：优先该账号的「上次登录角色」，其次等级最高的未删除角色；
    /// 账号下只剩已删除角色时仍归给等级最高的那个，宁可页面上看到个死角色，也不让旧进度变成孤立行。
    /// </summary>
    internal static class LegacyDropMigration
    {
        internal static CharacterInfo ResolveCharacter(Session session, int accountIndex)
        {
            if (session == null || accountIndex <= 0) return null;

            AccountInfo account = session.GetCollection<AccountInfo>().Binding.FirstOrDefault(x => x.Index == accountIndex);

            if (account == null) return null;

            CharacterInfo last = account.LastCharacter;

            if (last != null && !last.Deleted) return last;

            CharacterInfo alive = account.Characters
                .Where(x => !x.Deleted)
                .OrderByDescending(x => x.Level)
                .ThenBy(x => x.Index)
                .FirstOrDefault();

            if (alive != null) return alive;

            return account.Characters
                .OrderByDescending(x => x.Level)
                .ThenBy(x => x.Index)
                .FirstOrDefault();
        }
    }
}
