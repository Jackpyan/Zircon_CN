using Library;
using Server.Envir.Commands.Command;
using Server.Envir.Commands.Command.Player;
using Server.Models;

namespace Server.Envir.Commands.Player
{
    class ToggleTrade : AbstractCommand<IPlayerCommand>
    {
        public override string VALUE => "ALLOWTRADE";
        public override string ALIAS => "允许交易";

        public override void Action(PlayerObject player)
        {
            player.Character.AllowTrade = !player.Character.AllowTrade;
            player.Connection.ReceiveChat(player.Character.AllowTrade ?
                player.Connection.Language.TradingEnabled : player.Connection.Language.TradingDisabled, MessageType.System);
        }
    }
}
