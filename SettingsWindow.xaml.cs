using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;

namespace UsageWidget;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public SettingsWindow()
    {
        InitializeComponent();

        _settings = AppSettings.Load();
        ApiKeyBox.Text = _settings.ApiKey;
        AccountBox.Text = _settings.AccountNumber;
        ElectricMpanBox.Text = _settings.ElectricMpan;
        ElectricSerialBox.Text = _settings.ElectricMeterSerial;
        GasMpanBox.Text = _settings.GasMpan;
        GasSerialBox.Text = _settings.GasMeterSerial;
        GasCubicMetresBox.IsChecked = _settings.GasMeterIsCubicMetres;
        CloseToTrayBox.IsChecked = _settings.CloseToTray;
        ElectricGoalBox.Text = FormatGoal(_settings.ElectricGoal);
        GasGoalBox.Text = FormatGoal(_settings.GasGoal);
    }

    private void Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Save(object sender, RoutedEventArgs e)
    {
        // Work out what changed first so the audit log can name the fields
        // (names only - secret values are never written to the log).
        var changed = new List<string>();
        void Note(bool differs, string field)
        {
            if (differs) changed.Add(field);
        }

        Note(ApiKeyBox.Text.Trim() != _settings.ApiKey, "API key");
        Note(AccountBox.Text.Trim() != _settings.AccountNumber, "account number");
        Note(ElectricMpanBox.Text.Trim() != _settings.ElectricMpan, "electricity MPAN");
        Note(ElectricSerialBox.Text.Trim() != _settings.ElectricMeterSerial, "electricity meter serial");
        Note(GasMpanBox.Text.Trim() != _settings.GasMpan, "gas meter MPAN");
        Note(GasSerialBox.Text.Trim() != _settings.GasMeterSerial, "gas meter serial");
        Note((GasCubicMetresBox.IsChecked == true) != _settings.GasMeterIsCubicMetres, "gas units");
        Note((CloseToTrayBox.IsChecked == true) != _settings.CloseToTray, "X button behaviour");
        Note(ParseGoal(ElectricGoalBox.Text) != _settings.ElectricGoal, "electricity goal");
        Note(ParseGoal(GasGoalBox.Text) != _settings.GasGoal, "gas goal");

        _settings.ApiKey = ApiKeyBox.Text.Trim();
        _settings.AccountNumber = AccountBox.Text.Trim();
        _settings.ElectricMpan = ElectricMpanBox.Text.Trim();
        _settings.ElectricMeterSerial = ElectricSerialBox.Text.Trim();
        _settings.GasMpan = GasMpanBox.Text.Trim();
        _settings.GasMeterSerial = GasSerialBox.Text.Trim();
        _settings.GasMeterIsCubicMetres = GasCubicMetresBox.IsChecked == true;
        _settings.CloseToTray = CloseToTrayBox.IsChecked == true;
        _settings.ElectricGoal = ParseGoal(ElectricGoalBox.Text);
        _settings.GasGoal = ParseGoal(GasGoalBox.Text);

        _settings.Save();

        Audit.Event(
            changed.Count == 0 ? "Settings saved (no changes)" : $"Settings saved ({changed.Count} changed)",
            changed.Count == 0 ? "" : "Changed: " + string.Join(", ", changed));

        Status.Text = $"Saved to {AppSettings.FilePath}";
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    private static string FormatGoal(double goal) =>
        goal > 0 ? goal.ToString("0.##", CultureInfo.CurrentCulture) : "";

    private static double ParseGoal(string text) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var value) && value > 0
            ? value
            : 0;
}
