namespace Server.Envir.Commands.Command
{
    public interface ICommand
    {
        /// <summary>英文命令名（输入 @VALUE）</summary>
        string VALUE { get; }

        /// <summary>中文别名（输入 @中文名），不填则只能使用英文命令</summary>
        string ALIAS => null;
    }
}
