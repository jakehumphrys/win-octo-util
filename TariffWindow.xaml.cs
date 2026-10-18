using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace UsageWidget;

/// <summary>
/// Shows the tariff that applied on the selected day and how the estimated cost is built
/// up. Tariffs come from the on-disk cache, so this usually costs no API requests;
/// "Refresh tariff" forces a re-fetch.
/// </summary>
public partial class TariffWindow : Window
{
    private readonly AppSettings _settings;
    private readonly DateTime _day;
    private readonly List<Consumption> _electric;
    private readonly List<Consumption> _gas;
    private readonly double _gasFactor;
    private bool _busy;

    public TariffWindow(AppSettings settings, DateTime day,
                        List<Consumption> electric, List<Consumption> gas, double gasFactor)
    {
        InitializeComponent();

        _settings = settings;
        _day = day;
        _electric = electric;
        _gas = gas;
        _gasFactor = gasFactor;

        DayLabel.Text = day == DateTime.Today ? "Today" : day.ToString("ddd d MMM yyyy");
        Loaded += async (_, _) => await LoadAsync(force: false);
    }

    private void Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    private async void RefreshTariff(object sender, RoutedEventArgs e) => await LoadAsync(force: true);

    private async Task LoadAsync(bool force)
    {
        if (_busy) return;

        if (string.IsNullOrWhiteSpace(_settings.ApiKey) ||
            string.IsNullOrWhiteSpace(_settings.AccountNumber))
        {
            Status.Text = "Add your Octopus API key and account number in settings to see tariffs.";
            return;
        }

        _busy = true;
        RefreshButton.IsEnabled = false;
        Status.Text = force ? "Fetching tariff…" : "Loading tariff…";
        Audit.Event(force ? "Tariff refresh requested" : "Tariff view opened",
                    $"Day: {_day:yyyy-MM-dd}");

        try
        {
            var client = new OctopusClient(_settings.ApiKey);
            var estimate = await TariffService.EstimateAsync(
                client, _settings, _day, _electric, _gas, _gasFactor, force);

            Paint(estimate);
        }
        catch (Exception ex)
        {
            Status.Text = $"Could not load the tariff: {ex.Message}";
        }
        finally
        {
            _busy = false;
            RefreshButton.IsEnabled = true;
        }
    }

    private void Paint(CostEstimate estimate)
    {
        PaintFuel(estimate.ElectricTariff, estimate.ElectricKwh, estimate.ElectricUnitCost,
                  estimate.ElectricStanding, ElectricCode, ElectricRate, ElectricStanding,
                  ElectricBreakdown, ElectricTotal);

        PaintFuel(estimate.GasTariff, estimate.GasKwh, estimate.GasUnitCost,
                  estimate.GasStanding, GasCode, GasRate, GasStanding,
                  GasBreakdown, GasTotal);

        var any = estimate.HasElectricTariff || estimate.HasGasTariff;
        TotalCost.Text = any ? $"£{estimate.TotalCost:0.00}" : "—";

        var fetched = estimate.ElectricTariff?.FetchedAt ?? estimate.GasTariff?.FetchedAt;
        CachedLabel.Text = fetched is null
            ? ""
            : $"rates cached {fetched:yyyy-MM-dd HH:mm} · estimates are local unless refreshed";

        Status.Text = !any
            ? "No tariff agreement was found on the account for this day."
            : "Estimate = consumption × unit rate + one day's standing charge, inc VAT.";
    }

    private void PaintFuel(Tariff? tariff, double kwh, double unitCost, double standing,
                           TextBlock code, TextBlock rate, TextBlock standingText,
                           TextBlock breakdown, TextBlock total)
    {
        if (tariff is null)
        {
            code.Text = "no tariff";
            rate.Text = "No tariff was found for this day.";
            standingText.Text = "";
            breakdown.Text = "";
            total.Text = "";
            return;
        }

        code.Text = tariff.TariffCode;
        rate.Text = DescribeRates(tariff);
        standingText.Text = $"Standing charge  {tariff.StandingChargeAt(DayNoon):0.00}p per day";
        breakdown.Text = $"{kwh:0.00} kWh = £{unitCost:0.00}   +   £{standing:0.00} standing";
        total.Text = $"≈ £{unitCost + standing:0.00}";
    }

    /// <summary>Describes the unit rate(s) that applied during the selected day.</summary>
    private string DescribeRates(Tariff tariff)
    {
        var from = new DateTimeOffset(_day.Date);
        var to = new DateTimeOffset(_day.Date.AddDays(1));

        var rates = tariff.UnitRates
            .Where(r => r.ValidFrom < to && (r.ValidTo is null || r.ValidTo > from))
            .ToList();

        if (rates.Count == 0)
            return "No unit rate was published for this day.";

        var min = rates.Min(r => r.ValueIncVat);
        var max = rates.Max(r => r.ValueIncVat);

        return Math.Abs(max - min) < 0.0001
            ? $"Unit rate  {min:0.00}p per kWh"
            : $"Unit rate  {min:0.00}p – {max:0.00}p per kWh (varies through the day)";
    }

    /// <summary>Local midday on the selected day - used to pick the day's standing charge.</summary>
    private DateTimeOffset DayNoon => new(_day.Date.AddHours(12));
}
