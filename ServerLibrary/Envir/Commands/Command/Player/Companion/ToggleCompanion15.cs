namespace Server.Envir.Commands.Player
{
    class ToggleCompanionLock15 : AbstractToggleCompanion
    {
        public override string VALUE => string.Format(base.VALUE, 15);
        public override string ALIAS => "开启等级15";
    }
}
