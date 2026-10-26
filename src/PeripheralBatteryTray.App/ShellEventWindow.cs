using System.Runtime.InteropServices;

namespace PeripheralBatteryTray.App;

/// <summary>Top-level hidden window receives shell broadcasts without polling.</summary>
internal sealed class ShellEventWindow : NativeWindow, IDisposable
{
    private readonly Action _recoverTray;
    private readonly Action _displayChanged;
    private readonly Action? _suspend, _resume;
    private bool _suspended;
    internal uint TaskbarCreatedMessage { get; } = RegisterWindowMessage("TaskbarCreated");

    public ShellEventWindow(Action recoverTray, Action displayChanged, Action? suspend = null, Action? resume = null)
    {
        _recoverTray = recoverTray;
        _displayChanged = displayChanged;
        _suspend = suspend; _resume = resume;
        CreateHandle(new CreateParams { Caption = "PeripheralBatteryTray.Events", ExStyle = 0x80 });
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == TaskbarCreatedMessage) _recoverTray();
        else if (m.Msg == 0x0218)
        {
            // Automatic resume is followed by a second user-presence resume on some systems.
            if (m.WParam == 4 && !_suspended) { _suspended = true; _suspend?.Invoke(); }
            else if ((m.WParam == 18 || m.WParam == 7) && _suspended) { _suspended = false; _resume?.Invoke(); }
            m.Result = 1; return;
        }
        else if (m.Msg is 0x007E or 0x02E0 or 0x001A) _displayChanged(); // display / DPI / settings
        base.WndProc(ref m);
    }

    internal void SimulateTaskbarCreated() => SendMessage(Handle, TaskbarCreatedMessage, 0, 0);
    internal void SimulatePower(int kind) => SendMessage(Handle, 0x0218, kind, 0);
    public void Dispose() => DestroyHandle();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
}
