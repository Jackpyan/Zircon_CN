using Library;
using Server.Models;

namespace Server.Envir.Commands.Command.Player
{
    class ToggleGroupRecall : AbstractCommand<IPlayerCommand>
    {
        public override string VALUE => "ALLOWRECALL";
        public override string ALIAS => "允许召唤";

        public override void Action(PlayerObject player)
        {
            player.Character.AllowGroupRecall = !player.Character.AllowGroupRecall;
            player.Connection.ReceiveChatWithObservers(con => player.Character.AllowGroupRecall ? con.Language.GroupRecallEnabled : con.Language.GroupRecallDisabled, MessageType.System);
        }
    }
}
