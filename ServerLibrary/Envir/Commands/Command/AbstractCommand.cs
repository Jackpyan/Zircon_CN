using Server.Models;

namespace Server.Envir.Commands.Command
{
    public abstract class AbstractCommand<CommandType> : ICommand
        where CommandType : ICommand
    {
        public abstract string VALUE { get; }
        public virtual string ALIAS => null;
        public virtual string[] ALIASES => null;
        public abstract void Action(PlayerObject player);
    }
}
