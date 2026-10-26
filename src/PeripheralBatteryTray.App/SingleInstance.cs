using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PeripheralBatteryTray.App;

internal enum AppMode { Mock, Real }
internal enum ExistingInstanceState { Missing, Mock, Real, Unknown }

internal sealed class SingleInstance : IDisposable
{
    internal const string LegacyPrefix = @"Local\PeripheralBatteryTray.Phase1.v1.";
    internal static string Prefix => @"Local\PeripheralBatteryTray.Phase2.v1."
        + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(WindowsIdentity.GetCurrent().User!.Value)))[..16] + ".";
    private readonly Mutex _mutex;
    private readonly EventWaitHandle? _marker;
    private bool _disposed;
    public bool IsPrimary { get; }
    public EventWaitHandle? ShowEvent { get; }
    public EventWaitHandle? ExitEvent { get; }
    public EventWaitHandle? ExactExitEvent { get; }
    internal static string ExactName(string prefix, int pid, long ticks) => $"{prefix}Exit.{pid}.{ticks}";

    public SingleInstance(AppMode mode = AppMode.Mock, string? prefix = null)
    {
        string p = prefix ?? Prefix;
        // A second startup never acquires an existing mutex recursively.
        _mutex = new Mutex(true, p + "Mutex", out bool created);
        IsPrimary = created;
        if (!created) return;
        try
        {
            ShowEvent = CreateEvent(p + "Show", EventResetMode.AutoReset);
            ExitEvent = CreateEvent(p + "Exit", EventResetMode.AutoReset);
            using var process = Process.GetCurrentProcess();
            ExactExitEvent = CreateEvent(ExactName(p, process.Id, process.StartTime.ToUniversalTime().Ticks), EventResetMode.AutoReset);
            _marker = CreateEvent(p + "Mode." + mode, EventResetMode.ManualReset);
            _marker.Set();
        }
        catch { Dispose(); throw; }
    }

    private static EventWaitHandle CreateEvent(string name, EventResetMode mode)
    {
        var handle = new EventWaitHandle(false, mode, name, out bool created);
        if (created) return handle;
        handle.Dispose();
        throw new InvalidOperationException("A stale instance event is still open.");
    }
    internal static bool LegacyExists() => MutexExists(LegacyPrefix + "Mutex");
    private static bool MutexExists(string name)
    {
        using var handle = OpenMutex(0x00100000, false, name);
        return Exists(handle);
    }
    internal static ExistingInstanceState Probe(string? prefix = null)
    {
        string p = prefix ?? Prefix;
        try
        {
            if (!MutexExists(p + "Mutex")) return ExistingInstanceState.Missing;
            bool mock = EventExists(p + "Mode.Mock"), real = EventExists(p + "Mode.Real");
            return mock == real ? ExistingInstanceState.Unknown : mock ? ExistingInstanceState.Mock : ExistingInstanceState.Real;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return ExistingInstanceState.Unknown; }
    }
    private static bool EventExists(string name)
    {
        using var handle = OpenEvent(0x00100000, false, name);
        return Exists(handle);
    }
    private static bool Exists(SafeWaitHandle handle)
    {
        if (!handle.IsInvalid) return true;
        int error = Marshal.GetLastPInvokeError();
        return error == 2 ? false : throw new System.ComponentModel.Win32Exception(error);
    }
    [DllImport("kernel32.dll", EntryPoint = "OpenMutexW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle OpenMutex(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", EntryPoint = "OpenEventW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle OpenEvent(uint access, bool inherit, string name);
    public static bool SignalShow(string? prefix = null) => Signal((prefix ?? Prefix) + "Show");
    public static bool SignalExit(string? prefix = null) => Signal((prefix ?? Prefix) + "Exit");
    internal static bool SignalExpectedExit(int pid, long ticks, string? expectedPath = null, string? prefix = null)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != ticks) return false;
            string path = process.MainModule!.FileName;
            if (!string.Equals(Path.GetFullPath(expectedPath ?? Environment.ProcessPath!), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) return false;
            return Signal(ExactName(prefix ?? Prefix, pid, ticks));
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        { return false; }
    }
    private static bool Signal(string name)
    {
        try { using var handle = EventWaitHandle.OpenExisting(name); return handle.Set(); }
        catch (Exception e) when (e is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException) { return false; }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _marker?.Dispose(); ExactExitEvent?.Dispose(); ShowEvent?.Dispose(); ExitEvent?.Dispose();
        if (IsPrimary) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
