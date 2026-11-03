using Server.Envir.Commands.Command.Admin;
using Server.Envir.Commands.Handler;
using Server.Models;

namespace Server.Envir.Commands
{
    public class AdminCommandHandler : AbstractCommandHandler<IAdminCommand>
    {
        /// <summary>
        /// 身份分级后的一期门卫：处理器级只要求账号身份 ≥ 监督者
        /// （TempAdmin 主密码登录视为 Admin）。
        /// 具体命令的最低身份（MinIdentity）与 GM 模式要求（RequireGameMaster）
        /// 在 AbstractCommandHandler.Handle 里按命令逐条判定。
        /// </summary>
        public override bool IsAllowedByPlayer(PlayerObject player)
        {
            if (player?.Character?.Account == null) return false;

            return player.Character.Account.GetEffectiveIdentity(includeTemp: true) >= Server.DBModels.AccountIdentity.Supervisor;
        }
    }
}
