using Newtonsoft.Json.Linq;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace RPGModder.Core.Merge;

/// <summary>
/// Where an id is referenced (guide 3.11, reference table), for the two kinds of step 1. <see cref="Scan"/> reads a
/// change set's free text and the references the table cannot rewrite (GR-R9); <see cref="Rewrite"/> moves every
/// reference the table lists. <c>p[n]</c> below is <c>parameters[n]</c> of an event or route command; only values
/// above 0 are ids. Every position was checked against the MV and MZ core scripts.
/// </summary>
internal static class ReferenceMap
{
    // Route command (inside a moveRoute list): Script.
    private const int RouteScript = 45;

    // Event commands.
    private const int ConditionalBranch = 111;
    private const int ControlVariables = 122;
    private const int TransferPlayer = 201;
    private const int SetVehicleLocation = 202;
    private const int SetEventLocation = 203;
    private const int SetMovementRoute = 205;
    private const int ShowAnimation = 212;
    private const int ShowBalloon = 213;
    private const int GetLocationInfo = 285;
    private const int Script = 355;
    private const int PluginCommandMv = 356;
    private const int PluginCommandMz = 357;
    private const int ScriptMore = 655;

    // 111 p[0], 122 p[3] and p[4], 201 p[0], 202 p[1], 203 p[1], 285 p[2]: the operand forms the table names.
    private const int BranchCharacter = 6;
    private const int BranchScript = 12;
    private const int OperandConstant = 0;
    private const int OperandGameData = 3;
    private const int OperandScript = 4;
    private const int GameDataCharacter = 5;
    private const int DesignationDirect = 0;
    private const int DesignationByVariable = 1;
    private const int EventLocationExchange = 2;
    private const int LocationByCharacter = 2;

    private const string NoteProperty = "note";
    private const string DamageProperty = "damage";
    private const string FormulaProperty = "formula";
    private const string ParentIdProperty = "parentId";
    private const string IdProperty = "id";
    private const string ParametersProperty = "parameters";
    private const string CodeProperty = "code";

    /// <summary>The <c>System.json</c> places that hold a map id.</summary>
    private static readonly HashSet<string> StartMapPaths = new(StringComparer.Ordinal)
    {
        "startMapId",
        "boat.startMapId",
        "ship.startMapId",
        "airship.startMapId"
    };

    /// <summary>What in one mod's change set stops a move (GR-R9) or makes it unchecked (GR-R11).</summary>
    public static ModReferences Scan(ModChangeSet changeSet, IReadOnlyList<string> pluginScripts)
    {
        var references = new ModReferences();
        foreach (ChangeOp op in changeSet.Ops)
        {
            if (op.Value is null || MergePath.IsPseudoLeaf(op.Path))
            {
                continue;
            }

            IReadOnlyList<PathSegment> segments = MergePath.Parse(op.Path);
            string? name = segments.Count > 0 ? segments[^1].Name : null;
            string? parent = segments.Count > 1 ? segments[^2].Name : null;
            bool isPluginList = op.Key.File == RpgDataSchema.PluginListKey;
            var context = new ScanContext(references, RpgDataSchema.IsMapFile(op.Key.File), isPluginList);

            // Plugin parameters the mod sets are free text (GR-R9), whether the op names one or the whole entry.
            bool inParameters = isPluginList && segments.Count > 0 && segments[0].Name == ParametersProperty;
            Visit(op.Value, name, parent, inParameters, context);
        }

        foreach (string script in pluginScripts)
        {
            AddNumbers(script, references.PluginScriptNumbers);
        }

        return references;
    }

    /// <summary>
    /// <paramref name="op"/> with every listed reference moved by <paramref name="table"/>, or the op itself when
    /// nothing moved. A whole new file whose content changed loses its source file, so it is written as text.
    /// </summary>
    public static ChangeOp Rewrite(ChangeOp op, RemapTable table)
    {
        RecordKey key = op.Key;
        JToken? value = op.Value?.DeepClone();
        if (op.Kind == ChangeKind.AddRecord && op.Path.Length == 0)
        {
            if (key.IsWholeFile && RpgDataSchema.TryGetMapId(key.File, out int mapId) && table.Maps.TryGetValue(mapId, out int newMap))
            {
                key = new RecordKey(RpgDataSchema.MapKey(newMap), RecordKey.WholeFile);
            }
            else if (key.File == RpgDataSchema.MapInfosKey && RecordKey.TryGetIndex(key.Record, out int infoId) &&
                     table.Maps.TryGetValue(infoId, out int newInfo))
            {
                key = key with { Record = RecordKey.ForIndex(newInfo) };
                SetOwnId(value, infoId, newInfo);
            }
            else if (table.Events.TryGetValue(key.File, out Dictionary<int, int>? moved) && RecordKey.TryGetEvent(key.Record, out int eventId) &&
                     moved.TryGetValue(eventId, out int newEvent))
            {
                key = key with { Record = RecordKey.ForEvent(newEvent) };
                SetOwnId(value, eventId, newEvent);
            }
        }

        if (value is not null && !MergePath.IsPseudoLeaf(op.Path))
        {
            // Event references are scoped to the map file the commands sit in, so the op's own file decides.
            Dictionary<int, int>? events = table.Events.GetValueOrDefault(op.Key.File);
            VisitCommands(value, command => RewriteCommand(command, table.Maps, events));
            if (op.Key.File == RpgDataSchema.MapInfosKey && !op.Key.IsWholeFile)
            {
                // A map's parent, in the moved map's own record or in a vanilla map the mod hangs under it.
                value = RewriteAt(value, op.Path, path => path == ParentIdProperty, table.Maps);
            }
            else if (op.Key.File == RpgDataSchema.SystemFileKey)
            {
                value = RewriteAt(value, op.Path, StartMapPaths.Contains, table.Maps);
            }
        }

        bool changed = key != op.Key || !JToken.DeepEquals(value, op.Value);
        if (!changed)
        {
            return op;
        }

        string? source = op.Kind == ChangeKind.AddRecord && op.Key.IsWholeFile ? null : op.SourceFile;
        return op with { Key = key, Value = value, SourceFile = source };
    }

    private static void Visit(JToken token, string? name, string? parent, bool inParameters, ScanContext context)
    {
        if (inParameters)
        {
            AddNumbers(token, context.References.FreeTextNumbers);
            return;
        }

        switch (token)
        {
            case JObject item:
                if (TryCommand(item, out int code, out JArray? parameters))
                {
                    ScanCommand(code, parameters, context);
                }

                foreach (JProperty property in item.Properties())
                {
                    bool pluginParameters = context.IsPluginList && property.Name == ParametersProperty;
                    Visit(property.Value, property.Name, name, pluginParameters, context);
                }

                break;
            case JArray list:
                foreach (JToken element in list)
                {
                    Visit(element, null, name, false, context);
                }

                break;
            case JValue { Type: JTokenType.String } text when name == NoteProperty || (name == FormulaProperty && parent == DamageProperty):
                AddNumbers(text, context.References.FreeTextNumbers);
                break;
        }
    }

    private static void ScanCommand(int code, JArray parameters, ScanContext context)
    {
        ModReferences references = context.References;
        switch (code)
        {
            case Script or ScriptMore or PluginCommandMv or RouteScript:
                AddNumbers(At(parameters, 0), references.FreeTextNumbers);
                break;
            case PluginCommandMz:
                AddNumbers(At(parameters, 3), references.FreeTextNumbers);
                break;
            case ConditionalBranch when Is(parameters, 0, BranchScript):
                AddNumbers(At(parameters, 1), references.FreeTextNumbers);
                break;
            case ControlVariables when Is(parameters, 3, OperandScript):
                AddNumbers(At(parameters, 4), references.FreeTextNumbers);
                break;
            case ControlVariables when Is(parameters, 3, OperandConstant):
                AddId(At(parameters, 4), references.VariableConstants);
                break;
            case TransferPlayer when Is(parameters, 0, DesignationByVariable):
            case SetVehicleLocation when Is(parameters, 1, DesignationByVariable):
                references.UsesVariableTransfer = true;
                break;
        }

        if (context.IsMapFile)
        {
            return;
        }

        // Outside a map the event commands act on whatever map is loaded, so their ids cannot be rewritten (3.11).
        foreach (int index in EventPositions(code, parameters))
        {
            AddId(At(parameters, index), references.AmbiguousEventIds);
        }
    }

    private static void RewriteCommand(JObject command, Dictionary<int, int> maps, Dictionary<int, int>? events)
    {
        if (!TryCommand(command, out int code, out JArray? parameters))
        {
            return;
        }

        if (maps.Count > 0)
        {
            if (code == TransferPlayer && Is(parameters, 0, DesignationDirect))
            {
                Move(parameters, 1, maps);
            }
            else if (code == SetVehicleLocation && Is(parameters, 1, DesignationDirect))
            {
                Move(parameters, 2, maps);
            }
        }

        if (events is not null)
        {
            foreach (int index in EventPositions(code, parameters))
            {
                Move(parameters, index, events);
            }
        }
    }

    /// <summary>The positions of a command that hold an event id of the current map, from the reference table.</summary>
    private static IEnumerable<int> EventPositions(int code, JArray parameters)
    {
        switch (code)
        {
            case SetEventLocation:
                yield return 0;
                if (Is(parameters, 1, EventLocationExchange))
                {
                    yield return 2;
                }

                break;
            case SetMovementRoute or ShowAnimation or ShowBalloon:
                yield return 0;
                break;
            case ConditionalBranch when Is(parameters, 0, BranchCharacter):
                yield return 1;
                break;
            case ControlVariables when Is(parameters, 3, OperandGameData) && Is(parameters, 4, GameDataCharacter):
                yield return 5;
                break;
            case GetLocationInfo when Is(parameters, 2, LocationByCharacter):
                yield return 3;
                break;
        }
    }

    /// <summary>Every command object in <paramref name="token"/>, including route commands inside a move route.</summary>
    private static void VisitCommands(JToken token, Action<JObject> visit)
    {
        switch (token)
        {
            case JObject item:
                visit(item);
                foreach (JProperty property in item.Properties())
                {
                    VisitCommands(property.Value, visit);
                }

                break;
            case JArray list:
                foreach (JToken element in list)
                {
                    VisitCommands(element, visit);
                }

                break;
        }
    }

    /// <summary>
    /// Moves the map ids at the places <paramref name="isPlace"/> accepts, walking <paramref name="token"/> down
    /// from its own path <paramref name="path"/> (relative to the record, as op paths are).
    /// </summary>
    private static JToken RewriteAt(JToken token, string path, Func<string, bool> isPlace, Dictionary<int, int> maps)
    {
        if (isPlace(path))
        {
            return token is JValue value && TryId(value, out int id) && maps.TryGetValue(id, out int moved) ? new JValue(moved) : token;
        }

        if (token is not JObject item)
        {
            return token;
        }

        foreach (JProperty property in item.Properties().ToList())
        {
            property.Value = RewriteAt(property.Value, MergePath.Child(path, property.Name), isPlace, maps);
        }

        return item;
    }

    private static void SetOwnId(JToken? value, int oldId, int newId)
    {
        if (value is JObject record && record[IdProperty] is JValue id && TryId(id, out int current) && current == oldId)
        {
            record[IdProperty] = newId;
        }
    }

    private static void Move(JArray parameters, int index, Dictionary<int, int> moves)
    {
        if (At(parameters, index) is JValue value && TryId(value, out int id) && moves.TryGetValue(id, out int moved))
        {
            parameters[index] = moved;
        }
    }

    private static bool TryCommand(JObject item, out int code, [NotNullWhen(true)] out JArray? parameters)
    {
        parameters = item[ParametersProperty] as JArray;
        code = 0;
        return parameters is not null && item[CodeProperty] is JValue { Type: JTokenType.Integer } value && TryInt(value, out code);
    }

    private static JToken? At(JArray parameters, int index)
    {
        return index < parameters.Count ? parameters[index] : null;
    }

    private static bool Is(JArray parameters, int index, int expected)
    {
        return At(parameters, index) is JValue value && TryInt(value, out int actual) && actual == expected;
    }

    /// <summary>An id is a whole number above 0 (reference table).</summary>
    private static bool TryId(JValue value, out int id)
    {
        return TryInt(value, out id) && id > 0;
    }

    private static bool TryInt(JValue value, out int number)
    {
        number = 0;
        if (value.Type != JTokenType.Integer || value.Value is not long whole || whole is < int.MinValue or > int.MaxValue)
        {
            return false;
        }

        number = (int)whole;
        return true;
    }

    private static void AddId(JToken? token, HashSet<long> ids)
    {
        if (token is JValue value && TryId(value, out int id))
        {
            ids.Add(id);
        }
    }

    /// <summary>Every whole number in <paramref name="token"/>: each run of ASCII digits in its text, and each integer.</summary>
    private static void AddNumbers(JToken? token, HashSet<long> numbers)
    {
        switch (token)
        {
            case JValue { Type: JTokenType.String } text:
                AddNumbers(text.Value<string>() ?? "", numbers);
                break;
            case JValue { Type: JTokenType.Integer, Value: long whole }:
                numbers.Add(whole);
                break;
            case JContainer container:
                foreach (JToken child in container.Children())
                {
                    AddNumbers(child is JProperty property ? property.Value : child, numbers);
                }

                break;
        }
    }

    private static void AddNumbers(string text, HashSet<long> numbers)
    {
        int index = 0;
        while (index < text.Length)
        {
            if (!char.IsAsciiDigit(text[index]))
            {
                index++;
                continue;
            }

            int start = index;
            while (index < text.Length && char.IsAsciiDigit(text[index]))
            {
                index++;
            }

            // A run too long for a long names no id; "003" is 3, so a padded map name counts.
            if (long.TryParse(text.AsSpan(start, index - start), NumberStyles.None, CultureInfo.InvariantCulture, out long number))
            {
                numbers.Add(number);
            }
        }
    }

    private sealed record ScanContext(ModReferences References, bool IsMapFile, bool IsPluginList);
}
