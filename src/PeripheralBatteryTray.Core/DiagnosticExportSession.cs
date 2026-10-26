namespace PeripheralBatteryTray.Core;

public enum ExportResult { Saved, Cancelled, Busy, Failed }
public sealed class DiagnosticExportSession
{
    private int _active;
    public bool IsActive => Volatile.Read(ref _active) != 0;
    public async Task<ExportResult> ExportAsync(Func<string?> choosePath, Func<DiagnosticReport> capture,
        Func<string, byte[], Task>? write = null)
    {
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0) return ExportResult.Busy;
        try
        {
            string? path = choosePath();
            if (path is null) return ExportResult.Cancelled;
            byte[] data = DiagnosticExporter.Serialize(capture());
            await (write ?? WriteAtomicAsync)(path, data);
            return ExportResult.Saved;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { return ExportResult.Failed; }
        finally { Volatile.Write(ref _active, 0); }
    }
    private static async Task WriteAtomicAsync(string path, byte[] data)
    {
        string full = Path.GetFullPath(path);
        string temp = Path.Combine(Path.GetDirectoryName(full)!, ".diagnostic-" + Guid.NewGuid().ToString("N") + ".tmp");
        bool own = false;
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            { own = true; await stream.WriteAsync(data); await stream.FlushAsync(); }
            File.Move(temp, full, true);
        }
        finally { if (own && File.Exists(temp)) File.Delete(temp); }
    }
}
