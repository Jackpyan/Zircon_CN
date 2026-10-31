namespace Server.Envir.Commands.Player
{
    class ToggleCompanionLock10 : AbstractToggleCompanion
    {
        public override string VALUE => string.Format(base.VALUE, 10);
        public override string ALIAS => "开启等级10";
    }
}
