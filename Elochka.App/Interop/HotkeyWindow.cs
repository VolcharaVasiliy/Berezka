namespace Elochka.App.Interop;

internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    public event EventHandler? HotkeyPressed;

    public HotkeyWindow()
    {
        CreateHandle(new CreateParams());
    }

    public void Dispose()
    {
        DestroyHandle();
        GC.SuppressFinalize(this);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == User32.WmHotKey)
        {
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
        }

        base.WndProc(ref m);
    }
}
