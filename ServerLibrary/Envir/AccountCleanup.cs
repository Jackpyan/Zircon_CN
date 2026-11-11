using Server.DBModels;
using System.Linq;

namespace Server.Envir;

/// <summary>
/// 清理统计（假人清理的 dry-run 预览与执行结果共用）。
/// </summary>
public sealed class BotCleanupStats
{
    /// <summary>被处理的账号数。</summary>
    public int Accounts { get; set; }

    /// <summary>被处理的角色数。</summary>
    public int Characters { get; set; }

    /// <summary>背包 + 仓库物品总数。</summary>
    public int Items { get; set; }

    /// <summary>角色级货币记录数。</summary>
    public int Currencies { get; set; }

    /// <summary>宠物名册数。</summary>
    public int Companions { get; set; }

    /// <summary>邮件数。</summary>
    public int Mails { get; set; }

    /// <summary>寄售数。</summary>
    public int Auctions { get; set; }

    /// <summary>任务数。</summary>
    public int Quests { get; set; }

    /// <summary>仍在在线、暂时删不掉的账号数（调用方需要先让它们下线）。</summary>
    public int Online { get; set; }

    public static BotCleanupStats Empty => new();

    /// <summary>一句话摘要，给后台提示用。</summary>
    public string Summary =>
        $"账号 {Accounts} 个、角色 {Characters} 个（物品 {Items}、货币记录 {Currencies}、" +
        $"宠物 {Companions}、邮件 {Mails}、寄售 {Auctions}、任务 {Quests}）";
}

/// <summary>
/// 账号清理（2026-11-11）。
/// <para>
/// 放在 ServerLibrary 而不是后台服务里，是为了让「删一个账号要按什么顺序删」这条规则只有一份实现 ——
/// 阶段 C 把 10 类数据下沉到角色级之后，删除顺序只要有一处不对就会留下孤儿行。
/// 后台（Web 层）只负责审计、存库与界面。
/// </para>
/// </summary>
public static class AccountCleanup
{
    /// <summary>
    /// 删除一个账号的完整清理（不含存库与审计）。
    /// <para>
    /// 账号自身的集合没有 Aggregate 级联，必须按依赖顺序逐个删除，否则会留下孤儿行；
    /// 角色子集合（Items/Magics/Buffs/Quests…）是 Aggregate，删角色即级联清理。
    /// </para>
    /// </summary>
    public static void DeleteAccount(AccountInfo account)
    {
        if (account == null) return;

        // 1) 解除外部引用：以本账号为推荐人的账号、屏蔽本账号的记录、行会成员记录
        foreach (AccountInfo child in account.Referrals.ToList())
            child.Referral = null;

        foreach (GuildMemberInfo member in SEnvir.GuildMemberInfoList.Binding
                     .Where(x => x.Account == account).ToList())
            member.Delete();

        // 2) 角色（其 Items/Magics/Buffs/Quests 等是 Aggregate，会级联删除）
        foreach (CharacterInfo character in account.Characters.ToList())
        {
            // 角色级货币（Currencies）不是 Aggregate，不会随角色级联删除，要单独删。
            foreach (UserCurrency currency in character.Currencies.ToList()) currency.Delete();

            character.Delete();
        }

        // 3) 账号自有集合（无级联，逐个删）
        foreach (UserCurrency currency in account.Currencies.ToList()) currency.Delete();

        // Buff / 屏蔽名单 / 商城记录 / 邮件 / 寄售 / 保底账本 / 宠物名册 / 宠物解锁 / 任务
        // 都挂在角色上（Aggregate），删角色时已随之级联清理，这里不用重复删。
        foreach (GameGoldPayment payment in account.Payments.ToList()) payment.Delete();

        // 4) 最后删账号
        account.Delete();
    }

    /// <summary>
    /// 统计假人账号将要被清掉多少东西（dry-run 预览，不做任何删除）。
    /// </summary>
    public static BotCleanupStats PreviewBots()
    {
        BotCleanupStats stats = BotCleanupStats.Empty;

        foreach (AccountInfo account in SEnvir.AccountInfoList.Binding.Where(x => x.IsBot))
        {
            if (account.Connection != null)
            {
                stats.Online++;
                continue;
            }

            stats.Accounts++;
            stats.Characters += account.Characters.Count;

            foreach (CharacterInfo character in account.Characters)
            {
                stats.Items += character.Items.Count + character.Storage.Count;
                stats.Currencies += character.Currencies.Count;
                stats.Companions += character.Companions.Count;
                stats.Mails += character.Mail.Count;
                stats.Auctions += character.Auctions.Count;
                stats.Quests += character.Quests.Count;
            }
        }

        return stats;
    }

    /// <summary>
    /// 删除所有假人账号（严格限定 <c>IsBot == true</c>，真人账号一个都不碰）。
    /// <para>
    /// 正在线上的假人账号跳过（<c>Connection != null</c> 删不掉），调用方需要先让它们下线。
    /// </para>
    /// </summary>
    public static BotCleanupStats DeleteBots()
    {
        BotCleanupStats stats = BotCleanupStats.Empty;

        foreach (AccountInfo account in SEnvir.AccountInfoList.Binding
                     .Where(x => x.IsBot && x.Connection == null).ToList())
        {
            stats.Accounts++;
            stats.Characters += account.Characters.Count;

            DeleteAccount(account);
        }

        return stats;
    }
}
