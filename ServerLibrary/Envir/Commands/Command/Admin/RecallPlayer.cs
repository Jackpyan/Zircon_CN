using Library;
using Server.Models;

namespace Server.Envir.Commands.Command.Admin
{
    class RecallPlayer : AbstractParameterizedCommand<IAdminCommand>
    {
        public override string VALUE => "RECALL";
        public override Server.DBModels.AccountIdentity MinIdentity => Server.DBModels.AccountIdentity.Supervisor;
        public override bool RequireGameMaster => false;   // 查人/系统维护类，免 GM 态（对齐 A 端）
        public override string ALIAS => "召唤";
        public override int PARAMS_LENGTH => 2;

        public override void Action(PlayerObject player, string[] vals)
        {
            if (vals.Length < PARAMS_LENGTH)
                ThrowNewInvalidParametersException();

            PlayerObject targetPlayer = SEnvir.GetPlayerByCharacter(vals[1]);
            targetPlayer?.Teleport(player.CurrentMap, Functions.Move(player.CurrentLocation, player.Direction));
        }
    }
}
