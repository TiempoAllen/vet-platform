using System.Text.Json;
using System.Text.Json.Serialization;

namespace VetPlatform.Api.Common.Http;

/// <summary>
/// A JSON property in a partial update (PATCH) that tells "omitted" apart from an explicit <c>null</c>: omitted
/// leaves the field unchanged, <c>null</c> clears it. Use it only where null is a meaningful value that cannot be
/// written another way (string fields use <c>""</c> to clear instead).
/// </summary>
[JsonConverter(typeof(OptionalConverterFactory))]
public readonly record struct Optional<T>(T Value)
{
    /// <summary>True when the property was present in the request body, even as <c>null</c>.</summary>
    public bool IsSet { get; } = true;
}

public sealed class OptionalConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(Optional<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(
            typeof(OptionalConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;

    private sealed class OptionalConverter<T> : JsonConverter<Optional<T>>
    {
        // Called only for properties present in the body, including an explicit null.
        public override bool HandleNull => true;

        public override Optional<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new(JsonSerializer.Deserialize<T>(ref reader, options)!);

        public override void Write(Utf8JsonWriter writer, Optional<T> value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.Value, options);
    }
}
