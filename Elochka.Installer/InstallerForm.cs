namespace Elochka.Installer;

internal sealed class InstallerForm : Form
{
    private readonly InstallerManifest _manifest;
    private readonly InstallerEngine _engine;
    private readonly TextBox _installDirectoryTextBox;
    private readonly CheckBox _desktopShortcutCheckBox;
    private readonly ProgressBar _progressBar;
    private readonly Label _statusLabel;
    private readonly Label _percentLabel;
    private readonly Button _installButton;
    private readonly Button _cancelButton;
    private readonly Button _browseButton;
    private readonly Button _openFolderButton;
    private readonly Label _stepLabel;
    private readonly Label _downloadSummaryLabel;
    private readonly Label _releaseSummaryLabel;

    private CancellationTokenSource? _installationCts;
    private bool _installationCompleted;
    private string _currentInstallDirectory;

    public InstallerForm(InstallerManifest manifest, InstallerOptions options)
    {
        _manifest = manifest;
        _engine = new InstallerEngine(manifest);
        _currentInstallDirectory = string.IsNullOrWhiteSpace(options.InstallDirectory)
            ? manifest.GetDefaultInstallDirectory()
            : options.InstallDirectory;

        Text = $"{manifest.ProductName} Setup";
        MinimumSize = new Size(920, 620);
        MaximumSize = new Size(920, 620);
        Size = new Size(920, 620);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.WhiteSmoke;

        var sidebar = new Panel
        {
            Dock = DockStyle.Left,
            Width = 270,
            BackColor = Color.FromArgb(28, 58, 99),
        };

        var accentBand = new Panel
        {
            Dock = DockStyle.Top,
            Height = 8,
            BackColor = Color.FromArgb(228, 179, 76),
        };
        sidebar.Controls.Add(accentBand);

        var productLabel = new Label
        {
            AutoSize = false,
            Location = new Point(28, 34),
            Size = new Size(210, 42),
            Font = new Font("Segoe UI Semibold", 23.0f, FontStyle.Bold),
            ForeColor = Color.White,
            Text = "Elochka",
        };

        var setupLabel = new Label
        {
            AutoSize = false,
            Location = new Point(30, 78),
            Size = new Size(190, 28),
            Font = new Font("Segoe UI", 11.0f, FontStyle.Regular),
            ForeColor = Color.FromArgb(210, 221, 237),
            Text = "Bootstrap Installer",
        };

        var taglineLabel = new Label
        {
            AutoSize = false,
            Location = new Point(30, 130),
            Size = new Size(210, 74),
            Font = new Font("Segoe UI", 10.0f, FontStyle.Regular),
            ForeColor = Color.FromArgb(214, 226, 241),
            Text = "Downloads the packaged release, verifies integrity, extracts the app, and prepares a desktop shortcut.",
        };

        var summaryCard = CreateSidebarCard(new Point(18, 236), new Size(234, 132), "This build");
        _releaseSummaryLabel = CreateSidebarBodyLabel(
            new Point(14, 38),
            new Size(206, 78),
            $"Release {_manifest.VersionTag}{Environment.NewLine}Package {_manifest.ArchiveSizeBytes / 1024 / 1024} MB{Environment.NewLine}Target app: {_manifest.ProductName}");
        summaryCard.Controls.Add(_releaseSummaryLabel);

        var flowCard = CreateSidebarCard(new Point(18, 384), new Size(234, 164), "Installer flow");
        _downloadSummaryLabel = CreateSidebarBodyLabel(
            new Point(14, 38),
            new Size(206, 110),
            "1. Download package with aria2 in 8 connections" + Environment.NewLine +
            "2. Verify SHA-256" + Environment.NewLine +
            "3. Extract with 7-Zip" + Environment.NewLine +
            "4. Create desktop shortcut");
        flowCard.Controls.Add(_downloadSummaryLabel);

        sidebar.Controls.Add(productLabel);
        sidebar.Controls.Add(setupLabel);
        sidebar.Controls.Add(taglineLabel);
        sidebar.Controls.Add(summaryCard);
        sidebar.Controls.Add(flowCard);

        var contentPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(246, 248, 251),
            Padding = new Padding(28, 28, 28, 18),
        };

        var headerTitle = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 21.0f, FontStyle.Bold),
            ForeColor = Color.FromArgb(33, 37, 41),
            Location = new Point(0, 0),
            Text = "Install Elochka",
        };

        var headerSubtitle = new Label
        {
            AutoSize = false,
            Font = new Font("Segoe UI", 10.0f, FontStyle.Regular),
            ForeColor = Color.FromArgb(95, 103, 117),
            Location = new Point(2, 40),
            Size = new Size(560, 40),
            Text = "Choose the installation folder and let the installer handle download, verification, extraction, and shortcut creation.",
        };

        var mainCard = new Panel
        {
            Location = new Point(0, 92),
            Size = new Size(592, 346),
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
        };

        _stepLabel = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 9.0f, FontStyle.Bold),
            ForeColor = Color.FromArgb(32, 95, 162),
            Location = new Point(24, 20),
            Text = "STEP 1 OF 1",
        };

        var installPathLabel = new Label
        {
            AutoSize = true,
            Location = new Point(24, 60),
            Font = new Font("Segoe UI Semibold", 10.0f, FontStyle.Bold),
            ForeColor = Color.FromArgb(44, 48, 56),
            Text = "Install location",
        };

        _installDirectoryTextBox = new TextBox
        {
            Location = new Point(24, 84),
            Size = new Size(430, 29),
            Font = new Font("Segoe UI", 10.0f, FontStyle.Regular),
            Text = _currentInstallDirectory,
        };

        _browseButton = CreatePrimaryButton("Browse...", 468, 82, 100);
        _browseButton.BackColor = Color.FromArgb(232, 237, 244);
        _browseButton.ForeColor = Color.FromArgb(33, 37, 41);
        _browseButton.Click += (_, _) => BrowseForInstallDirectory();

        var detailsSeparator = new Panel
        {
            Location = new Point(24, 132),
            Size = new Size(544, 1),
            BackColor = Color.FromArgb(226, 231, 238),
        };

        var releaseCard = CreateInfoCard(new Point(24, 156), new Size(258, 94), "Release");
        releaseCard.Controls.Add(CreateInfoValueLabel(new Point(16, 34), $"{_manifest.VersionTag}"));
        releaseCard.Controls.Add(CreateInfoHintLabel(new Point(16, 62), $"{_manifest.ArchiveSizeBytes / 1024 / 1024} MB package"));

        var shortcutCard = CreateInfoCard(new Point(310, 156), new Size(258, 94), "Shortcut");
        _desktopShortcutCheckBox = new CheckBox
        {
            AutoSize = true,
            Checked = options.CreateDesktopShortcut,
            Location = new Point(16, 38),
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular),
            Text = "Create desktop shortcut",
        };
        shortcutCard.Controls.Add(_desktopShortcutCheckBox);
        shortcutCard.Controls.Add(CreateInfoHintLabel(new Point(16, 64), "Recommended for quick access"));

        var progressSectionLabel = new Label
        {
            AutoSize = true,
            Location = new Point(24, 274),
            Font = new Font("Segoe UI Semibold", 10.0f, FontStyle.Bold),
            ForeColor = Color.FromArgb(44, 48, 56),
            Text = "Install progress",
        };

        _percentLabel = new Label
        {
            AutoSize = true,
            Location = new Point(530, 274),
            Font = new Font("Segoe UI Semibold", 10.0f, FontStyle.Bold),
            ForeColor = Color.FromArgb(32, 95, 162),
            Text = "0%",
        };

        _progressBar = new ProgressBar
        {
            Location = new Point(24, 300),
            Size = new Size(544, 18),
            Minimum = 0,
            Maximum = 100,
            Style = ProgressBarStyle.Continuous,
        };

        _statusLabel = new Label
        {
            AutoSize = false,
            Location = new Point(24, 324),
            Size = new Size(544, 20),
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular),
            ForeColor = Color.FromArgb(88, 94, 105),
            Text = "Ready to install.",
        };

        mainCard.Controls.Add(_stepLabel);
        mainCard.Controls.Add(installPathLabel);
        mainCard.Controls.Add(_installDirectoryTextBox);
        mainCard.Controls.Add(_browseButton);
        mainCard.Controls.Add(detailsSeparator);
        mainCard.Controls.Add(releaseCard);
        mainCard.Controls.Add(shortcutCard);
        mainCard.Controls.Add(progressSectionLabel);
        mainCard.Controls.Add(_percentLabel);
        mainCard.Controls.Add(_progressBar);
        mainCard.Controls.Add(_statusLabel);

        var noteLabel = new Label
        {
            AutoSize = false,
            Location = new Point(0, 454),
            Size = new Size(592, 44),
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular),
            ForeColor = Color.FromArgb(95, 103, 117),
            Text = "The installer downloads the package from GitHub Releases, verifies integrity automatically, and then extracts the app for you.",
        };

        _installButton = CreatePrimaryButton("Install", 388, 532, 96);
        _installButton.Click += async (_, _) => await StartInstallationAsync();

        _cancelButton = CreatePrimaryButton("Cancel", 496, 532, 96);
        _cancelButton.BackColor = Color.FromArgb(232, 237, 244);
        _cancelButton.ForeColor = Color.FromArgb(33, 37, 41);
        _cancelButton.Click += (_, _) => CancelOrClose();

        _openFolderButton = CreatePrimaryButton("Open Folder", 278, 532, 98);
        _openFolderButton.Visible = false;
        _openFolderButton.Enabled = false;
        _openFolderButton.Click += (_, _) => OpenInstallDirectory();

        contentPanel.Controls.Add(headerTitle);
        contentPanel.Controls.Add(headerSubtitle);
        contentPanel.Controls.Add(mainCard);
        contentPanel.Controls.Add(noteLabel);
        contentPanel.Controls.Add(_openFolderButton);
        contentPanel.Controls.Add(_installButton);
        contentPanel.Controls.Add(_cancelButton);

        Controls.Add(contentPanel);
        Controls.Add(sidebar);

        AcceptButton = _installButton;
        CancelButton = _cancelButton;
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

        _currentInstallDirectory = installDirectory;
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
            _stepLabel.Text = "DONE";
            _cancelButton.Text = "Close";
            _openFolderButton.Visible = true;
            _openFolderButton.Enabled = true;
        }
        catch (OperationCanceledException)
        {
            _statusLabel.Text = "Installation cancelled.";
            _stepLabel.Text = "CANCELLED";
        }
        catch (Exception exception)
        {
            _statusLabel.Text = "Installation failed.";
            _stepLabel.Text = "FAILED";
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
        _percentLabel.Text = $"{update.Percent}%";
        _statusLabel.Text = update.Message;
        _stepLabel.Text = update.Stage switch
        {
            InstallerStage.Downloading => "DOWNLOADING",
            InstallerStage.Verifying => "VERIFYING",
            InstallerStage.Extracting => "EXTRACTING",
            InstallerStage.Installing => "INSTALLING",
            InstallerStage.Shortcut => "FINALIZING",
            InstallerStage.Completed => "DONE",
            _ => "PREPARING",
        };
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

    private void OpenInstallDirectory()
    {
        if (!Directory.Exists(_currentInstallDirectory))
        {
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = _currentInstallDirectory,
            UseShellExecute = true,
        });
    }

    private void SetBusyState(bool isBusy)
    {
        _installButton.Enabled = !isBusy && !_installationCompleted;
        _installDirectoryTextBox.Enabled = !isBusy;
        _desktopShortcutCheckBox.Enabled = !isBusy;
        _browseButton.Enabled = !isBusy;
        _cancelButton.Enabled = true;
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

    private static Panel CreateSidebarCard(Point location, Size size, string title)
    {
        var card = new Panel
        {
            Location = location,
            Size = size,
            BackColor = Color.FromArgb(39, 72, 117),
        };

        var titleLabel = new Label
        {
            AutoSize = true,
            Location = new Point(14, 12),
            Font = new Font("Segoe UI Semibold", 10.0f, FontStyle.Bold),
            ForeColor = Color.White,
            Text = title,
        };
        card.Controls.Add(titleLabel);
        return card;
    }

    private static Label CreateSidebarBodyLabel(Point location, Size size, string text)
    {
        return new Label
        {
            AutoSize = false,
            Location = location,
            Size = size,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular),
            ForeColor = Color.FromArgb(214, 226, 241),
            Text = text,
        };
    }

    private static Panel CreateInfoCard(Point location, Size size, string title)
    {
        var card = new Panel
        {
            Location = location,
            Size = size,
            BackColor = Color.FromArgb(248, 250, 253),
            BorderStyle = BorderStyle.FixedSingle,
        };

        var titleLabel = new Label
        {
            AutoSize = true,
            Location = new Point(16, 12),
            Font = new Font("Segoe UI", 9.0f, FontStyle.Bold),
            ForeColor = Color.FromArgb(83, 92, 104),
            Text = title.ToUpperInvariant(),
        };

        card.Controls.Add(titleLabel);
        return card;
    }

    private static Label CreateInfoValueLabel(Point location, string text)
    {
        return new Label
        {
            AutoSize = true,
            Location = location,
            Font = new Font("Segoe UI Semibold", 13.0f, FontStyle.Bold),
            ForeColor = Color.FromArgb(31, 37, 50),
            Text = text,
        };
    }

    private static Label CreateInfoHintLabel(Point location, string text)
    {
        return new Label
        {
            AutoSize = true,
            Location = location,
            Font = new Font("Segoe UI", 9.0f, FontStyle.Regular),
            ForeColor = Color.FromArgb(95, 103, 117),
            Text = text,
        };
    }

    private static Button CreatePrimaryButton(string text, int x, int y, int width)
    {
        return new Button
        {
            Location = new Point(x, y),
            Size = new Size(width, 34),
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(32, 95, 162),
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
        };
    }
}
