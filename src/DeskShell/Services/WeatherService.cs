using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace DeskShell.Services;

public sealed record Weather(string Place, double Temperature, double High, double Low, string Condition, string Glyph, bool Fahrenheit);

/// <summary>Current weather from Open-Meteo (free, no account). Cached for 15 minutes.</summary>
internal static class WeatherService
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    static Weather? _cached;
    static DateTime _cachedAt;
    static string _cachedKey = "";

    /// <returns>The weather, or null with a reason the tile can show.</returns>
    public static async Task<(Weather? Weather, string? Problem)> GetAsync(string city, bool fahrenheit)
    {
        var key = $"{city}|{fahrenheit}";
        if (_cached != null && key == _cachedKey && DateTime.Now - _cachedAt < TimeSpan.FromMinutes(15))
            return (_cached, null);

        try
        {
            var place = await LocateAsync(city);
            if (place == null)
                return (null, string.IsNullOrWhiteSpace(city) ? "Set your city in settings" : $"Can't find \"{city}\"");

            var unit = fahrenheit ? "fahrenheit" : "celsius";
            var url = string.Create(CultureInfo.InvariantCulture,
                $"https://api.open-meteo.com/v1/forecast?latitude={place.Value.Lat:0.###}&longitude={place.Value.Lon:0.###}&current=temperature_2m,weather_code,is_day&daily=temperature_2m_max,temperature_2m_min&forecast_days=1&timezone=auto&temperature_unit={unit}");
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var current = doc.RootElement.GetProperty("current");
            var daily = doc.RootElement.GetProperty("daily");
            var code = current.GetProperty("weather_code").GetInt32();
            var isDay = current.GetProperty("is_day").GetInt32() == 1;
            var (condition, glyph) = Describe(code, isDay);

            _cached = new Weather(place.Value.Name,
                current.GetProperty("temperature_2m").GetDouble(),
                daily.GetProperty("temperature_2m_max")[0].GetDouble(),
                daily.GetProperty("temperature_2m_min")[0].GetDouble(),
                condition, glyph, fahrenheit);
            _cachedAt = DateTime.Now;
            _cachedKey = key;
            return (_cached, null);
        }
        catch (Exception ex)
        {
            Log.Error("Weather lookup failed", ex);
            return (_cached, _cached == null ? "Weather unavailable" : null);
        }
    }

    static async Task<(double Lat, double Lon, string Name)?> LocateAsync(string city)
    {
        // Only the city you type is used; DeskShell never asks Windows for your location.
        if (string.IsNullOrWhiteSpace(city)) return null;
        var url = $"https://geocoding-api.open-meteo.com/v1/search?count=1&name={Uri.EscapeDataString(city.Trim())}";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) return null;
        var r = results[0];
        return (r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble(), r.GetProperty("name").GetString() ?? city);
    }

    /// <summary>WMO weather codes → words and a Segoe icon.</summary>
    static (string, string) Describe(int code, bool day) => code switch
    {
        0 => ("Clear", day ? "" : ""),
        1 or 2 => ("Partly cloudy", ""),
        3 => ("Cloudy", ""),
        45 or 48 => ("Fog", ""),
        >= 51 and <= 57 => ("Drizzle", ""),
        >= 61 and <= 67 or >= 80 and <= 82 => ("Rain", ""),
        >= 71 and <= 77 or 85 or 86 => ("Snow", ""),
        >= 95 => ("Thunderstorm", ""),
        _ => ("—", "")
    };
}
