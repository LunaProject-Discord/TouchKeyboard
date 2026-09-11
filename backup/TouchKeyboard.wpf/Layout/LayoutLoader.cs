using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TouchKeyboard.Layout;

/// <summary>
/// "0x29" のような 16 進文字列を ushort として読む。
/// 10 進表記や数値リテラルも受け付ける。
/// </summary>
public sealed class HexUShortConverter : JsonConverter<ushort>
{
    public override ushort Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            return reader.GetUInt16();
        }

        var text = reader.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new JsonException("スキャンコードが空です。");
        }

        return Parse(text);
    }

    public override void Write(Utf8JsonWriter writer, ushort value, JsonSerializerOptions options)
        => writer.WriteStringValue($"0x{value:X2}");

    internal static ushort Parse(string text)
    {
        var span = text.AsSpan().Trim();
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ushort.Parse(span[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return ushort.Parse(span, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }
}

/// <summary>省略可能なスキャンコード用。</summary>
public sealed class NullableHexUShortConverter : JsonConverter<ushort?>
{
    public override ushort? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType == JsonTokenType.Number) return reader.GetUInt16();

        var text = reader.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : HexUShortConverter.Parse(text);
    }

    public override void Write(Utf8JsonWriter writer, ushort? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue($"0x{value.Value:X2}");
    }
}

/// <summary>
/// レイアウト定義の読み込み。
///
/// 絶対制約: キー配列はデータとして外部化する。XAML への直書きは
/// 配列変更・レイアウト追加を不可能にする。
/// </summary>
public static class LayoutLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>既定レイアウトのファイル名。</summary>
    public const string DefaultFileName = "jis-full.json";

    /// <summary>
    /// 実行ファイルと同じ場所の layouts/ から読み込む。
    /// 埋め込みリソースにしていないのは、実機でスキャンコードを調整する際に
    /// 再ビルドなしで差し替えられるようにするため。
    /// </summary>
    public static string DefaultPath =>
        Path.Combine(AppContext.BaseDirectory, "layouts", DefaultFileName);

    public static LayoutDefinition LoadDefault() => Load(DefaultPath);

    /// <exception cref="FileNotFoundException">定義ファイルが無い場合。</exception>
    /// <exception cref="InvalidDataException">内容が不正な場合。</exception>
    public static LayoutDefinition Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"レイアウト定義が見つかりません: {path}", path);
        }

        LayoutDefinition? layout;
        try
        {
            using var stream = File.OpenRead(path);
            layout = JsonSerializer.Deserialize<LayoutDefinition>(stream, Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"レイアウト定義の解析に失敗しました: {path}", ex);
        }

        if (layout is null)
        {
            throw new InvalidDataException($"レイアウト定義が空です: {path}");
        }

        var errors = layout.Validate();
        if (errors.Count > 0)
        {
            throw new InvalidDataException(
                $"レイアウト定義が不正です: {path}{Environment.NewLine}"
                + string.Join(Environment.NewLine, errors));
        }

        return layout;
    }
}
