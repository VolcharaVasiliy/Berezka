using Elochka.App.Interop;
using Elochka.App.Models;

namespace Elochka.App.Forms;

internal sealed class ResultForm : Form
{
    private const int MinPopupWidth = 280;
    private const int MaxPopupWidth = 560;
    private const int MinPopupHeight = 72;
    private const int MaxPopupHeight = 260;
    private const int ContentPadding = 12;

    private readonly Panel _contentPanel;
    private readonly RichTextBox _translationTextBox;

    public ResultForm()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.None;
        KeyPreview = true;
        MaximizeBox = false;
        MinimizeBox = false;
        MinimumSize = new Size(MinPopupWidth, MinPopupHeight);
        Padding = new Padding(1);
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = "Ёлочка";
        TopMost = true;
        Width = 420;
        Height = 120;

        _translationTextBox = new RichTextBox
        {
            BorderStyle = BorderStyle.None,
            DetectUrls = false,
            Dock = DockStyle.Fill,
            HideSelection = false,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            ShortcutsEnabled = true,
        };

        _contentPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(ContentPadding, ContentPadding - 2, ContentPadding, ContentPadding - 2),
        };
        _contentPanel.Controls.Add(_translationTextBox);

        Controls.Add(_contentPanel);

        WireMoveGesture(this);
        WireMoveGesture(_contentPanel);
        WireMoveGesture(_translationTextBox);
    }

    public void ShowLoading(AppSettings settings, Rectangle anchor)
    {
        ApplySettings(settings);
        _translationTextBox.Text = "Перевожу...";
        _translationTextBox.Select(0, 0);
        ResizeToText(_translationTextBox.Text, anchor);
        ShowNear(anchor);
    }

    public void ShowResult(CaptureResult result, Rectangle anchor, AppSettings settings)
    {
        ApplySettings(settings);

        var displayText = string.IsNullOrWhiteSpace(result.DisplayText)
            ? result.StatusMessage
            : result.DisplayText;

        _translationTextBox.Text = displayText;
        _translationTextBox.Select(0, 0);
        ResizeToText(displayText, anchor);
        ShowNear(anchor);
    }

    public void ApplySettings(AppSettings settings)
    {
        var palette = settings.ColorTheme.GetPalette();
        using var baseFont = settings.BuildFont();

        BackColor = palette.AccentColor;
        _contentPanel.BackColor = palette.WindowBackColor;

        _translationTextBox.BackColor = palette.WindowBackColor;
        _translationTextBox.ForeColor = palette.WindowForeColor;
        _translationTextBox.Font = (Font)baseFont.Clone();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Hide();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void ResizeToText(string text, Rectangle anchor)
    {
        var workingArea = Screen.FromRectangle(anchor).WorkingArea;
        var maxWidth = Math.Min(MaxPopupWidth, Math.Max(MinPopupWidth, workingArea.Width / 2));
        var textSize = TextRenderer.MeasureText(
            text + " ",
            _translationTextBox.Font,
            new Size(maxWidth - (ContentPadding * 2), 10000),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);

        var width = Math.Clamp(textSize.Width + (ContentPadding * 2) + 16, MinPopupWidth, maxWidth);
        var height = Math.Clamp(textSize.Height + (ContentPadding * 2) + 10, MinPopupHeight, MaxPopupHeight);

        Size = new Size(width, height);
    }

    private void ShowNear(Rectangle anchor)
    {
        var workingArea = Screen.FromRectangle(anchor).WorkingArea;
        var x = anchor.Right + 14;
        var y = anchor.Top;

        if (x + Width > workingArea.Right)
        {
            x = Math.Max(workingArea.Left, workingArea.Right - Width - 16);
        }

        if (y + Height > workingArea.Bottom)
        {
            y = Math.Max(workingArea.Top, workingArea.Bottom - Height - 16);
        }

        Location = new Point(x, y);

        if (!Visible)
        {
            Show();
        }

        BringToFront();
        Activate();
    }

    private void WireMoveGesture(Control control)
    {
        control.MouseDown += (_, eventArgs) =>
        {
            if (eventArgs.Button != MouseButtons.Right)
            {
                return;
            }

            User32.ReleaseCapture();
            User32.SendMessage(Handle, User32.WmNcLButtonDown, (IntPtr)User32.HtCaption, IntPtr.Zero);
        };
    }
}
