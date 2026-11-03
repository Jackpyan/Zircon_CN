using Server.Models;

namespace Server.Envir.Commands.Command.Admin
{
    class ClearIPBlocks : AbstractCommand<IAdminCommand>
    {
        public override string VALUE => "CLEARIPBLOCKS";
        public override bool RequireGameMaster => false;   // 查人/系统维护类，免 GM 态（对齐 A 端）
        public override string ALIAS => "清IP封禁";

        public override void Action(PlayerObject player)
        {
            SEnvir.IPBlocks.Clear();
        }
    }
}
