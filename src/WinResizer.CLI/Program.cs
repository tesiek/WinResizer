using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Help;
using System.CommandLine.Parsing;
using System.Linq;
using System.Threading.Tasks;
using Spectre.Console;
using WinResizer.CLI.Commands;
using WinResizer.CLI.Utils;

namespace WinResizer.CLI
{
    internal static class Program
    {
        static Task<int> Main(string[] args)
        {
            System.Console.OutputEncoding = System.Text.Encoding.UTF8;

            var rootCommand = new RootCommand($"{nameof(WinResizer)} CLI.");
            rootCommand.AddCommand(new ResizeCommand());

            var parser = new CommandLineBuilder(rootCommand)
                         .UseDefaults()
                         .UseExceptionHandler((e, _) =>
                         {
                             Output.Error(e.Message);
                         }, 1)
                         .UseHelp(ctx =>
                         {
                             ctx.HelpBuilder.CustomizeLayout(
                                 c =>
                                     HelpBuilder.Default
                                                .GetLayout()
                                                .Skip(1)
                                                .Prepend(p =>
                                                    AnsiConsole.Write(new FigletText(nameof(WinResizer)).LeftJustified().Color(Color.Blue)))
                             );
                         })
                         .Build();

            return parser.InvokeAsync(args);
        }
    }
}
