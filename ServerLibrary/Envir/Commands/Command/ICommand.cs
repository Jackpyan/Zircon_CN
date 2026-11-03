namespace Server.Envir.Commands.Command
{
    public interface ICommand
    {
        /// <summary>英文命令名（输入 @VALUE）</summary>
        string VALUE { get; }

        /// <summary>中文别名（输入 @中文名），不填则只能使用英文命令</summary>
        string ALIAS => null;

        /// <summary>额外的中文别名（同一功能支持多种写法）</summary>
        string[] ALIASES => null;

        /// <summary>使用本命令所需的最低身份（接口默认：管理员；各命令可 override）</summary>
        Server.DBModels.AccountIdentity MinIdentity => Server.DBModels.AccountIdentity.Admin;

        /// <summary>是否必须处于 GM 模式（对齐 A 端：查人类命令可免，默认需要）</summary>
        bool RequireGameMaster => true;
    }
}
