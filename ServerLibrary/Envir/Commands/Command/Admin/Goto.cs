using Server.Envir.Commands.Exceptions;
using Server.Models;

namespace Server.Envir.Commands.Command.Admin
{
    class Goto : AbstractParameterizedCommand<IAdminCommand>
    {
        public override string VALUE => "GOTO";
        public override Server.DBModels.AccountIdentity MinIdentity => Server.DBModels.AccountIdentity.Supervisor;
        public override bool RequireGameMaster => false;   // 查人/系统维护类，免 GM 态（对齐 A 端）
        public override string ALIAS => "去";
        public override int PARAMS_LENGTH => 2;

        public override void Action(PlayerObject player, string[] vals)
        {
            if (vals.Length < PARAMS_LENGTH)
                ThrowNewInvalidParametersException();

            PlayerObject target = SEnvir.GetPlayerByCharacter(vals[1]);

            if (target == null)
                throw new UserCommandException(string.Format("找不到玩家（需在线）：{0}", vals[1]));

            // 把使用者（自己）传送到目标所在位置
            player.Teleport(target.CurrentMap, target.CurrentLocation);
        }
    }
}
