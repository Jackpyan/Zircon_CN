using Server.Models;

namespace Server.Envir.Commands.Command.Player
{
    class LeaveGuild : AbstractCommand<IPlayerCommand>
    {
        public override string VALUE => "LEAVEGUILD";
        public override string ALIAS => "退会";

        public override void Action(PlayerObject player)
        {
            player.GuildLeave();
        }
    }
}
