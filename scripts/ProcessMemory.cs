using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

// External read-only profiler. Never loaded into the tray application.
public static class ProcessMemoryInspector
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Region
    {
        public IntPtr BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public UIntPtr RegionSize;
        public uint State, Protect, Type;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Page { public IntPtr Address; public UIntPtr Flags; }
    public sealed class Bucket
    {
        public string Kind { get; set; }
        public string File { get; set; }
        public long ResidentBytes { get; set; }
        public long ShareableBytes { get; set; }
        public long ActuallySharedBytes { get; set; }
    }
    public sealed class Result
    {
        public long WorkingSetBefore { get; set; }
        public long WorkingSetAfter { get; set; }
        public long ResidentBytes { get; set; }
        public long PrivateBytes { get; set; }
        public int QueryFailures { get; set; }
        public Bucket[] Buckets { get; set; }
    }
    public static Result Capture(int pid, long startTicks, string expectedPath)
    {
        if (IntPtr.Size != 8) throw new InvalidOperationException("x64 profiler required.");
        using (var process = Process.GetProcessById(pid))
        using (var handle = OpenProcess(0x0410, false, pid)) // QUERY_INFORMATION | VM_READ; no write rights
        {
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            Verify(process, startTicks, expectedPath);
            var result = new Result { WorkingSetBefore = process.WorkingSet64, PrivateBytes = process.PrivateMemorySize64 };
            var buckets = new Dictionary<string, Bucket>(StringComparer.OrdinalIgnoreCase);
            ulong address = 0;
            int pageSize = Environment.SystemPageSize;
            while (address < 0x00007FFFFFFF0000UL)
            {
                Region region;
                if (VirtualQueryEx(handle, new IntPtr((long)address), out region, (UIntPtr)Marshal.SizeOf(typeof(Region))) == UIntPtr.Zero) break;
                ulong next = (ulong)region.BaseAddress.ToInt64() + region.RegionSize.ToUInt64();
                if (next <= address) throw new InvalidOperationException("Invalid virtual memory region.");
                address = next;
                if (region.State != 0x1000) continue; // MEM_COMMIT, not reserved virtual address space
                string kind = region.Type == 0x1000000 ? "Image" : region.Type == 0x40000 ? "Mapped" : "Private";
                string file = "";
                if (kind != "Private")
                {
                    var buffer = new StringBuilder(2048);
                    if (GetMappedFileName(handle, region.BaseAddress, buffer, buffer.Capacity) > 0) file = Path.GetFileName(buffer.ToString());
                }
                string key = kind + ":" + file;
                Bucket bucket;
                if (!buckets.TryGetValue(key, out bucket)) buckets[key] = bucket = new Bucket { Kind = kind, File = file };
                ulong remaining = region.RegionSize.ToUInt64();
                ulong cursor = (ulong)region.BaseAddress.ToInt64();
                while (remaining > 0)
                {
                    int count = (int)Math.Min(4096UL, (remaining + (ulong)pageSize - 1) / (ulong)pageSize);
                    var pages = new Page[count];
                    for (int i = 0; i < count; i++) pages[i].Address = new IntPtr((long)(cursor + (ulong)i * (ulong)pageSize));
                    if (!QueryWorkingSetEx(handle, pages, count * Marshal.SizeOf(typeof(Page)))) result.QueryFailures++;
                    else foreach (var page in pages)
                    {
                        ulong flags = page.Flags.ToUInt64();
                        if ((flags & 1) == 0) continue;
                        bucket.ResidentBytes += pageSize;
                        if ((flags & (1UL << 15)) != 0) bucket.ShareableBytes += pageSize;
                        if (((flags >> 1) & 7) > 1) bucket.ActuallySharedBytes += pageSize;
                    }
                    ulong bytes = Math.Min(remaining, (ulong)count * (ulong)pageSize);
                    remaining -= bytes; cursor += bytes;
                }
            }
            Verify(process, startTicks, expectedPath);
            process.Refresh(); result.WorkingSetAfter = process.WorkingSet64;
            result.Buckets = buckets.Values.Where(b => b.ResidentBytes > 0).OrderByDescending(b => b.ResidentBytes).ToArray();
            result.ResidentBytes = result.Buckets.Sum(b => b.ResidentBytes);
            return result;
        }
    }
    private static void Verify(Process process, long ticks, string path)
    {
        process.Refresh();
        if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != ticks ||
            !String.Equals(Path.GetFullPath(process.MainModule.FileName), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Process identity mismatch.");
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern UIntPtr VirtualQueryEx(SafeProcessHandle process, IntPtr address, out Region region, UIntPtr length);
    [DllImport("psapi.dll", SetLastError = true)] private static extern bool QueryWorkingSetEx(SafeProcessHandle process, [In, Out] Page[] pages, int bytes);
    [DllImport("psapi.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMappedFileNameW", SetLastError = true)]
    private static extern uint GetMappedFileName(SafeProcessHandle process, IntPtr address, StringBuilder text, int count);
}
