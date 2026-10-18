using System;
using System.Collections.Generic;
using System.ComponentModel;
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

    // Tray icon state. Closing the widget hides it here rather than quitting.
    private System.Windows.Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _trayIcon;
    private bool _exiting;
    private bool _trayHintShown;

    public MainWindow()
    {
        InitializeComponent();

        SetUpTrayIcon();
        Closing += OnClosing;

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

    // The ✕ hides the widget to the notification area; quitting is done from the tray menu.
    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    private async void OpenSettings(object sender, RoutedEventArgs e)
    {
        Audit.Event("Settings opened");

        var window = new SettingsWindow { Owner = this };
        window.ShowDialog();

        // Pick up anything that was changed, then show it straight away.
        _settings = AppSettings.Load();
        await RefreshData();
    }

    private void OpenAudit(object sender, RoutedEventArgs e) => ShowAudit();

    private void ShowAudit()
    {
        if (_auditWindow is { IsVisible: true })
        {
            _auditWindow.Activate();
            return;
        }

        _auditWindow = new AuditWindow { Owner = this };
        _auditWindow.Show();
    }

    // The refresh button forces a fresh call for the day on screen, even if it is cached.
    private async void Refresh(object sender, RoutedEventArgs e) => await RefreshData(force: true);

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
    /// Past days are served from the local cache when possible; pass <paramref name="force"/>
    /// to fetch the day on screen again (the refresh button does this).
    /// </summary>
    private async Task RefreshData(bool force = false)
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

        // A completed past day never changes, so serve it from disk and skip the network
        // entirely. Today is always fetched because its readings are still arriving.
        if (!force && !isToday && UsageCache.TryGet(_day, _settings, out var cached))
        {
            ShowCached(cached);
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

            SetReadingInfo(electric, gas);
            ShowCacheBadge(false);

            // A completed day is worth keeping so revisiting it costs nothing later.
            if (!isToday)
            {
                UsageCache.Put(_day, _settings, electric, gas);
                Audit.Event("Day cached", $"Day: {_day:yyyy-MM-dd}");
            }

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

    /// <summary>
    /// Paints a day straight from the local cache - no API request is made. The status
    /// line and the "from cache" badge make it clear the numbers were not re-fetched,
    /// and the hit is written to the activity log.
    /// </summary>
    private void ShowCached(UsageCache.Day cached)
    {
        var gasFactor = _settings.GasMeterIsCubicMetres ? CubicMetresToKwh : 1.0;
        var electricTotal = ApplyFuel(ElectricValue, ElectricBar, ElectricChart,
                                      cached.Electricity, _settings.ElectricGoal, 1.0);
        var gasTotal = ApplyFuel(GasValue, GasBar, GasChart,
                                 cached.Gas, _settings.GasGoal, gasFactor);

        SetReadingInfo(cached.Electricity, cached.Gas);
        ShowCacheBadge(true);
        SetStatus("Showing cached data", true);

        Audit.Event("Cache hit - served without a request",
            $"Day: {_day:yyyy-MM-dd}\n" +
            $"Electricity: {electricTotal:0.00} kWh ({cached.Electricity.Count} readings)\n" +
            $"Gas: {gasTotal:0.00} kWh ({cached.Gas.Count} readings)\n" +
            $"Cached: {cached.CachedAt:yyyy-MM-dd HH:mm:ss}");
    }

    /// <summary>Writes the "last reading" line shared by the live and cached paths.</summary>
    private void SetReadingInfo(IReadOnlyList<Consumption> electric, IReadOnlyList<Consumption> gas)
    {
        var isToday = _day == DateTime.Today;

        var lastEnd = electric.Select(r => (DateTimeOffset?)r.IntervalEnd)
            .Concat(gas.Select(r => (DateTimeOffset?)r.IntervalEnd))
            .DefaultIfEmpty()
            .Max();

        ReadingInfo.Text = lastEnd.HasValue
            ? $"{(isToday ? "today so far" : "full day")} · last reading {lastEnd.Value.ToLocalTime():HH:mm}"
            : $"{(isToday ? "today so far" : "this day")} · no readings{(isToday ? " yet" : "")}";
    }

    /// <summary>Shows or hides the "from cache" badge under the day navigator.</summary>
    private void ShowCacheBadge(bool fromCache)
        => CacheBadge.Visibility = fromCache ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Refreshes the day label, the "still arriving" hint and the chart headings.</summary>
    private void UpdateDayHeader()
    {
        var isToday = _day == DateTime.Today;

        // Any day change clears the badge; the cache/live paths set it again below.
        ShowCacheBadge(false);

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
    /// Each slot also carries the time range and kWh for its hover tooltip.
    /// </summary>
    private List<ChartSlot> BuildSlots(IReadOnlyList<Consumption> records, double unitFactor)
    {
        var values = new double[HalfHourSlots];
        var seen = new bool[HalfHourSlots];

        foreach (var record in records)
        {
            var local = record.IntervalStart.ToLocalTime().DateTime;
            var slot = (int)((local - _day).TotalMinutes / 30);
            if (slot < 0 || slot >= HalfHourSlots) continue;

            // Summing copes with the repeated hour on the autumn clock change.
            values[slot] += record.Value * unitFactor;
            seen[slot] = true;
        }

        var max = values.Max();

        var slots = new List<ChartSlot>(HalfHourSlots);
        for (var i = 0; i < HalfHourSlots; i++)
        {
            var start = _day.AddMinutes(i * 30);

            slots.Add(new ChartSlot
            {
                Height = max <= 0 || values[i] <= 0 ? 0.0 : Math.Max(2, values[i] / max * ChartHeight),
                HasReading = seen[i],
                Time = $"{start:HH:mm} – {start.AddMinutes(30):HH:mm}",
                Value = values[i]
            });
        }

        return slots;
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

    // ── Notification area (tray) ─────────────────────────────────────────────
    // WPF has no tray icon of its own, so the WinForms NotifyIcon is used. It shares
    // the WPF UI thread, so its events are raised on the right dispatcher.

    /// <summary>Creates the tray icon and its menu.</summary>
    private void SetUpTrayIcon()
    {
        _trayIcon = LoadAppIcon();

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Show widget", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Activity log", null, (_, _) => ShowAudit());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = _trayIcon,
            Text = "WinOctoUtil - energy usage",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => RestoreFromTray();
    }

    /// <summary>The app's own icon (the octopus), taken from the built .exe.</summary>
    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon is not null) return icon;
            }
        }
        catch
        {
            // Fall back to the generic application icon below.
        }

        return System.Drawing.SystemIcons.Application;
    }

    /// <summary>Brings the widget back from the tray.</summary>
    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
    }

    /// <summary>Hides the widget and shows a one-time hint that it is still running.</summary>
    private void HideToTray()
    {
        Hide();
        Audit.Event("Widget hidden to tray");

        if (_trayHintShown) return;
        _trayHintShown = true;
        _tray?.ShowBalloonTip(
            3000,
            "WinOctoUtil",
            "Still running here - double-click the tray icon to reopen.",
            System.Windows.Forms.ToolTipIcon.Info);
    }

    /// <summary>Really quits, called from the tray menu.</summary>
    private void ExitApplication()
    {
        _exiting = true;
        _tray?.Dispose();
        _tray = null;
        Application.Current.Shutdown();
    }

    // The ✕ button raises Closing; unless we are really quitting, hide to the tray instead.
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;

        e.Cancel = true;
        HideToTray();
    }

    private static Brush Brush(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;
}

/// <summary>
/// One half-hour column of a day chart. Beyond the bar height it carries the time range
/// and usage so hovering a column can report them.
/// </summary>
public class ChartSlot
{
    /// <summary>Bar height in pixels; 0 for an empty slot so gaps stay flat.</summary>
    public double Height { get; init; }

    /// <summary>True when at least one reading fell in this half hour.</summary>
    public bool HasReading { get; init; }

    /// <summary>Local "HH:mm – HH:mm" range the slot covers.</summary>
    public string Time { get; init; } = "";

    /// <summary>Usage for the slot, already unit-converted to kWh.</summary>
    public double Value { get; init; }

    /// <summary>Text shown when the column is hovered.</summary>
    public string ToolTip => HasReading
        ? $"{Time}\n{Value:0.000} kWh"
        : $"{Time}\nno reading";
}
