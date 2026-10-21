using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VetPlatform.Api.Common.Http;

/// <summary>
/// Instants cross the API boundary only as ISO 8601 with an explicit offset (<c>2026-11-02T09:00:00Z</c> or
/// <c>...+01:00</c>). Without this, a value with no offset would silently be read in the server's own time zone.
/// </summary>
public static partial class StrictInstant
{
    private static readonly string[] Formats =
    [
        "yyyy-MM-dd'T'HH:mmK", "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
    ];

    public static bool TryParse(string? value, out DateTimeOffset result)
    {
        result = default;
        return value is not null
            && ExplicitOffset().IsMatch(value)
            && DateTimeOffset.TryParseExact(value, Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out result);
    }

    /// <summary>A calendar date, exactly <c>YYYY-MM-DD</c>.</summary>
    public static bool TryParseDate(string? value, out DateOnly result)
    {
        result = default;
        return value is not null
            && DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out result);
    }

    [GeneratedRegex(@"(Z|[+-]\d{2}:\d{2})$")]
    private static partial Regex ExplicitOffset();
}

/// <summary>JSON converter applying <see cref="StrictInstant"/> to every <see cref="DateTimeOffset"/> in request bodies.</summary>
public sealed class StrictInstantConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && StrictInstant.TryParse(reader.GetString(), out var value)
            ? value
            : throw new JsonException("Expected an ISO 8601 date-time with an explicit offset, e.g. 2026-11-02T09:00:00Z.");

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}
