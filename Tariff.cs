using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UsageWidget;

/// <summary>
/// One dated rate from a tariff. The unit that <see cref="ValueIncVat"/> is expressed in
/// depends on where it came from: pence per kWh for unit rates, pence per day for standing charges.
/// </summary>
public class TariffRate
{
    [JsonPropertyName("value_inc_vat")] public double ValueIncVat { get; set; }
    [JsonPropertyName("valid_from")] public DateTimeOffset ValidFrom { get; set; }
    [JsonPropertyName("valid_to")] public DateTimeOffset? ValidTo { get; set; }
}

internal class TariffRatePage
{
    [JsonPropertyName("results")] public List<TariffRate> Results { get; set; } = new();
    [JsonPropertyName("next")] public string? Next { get; set; }
}

/// <summary>
/// A fuel's tariff: its complete unit-rate and standing-charge history. Once fetched the
/// history never changes for past periods, so the whole thing is cached to disk and reused.
/// </summary>
public class Tariff
{
    public string TariffCode { get; set; } = "";
    public string ProductCode { get; set; } = "";
    public string Fuel { get; set; } = ""; // "electricity" or "gas"
    public List<TariffRate> UnitRates { get; set; } = new();
    public List<TariffRate> StandingCharges { get; set; } = new();
    public DateTime FetchedAt { get; set; }

    /// <summary>Unit rate in pence per kWh covering an instant (falls back to the nearest rate).</summary>
    public double UnitRateAt(DateTimeOffset instant) => RateAt(UnitRates, instant);

    /// <summary>Standing charge in pence per day covering an instant (falls back to the nearest rate).</summary>
    public double StandingChargeAt(DateTimeOffset instant) => RateAt(StandingCharges, instant);

    private static double RateAt(List<TariffRate> rates, DateTimeOffset instant)
    {
        var covering = rates.FirstOrDefault(r =>
            r.ValidFrom <= instant && (r.ValidTo is null || r.ValidTo > instant));
        if (covering is not null) return covering.ValueIncVat;

        // Nothing covers the instant exactly - use the most recent rate before it,
        // otherwise the earliest rate we know about.
        var before = rates.Where(r => r.ValidFrom <= instant)
            .OrderByDescending(r => r.ValidFrom)
            .FirstOrDefault();

        return before?.ValueIncVat ?? rates.OrderBy(r => r.ValidFrom).FirstOrDefault()?.ValueIncVat ?? 0;
    }

    /// <summary>
    /// The product code hidden inside a tariff code, e.g. "VAR-22-11-01" from
    /// "E-1R-VAR-22-11-01-A" (or the gas equivalent "G-1R-VAR-22-11-01-A").
    /// </summary>
    public static string ProductCodeFrom(string tariffCode)
    {
        var parts = tariffCode.Split('-');
        return parts.Length >= 4 ? string.Join("-", parts[2..^1]) : tariffCode;
    }
}

/// <summary>
/// Disk cache of tariffs, keyed by tariff code and stored as JSON in
/// %APPDATA%\WinOctoUtil\tariffcache.json. Tariff history is immutable, so entries are
/// kept indefinitely and only refetched when a code is missing or refresh is forced.
/// </summary>
public static class TariffCache
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Dictionary<string, Tariff> Tariffs = Load();

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinOctoUtil");

    /// <summary>Full path of the file the tariff cache is stored in.</summary>
    public static string FilePath => Path.Combine(Dir, "tariffcache.json");

    public static bool TryGet(string tariffCode, [NotNullWhen(true)] out Tariff? tariff)
        => Tariffs.TryGetValue(tariffCode, out tariff);

    public static void Put(Tariff tariff)
    {
        Tariffs[tariff.TariffCode] = tariff;
        Save();
    }

    private static Dictionary<string, Tariff> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<Dictionary<string, Tariff>>(json)
                       ?? new Dictionary<string, Tariff>();
            }
        }
        catch
        {
            // Corrupt or unreadable - start empty rather than blocking startup.
        }

        return new Dictionary<string, Tariff>();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Tariffs, JsonOptions));
        }
        catch
        {
            // A failed cache write must never break a refresh.
        }
    }
}
