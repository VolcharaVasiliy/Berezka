namespace Elochka.Installer;

internal sealed class InstallerForm : Form
{
    private readonly InstallerEngine _engine;
    private readonly TextBox _installDirectoryTextBox;
    private readonly CheckBox _desktopShortcutCheckBox;
    private readonly ProgressBar _progressBar;
    private readonly Label _statusLabel;
    private readonly TextBox _logTextBox;
    private readonly Button _installButton;
    private readonly Button _cancelButton;

    private CancellationTokenSource? _installationCts;
    private bool _installationCompleted;

    public InstallerForm(InstallerManifest manifest, InstallerOptions options)
    {
        _engine = new InstallerEngine(manifest);

        Text = $"{manifest.ProductName} Installer";
        MinimumSize = new Size(760, 520);
        Size = new Size(760, 520);
        StartPosition = FormStartPosition.CenterScreen;

        var titleLabel = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 16.0f, FontStyle.Bold),
            Location = new Point(24, 20),
            Text = $"{manifest.ProductName} Setup",
        };

        var subtitleLabel = new Label
        {
            AutoSize = false,
            Location = new Point(28, 60),
            Size = new Size(680, 42),
            Text = $"This installer downloads {manifest.VersionTag}, verifies the package, extracts it, and creates a desktop shortcut.",
        };

        var versionLabel = new Label
        {
            AutoSize = true,
            Location = new Point(28, 112),
            Text = $"Release: {manifest.VersionTag}",
        };

        var installPathLabel = new Label
        {
            AutoSize = true,
            Location = new Point(28, 156),
            Text = "Install location",
        };

        _installDirectoryTextBox = new TextBox
        {
            Location = new Point(28, 178),
            Size = new Size(580, 27),
            Text = string.IsNullOrWhiteSpace(options.InstallDirectory)
                ? manifest.GetDefaultInstallDirectory()
                : options.InstallDirectory,
        };

        var browseButton = new Button
        {
            Location = new Point(620, 176),
            Size = new Size(88, 31),
            Text = "Browse...",
        };
        browseButton.Click += (_, _) => BrowseForInstallDirectory();

        _desktopShortcutCheckBox = new CheckBox
        {
            AutoSize = true,
            Checked = options.CreateDesktopShortcut,
            Location = new Point(28, 220),
            Text = "Create desktop shortcut",
        };

        _progressBar = new ProgressBar
        {
            Location = new Point(28, 270),
            Size = new Size(680, 24),
            Minimum = 0,
            Maximum = 100,
        };

        _statusLabel = new Label
        {
            AutoSize = false,
            Location = new Point(28, 304),
            Size = new Size(680, 24),
            Text = "Ready to install.",
        };

        _logTextBox = new TextBox
        {
            Location = new Point(28, 336),
            Size = new Size(680, 100),
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            ReadOnly = true,
        };

        _installButton = new Button
        {
            Location = new Point(514, 448),
            Size = new Size(94, 34),
            Text = "Install",
        };
        _installButton.Click += async (_, _) => await StartInstallationAsync();

        _cancelButton = new Button
        {
            Location = new Point(614, 448),
            Size = new Size(94, 34),
            Text = "Cancel",
        };
        _cancelButton.Click += (_, _) => CancelOrClose();

        Controls.Add(titleLabel);
        Controls.Add(subtitleLabel);
        Controls.Add(versionLabel);
        Controls.Add(installPathLabel);
        Controls.Add(_installDirectoryTextBox);
        Controls.Add(browseButton);
        Controls.Add(_desktopShortcutCheckBox);
        Controls.Add(_progressBar);
        Controls.Add(_statusLabel);
        Controls.Add(_logTextBox);
        Controls.Add(_installButton);
        Controls.Add(_cancelButton);

        FormClosing += OnFormClosing;
    }

    private async Task StartInstallationAsync()
    {
        var installDirectory = _installDirectoryTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(installDirectory))
        {
            MessageBox.Show("Choose an install directory first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (Directory.Exists(installDirectory) &&
            Directory.GetFileSystemEntries(installDirectory).Length > 0 &&
            MessageBox.Show(
                "The target directory already contains files. They will be replaced. Continue?",
                Text,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        SetBusyState(isBusy: true);
        _installationCts = new CancellationTokenSource();
        var progress = new Progress<InstallerProgress>(UpdateProgress);

        try
        {
            await _engine.RunAsync(
                installDirectory,
                _desktopShortcutCheckBox.Checked,
                progress,
                _installationCts.Token);

            _installationCompleted = true;
            _statusLabel.Text = "Installation completed.";
            AppendLog("Installation completed.");
            _cancelButton.Text = "Close";
        }
        catch (OperationCanceledException)
        {
            _statusLabel.Text = "Installation cancelled.";
            AppendLog("Installation cancelled.");
        }
        catch (Exception exception)
        {
            _statusLabel.Text = "Installation failed.";
            AppendLog(exception.ToString());
            MessageBox.Show(exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _installationCts?.Dispose();
            _installationCts = null;
            SetBusyState(isBusy: false);
        }
    }

    private void UpdateProgress(InstallerProgress update)
    {
        _progressBar.Value = Math.Clamp(update.Percent, _progressBar.Minimum, _progressBar.Maximum);
        _statusLabel.Text = update.Message;
        AppendLog($"{update.Percent}% - {update.Message}");
    }

    private void BrowseForInstallDirectory()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select the folder where Elochka will be installed.",
            UseDescriptionForTitle = true,
            SelectedPath = _installDirectoryTextBox.Text,
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _installDirectoryTextBox.Text = dialog.SelectedPath;
        }
    }

    private void CancelOrClose()
    {
        if (_installationCts is not null)
        {
            _installationCts.Cancel();
            _cancelButton.Enabled = false;
            return;
        }

        Close();
    }

    private void SetBusyState(bool isBusy)
    {
        _installButton.Enabled = !isBusy && !_installationCompleted;
        _installDirectoryTextBox.Enabled = !isBusy;
        _desktopShortcutCheckBox.Enabled = !isBusy;
        _cancelButton.Enabled = true;
    }

    private void AppendLog(string message)
    {
        _logTextBox.AppendText(message + Environment.NewLine);
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs eventArgs)
    {
        if (_installationCts is null)
        {
            return;
        }

        var shouldCancel = MessageBox.Show(
            "Installation is still running. Cancel it and close the installer?",
            Text,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (shouldCancel != DialogResult.Yes)
        {
            eventArgs.Cancel = true;
            return;
        }

        _installationCts.Cancel();
    }
}
