using System.Text;

namespace Elochka.Installer;

internal static class SilentInstallRunner
{
    public static int Run(InstallerManifest manifest, InstallerOptions options)
    {
        var installDirectory = string.IsNullOrWhiteSpace(options.InstallDirectory)
            ? manifest.GetDefaultInstallDirectory()
            : options.InstallDirectory;
        var logPath = string.IsNullOrWhiteSpace(options.LogPath)
            ? Path.Combine(Path.GetTempPath(), "ElochkaInstaller", "silent-install.log")
            : options.LogPath;

        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        try
        {
            using var writer = new StreamWriter(logPath, append: false, Encoding.UTF8);
            var progress = new Progress<InstallerProgress>(update =>
            {
                writer.WriteLine($"{DateTime.Now:O} [{update.Stage}] {update.Percent}% {update.Message}");
                writer.Flush();
            });

            var engine = new InstallerEngine(manifest);
            engine.RunAsync(installDirectory!, options.CreateDesktopShortcut, progress, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            writer.WriteLine($"{DateTime.Now:O} [Completed] Silent install finished.");
            return 0;
        }
        catch (Exception exception)
        {
            File.AppendAllText(logPath, $"{DateTime.Now:O} [Error] {exception}{Environment.NewLine}", Encoding.UTF8);
            return 1;
        }
    }
}
