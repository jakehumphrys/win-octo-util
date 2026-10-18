using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Json;

namespace UsageWidget;

/// <summary>
/// Local store of previously-fetched daily consumption, so revisiting a past day
/// costs no API request. Only completed days are cached: today is partial and is
/// always pulled fresh. Persisted as JSON to %APPDATA%\WinOctoUtil\usagecache.json.
/// </summary>
public static class UsageCache
{
    /// <summary>A day's readings for both fuels, exactly as returned by the API.</summary>
    public class Day
    {
        public List<Consumption> Electricity { get; set; } = new();
        public List<Consumption> Gas { get; set; } = new();
        public DateTime CachedAt { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Dictionary<string, Day> Days = Load();

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinOctoUtil");

    /// <summary>Full path of the file the cache is stored in.</summary>
    public static string FilePath => Path.Combine(Dir, "usagecache.json");

    /// <summary>
    /// Cache identity: the day plus the meters it was read from. Pointing the widget at
    /// a different meter (or serial) naturally misses the entries captured from the old one.
    /// </summary>
    private static string Key(DateTime day, AppSettings settings) =>
        $"{day:yyyy-MM-dd}|{settings.ElectricMpan}|{settings.ElectricMeterSerial}" +
        $"|{settings.GasMpan}|{settings.GasMeterSerial}";

    /// <summary>Looks up a day in the cache, reporting whether a stored copy was found.</summary>
    public static bool TryGet(DateTime day, AppSettings settings, [NotNullWhen(true)] out Day? entry)
        => Days.TryGetValue(Key(day, settings), out entry);

    /// <summary>Stores (or replaces) a day's readings and writes the cache back to disk.</summary>
    public static void Put(DateTime day, AppSettings settings,
                           List<Consumption> electricity, List<Consumption> gas)
    {
        Days[Key(day, settings)] = new Day
        {
            Electricity = electricity,
            Gas = gas,
            CachedAt = DateTime.Now
        };
        Save();
    }

    /// <summary>Reads the saved cache, falling back to an empty store if it is missing or unreadable.</summary>
    private static Dictionary<string, Day> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<Dictionary<string, Day>>(json)
                       ?? new Dictionary<string, Day>();
            }
        }
        catch
        {
            // Corrupt or unreadable - start empty rather than blocking startup.
        }

        return new Dictionary<string, Day>();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Days, JsonOptions));
        }
        catch
        {
            // A failed cache write must never break a refresh.
        }
    }
}
