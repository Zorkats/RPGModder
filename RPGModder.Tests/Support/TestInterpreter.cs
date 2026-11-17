using Newtonsoft.Json.Linq;

namespace RPGModder.Tests.Support;

/// <summary>
/// The load step of RPG Maker's <c>DataManager</c> for one database file, written from its behavior in the MV and MZ
/// core scripts (TST-15). The game parses the file as strict JSON, then reads the note tags of every entry that is
/// an object; reading tags cannot fail. Every later lookup takes a record by its index, so this also checks what the
/// editor writes: entry 0 is <c>null</c> and every other entry is <c>null</c> or an object whose <c>id</c> is its
/// index. It runs no game code.
/// </summary>
internal static class TestInterpreter
{
    /// <summary>Returns the records of <paramref name="text"/>, or throws <see cref="InvalidDataException"/> naming the first problem.</summary>
    public static JArray LoadDatabase(string file, string text)
    {
        try
        {
            // Newtonsoft accepts comments, NaN and single quotes; JSON.parse in the game does not.
            System.Text.Json.JsonDocument.Parse(text).Dispose();
        }
        catch (System.Text.Json.JsonException error)
        {
            throw new InvalidDataException($"{file}: not JSON the game can parse ({error.Message}).", error);
        }

        if (JToken.Parse(text) is not JArray records)
        {
            throw new InvalidDataException($"{file}: the root is not a list.");
        }

        if (records.Count == 0 || records[0].Type != JTokenType.Null)
        {
            throw new InvalidDataException($"{file}: entry 0 is not null.");
        }

        for (int index = 1; index < records.Count; index++)
        {
            JToken entry = records[index];
            if (entry.Type == JTokenType.Null)
            {
                continue;
            }

            if (entry is not JObject record || record["id"] is not JValue { Type: JTokenType.Integer } id || id.Value<int>() != index)
            {
                throw new InvalidDataException($"{file}: entry {index} is not a record whose id is {index}.");
            }
        }

        return records;
    }
}
