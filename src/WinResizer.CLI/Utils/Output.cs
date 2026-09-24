using System.Collections.Generic;
using Spectre.Console;

namespace WinResizer.CLI.Utils
{
    public static class Output
    {
        public static void Echo(string str)
        {
            AnsiConsole.MarkupLineInterpolated($"{str}");
        }

        public static void Error(string str)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{str}[/]");
        }
    }
}
