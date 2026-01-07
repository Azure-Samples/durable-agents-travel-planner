// =============================================================================
// CurrencyConverterTool.cs - AI Agent Tool for Currency Conversion
// =============================================================================
// This tool enables AI agents to convert between currencies using real-time
// exchange rates from the free ExchangeRate-API (https://www.exchangerate-api.com).
// No API key required for basic usage.
// =============================================================================

using System.ComponentModel;
using System.Text.Json;
using TravelPlannerFunctions.Models;

namespace TravelPlannerFunctions.Tools;

/// <summary>
/// AI agent tool for currency conversion using real-time exchange rates.
/// Used by the Itinerary Planner Agent to show costs in both local and user currencies.
/// </summary>
public class CurrencyConverterTool
{
    // =========================================================================
    // Static Configuration
    // =========================================================================
    
    private static IHttpClientFactory? _httpClientFactory;
    
    /// <summary>
    /// Lazily initialized HTTP client with base URL configured.
    /// </summary>
    private static readonly Lazy<HttpClient> _httpClient = new(() =>
    {
        var client = _httpClientFactory?.CreateClient("CurrencyConverter") ?? new HttpClient();
        client.BaseAddress = new Uri("https://open.er-api.com/v6/");
        return client;
    });

    /// <summary>
    /// Initializes the tool with an HTTP client factory for proper lifecycle management.
    /// </summary>
    /// <param name="httpClientFactory">Factory for creating HTTP clients.</param>
    public static void Initialize(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    // =========================================================================
    // AI Agent Tool Methods
    // =========================================================================
    // These methods are exposed to AI agents via the [Description] attributes.
    // The AI uses the descriptions to understand when and how to call them.
    // =========================================================================

    /// <summary>
    /// Converts an amount from one currency to another using current exchange rates.
    /// </summary>
    /// <param name="amount">The amount to convert.</param>
    /// <param name="fromCurrency">Source currency code (e.g., USD, EUR, GBP).</param>
    /// <param name="toCurrency">Target currency code (e.g., USD, EUR, GBP).</param>
    /// <returns>Currency conversion details including the converted amount and exchange rate.</returns>
    [Description("Converts an amount from one currency to another using current exchange rates. " +
                 "Useful for helping travelers understand costs in their home currency.")]
    public static async Task<CurrencyConversion> ConvertCurrency(
        [Description("The amount to convert")] decimal amount,
        [Description("Source currency code (e.g., USD, EUR, GBP, JPY)")] string fromCurrency,
        [Description("Target currency code (e.g., USD, EUR, GBP, JPY)")] string toCurrency)
    {
        try
        {
            // Get exchange rates for the source currency
            var response = await _httpClient.Value.GetAsync($"latest/{fromCurrency.ToUpper()}");
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync();
            
            // Parse the JSON response with proper property mapping
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            
            // Check if the response has an error
            if (root.TryGetProperty("error-type", out _))
            {
                throw new ArgumentException($"Invalid currency code: {fromCurrency}");
            }
            
            // Get the rates object
            if (!root.TryGetProperty("rates", out var rates))
            {
                throw new InvalidOperationException("API response does not contain 'rates' property");
            }
            
            // Get the specific exchange rate
            if (!rates.TryGetProperty(toCurrency.ToUpper(), out var rateElement))
            {
                throw new ArgumentException($"Unable to find exchange rate for {toCurrency}");
            }
            
            var exchangeRate = rateElement.GetDecimal();
            var convertedAmount = amount * exchangeRate;
            
            // Get timestamp
            var timestamp = root.TryGetProperty("time_last_update_unix", out var timeElement)
                ? DateTimeOffset.FromUnixTimeSeconds(timeElement.GetInt64()).DateTime
                : DateTime.UtcNow;

            return new CurrencyConversion(
                FromCurrency: fromCurrency.ToUpper(),
                ToCurrency: toCurrency.ToUpper(),
                OriginalAmount: amount,
                ConvertedAmount: Math.Round(convertedAmount, 2),
                ExchangeRate: exchangeRate,
                Timestamp: timestamp
            );
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Failed to fetch exchange rates: {ex.Message}", ex);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Failed to parse exchange rate response: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Gets the current exchange rate between two currencies without converting an amount.
    /// </summary>
    /// <param name="fromCurrency">Source currency code (e.g., USD, EUR, GBP).</param>
    /// <param name="toCurrency">Target currency code (e.g., USD, EUR, GBP).</param>
    /// <returns>The current exchange rate (multiply by this to convert from -> to).</returns>
    [Description("Gets the current exchange rate between two currencies. " +
                 "Use this to check conversion rates before calculating costs.")]
    public static async Task<decimal> GetExchangeRate(
        [Description("Source currency code (e.g., USD, EUR, GBP, JPY)")] string fromCurrency,
        [Description("Target currency code (e.g., USD, EUR, GBP, JPY)")] string toCurrency)
    {
        var conversion = await ConvertCurrency(1, fromCurrency, toCurrency);
        return conversion.ExchangeRate;
    }
}
