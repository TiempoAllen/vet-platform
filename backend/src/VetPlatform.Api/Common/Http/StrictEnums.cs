using System.Text.Json;
using System.Text.Json.Serialization;

namespace VetPlatform.Api.Common.Http;

/// <summary>
/// Enums cross the API boundary only as their exact, defined names (docs/API.md section 13). Unlike the framework
/// defaults, numbers ("0", 99), other casings ("admin") and flag lists ("Admin, Nurse") are rejected, so an undefined
/// value can never reach the database.
/// </summary>
public static class StrictEnum
{
    public static bool TryParse<TEnum>(string? value, out TEnum result)
        where TEnum : struct, Enum
    {
        result = default;
        return value is not null
            && Enum.GetNames<TEnum>().Contains(value, StringComparer.Ordinal)
            && Enum.TryParse(value, ignoreCase: false, out result);
    }

    public static string Names<TEnum>()
        where TEnum : struct, Enum => string.Join(", ", Enum.GetNames<TEnum>());
}

/// <summary>JSON converter applying <see cref="StrictEnum"/> to every enum. A rejected value fails parsing (400).</summary>
public sealed class StrictEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(StrictEnumConverter<>).MakeGenericType(typeToConvert))!;

    private sealed class StrictEnumConverter<TEnum> : JsonConverter<TEnum>
        where TEnum : struct, Enum
    {
        public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String && StrictEnum.TryParse<TEnum>(reader.GetString(), out var value)
                ? value
                : throw new JsonException($"Expected one of: {StrictEnum.Names<TEnum>()}.");

        public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }
}
