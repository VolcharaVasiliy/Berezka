using System.Reflection;
using System.Text.Json;

namespace Elochka.Installer;

internal sealed class InstallerManifest
{
    public string ProductName { get; init; } = "Elochka";

    public string VersionTag { get; init; } = string.Empty;

    public string DownloadUrl { get; init; } = string.Empty;

    public string ArchiveName { get; init; } = string.Empty;

    public long ArchiveSizeBytes { get; init; }

    public string ArchiveSha256 { get; init; } = string.Empty;

    public string MainExecutableRelativePath { get; init; } = "Elochka.App.exe";

    public string ShortcutName { get; init; } = "Elochka";

    public string DefaultInstallSubdirectory { get; init; } = "Elochka";

    public string ReleasePageUrl { get; init; } = string.Empty;

    public static InstallerManifest Load(string? manifestPath)
    {
        var json = string.IsNullOrWhiteSpace(manifestPath)
            ? ReadEmbeddedManifest()
            : File.ReadAllText(manifestPath);

        var manifest = JsonSerializer.Deserialize<InstallerManifest>(
            json,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });

        if (manifest is null)
        {
            throw new InvalidOperationException("Installer manifest could not be parsed.");
        }

        manifest.Validate();
        return manifest;
    }

    public string GetDefaultInstallDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            DefaultInstallSubdirectory);
    }

    private static string ReadEmbeddedManifest()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly
            .GetManifestResourceNames()
            .FirstOrDefault(static name => name.EndsWith("installer-manifest.json", StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
        {
            throw new InvalidOperationException("Embedded installer manifest is missing.");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Embedded installer manifest stream is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(ProductName) ||
            string.IsNullOrWhiteSpace(VersionTag) ||
            string.IsNullOrWhiteSpace(DownloadUrl) ||
            string.IsNullOrWhiteSpace(ArchiveName) ||
            string.IsNullOrWhiteSpace(ArchiveSha256) ||
            ArchiveSizeBytes <= 0)
        {
            throw new InvalidOperationException("Installer manifest is incomplete.");
        }
    }
}
