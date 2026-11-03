using Server.Models;

namespace Server.Envir.Commands.Command
{
    public abstract class AbstractCommand<CommandType> : ICommand
        where CommandType : ICommand
    {
        public abstract string VALUE { get; }
        public virtual string ALIAS => null;
        public virtual string[] ALIASES => null;

        /// <summary>使用本命令所需的最低身份（默认管理员，各命令可 override）</summary>
        public virtual Server.DBModels.AccountIdentity MinIdentity => Server.DBModels.AccountIdentity.Admin;

        /// <summary>是否必须处于 GM 模式（对齐 A 端：查人类命令可免，默认需要）</summary>
        public virtual bool RequireGameMaster => true;
        public abstract void Action(PlayerObject player);
    }
}
