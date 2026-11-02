using Server.Envir.Commands.Exceptions;
using Server.Models;

namespace Server.Envir.Commands.Command
{
    public abstract class AbstractParameterizedCommand<CommandType> : IParameterizedCommand
        where CommandType : ICommand
    {
        public abstract string VALUE { get; }
        public virtual string ALIAS => null;
        public abstract int PARAMS_LENGTH { get; }

        public abstract void Action(PlayerObject player, string[] vals);

        /// <summary>玩家本次实际输入的命令名（中文或英文），用于错误提示。</summary>
        public string InvokedAs { get; set; }

        public UserCommandException ThrowNewInvalidParametersException()
        {
            string name = string.IsNullOrWhiteSpace(InvokedAs) ? VALUE : InvokedAs;
            throw new UserCommandException(string.Format("命令 @{0} 的参数无效", name));
        }
    }
}
