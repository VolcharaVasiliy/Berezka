namespace Elochka.Installer;

internal sealed record InstallerOptions(
    bool Silent = false,
    string? ManifestPath = null,
    string? InstallDirectory = null,
    bool CreateDesktopShortcut = true,
    string? LogPath = null)
{
    public static InstallerOptions Parse(string[] args)
    {
        var options = new InstallerOptions();

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            switch (argument.ToLowerInvariant())
            {
                case "--silent":
                    options = options with { Silent = true };
                    break;
                case "--manifest":
                    options = options with { ManifestPath = ReadValue(args, ref index, argument) };
                    break;
                case "--target":
                    options = options with { InstallDirectory = ReadValue(args, ref index, argument) };
                    break;
                case "--log":
                    options = options with { LogPath = ReadValue(args, ref index, argument) };
                    break;
                case "--desktop-shortcut":
                    options = options with { CreateDesktopShortcut = true };
                    break;
                case "--no-shortcut":
                    options = options with { CreateDesktopShortcut = false };
                    break;
                default:
                    throw new ArgumentException($"Unknown installer argument: {argument}");
            }
        }

        return options;
    }

    private static string ReadValue(string[] args, ref int index, string argumentName)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Argument {argumentName} expects a value.");
        }

        index++;
        return args[index];
    }
}
