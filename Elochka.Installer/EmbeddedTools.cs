using System.Reflection;

namespace Elochka.Installer;

internal sealed class EmbeddedTools : IDisposable
{
    private readonly string _rootPath;
    private bool _disposed;

    private EmbeddedTools(string rootPath, string aria2Path, string sevenZipPath, string sevenZipDllPath)
    {
        _rootPath = rootPath;
        Aria2Path = aria2Path;
        SevenZipPath = sevenZipPath;
        SevenZipDllPath = sevenZipDllPath;
    }

    public string Aria2Path { get; }

    public string SevenZipPath { get; }

    public string SevenZipDllPath { get; }

    public static EmbeddedTools Create()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "ElochkaInstaller", $"tools-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);

        var aria2Path = ExtractResource(rootPath, "aria2c.exe", "aria2c.exe");
        var sevenZipPath = ExtractResource(rootPath, "7z.exe", "7z.exe");
        var sevenZipDllPath = ExtractResource(rootPath, "7z.dll", "7z.dll");

        return new EmbeddedTools(rootPath, aria2Path, sevenZipPath, sevenZipDllPath);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (Directory.Exists(_rootPath))
            {
                Directory.Delete(_rootPath, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static string ExtractResource(string rootPath, string suffix, string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly
            .GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
        {
            throw new InvalidOperationException($"Embedded resource is missing: {suffix}");
        }

        var outputPath = Path.Combine(rootPath, fileName);
        using var input = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource stream is missing: {resourceName}");
        using var output = File.Create(outputPath);
        input.CopyTo(output);
        return outputPath;
    }
}
