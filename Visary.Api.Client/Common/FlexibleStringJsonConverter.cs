using System.Text.Json;
using System.Text.Json.Serialization;

namespace Visary.Api.Common;

/// <summary>
/// Десериализатор строкового поля, которое Visary может прислать как
/// <see cref="JsonTokenType.String"/>, <see cref="JsonTokenType.Number"/> или
/// <see cref="JsonTokenType.Null"/>. Без него ConstructionSiteRaw.StageNumber
/// (объявлено как <c>string?</c>) падало на listview-ответе вида
/// <c>"StageNumber": 1</c> (см. doc_project/56-visary-dto-deserialization-pitfalls.md,
/// doc 101 — баг «JSON value could not be converted to System.String»).
///
/// При сериализации обратно — отдаёт как обычную строку.
/// </summary>
public sealed class FlexibleStringJsonConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out var i)
                ? i.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            _ => null,
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value);
    }
}
