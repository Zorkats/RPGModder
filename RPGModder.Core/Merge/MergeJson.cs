using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Security.Cryptography;
using System.Text;
using StrictJsonException = System.Text.Json.JsonException;
using StrictReader = System.Text.Json.Utf8JsonReader;
using StrictReaderOptions = System.Text.Json.JsonReaderOptions;
using StrictTokenType = System.Text.Json.JsonTokenType;

namespace RPGModder.Core.Merge;

/// <summary>
/// The one reader and the one writer of game data inside the merge (GR-M7, seam 4.1). The reader accepts what the
/// game's <c>JSON.parse</c> accepts and nothing more, so a file the engine reads is a file the game can load. A
/// string that looks like a date stays a string.
/// </summary>
internal static class MergeJson
{
    private const int MaxDepth = 128;
    private const char ByteOrderMark = '﻿';
    private const byte LineFeed = (byte)'\n';

    /// <summary>
    /// Throws <see cref="JsonReaderException"/>, with the line and position, when <paramref name="text"/> is not
    /// exactly one strict JSON value: no comment, single quote, bare name, trailing comma, <c>undefined</c>,
    /// <c>NaN</c>, constructor, or number too large for a double.
    /// </summary>
    public static JToken Parse(string text)
    {
        if (text.Length > 0 && text[0] == ByteOrderMark)
        {
            text = text[1..];
        }

        Validate(text);

        using var reader = new JsonTextReader(new StringReader(text))
        {
            DateParseHandling = DateParseHandling.None,
            FloatParseHandling = FloatParseHandling.Double,
            MaxDepth = MaxDepth
        };

        return JToken.ReadFrom(reader, new JsonLoadSettings { LineInfoHandling = LineInfoHandling.Ignore });
    }

    /// <summary>Compact text: no indentation, property order as held, so equal trees give equal bytes.</summary>
    public static string Write(JToken token)
    {
        return token.ToString(Formatting.None);
    }

    public static string Sha256Hex(string text)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>Newtonsoft repairs loose input, so a strict reader checks the text first.</summary>
    private static void Validate(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        var reader = new StrictReader(bytes, new StrictReaderOptions { MaxDepth = MaxDepth });
        try
        {
            while (reader.Read())
            {
                if (reader.TokenType == StrictTokenType.Number &&
                    (!reader.TryGetDouble(out double number) || !double.IsFinite(number)))
                {
                    throw Rejected("A number is too large for the game to read.", bytes, (int)reader.TokenStartIndex);
                }
            }
        }
        catch (StrictJsonException exception)
        {
            throw new JsonReaderException(
                exception.Message,
                "",
                (int)(exception.LineNumber ?? 0) + 1,
                (int)(exception.BytePositionInLine ?? 0) + 1,
                exception);
        }
    }

    private static JsonReaderException Rejected(string message, byte[] bytes, int byteIndex)
    {
        int lineStart = byteIndex == 0 ? 0 : Array.LastIndexOf(bytes, LineFeed, byteIndex - 1) + 1;
        int line = bytes.AsSpan(0, lineStart).Count(LineFeed) + 1;
        int position = Encoding.UTF8.GetCharCount(bytes, lineStart, byteIndex - lineStart) + 1;
        return new JsonReaderException(message, "", line, position, null);
    }
}
