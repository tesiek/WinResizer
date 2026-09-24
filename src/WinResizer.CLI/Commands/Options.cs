using System.CommandLine;
using System.IO;
using System.Linq;

namespace WinResizer.CLI.Commands
{
    public class ConfigOption : Option<FileInfo>
    {
        public ConfigOption() : base(
            aliases: new[]
            {
                "--config",
                "-c"
            },
            description: "Config file path, use current config file if omitted.",
            parseArgument: result =>
            {
                if (result.Tokens.Count == 0)
                {
                    return null;
                }

                var filePath = result.Tokens.Single().Value;
                return new FileInfo(filePath);
            })
        {
            IsRequired = false;
            AllowMultipleArgumentsPerToken = false;
        }
    }

    public class ProfileOption : Option<string>
    {
        public ProfileOption() : base(
            aliases: new[]
            {
                "--profile",
                "-P"
            },
            description: "Profile name, use current profile if omitted.")
        {
            IsRequired = false;
            AllowMultipleArgumentsPerToken = false;
        }
    }

    public class ProcessOption : Option<string>
    {
        public ProcessOption() : base(
            aliases: new[]
            {
                "--process",
                "-p"
            },
            description: "Filter by process name; if omitted, consider windows from all processes.")
        {
            IsRequired = false;
            AllowMultipleArgumentsPerToken = false;
        }
    }

    public class TitleOption : Option<string>
    {
        public TitleOption() : base(
            aliases: new[]
            {
                "--title",
                "-t"
            },
            description: "Window title regex filter; if omitted, no title filter is applied.")
        {
            IsRequired = false;
            AllowMultipleArgumentsPerToken = false;
        }
    }

    public class VerboseOption : Option<bool>
    {
        public VerboseOption() : base(
            aliases: new[]
            {
                "--verbose",
                "-v"
            },
            description: "Show more details.")
        {
            IsRequired = false;
            AllowMultipleArgumentsPerToken = false;
        }
    }
}
