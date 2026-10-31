namespace Server.Envir.Commands.Player
{
    class ToggleCompanionLock5 : AbstractToggleCompanion
    {
        public override string VALUE => string.Format(base.VALUE, 5);
        public override string ALIAS => "开启等级5";
    }
}
