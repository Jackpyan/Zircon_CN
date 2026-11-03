using Library;
using Server.Models;

namespace Server.Envir.Commands.Command.Admin
{
    /// <summary>
    /// Toggles the player being targetted by players or monsters
    /// </summary>
    class ToggleGameMaster : AbstractCommand<IAdminCommand>
    {
        public override string VALUE => "GAMEMASTER";
        public override Server.DBModels.AccountIdentity MinIdentity => Server.DBModels.AccountIdentity.Supervisor;
        public override bool RequireGameMaster => false;   // 查人/系统维护类，免 GM 态（对齐 A 端）
        public override string ALIAS => "GM模式";

        public override void Action(PlayerObject player)
        {
            player.GameMaster = !player.GameMaster;
            player.Connection.ReceiveChat($"{VALUE} {(player.GameMaster ? "已启用" : "已禁用")}", MessageType.System);
        }
    }
}
