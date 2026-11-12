using LZStringCSharp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Text;

namespace RPGModder.Core.Merge;

/// <summary>
/// The config saves 2a decodes (GR-M10), each holding one JSON object in the form the game's
/// <c>StorageManager</c> writes:
/// <list type="bullet">
/// <item>MV <c>config.rpgsave</c>: LZ-String base64 of the JSON text.</item>
/// <item>MZ <c>config.rmmzsave</c>: the zlib deflate of the UTF-8 JSON, each byte one character (pako's binary
/// string), and that text stored as UTF-8. Checked against the MZ editor's new-project scripts.</item>
/// </list>
/// Input that does not decode is refused, never guessed, so the text is checked before and after each step.
/// </summary>
internal static class ConfigSaveText
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <param name="fileKey">A key <see cref="RpgDataSchema.IsDecodedConfigSave"/> accepts; it picks the form.</param>
    public static bool TryDecode(string fileKey, string? text, [NotNullWhen(true)] out JObject? config, [NotNullWhen(false)] out string? error)
    {
        config = null;
        string? json = RpgDataSchema.IsMzConfigSave(fileKey) ? InflateMz(text, out error) : DecompressMv(text, out error);
        if (json is null)
        {
            error ??= "it does not decode";
            return false;
        }

        try
        {
            config = MergeJson.Parse(json) as JObject;
        }
        catch (JsonReaderException exception)
        {
            error = $"it decodes to text that is not valid JSON (line {exception.LineNumber}, position {exception.LinePosition})";
            return false;
        }

        error = config is null ? "it decodes to JSON that is not an object" : null;
        return config is not null;
    }

    public static string Encode(string fileKey, JObject config)
    {
        string json = MergeJson.Write(config);
        if (!RpgDataSchema.IsMzConfigSave(fileKey))
        {
            return LZString.CompressToBase64(json);
        }

        using var buffer = new MemoryStream();
        using (var zlib = new ZLibStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(StrictUtf8.GetBytes(json));
        }

        // Latin-1 maps byte n to character n, the binary string pako's deflate returns.
        return Encoding.Latin1.GetString(buffer.ToArray());
    }

    private static string? DecompressMv(string? text, out string? error)
    {
        error = null;
        string encoded = text?.Trim() ?? "";
        if (encoded.Length == 0 || !encoded.All(IsBase64Char))
        {
            error = "it is not LZ-String base64";
            return null;
        }

        try
        {
            string? json = LZString.DecompressFromBase64(encoded);
            return string.IsNullOrEmpty(json) ? null : json;
        }
        catch (Exception exception) when (exception is IndexOutOfRangeException or KeyNotFoundException)
        {
            // The library's own faults on a truncated or foreign text; both mean "does not decode".
            return null;
        }
    }

    private static string? InflateMz(string? text, out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(text) || text.Any(current => current > byte.MaxValue))
        {
            error = "it is not a zlib byte string";
            return null;
        }

        try
        {
            using var input = new MemoryStream(Encoding.Latin1.GetBytes(text));
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            zlib.CopyTo(output);
            return output.Length == 0 ? null : StrictUtf8.GetString(output.ToArray());
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (DecoderFallbackException)
        {
            error = "it decodes to bytes that are not UTF-8";
            return null;
        }
    }

    private static bool IsBase64Char(char current)
    {
        return char.IsAsciiLetterOrDigit(current) || current is '+' or '/' or '=';
    }
}
