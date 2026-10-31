namespace Server.Envir.Commands.Player
{
    class ToggleCompanionLock3 : AbstractToggleCompanion
    {
        public override string VALUE => string.Format(base.VALUE, 3);
        public override string ALIAS => "开启等级3";
    }
}
