using Server.Envir.Commands.Command.Admin;
using Server.Envir.Commands.Handler;
using Server.Models;

namespace Server.Envir.Commands
{
    public class AdminCommandHandler : AbstractCommandHandler<IAdminCommand>
    {
        /// <summary>
        /// 管理命令需要两个条件同时成立：
        /// 1) 账号具备管理权限（DB 里的 Admin，或主密码登录的 TempAdmin）；
        /// 2) 当前处于 GM 模式（GameMaster）。
        ///
        /// 邮箱 + 主密码登录的管理员默认不是 GM 模式，头顶与普通玩家一致，
        /// 必须在游戏内输入 //gamemaster/ 开启后才可使用任何 GM 命令。
        /// </summary>
        public override bool IsAllowedByPlayer(PlayerObject player)
        {
            if (player?.Character?.Account == null) return false;

            Server.DBModels.AccountInfo account = player.Character.Account;

            if (!account.Admin && !account.TempAdmin) return false;

            return player.GameMaster;
        }
    }
}
