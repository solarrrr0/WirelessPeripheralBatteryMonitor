using System.Text.Json;
using System.Text.Json.Nodes;
using PeripheralBatteryTray.Core;

namespace PeripheralBatteryTray.Windows;

public sealed record PollingSettings(int Minutes, bool CanSave, string? Notice, bool AtkAutoEnabled = false, int AtkMinutes = 5,
    bool LowBatteryEnabled = false, int LowBatteryThreshold = 20)
{ public DisplaySettings Display { get; init; } = DisplaySettings.Default; }
public interface ISettingsFiles
{
    bool Exists(string path);
    string Read(string path);
    void CreateDirectory(string path);
    void WriteNew(string path, string text, Action created);
    void Replace(string temp, string destination);
    void MoveNew(string temp, string destination);
    void DeleteOwn(string path);
}
public sealed class SettingsFiles : ISettingsFiles
{
    public bool Exists(string path) => File.Exists(path);
    public string Read(string path) => File.ReadAllText(path);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public void WriteNew(string path, string text, Action created) { using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); created();
        using var writer = new StreamWriter(stream); writer.Write(text); }
    public void Replace(string temp, string destination)
    {
        try { File.Replace(temp, destination, null, false); }
        catch (UnauthorizedAccessException) when (CanRenameEncryptedReplacement(temp, destination))
        {
            // Some EFS directories reject ReplaceFile's metadata merge. Both files are
            // already encrypted; a same-directory rename preserves encrypted storage.
            File.Move(temp, destination, overwrite: true);
        }
    }
    private static bool CanRenameEncryptedReplacement(string temp, string destination)
    {
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(temp)), Path.GetDirectoryName(Path.GetFullPath(destination)),
            StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            const FileAttributes mask = FileAttributes.Encrypted | FileAttributes.ReadOnly | FileAttributes.ReparsePoint | FileAttributes.Directory;
            return (File.GetAttributes(temp) & mask) == FileAttributes.Encrypted
                && (File.GetAttributes(destination) & mask) == FileAttributes.Encrypted;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
    public void MoveNew(string temp, string destination) => File.Move(temp, destination, false);
    public void DeleteOwn(string path) => File.Delete(path);
}
public sealed class PollingSettingsStore
{
    private readonly object _gate = new();
    private readonly ISettingsFiles _files;
    private readonly string? _path;
    private JsonObject _document = new() { ["schemaVersion"] = 4, ["pollIntervalMinutes"] = 5 };
    public PollingSettings Current { get; private set; } = new(5, true, null);
    // Null path is an explicitly isolated memory-only store for Mock and self-tests.
    public PollingSettingsStore(string? path = null, ISettingsFiles? files = null)
    { _path = path is null ? null : Path.GetFullPath(path); _files = files ?? new SettingsFiles(); Load(); }
    public static string UserConfigPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PeripheralBatteryTray", "config.json");
    private void Load()
    {
        if (_path is null || !_files.Exists(_path)) return;
        try
        {
            string text = _files.Read(_path);
            if (text.Length > 65536) throw new JsonException();
            using JsonDocument document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() != 1)
                || !root.TryGetProperty("schemaVersion", out var schema) || !schema.TryGetInt32(out int version)) throw new JsonException();
            if (version is not (1 or 2 or 3 or 4)) { Current = new(5, false, "配置版本尚不支持，已使用默认设置并关闭自动ATK及提醒；保留原文件。"); return; }
            if (!root.TryGetProperty("pollIntervalMinutes", out var interval) || !interval.TryGetInt32(out int minutes) || minutes is < 1 or > 30) throw new JsonException();
            bool enabled = false; int atkMinutes = 5;
            if (version >= 2 && root.TryGetProperty("atk", out var atk))
            {
                if (atk.ValueKind != JsonValueKind.Object || atk.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() != 1)
                    || !atk.TryGetProperty("autoRefreshEnabled", out var on) || on.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                    || !atk.TryGetProperty("pollIntervalMinutes", out var rate) || !rate.TryGetInt32(out atkMinutes) || atkMinutes is < 5 or > 30)
                    throw new JsonException();
                enabled = on.GetBoolean();
            }
            bool notifications = false; int threshold = 20;
            if (version >= 3 && root.TryGetProperty("notifications", out var notification))
            {
                if (notification.ValueKind != JsonValueKind.Object
                    || notification.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() != 1)
                    || !notification.TryGetProperty("lowBatteryEnabled", out var notify) || notify.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                    || !notification.TryGetProperty("thresholdPercent", out var level) || !level.TryGetInt32(out threshold) || threshold is < 1 or > 50)
                    throw new JsonException();
                notifications = notify.GetBoolean();
            }
            DisplaySettings display = DisplaySettings.Default;
            if (version == 4 && root.TryGetProperty("display", out var presentation)) display = ReadDisplay(presentation);
            _document = JsonNode.Parse(text)!.AsObject(); _document["schemaVersion"] = 4;
            // Version 1 never grants automatic HID permission, including unknown fields named atk.
            if (version == 1) _document["atk"] = null;
            if (version < 3) _document["notifications"] = null;
            if (version < 4) _document["display"] = null;
            Current = new(minutes, true, null, enabled, atkMinutes, notifications, threshold) { Display = display };
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        { Current = new(5, false, "配置无效或不可读，已使用默认周期；保留原文件。"); }
    }
    public bool TrySave(int minutes) => TrySave(minutes, Current.AtkAutoEnabled, Current.AtkMinutes, Current.LowBatteryEnabled, Current.LowBatteryThreshold);
    public bool TrySave(int minutes, bool atkAutoEnabled, int atkMinutes)
        => TrySave(minutes, atkAutoEnabled, atkMinutes, Current.LowBatteryEnabled, Current.LowBatteryThreshold);
    public bool TrySave(int minutes, bool atkAutoEnabled, int atkMinutes, bool lowBatteryEnabled, int lowBatteryThreshold)
        => TrySave(minutes, atkAutoEnabled, atkMinutes, lowBatteryEnabled, lowBatteryThreshold, Current.Display);
    public bool TrySave(int minutes, bool atkAutoEnabled, int atkMinutes, bool lowBatteryEnabled, int lowBatteryThreshold, DisplaySettings display)
    {
        if (minutes is < 1 or > 30 || atkMinutes is < 5 or > 30 || lowBatteryThreshold is < 1 or > 50 || display is null) return false;
        lock (_gate)
        {
            if (!Current.CanSave) return false;
            JsonObject updated = (JsonObject)_document.DeepClone(); updated["pollIntervalMinutes"] = minutes;
            updated["schemaVersion"] = 4;
            JsonObject atk = updated["atk"] as JsonObject ?? new();
            atk["autoRefreshEnabled"] = atkAutoEnabled; atk["pollIntervalMinutes"] = atkMinutes;
            if (updated["atk"] is not JsonObject) updated["atk"] = atk;
            JsonObject notifications = updated["notifications"] as JsonObject ?? new();
            notifications["lowBatteryEnabled"] = lowBatteryEnabled; notifications["thresholdPercent"] = lowBatteryThreshold;
            if (updated["notifications"] is not JsonObject) updated["notifications"] = notifications;
            JsonObject presentation = updated["display"] as JsonObject ?? new();
            presentation["sort"] = display.Sort.ToString(); presentation["iconMode"] = display.IconMode.ToString();
            var previousDevices = presentation["devices"] as JsonObject;
            var preferences = new JsonObject();
            foreach (var pair in display.Devices)
            {
                var item = previousDevices?[pair.Key]?.DeepClone() as JsonObject ?? new();
                item["alias"] = pair.Value.Alias; item["hidden"] = pair.Value.Hidden;
                preferences[pair.Key] = item;
            }
            presentation["devices"] = preferences;
            if (updated["display"] is not JsonObject) updated["display"] = presentation;
            string? temp = null;
            bool ownsTemp = false;
            try
            {
                if (_path is not null)
                {
                    _files.CreateDirectory(Path.GetDirectoryName(_path)!);
                    temp = Path.Combine(Path.GetDirectoryName(_path)!, ".config-" + Guid.NewGuid().ToString("N") + ".tmp");
                    string text = updated.ToJsonString(); if (text.Length > 65536) return false;
                    _files.WriteNew(temp, text, () => ownsTemp = true);
                    if (_files.Exists(_path)) _files.Replace(temp, _path); else _files.MoveNew(temp, _path);
                }
                _document = updated; Current = new(minutes, true, null, atkAutoEnabled, atkMinutes, lowBatteryEnabled, lowBatteryThreshold) { Display = display }; return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { return false; }
            finally { if (ownsTemp && temp is not null) { try { _files.DeleteOwn(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } } }
        }
    }
    private static DisplaySettings ReadDisplay(JsonElement element)
    {
        static void Object(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() != 1))
                throw new JsonException();
        }
        Object(element);
        if (!element.TryGetProperty("sort", out var sort) || sort.ValueKind != JsonValueKind.String
            || !Enum.TryParse<DeviceSort>(sort.GetString(), out var order) || sort.GetString() != order.ToString() || !Enum.IsDefined(order)
            || !element.TryGetProperty("iconMode", out var mode) || mode.ValueKind != JsonValueKind.String
            || !Enum.TryParse<TrayIconMode>(mode.GetString(), out var icons) || mode.GetString() != icons.ToString() || !Enum.IsDefined(icons)
            || !element.TryGetProperty("devices", out var devices)) throw new JsonException();
        Object(devices);
        var entries = new List<KeyValuePair<string, DevicePreference>>();
        foreach (var pair in devices.EnumerateObject())
        {
            Object(pair.Value);
            if (entries.Count >= DisplaySettings.MaxDevices || !DisplaySettings.ValidKey(pair.Name)
                || !pair.Value.TryGetProperty("alias", out var alias) || alias.ValueKind != JsonValueKind.String || !DisplaySettings.ValidAlias(alias.GetString())
                || !pair.Value.TryGetProperty("hidden", out var hidden) || hidden.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new JsonException();
            entries.Add(new(pair.Name, new(alias.GetString()!, hidden.GetBoolean())));
        }
        return new(order, icons, entries);
    }
}
