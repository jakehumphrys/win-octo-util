using System;
using System.IO;
using System.Text.Json;

namespace UsageWidget;

/// <summary>
/// Octopus Energy connection details, persisted as JSON to
/// %APPDATA%\WinOctoUtil\appsettings.json.
/// </summary>
public class AppSettings
{
    public string ApiKey { get; set; } = "";
    public string AccountNumber { get; set; } = "";
    public string GasMpan { get; set; } = "";
    public string ElectricMpan { get; set; } = "";
    public string GasMeterSerial { get; set; } = "";
    public string ElectricMeterSerial { get; set; } = "";

    /// <summary>
    /// SMETS2 gas meters report consumption in m³, SMETS1 in kWh. When true the
    /// reported values are converted to kWh for display.
    /// </summary>
    public bool GasMeterIsCubicMetres { get; set; }

    /// <summary>Daily usage targets in kWh, used to scale the overview progress bars.</summary>
    public double GasGoal { get; set; }
    public double ElectricGoal { get; set; }

    /// <summary>
    /// What the window's ✕ button does. True (the default) hides the widget to the
    /// notification area; false quits the application outright.
    /// </summary>
    public bool CloseToTray { get; set; } = true;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinOctoUtil");

    /// <summary>Full path of the file the settings are stored in.</summary>
    public static string FilePath => Path.Combine(Dir, "appsettings.json");

    /// <summary>Reads the saved settings, falling back to blanks if the file is missing or unreadable.</summary>
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch
        {
            // Corrupt or locked file - fall back to defaults rather than blocking startup.
        }

        return new AppSettings();
    }

    /// <summary>Writes the current values to disk, creating the folder if needed.</summary>
    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
    }
}
