using Library;
using Server.Models;
using System;

namespace Server.Envir.Commands.Command.Admin
{
    class Reboot : AbstractCommand<IAdminCommand>
    {
        public override string VALUE => "REBOOT";
        public override bool RequireGameMaster => false;   // 查人/系统维护类，免 GM 态（对齐 A 端）
        public override string ALIAS => "重启";

        public override void Action(PlayerObject player)
        {
            DateTime time = Time.Now;
            player.MarketPlaceCancelSuperior();
            player.Connection.ReceiveChat($"[重启命令] {(Time.Now - time).Ticks / TimeSpan.TicksPerMillisecond}ms", MessageType.System);
        }
    }
}
