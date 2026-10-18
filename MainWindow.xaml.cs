using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace UsageWidget;

public partial class MainWindow : Window
{
    private static readonly Brush Green  = Brush("#3DDC84");
    private static readonly Brush Yellow = Brush("#F2C94C");
    private static readonly Brush Red    = Brush("#EB5757");

    // UK gas billing converts m³ to kWh as: m³ × 1.02264 (correction factor) ×
    // calorific value ÷ 3.6. The calorific value varies (~38-41 MJ/m³); 11.2 is the usual average.
    private const double CubicMetresToKwh = 11.2;

    // A day is always drawn as 48 half-hour slots so gaps show honestly.
    private const int HalfHourSlots = 48;
    private const double ChartHeight = 40;

    private AppSettings _settings = AppSettings.Load();
    private DateTime _day = DateTime.Today;
    private bool _busy;
    private AuditWindow? _auditWindow;

    public MainWindow()
    {
        InitializeComponent();

        // A wall clock only. The widget never polls the API on a timer - data is
        // pulled once on start-up and whenever the refresh button is pressed.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Stamp.Text = DateTime.Now.ToString("HH:mm:ss");
        timer.Start();

        UpdateDayHeader();
        Audit.Event("Widget started");

        Loaded += async (_, _) => await RefreshData();
    }

    private void Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Minimise(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    private async void OpenSettings(object sender, RoutedEventArgs e)
    {
        Audit.Event("Settings opened");

        var window = new SettingsWindow { Owner = this };
        window.ShowDialog();

        // Pick up anything that was changed, then show it straight away.
        _settings = AppSettings.Load();
        await RefreshData();
    }

    private void OpenAudit(object sender, RoutedEventArgs e)
    {
        if (_auditWindow is { IsVisible: true })
        {
            _auditWindow.Activate();
            return;
        }

        _auditWindow = new AuditWindow { Owner = this };
        _auditWindow.Show();
    }

    private async void Refresh(object sender, RoutedEventArgs e) => await RefreshData();

    private async void PreviousDay(object sender, RoutedEventArgs e)
    {
        _day = _day.AddDays(-1);
        await RefreshData();
    }

    private async void NextDay(object sender, RoutedEventArgs e)
    {
        if (_day >= DateTime.Today) return;

        _day = _day.AddDays(1);
        await RefreshData();
    }

    /// <summary>
    /// Pulls the selected day's half-hourly consumption for both fuels and paints the widget.
    /// Called on start-up, on the refresh button and on day navigation - never on a timer.
    /// </summary>
    private async Task RefreshData()
    {
        if (_busy) return;

        var isToday = _day == DateTime.Today;
        UpdateDayHeader();

        if (string.IsNullOrWhiteSpace(_settings.ApiKey) ||
            string.IsNullOrWhiteSpace(_settings.AccountNumber))
        {
            Audit.Event("Refresh skipped - Octopus details not configured");
            SetStatus("Add your Octopus details in settings", false);
            return;
        }

        _busy = true;
        RefreshButton.IsEnabled = false;
        PrevDayButton.IsEnabled = false;
        NextDayButton.IsEnabled = false;
        SetStatus("Fetching…", true);
        Audit.Event("Refreshing usage data", $"Day: {_day:yyyy-MM-dd}{(isToday ? " (today)" : "")}");

        try
        {
            var client = new OctopusClient(_settings.ApiKey);

            // Local midnight for the selected day, expressed in UTC for the API. For today
            // the window ends "now"; for a past day it covers the whole day.
            var from = _day.ToUniversalTime();
            var to = isToday
                ? DateTime.UtcNow
                : _day.AddDays(1).AddSeconds(-1).ToUniversalTime();

            var electricTask = client.GetElectricityConsumptionAsync(
                _settings.ElectricMpan, _settings.ElectricMeterSerial, from, to);
            var gasTask = client.GetGasConsumptionAsync(
                _settings.GasMpan, _settings.GasMeterSerial, from, to);

            await Task.WhenAll(electricTask, gasTask);

            var electric = electricTask.Result;
            var gas = gasTask.Result;

            var gasFactor = _settings.GasMeterIsCubicMetres ? CubicMetresToKwh : 1.0;
            var electricTotal = ApplyFuel(ElectricValue, ElectricBar, ElectricChart,
                                          electric, _settings.ElectricGoal, 1.0);
            var gasTotal = ApplyFuel(GasValue, GasBar, GasChart,
                                     gas, _settings.GasGoal, gasFactor);

            var lastEnd = electric.Select(r => (DateTimeOffset?)r.IntervalEnd)
                .Concat(gas.Select(r => (DateTimeOffset?)r.IntervalEnd))
                .DefaultIfEmpty()
                .Max();

            ReadingInfo.Text = lastEnd.HasValue
                ? $"{(isToday ? "today so far" : "full day")} · last reading {lastEnd.Value.ToLocalTime():HH:mm}"
                : $"{(isToday ? "today so far" : "this day")} · no readings{(isToday ? " yet" : "")}";

            SetStatus("All systems operational", true);
            Audit.Event("Usage loaded",
                $"Day: {_day:yyyy-MM-dd}\n" +
                $"Electricity: {electricTotal:0.00} kWh ({electric.Count} readings)\n" +
                $"Gas: {gasTotal:0.00} kWh ({gas.Count} readings)");
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}", false);
            Audit.Event("Refresh failed", ex.Message, true);
        }
        finally
        {
            _busy = false;
            RefreshButton.IsEnabled = true;
            PrevDayButton.IsEnabled = true;
            NextDayButton.IsEnabled = _day < DateTime.Today;
        }
    }

    /// <summary>Refreshes the day label, the "still arriving" hint and the chart headings.</summary>
    private void UpdateDayHeader()
    {
        var isToday = _day == DateTime.Today;

        DayLabel.Text = isToday ? "Today" : _day.ToString("ddd d MMM");
        TodayHint.Visibility = isToday ? Visibility.Visible : Visibility.Collapsed;
        NextDayButton.IsEnabled = !isToday;

        var day = isToday ? "TODAY" : _day.ToString("ddd d MMM").ToUpperInvariant();
        ElectricChartLabel.Text = $"ELECTRICITY · {day}";
        GasChartLabel.Text = $"GAS · {day}";

        var range = isToday ? "00:00 → now" : "00:00 → 24:00";
        ElectricRangeLabel.Text = range;
        GasRangeLabel.Text = range;
    }

    /// <summary>Writes the total, goal progress and bar chart for one fuel, returning the total in kWh.</summary>
    private double ApplyFuel(TextBlock value, ProgressBar bar, ItemsControl chart,
                             IReadOnlyList<Consumption> records, double goal, double unitFactor)
    {
        var total = records.Sum(r => r.Value) * unitFactor;
        value.Text = total.ToString("0.00");

        bar.Value = goal > 0 ? Math.Min(100, total / goal * 100) : 0;
        chart.ItemsSource = BuildSlots(records, unitFactor);

        return total;
    }

    /// <summary>
    /// Lays the day out as 48 half-hour slots, dropping each reading into its time slot.
    /// Empty slots stay flat, so a partial day looks partial instead of stretching to fill.
    /// </summary>
    private List<double> BuildSlots(IReadOnlyList<Consumption> records, double unitFactor)
    {
        var values = new double[HalfHourSlots];

        foreach (var record in records)
        {
            var local = record.IntervalStart.ToLocalTime().DateTime;
            var slot = (int)((local - _day).TotalMinutes / 30);
            if (slot < 0 || slot >= HalfHourSlots) continue;

            // Summing copes with the repeated hour on the autumn clock change.
            values[slot] += record.Value * unitFactor;
        }

        var max = values.Max();
        if (max <= 0) return values.ToList();

        return values
            .Select(v => v <= 0 ? 0.0 : Math.Max(2, v / max * ChartHeight))
            .ToList();
    }

    private void SetStatus(string text, bool ok)
    {
        StatusText.Text = text;
        StatusDot.Fill = ok ? Green : Red;
    }

    // ▾ / ▴ collapses the window down to just the gas + electricity overview.
    private void ToggleDetails(object sender, RoutedEventArgs e)
    {
        var collapse = Details.Visibility == Visibility.Visible;
        Details.Visibility = collapse ? Visibility.Collapsed : Visibility.Visible;
        CollapseToggle.Content = collapse ? "▴" : "▾";
        CollapseToggle.ToolTip = collapse ? "Expand" : "Collapse";
    }

    private static Brush Brush(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;
}
