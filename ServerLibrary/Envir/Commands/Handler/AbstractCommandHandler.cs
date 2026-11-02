using Server.Envir.Commands.Command;
using Server.Envir.Commands.Exceptions;
using Server.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Server.Envir.Commands.Handler
{
    public abstract class AbstractCommandHandler<CommandType> : IValidatingCommandHandler
        where CommandType : ICommand
    {

        public readonly List<ICommand> Commands = new List<ICommand>();

        public AbstractCommandHandler()
        {
            this.Commands = Assembly.GetAssembly(typeof(CommandType)).GetTypes()
                .Where(type => type.IsClass)
                .Where(type => !type.IsAbstract)
                .Where(type => type.IsSubclassOf(typeof(AbstractCommand<CommandType>)) || type.IsSubclassOf(typeof(AbstractParameterizedCommand<CommandType>)))
                .Select(type => (ICommand)Activator.CreateInstance(type))
                .ToList();
        }

        public virtual bool IsAllowedByPlayer(PlayerObject player)
        {
            return false;
        }

        /// <summary>命令是否存在：英文命令名或中文别名都算。</summary>
        public virtual bool CommandExists(string command)
        {
            return Commands.Exists(userCommand => IsMatch(userCommand, command));
        }

        /// <summary>英文名忽略大小写匹配，中文别名精确匹配。</summary>
        protected static bool IsMatch(ICommand userCommand, string input)
        {
            if (userCommand == null || string.IsNullOrEmpty(input)) return false;

            if (string.Equals(userCommand.VALUE, input, StringComparison.OrdinalIgnoreCase)) return true;

            return !string.IsNullOrEmpty(userCommand.ALIAS)
                   && string.Equals(userCommand.ALIAS, input, StringComparison.Ordinal);
        }

        public virtual void Handle(PlayerObject player, string[] input)
        {
            if (IsAllowedByPlayer(player))
            {
                string CommandInput = input[0].ToUpper();

                ICommand command = Commands.Find(userCommand => IsMatch(userCommand, input[0]));
                if (command == null)
                    throw new UserCommandException(string.Format("命令 @{0} 不存在。", CommandInput));

                if (command is AbstractParameterizedCommand<CommandType> parameterized)
                {
                    parameterized.InvokedAs = input[0];
                    parameterized.Action(player, input);
                }
                else if (command is AbstractCommand<CommandType>)
                    (command as AbstractCommand<CommandType>).Action(player);

            }
        }
    }
}
