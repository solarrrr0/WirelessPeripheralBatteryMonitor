using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PeripheralBatteryTray.Core;

public enum DeviceSort { Discovery, Name, Battery }
public enum TrayIconMode { Aggregate, PerDevice }
public sealed record DevicePreference(string Alias = "", bool Hidden = false);

/// <summary>Local presentation preferences contain digests, never native device identities.</summary>
public sealed class DisplaySettings
{
    private static readonly DevicePreference Unconfigured = new();
    public const int MaxDevices = 64;
    public const int MaxAliasLength = 48;
    public static DisplaySettings Default { get; } = new();
    public DeviceSort Sort { get; }
    public TrayIconMode IconMode { get; }
    public IReadOnlyDictionary<string, DevicePreference> Devices { get; }
    public DisplaySettings(DeviceSort sort = DeviceSort.Discovery, TrayIconMode iconMode = TrayIconMode.Aggregate,
        IEnumerable<KeyValuePair<string, DevicePreference>>? devices = null)
    {
        if (!Enum.IsDefined(sort) || !Enum.IsDefined(iconMode)) throw new ArgumentException("Invalid display mode.");
        var copy = new Dictionary<string, DevicePreference>(StringComparer.Ordinal);
        foreach (var pair in devices ?? [])
        {
            if (copy.Count >= MaxDevices || !ValidKey(pair.Key) || pair.Value is null
                || !ValidAlias(pair.Value.Alias) || !copy.TryAdd(pair.Key, pair.Value with { Alias = pair.Value.Alias.Trim() }))
                throw new ArgumentException("Invalid device preference.");
        }
        Sort = sort; IconMode = iconMode;
        Devices = new ReadOnlyDictionary<string, DevicePreference>(copy);
    }
    public DevicePreference For(string? key) => key is not null && Devices.TryGetValue(key, out var preference) ? preference : Unconfigured;
    public static string KeyFor(string identity) => "device-v1-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    public static string AtkKey { get; } = KeyFor("atk:duckbill-ultra:single-target:v1");
    public static bool ValidKey(string? key) => key is { Length: 74 } && key.StartsWith("device-v1-", StringComparison.Ordinal)
        && key.AsSpan(10).ToArray().All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static bool ValidAlias(string? alias) => alias is not null && alias.Length <= MaxAliasLength
        && !alias.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format)
        && ValidUtf16(alias);
    private static bool ValidUtf16(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i])) { if (++i >= text.Length || !char.IsLowSurrogate(text[i])) return false; }
            else if (char.IsLowSurrogate(text[i])) return false;
        }
        return true;
    }
}
