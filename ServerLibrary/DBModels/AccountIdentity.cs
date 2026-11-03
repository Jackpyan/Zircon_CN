using System.ComponentModel;

namespace Server.DBModels
{
    /// <summary>
    /// 账号管理身份（对齐 A 端 AccountIdentity，去掉 SuperAdmin 档：
    /// B 端主密码登录的 TempAdmin 在判定时直接视为 Admin，即最高档）。
    /// 数值越大权限越高，高级包含低级全部能力。
    /// </summary>
    public enum AccountIdentity
    {
        /// <summary>普通玩家：只能使用玩家命令（@ROLL 等）。</summary>
        [Description("普通玩家")] Normal = 0,
        /// <summary>监督者：查人/传人/封号等低危命令（对齐 A 端 Supervisor）。</summary>
        [Description("监督者")] Supervisor = 1,
        /// <summary>运营者：+ 刷怪/清怪/踢人/改轻度角色数据（对齐 A 端 Operator）。</summary>
        [Description("运营者")] Operator = 2,
        /// <summary>管理员：+ 制造/改等级/发元宝/系统级命令（对齐 A 端 Admin）。</summary>
        [Description("管理员")] Admin = 3,
    }

    public static class AccountIdentityExtensions
    {
        /// <summary>身份的中文名（用于命令提示与审计）。</summary>
        public static string ToCN(this AccountIdentity identity) => identity switch
        {
            AccountIdentity.Normal => "普通玩家",
            AccountIdentity.Supervisor => "监督者",
            AccountIdentity.Operator => "运营者",
            AccountIdentity.Admin => "管理员",
            _ => identity.ToString(),
        };
    }
}
