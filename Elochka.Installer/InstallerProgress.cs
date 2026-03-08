namespace Elochka.Installer;

internal enum InstallerStage
{
    Initializing,
    Downloading,
    Verifying,
    Extracting,
    Installing,
    Shortcut,
    Completed,
}

internal sealed record InstallerProgress(
    InstallerStage Stage,
    int Percent,
    string Message);
