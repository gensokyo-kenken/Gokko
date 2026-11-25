using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gokko.Server.Services;

/// <summary>
/// 運行番号を JSON で読み書きする（文字列 "24S" で書く）。前の形の数字（24）や、人が書いた「24s運行」も読む。
/// 運行番号の形でなければ空文字にする（入力の確かめで「運行番号を入れてください」になる）。
/// </summary>
public sealed class UnyoJsonConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.Number => reader.TryGetInt64(out var n) ? Gokko.Common.Unyo.Normalize(n.ToString()) ?? "" : "",
        JsonTokenType.String => Gokko.Common.Unyo.Normalize(reader.GetString()) ?? "",
        JsonTokenType.Null => "",
        _ => throw new JsonException("運行番号は数字か文字で書いてください"),
    };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}
