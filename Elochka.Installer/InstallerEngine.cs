using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Elochka.Installer;

internal sealed class InstallerEngine
{
    private static readonly Regex SevenZipPercentRegex = new(@"(?<percent>\d{1,3})%", RegexOptions.Compiled);

    private readonly InstallerManifest _manifest;

    public InstallerEngine(InstallerManifest manifest)
    {
        _manifest = manifest;
    }

    public async Task RunAsync(
        string installDirectory,
        bool createDesktopShortcut,
        IProgress<InstallerProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(installDirectory))
        {
            throw new ArgumentException("Install directory is required.", nameof(installDirectory));
        }

        installDirectory = Path.GetFullPath(installDirectory);
        var installParent = Directory.GetParent(installDirectory)?.FullName
            ?? throw new InvalidOperationException("Install directory must have a parent folder.");
        Directory.CreateDirectory(installParent);

        Report(progress, InstallerStage.Initializing, 2, "Preparing installer workspace...");

        var tempRoot = Path.Combine(Path.GetTempPath(), "ElochkaInstaller", Guid.NewGuid().ToString("N"));
        var downloadDirectory = Path.Combine(tempRoot, "downloads");
        var archivePath = Path.Combine(downloadDirectory, _manifest.ArchiveName);
        var stagingDirectory = Path.Combine(installParent, $".elochka-stage-{Guid.NewGuid():N}");

        Directory.CreateDirectory(downloadDirectory);
        Directory.CreateDirectory(stagingDirectory);

        using var tools = EmbeddedTools.Create();

        try
        {
            await DownloadArchiveAsync(tools.Aria2Path, archivePath, progress, cancellationToken);
            await VerifyArchiveAsync(archivePath, progress, cancellationToken);
            await ExtractArchiveAsync(tools.SevenZipPath, archivePath, stagingDirectory, progress, cancellationToken);
            PromoteInstallation(stagingDirectory, installDirectory, progress);

            if (createDesktopShortcut)
            {
                Report(progress, InstallerStage.Shortcut, 96, "Creating desktop shortcut...");
                var executablePath = Path.Combine(installDirectory, _manifest.MainExecutableRelativePath);
                ShortcutHelper.CreateDesktopShortcut(_manifest.ShortcutName, executablePath);
            }

            Report(progress, InstallerStage.Completed, 100, "Installation completed.");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
            if (Directory.Exists(stagingDirectory))
            {
                TryDeleteDirectory(stagingDirectory);
            }
        }
    }

    private async Task DownloadArchiveAsync(
        string aria2Path,
        string archivePath,
        IProgress<InstallerProgress>? progress,
        CancellationToken cancellationToken)
    {
        Report(progress, InstallerStage.Downloading, 5, "Downloading release package...");

        var startInfo = new ProcessStartInfo
        {
            FileName = aria2Path,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(aria2Path)!,
        };

        startInfo.ArgumentList.Add("--max-connection-per-server=8");
        startInfo.ArgumentList.Add("--split=8");
        startInfo.ArgumentList.Add("--min-split-size=1M");
        startInfo.ArgumentList.Add("--continue=true");
        startInfo.ArgumentList.Add("--allow-overwrite=true");
        startInfo.ArgumentList.Add("--auto-file-renaming=false");
        startInfo.ArgumentList.Add("--file-allocation=none");
        startInfo.ArgumentList.Add("--summary-interval=0");
        startInfo.ArgumentList.Add("--download-result=hide");
        startInfo.ArgumentList.Add("--console-log-level=warn");
        startInfo.ArgumentList.Add("--dir");
        startInfo.ArgumentList.Add(Path.GetDirectoryName(archivePath)!);
        startInfo.ArgumentList.Add("--out");
        startInfo.ArgumentList.Add(Path.GetFileName(archivePath));
        startInfo.ArgumentList.Add(_manifest.DownloadUrl);

        using var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };

        var outputBuffer = new StringBuilder();
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
            {
                outputBuffer.AppendLine(eventArgs.Data);
            }
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
            {
                outputBuffer.AppendLine(eventArgs.Data);
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start aria2c.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            while (!process.HasExited)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (File.Exists(archivePath))
                {
                    var downloadedBytes = new FileInfo(archivePath).Length;
                    var percent = Math.Clamp(
                        (int)Math.Round((double)downloadedBytes / _manifest.ArchiveSizeBytes * 68.0),
                        0,
                        68);
                    Report(progress, InstallerStage.Downloading, percent, $"Downloading release package... {downloadedBytes / 1024 / 1024} MB");
                }

                await Task.Delay(500, cancellationToken);
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(BuildDownloadFailureMessage(outputBuffer.ToString()));
            }
        }
        catch
        {
            TryKillProcess(process);
            throw;
        }

        if (!File.Exists(archivePath))
        {
            throw new InvalidOperationException("aria2c finished without producing the release archive.");
        }
    }

    private Task VerifyArchiveAsync(
        string archivePath,
        IProgress<InstallerProgress>? progress,
        CancellationToken cancellationToken)
    {
        Report(progress, InstallerStage.Verifying, 72, "Verifying package checksum...");

        using var stream = File.OpenRead(archivePath);
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(stream);
        cancellationToken.ThrowIfCancellationRequested();

        var actualHash = Convert.ToHexString(hash);
        if (!actualHash.Equals(_manifest.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Downloaded archive checksum mismatch. Expected {_manifest.ArchiveSha256}, got {actualHash}.");
        }

        return Task.CompletedTask;
    }

    private async Task ExtractArchiveAsync(
        string sevenZipPath,
        string archivePath,
        string stagingDirectory,
        IProgress<InstallerProgress>? progress,
        CancellationToken cancellationToken)
    {
        Report(progress, InstallerStage.Extracting, 78, "Extracting package...");

        var startInfo = new ProcessStartInfo
        {
            FileName = sevenZipPath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(sevenZipPath)!,
        };

        startInfo.ArgumentList.Add("x");
        startInfo.ArgumentList.Add(archivePath);
        startInfo.ArgumentList.Add($"-o{stagingDirectory}");
        startInfo.ArgumentList.Add("-y");

        using var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };

        var outputBuffer = new StringBuilder();
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (string.IsNullOrWhiteSpace(eventArgs.Data))
            {
                return;
            }

            outputBuffer.AppendLine(eventArgs.Data);
            var match = SevenZipPercentRegex.Match(eventArgs.Data);
            if (match.Success && int.TryParse(match.Groups["percent"].Value, out var sevenZipPercent))
            {
                var mappedPercent = 78 + (int)Math.Round(sevenZipPercent * 0.14);
                Report(progress, InstallerStage.Extracting, mappedPercent, $"Extracting package... {sevenZipPercent}%");
            }
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
            {
                outputBuffer.AppendLine(eventArgs.Data);
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start 7z.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch
        {
            TryKillProcess(process);
            throw;
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"7z extraction failed with exit code {process.ExitCode}.{Environment.NewLine}{outputBuffer}");
        }
    }

    private void PromoteInstallation(
        string stagingDirectory,
        string installDirectory,
        IProgress<InstallerProgress>? progress)
    {
        Report(progress, InstallerStage.Installing, 92, "Installing files...");

        var extractedRoot = ResolveExtractedRoot(stagingDirectory);
        if (Directory.Exists(installDirectory))
        {
            TryDeleteDirectory(installDirectory);
        }

        if (string.Equals(extractedRoot, stagingDirectory, StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(installDirectory);
            MoveDirectoryContent(extractedRoot, installDirectory);
            return;
        }

        Directory.Move(extractedRoot, installDirectory);
    }

    private static string ResolveExtractedRoot(string stagingDirectory)
    {
        var directories = Directory.GetDirectories(stagingDirectory);
        var files = Directory.GetFiles(stagingDirectory);

        if (directories.Length == 1 && files.Length == 0)
        {
            return directories[0];
        }

        return stagingDirectory;
    }

    private static void MoveDirectoryContent(string sourceDirectory, string destinationDirectory)
    {
        foreach (var directory in Directory.GetDirectories(sourceDirectory))
        {
            var targetDirectory = Path.Combine(destinationDirectory, Path.GetFileName(directory));
            Directory.Move(directory, targetDirectory);
        }

        foreach (var file in Directory.GetFiles(sourceDirectory))
        {
            var targetFile = Path.Combine(destinationDirectory, Path.GetFileName(file));
            File.Move(file, targetFile);
        }
    }

    private string BuildDownloadFailureMessage(string output)
    {
        var message = new StringBuilder();
        message.AppendLine("Download failed.");

        if (output.Contains("404", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("resource not found", StringComparison.OrdinalIgnoreCase))
        {
            message.AppendLine("The release asset is not publicly reachable. If the GitHub repo is private, publish the release or change the download URL.");
        }

        if (!string.IsNullOrWhiteSpace(_manifest.ReleasePageUrl))
        {
            message.AppendLine($"Release page: {_manifest.ReleasePageUrl}");
        }

        if (!string.IsNullOrWhiteSpace(output))
        {
            message.AppendLine();
            message.AppendLine(output.Trim());
        }

        return message.ToString().Trim();
    }

    private static void Report(IProgress<InstallerProgress>? progress, InstallerStage stage, int percent, string message)
    {
        progress?.Report(new InstallerProgress(stage, percent, message));
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
