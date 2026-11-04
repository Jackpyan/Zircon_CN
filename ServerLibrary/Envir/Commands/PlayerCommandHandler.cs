using Server.Envir.Commands.Command.Player;
using Server.Envir.Commands.Handler;
using Server.Models;

namespace Server.Envir.Commands
{
    public class PlayerCommandHandler : AbstractCommandHandler<IPlayerCommand>
    {
        public override bool IsAllowedByPlayer(PlayerObject player)
        {
            return true;
        }

        /// <summary>
        /// 玩家命令不做身份 / GM 模式校验（也不进 GM 审计）。
        /// ICommand 的默认值是「管理员 + 需要 GM 态」，那是给 GM 命令用的；
        /// 玩家命令套用会变成「普通玩家一个 @ 命令都用不了」。
        /// </summary>
        protected override bool EnforcesIdentityRules => false;
    }
}
