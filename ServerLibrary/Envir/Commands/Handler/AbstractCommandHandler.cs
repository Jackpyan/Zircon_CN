using Server.DBModels;
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

        /// <summary>
        /// 本处理器是否执行「身份 + GM 模式」校验，以及是否写 GM 命令审计。
        /// 玩家命令（@ROLL 等）必须关掉：它们的 MinIdentity 默认是管理员、
        /// RequireGameMaster 默认是 true，套用会把普通玩家全部拦住。
        /// </summary>
        protected virtual bool EnforcesIdentityRules => true;

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

            if (!string.IsNullOrEmpty(userCommand.ALIAS) &&
                string.Equals(userCommand.ALIAS, input, StringComparison.Ordinal)) return true;

            if (userCommand.ALIASES == null) return false;

            foreach (string alias in userCommand.ALIASES)
            {
                if (!string.IsNullOrEmpty(alias) && string.Equals(alias, input, StringComparison.Ordinal)) return true;
            }

            return false;
        }

        public virtual void Handle(PlayerObject player, string[] input)
        {
            if (IsAllowedByPlayer(player))
            {
                string CommandInput = input[0].ToUpper();

                ICommand command = Commands.Find(userCommand => IsMatch(userCommand, input[0]));
                if (command == null)
                    throw new UserCommandException(string.Format("命令 @{0} 不存在。", CommandInput));

                if (EnforcesIdentityRules)
                {
                    // ---- 身份分级（对齐 A 端 AccountIdentity）：按命令所需最低身份判定 ----
                    Server.DBModels.AccountIdentity actual = player.Character.Account.GetEffectiveIdentity(includeTemp: true);
                    Server.DBModels.AccountIdentity required = command.MinIdentity;

                    if (actual < required)
                    {
                        CommandAudit.Record(player, command.VALUE, input, CommandAudit.Denied,
                            string.Format("需要 {0} 身份（当前 {1}）", required.ToCN(), actual.ToCN()));

                        throw new UserCommandException(string.Format("权限不足：@{0} 需要 {1} 身份。",
                            CommandInput, required.ToCN()));
                    }

                    // ---- GM 模式要求（对齐 A 端：查人类命令免 GM 态，造物/改数据类需要）----
                    if (command.RequireGameMaster && !player.GameMaster)
                    {
                        CommandAudit.Record(player, command.VALUE, input, CommandAudit.Denied, "未开启 GM 模式");

                        throw new UserCommandException(string.Format("@{0} 需要先开启 GM 模式（@GM模式）。", CommandInput));
                    }
                }

                try
                {
                    if (command is AbstractParameterizedCommand<CommandType> parameterized)
                    {
                        parameterized.InvokedAs = input[0];
                        parameterized.Action(player, input);
                    }
                    else if (command is AbstractCommand<CommandType>)
                        (command as AbstractCommand<CommandType>).Action(player);
                }
                catch (UserCommandException exception)
                {
                    CommandAudit.Record(player, command.VALUE, input, CommandAudit.Failed, exception.Message);
                    throw;
                }
                catch (Exception exception)
                {
                    CommandAudit.Record(player, command.VALUE, input, CommandAudit.Failed, exception.Message);
                    throw;
                }

                if (EnforcesIdentityRules)
                    CommandAudit.Record(player, command.VALUE, input, CommandAudit.Success, null);
            }
        }
    }
}
