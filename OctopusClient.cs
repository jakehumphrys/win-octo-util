using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace UsageWidget;

/// <summary>
/// Minimal read-only client for the Octopus Energy REST API (v1).
/// Auth is HTTP Basic with the API key as the username and a blank password.
/// Every call is recorded in the <see cref="Audit"/> log.
/// </summary>
public class OctopusClient
{
    private const int MaxLoggedBody = 4000;

    private static readonly HttpClient Http = CreateHttpClient();

    private readonly string _apiKey;

    public OctopusClient(string apiKey) => _apiKey = apiKey;

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri("https://api.octopus.energy/v1/"),
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    public Task<List<Consumption>> GetElectricityConsumptionAsync(
        string mpan, string meterSerial, DateTime fromUtc, DateTime toUtc)
        => GetConsumptionAsync($"electricity-meter-points/{mpan}/meters/{meterSerial}/consumption/", fromUtc, toUtc);

    public Task<List<Consumption>> GetGasConsumptionAsync(
        string mprn, string meterSerial, DateTime fromUtc, DateTime toUtc)
        => GetConsumptionAsync($"gas-meter-points/{mprn}/meters/{meterSerial}/consumption/", fromUtc, toUtc);

    private async Task<List<Consumption>> GetConsumptionAsync(string path, DateTime fromUtc, DateTime toUtc)
    {
        var url = $"{path}?page_size=100&order_by=period" +
                  $"&period_from={fromUtc:yyyy-MM-ddTHH:mm:ssZ}" +
                  $"&period_to={toUtc:yyyy-MM-ddTHH:mm:ssZ}";
        var body = await GetAsync(url);
        var page = JsonSerializer.Deserialize<ConsumptionPage>(body);
        return page?.Results ?? new List<Consumption>();
    }

    /// <summary>The account's meter points and the tariff agreements attached to them.</summary>
    public async Task<OctopusAccount> GetAccountAsync(string accountNumber)
    {
        var body = await GetAsync($"accounts/{accountNumber}/");
        return JsonSerializer.Deserialize<OctopusAccount>(body) ?? new OctopusAccount();
    }

    /// <summary>Unit rates (pence per kWh, inc VAT) for a tariff, following every page.</summary>
    public Task<List<TariffRate>> GetStandardUnitRatesAsync(string fuel, string productCode, string tariffCode)
        => GetRatesAsync($"products/{productCode}/{fuel}-tariffs/{tariffCode}/standard-unit-rates/");

    /// <summary>Standing charges (pence per day, inc VAT) for a tariff, following every page.</summary>
    public Task<List<TariffRate>> GetStandingChargesAsync(string fuel, string productCode, string tariffCode)
        => GetRatesAsync($"products/{productCode}/{fuel}-tariffs/{tariffCode}/standing-charges/");

    /// <summary>Follows the API's paging (its "next" links) to collect a tariff's full rate history.</summary>
    private async Task<List<TariffRate>> GetRatesAsync(string path)
    {
        var results = new List<TariffRate>();
        var url = $"{path}?page_size=1500";

        // Bounded loop: "next" is an absolute URL, or null once there is nothing left.
        for (var page = 0; page < 50 && !string.IsNullOrEmpty(url); page++)
        {
            var body = await GetAsync(url);
            var parsed = JsonSerializer.Deserialize<TariffRatePage>(body);
            if (parsed is null) break;

            results.AddRange(parsed.Results);
            url = parsed.Next;
        }

        return results;
    }

    /// <summary>
    /// Performs a GET against the API (a relative path or an absolute URL), records the
    /// request, status and body in the audit log, and returns the response body. The API
    /// key travels only in the Authorization header, so it never reaches the log.
    /// </summary>
    private async Task<string> GetAsync(string url)
    {
        var fullUrl = new Uri(Http.BaseAddress!, url).ToString();
        var stopwatch = Stopwatch.StartNew();
        var logged = false;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_apiKey}:"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

            using var response = await Http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            stopwatch.Stop();

            var status = $"{(int)response.StatusCode} {response.ReasonPhrase}";
            var ok = response.IsSuccessStatusCode;

            Audit.Api(
                $"GET {status} · {stopwatch.ElapsedMilliseconds} ms",
                $"GET {fullUrl}\n← {status} ({stopwatch.ElapsedMilliseconds} ms)\n\n" +
                (ok ? FormatJson(body) : body),
                !ok);
            logged = true;

            if (!ok)
                throw new HttpRequestException($"Octopus API returned {status}");

            return body;
        }
        catch (Exception ex)
        {
            if (!logged)
            {
                stopwatch.Stop();
                Audit.Api($"GET failed · {ex.Message}", $"GET {fullUrl}\n\n{ex}", true);
            }

            throw;
        }
    }

    /// <summary>Indents the JSON body for readability, truncating anything oversized.</summary>
    private static string FormatJson(string body)
    {
        string text;
        try
        {
            using var document = JsonDocument.Parse(body);
            text = JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            text = body;
        }

        return text.Length <= MaxLoggedBody ? text : text[..MaxLoggedBody] + "\n… (truncated)";
    }
}

/// <summary>A single consumption reading (half-hourly unless grouped).</summary>
public class Consumption
{
    [JsonPropertyName("consumption")] public double Value { get; set; }
    [JsonPropertyName("interval_start")] public DateTimeOffset IntervalStart { get; set; }
    [JsonPropertyName("interval_end")] public DateTimeOffset IntervalEnd { get; set; }
}

internal class ConsumptionPage
{
    [JsonPropertyName("results")] public List<Consumption> Results { get; set; } = new();
}

/// <summary>An Octopus account, as returned by the account endpoint.</summary>
public class OctopusAccount
{
    [JsonPropertyName("number")] public string Number { get; set; } = "";
    [JsonPropertyName("properties")] public List<AccountProperty> Properties { get; set; } = new();
}

public class AccountProperty
{
    [JsonPropertyName("electricity_meter_points")] public List<AccountMeterPoint> ElectricityMeterPoints { get; set; } = new();
    [JsonPropertyName("gas_meter_points")] public List<AccountMeterPoint> GasMeterPoints { get; set; } = new();
}

public class AccountMeterPoint
{
    [JsonPropertyName("mpan")] public string? Mpan { get; set; }
    [JsonPropertyName("mprn")] public string? Mprn { get; set; }
    [JsonPropertyName("agreements")] public List<Agreement> Agreements { get; set; } = new();
}

/// <summary>A period during which a meter point was on a particular tariff.</summary>
public class Agreement
{
    [JsonPropertyName("tariff_code")] public string TariffCode { get; set; } = "";
    [JsonPropertyName("valid_from")] public DateTimeOffset ValidFrom { get; set; }
    [JsonPropertyName("valid_to")] public DateTimeOffset? ValidTo { get; set; }
}
