namespace Elochka.Installer;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        InstallerOptions options;
        try
        {
            options = InstallerOptions.Parse(args);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                exception.Message,
                "Elochka Installer",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 2;
        }

        InstallerManifest manifest;
        try
        {
            manifest = InstallerManifest.Load(options.ManifestPath);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                exception.Message,
                "Elochka Installer",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 3;
        }

        if (options.Silent)
        {
            return SilentInstallRunner.Run(manifest, options);
        }

        Application.Run(new InstallerForm(manifest, options));
        return 0;
    }
}
