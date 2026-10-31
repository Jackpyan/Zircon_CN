using Server.Models;

namespace Server.Envir.Commands.Command.Admin
{
    class ClearIPBlocks : AbstractCommand<IAdminCommand>
    {
        public override string VALUE => "CLEARIPBLOCKS";
        public override string ALIAS => "清IP封禁";

        public override void Action(PlayerObject player)
        {
            SEnvir.IPBlocks.Clear();
        }
    }
}
