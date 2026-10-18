using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace UsageWidget;

/// <summary>An estimated cost for one day, broken down by fuel.</summary>
public class CostEstimate
{
    public Tariff? ElectricTariff { get; set; }
    public Tariff? GasTariff { get; set; }

    public double ElectricKwh { get; set; }
    public double GasKwh { get; set; }

    /// <summary>Consumption cost in pounds (kWh × unit rate).</summary>
    public double ElectricUnitCost { get; set; }
    public double GasUnitCost { get; set; }

    /// <summary>Standing charge for the whole day, in pounds.</summary>
    public double ElectricStanding { get; set; }
    public double GasStanding { get; set; }

    public bool HasElectricTariff => ElectricTariff is not null;
    public bool HasGasTariff => GasTariff is not null;

    public double ElectricCost => ElectricUnitCost + ElectricStanding;
    public double GasCost => GasUnitCost + GasStanding;
    public double TotalCost => ElectricCost + GasCost;
}

/// <summary>
/// Resolves which tariff was active for a fuel on a given day (from the account's
/// agreements) and turns consumption into an estimated cost. Tariffs are cached to disk,
/// so in normal use the rates are fetched once and every later estimate is offline.
/// </summary>
public static class TariffService
{
    // The account is fetched at most once per session (it only lists meter points and
    // which tariff applied when), and re-read if the account number changes.
    private static OctopusAccount? _account;
    private static string? _accountFor;

    // Tariff codes whose cache hit has already been noted this session, so navigating
    // between days does not fill the activity log with the same line.
    private static readonly HashSet<string> HitLogged = new();

    /// <summary>Builds the estimate for a day's readings. Tariff failures return a partial estimate.</summary>
    public static async Task<CostEstimate> EstimateAsync(
        OctopusClient client, AppSettings settings, DateTime day,
        IReadOnlyList<Consumption> electric, IReadOnlyList<Consumption> gas,
        double gasFactor, bool forceTariff = false)
    {
        var estimate = new CostEstimate();

        // Local midday avoids any DST edges when picking the day's standing charge.
        var instant = new DateTimeOffset(day.Date.AddHours(12));

        var electricTariff = await TryGetTariffAsync(client, settings, "electricity", instant, forceTariff);
        var gasTariff = await TryGetTariffAsync(client, settings, "gas", instant, forceTariff);

        estimate.ElectricTariff = electricTariff;
        estimate.GasTariff = gasTariff;

        estimate.ElectricKwh = electric.Sum(r => r.Value);
        if (electricTariff is not null)
        {
            estimate.ElectricUnitCost =
                electric.Sum(r => r.Value * electricTariff.UnitRateAt(r.IntervalStart)) / 100.0;
            estimate.ElectricStanding = electricTariff.StandingChargeAt(instant) / 100.0;
        }

        estimate.GasKwh = gas.Sum(r => r.Value) * gasFactor;
        if (gasTariff is not null)
        {
            estimate.GasUnitCost =
                gas.Sum(r => r.Value * gasFactor * gasTariff.UnitRateAt(r.IntervalStart)) / 100.0;
            estimate.GasStanding = gasTariff.StandingChargeAt(instant) / 100.0;
        }

        return estimate;
    }

    /// <summary>The tariff active for a fuel at an instant, from cache or fetched once.</summary>
    private static async Task<Tariff?> TryGetTariffAsync(
        OctopusClient client, AppSettings settings, string fuel, DateTimeOffset instant, bool force)
    {
        try
        {
            var account = await GetAccountAsync(client, settings);
            if (account is null) return null;

            var meterPoint = FindMeterPoint(account, settings, fuel);
            var agreement = SelectAgreement(meterPoint, instant);
            if (agreement is null) return null;

            var code = agreement.TariffCode;

            if (!force && TariffCache.TryGet(code, out var cached))
            {
                if (HitLogged.Add(code))
                    Audit.Event("Tariff cache hit", $"{fuel}: {code}");
                return cached;
            }

            var product = Tariff.ProductCodeFrom(code);
            var unitRates = await client.GetStandardUnitRatesAsync(fuel, product, code);
            var standing = await client.GetStandingChargesAsync(fuel, product, code);

            var tariff = new Tariff
            {
                TariffCode = code,
                ProductCode = product,
                Fuel = fuel,
                UnitRates = unitRates,
                StandingCharges = standing,
                FetchedAt = DateTime.Now
            };

            TariffCache.Put(tariff);
            Audit.Event("Tariff cached",
                $"{fuel}: {code}\n{unitRates.Count} unit rate(s), {standing.Count} standing charge(s)");

            return tariff;
        }
        catch (Exception ex)
        {
            Audit.Event($"Tariff fetch failed ({fuel})", ex.Message, true);
            return null;
        }
    }

    private static async Task<OctopusAccount?> GetAccountAsync(OctopusClient client, AppSettings settings)
    {
        if (_account is not null && _accountFor == settings.AccountNumber)
            return _account;

        try
        {
            _account = await client.GetAccountAsync(settings.AccountNumber);
            _accountFor = settings.AccountNumber;
            return _account;
        }
        catch (Exception ex)
        {
            Audit.Event("Account fetch failed", ex.Message, true);
            return null;
        }
    }

    /// <summary>The meter point matching the configured MPAN/MPRN, else the first one of that fuel.</summary>
    private static AccountMeterPoint? FindMeterPoint(OctopusAccount account, AppSettings settings, string fuel)
    {
        var points = account.Properties
            .SelectMany(p => fuel == "gas" ? p.GasMeterPoints : p.ElectricityMeterPoints)
            .ToList();

        if (points.Count == 0) return null;

        var wanted = fuel == "gas" ? settings.GasMpan : settings.ElectricMpan;
        var match = points.FirstOrDefault(m =>
            string.Equals(fuel == "gas" ? m.Mprn : m.Mpan, wanted, StringComparison.OrdinalIgnoreCase));

        return match ?? points[0];
    }

    /// <summary>The agreement covering an instant, else the most recent one.</summary>
    private static Agreement? SelectAgreement(AccountMeterPoint? meterPoint, DateTimeOffset instant)
    {
        if (meterPoint is null || meterPoint.Agreements.Count == 0) return null;

        var covering = meterPoint.Agreements
            .Where(a => a.ValidFrom <= instant && (a.ValidTo is null || a.ValidTo > instant))
            .OrderByDescending(a => a.ValidFrom)
            .FirstOrDefault();

        return covering ?? meterPoint.Agreements.OrderByDescending(a => a.ValidFrom).First();
    }
}
