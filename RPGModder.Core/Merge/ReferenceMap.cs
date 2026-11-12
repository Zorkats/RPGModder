using Newtonsoft.Json.Linq;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace RPGModder.Core.Merge;

/// <summary>
/// Where an id is referenced (guide 3.11). One walker reads a change set with the rows of <see cref="ReferenceTable"/>:
/// <see cref="Scan"/> collects what a remap cannot rewrite (GR-R9), <see cref="Rewrite"/> moves every reference the
/// rows name. Only whole numbers above 0 are ids.
/// </summary>
internal static class ReferenceMap
{
    private const string NoteProperty = "note";
    private const string DamageProperty = "damage";
    private const string FormulaProperty = "formula";
    private const string IdProperty = "id";
    private const string ParametersProperty = "parameters";
    private const string CodeProperty = "code";
    private const int CommentCode = 108;
    private const int CommentMoreCode = 408;

    /// <summary>
    /// What in one mod's change set stops a move (GR-R9), makes it unchecked (GR-R11) or uses an id (GR-R7). With
    /// <paramref name="dualWield"/>, the second equip slot may hold a weapon, so its ids are of no known kind. Without
    /// <paramref name="mz"/>, a row only MZ reads so is of no known kind too.
    /// </summary>
    public static ModReferences Scan(ModChangeSet changeSet, IReadOnlyList<string> pluginScripts, bool dualWield, bool mz)
    {
        var references = new ModReferences();
        var siblings = new SiblingOps(changeSet);
        foreach (ChangeOp op in changeSet.Ops)
        {
            if (op.Value is not null && !MergePath.IsPseudoLeaf(op.Path))
            {
                new Walker(op, siblings, dualWield, mz, hit => Collect(hit, references)).Walk(op.Value);
            }
        }

        foreach (string script in pluginScripts)
        {
            AddNumbers(script, references.PluginScriptNumbers);
        }

        return references;
    }

    /// <summary>
    /// The change set's ops with every reference the rows name moved by <paramref name="table"/>, and every added
    /// record that moves on its new id. An op nothing moved is returned as it was. A reference of no known kind is
    /// left alone: the planner never moves an id a mod names so (GR-R9). The second equip slot is read as an armor,
    /// because in a dual-wield game the planner moves neither kind of an id a mod puts there; a row only MZ reads so
    /// is read as on MZ, because on any other engine the planner never moves its id.
    /// </summary>
    public static IReadOnlyList<ChangeOp> Rewrite(ModChangeSet changeSet, RemapTable table)
    {
        var siblings = new SiblingOps(changeSet);
        return changeSet.Ops.Select(op => Rewrite(op, table, siblings)).ToList();
    }

    private static ChangeOp Rewrite(ChangeOp op, RemapTable table, SiblingOps siblings)
    {
        JToken? value = op.Value?.DeepClone();
        RecordKey key = op.Kind == ChangeKind.AddRecord && op.Path.Length == 0 ? OwnKey(op.Key, value, table) : op.Key;
        if (value is not null && !MergePath.IsPseudoLeaf(op.Path))
        {
            // Event references are scoped to the map file the commands sit in, so the op's own file decides.
            Dictionary<int, int>? events = table.Events.GetValueOrDefault(op.Key.File);
            new Walker(op, siblings, dualWield: false, mz: true, hit => Move(hit, table, events)).Walk(value);
        }

        if (key == op.Key && JToken.DeepEquals(value, op.Value))
        {
            return op;
        }

        // A whole new file whose content changed loses its source file, so it is written as text.
        string? source = op.Kind == ChangeKind.AddRecord && op.Key.IsWholeFile ? null : op.SourceFile;
        return op with { Key = key, Value = value, SourceFile = source };
    }

    /// <summary>The key an added record moves to, with its own <c>id</c> set: a map file, a tree record, an event or a database record.</summary>
    private static RecordKey OwnKey(RecordKey key, JToken? value, RemapTable table)
    {
        if (key.IsWholeFile)
        {
            return RpgDataSchema.TryGetMapId(key.File, out int mapId) && table.MovesOf(IdKind.Map) is { } maps && maps.TryGetValue(mapId, out int newMap)
                ? new RecordKey(RpgDataSchema.MapKey(newMap), RecordKey.WholeFile)
                : key;
        }

        Dictionary<int, int>? moves = null;
        Func<int, string> recordOf = RecordKey.ForIndex;
        if (RecordKey.TryGetEvent(key.Record, out int oldId))
        {
            moves = table.Events.GetValueOrDefault(key.File);
            recordOf = RecordKey.ForEvent;
        }
        else if (RecordKey.TryGetIndex(key.Record, out oldId))
        {
            IdKind? kind = key.File == RpgDataSchema.MapInfosKey ? IdKind.Map : RpgDataSchema.DatabaseKindOf(key.File);
            moves = kind is null ? null : table.MovesOf(kind.Value);
        }

        if (moves is null || !moves.TryGetValue(oldId, out int newId))
        {
            return key;
        }

        if (value is JObject record && record[IdProperty] is JValue id && TryId(id, out int current) && current == oldId)
        {
            record[IdProperty] = newId;
        }

        return key with { Record = recordOf(newId) };
    }

    private static void Collect(Hit hit, ModReferences references)
    {
        // An Id hit, or a text code the table knows, is a reference a remap rewrites: it stops nothing, but it uses the id.
        switch (hit.Use)
        {
            case HitUse.Id when hit.Token is JValue value && TryId(value, out int used):
                references.AddUsed(hit.Kind, used);
                break;
            case HitUse.Text or HitUse.Comment when hit.Token is JValue { Value: string text } && text.Contains('\\'):
                foreach ((string letters, _, _, int id) in TextCodesIn(text))
                {
                    if (ReferenceTable.TextCodes.TryGetValue(letters, out IdKind kind))
                    {
                        references.AddUsed(kind, id);
                    }
                    else if (hit.Use == HitUse.Text && !ReferenceTable.BuiltInTextCodes.Contains(letters))
                    {
                        // A plugin's code may name an id of any kind, so it is free text (GR-R9); a comment is not read.
                        references.FreeTextNumbers.Add(id);
                    }
                }

                break;
            case HitUse.FreeText:
                AddNumbers(hit.Token, references.FreeTextNumbers);
                break;
            case HitUse.Ambiguous when hit.Token is JValue value && TryLooseId(value, out int id):
                references.AddAmbiguous(hit.Kind, id);
                break;
            case HitUse.UnknownId:
                references.AddUnknownId(hit.Kind);
                break;
            case HitUse.Constant when hit.Token is JValue value && TryId(value, out int constant):
                references.VariableConstants.Add(constant);
                break;
            case HitUse.RandomRange when hit.Token is JArray { Count: 2 } range && range[0] is JValue low && TryInt(low, out int min) &&
                                         range[1] is JValue high && TryInt(high, out int max):
                references.VariableRanges.Add((Math.Min(min, max), Math.Max(min, max)));
                break;
            case HitUse.ByVariable:
                references.ByVariableKinds.Add(hit.Kind);
                break;
        }
    }

    private static void Move(Hit hit, RemapTable table, Dictionary<int, int>? events)
    {
        if (hit.Token is not JValue value)
        {
            return;
        }

        if (hit.Use is HitUse.Text or HitUse.Comment)
        {
            string text = value.Value<string>() ?? "";
            string moved = MoveTextCodes(text, table);
            if (!ReferenceEquals(moved, text))
            {
                value.Value = moved;
            }

            return;
        }

        Dictionary<int, int>? moves = hit.Kind == IdKind.MapEvent ? events : table.MovesOf(hit.Kind);
        if (hit.Use == HitUse.Id && moves is not null && TryId(value, out int id) && moves.TryGetValue(id, out int newId))
        {
            value.Value = (long)newId;
        }
    }

    /// <summary>
    /// <paramref name="text"/> with every text code that names a moved id rewritten, or the same instance when none
    /// does. A pair of backslashes is a literal backslash, as the game reads it; a code letter matches in either case.
    /// </summary>
    private static string MoveTextCodes(string text, RemapTable table)
    {
        StringBuilder? builder = null;
        int copied = 0;
        foreach ((string letters, int start, int end, int id) in TextCodesIn(text))
        {
            if (ReferenceTable.TextCodes.TryGetValue(letters, out IdKind kind) &&
                table.MovesOf(kind) is { } moves &&
                moves.TryGetValue(id, out int newId))
            {
                builder ??= new StringBuilder(text.Length);
                builder.Append(text, copied, start - copied).Append(newId.ToString(CultureInfo.InvariantCulture));
                copied = end;
            }
        }

        return builder is null ? text : builder.Append(text, copied, text.Length - copied).ToString();
    }

    /// <summary>
    /// Every text code with a number above 0 in <paramref name="text"/>, its letters in upper case. A pair of
    /// backslashes is a literal backslash, as the game reads it. <c>Start</c> and <c>End</c> bound the digits.
    /// </summary>
    private static IEnumerable<(string Letters, int Start, int End, int Id)> TextCodesIn(string text)
    {
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] != '\\')
            {
                continue;
            }

            if (index + 1 < text.Length && text[index + 1] == '\\')
            {
                index++;
                continue;
            }

            if (TryTextCode(text, index, out string letters, out int start, out int end, out int id))
            {
                yield return (letters, start, end, id);
                index = end;
            }
        }
    }

    /// <summary>A text code at <paramref name="slash"/>: letters, then digits in brackets, as the message window reads it.</summary>
    private static bool TryTextCode(string text, int slash, out string letters, out int start, out int end, out int id)
    {
        letters = "";
        id = 0;
        int open = slash + 1;
        while (open < text.Length && char.IsAsciiLetter(text[open]))
        {
            open++;
        }

        start = open + 1;
        end = start;
        if (open == slash + 1 || open >= text.Length || text[open] != '[')
        {
            return false;
        }

        while (end < text.Length && char.IsAsciiDigit(text[end]))
        {
            end++;
        }

        if (end == start || end >= text.Length || text[end] != ']' ||
            !int.TryParse(text.AsSpan(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture, out id) || id <= 0)
        {
            return false;
        }

        letters = text[(slash + 1)..open].ToUpperInvariant();
        return true;
    }

    private static bool TryCommand(JObject item, out int code, [NotNullWhen(true)] out JArray? parameters)
    {
        parameters = item[ParametersProperty] as JArray;
        code = 0;
        return parameters is not null && item[CodeProperty] is JValue value && TryInt(value, out code);
    }

    private static JToken? At(JArray parameters, int index)
    {
        return index < parameters.Count ? parameters[index] : null;
    }

    private static bool Holds(JArray parameters, ParameterTest test)
    {
        return At(parameters, test.Index) is JValue value && TryInt(value, out int actual) && actual >= test.Min && actual <= test.Max;
    }

    /// <summary>An id is a whole number above 0 (reference table).</summary>
    private static bool TryId(JValue value, out int id)
    {
        return TryInt(value, out id) && id > 0;
    }

    /// <summary>
    /// An id the game reads although the editor never writes it so: a whole number stored as a float (<c>3.0</c>) or
    /// as a string of digits (<c>"3"</c>), which JavaScript turns into the same array index.
    /// </summary>
    private static bool TryLooseId(JValue value, out int id)
    {
        id = 0;
        switch (value.Value)
        {
            case long:
                return TryId(value, out id);
            case double real when real is >= 1 and <= int.MaxValue && real == Math.Floor(real):
                id = (int)real;
                return true;
            case decimal exact when exact is >= 1 and <= int.MaxValue && exact == decimal.Truncate(exact):
                id = (int)exact;
                return true;
            case string text when text.Length > 0 && text[0] != '0' && text.All(char.IsAsciiDigit):
                return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id);
            default:
                return false;
        }
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

    private enum HitUse
    {
        /// <summary>An id of a known kind, which a remap rewrites.</summary>
        Id,

        /// <summary>
        /// An id that may be of <see cref="Hit.Kind"/> or of another kind, or that is not stored as an integer, so a
        /// remap cannot follow it.
        /// </summary>
        Ambiguous,

        /// <summary>Some id of <see cref="Hit.Kind"/> that only vanilla holds; which one, this walk cannot see.</summary>
        UnknownId,

        FreeText,
        Constant,

        /// <summary>A random range's two ends, as a new array of two values.</summary>
        RandomRange,

        ByVariable,

        /// <summary>A string, whose text codes a remap rewrites.</summary>
        Text,

        /// <summary>A string of a comment (108, 408): its text codes are rewritten, but a plugin's code stops nothing.</summary>
        Comment
    }

    private readonly record struct Hit(HitUse Use, IdKind Kind, JToken Token);

    /// <summary>
    /// The values other ops of the same change set set: the sibling a field row asks for when the op is a single
    /// leaf (a drop's <c>dataId</c> without its <c>kind</c>).
    /// </summary>
    private sealed class SiblingOps
    {
        private readonly ModChangeSet _changeSet;
        private Dictionary<(RecordKey Key, string Path), JToken?>? _values;

        public SiblingOps(ModChangeSet changeSet)
        {
            _changeSet = changeSet;
        }

        /// <summary>False when the change set has no op there, so the value is vanilla's and unknown here.</summary>
        public bool TryGet(RecordKey key, string path, out JToken? value)
        {
            _values ??= _changeSet.Ops
                .GroupBy(op => (op.Key, op.Path))
                .ToDictionary(group => group.Key, group => group.Last().Value);
            return _values.TryGetValue((key, path), out value);
        }
    }

    /// <summary>
    /// One walk over one op's value. The path is kept as segments relative to the record (or to the root of an
    /// object file) and turned into a pattern only at a leaf whose name a field row ends in, so a map's tile data
    /// builds no strings.
    /// </summary>
    private sealed class Walker
    {
        private readonly ChangeOp _op;
        private readonly SiblingOps _siblings;
        private readonly bool _dualWield;
        private readonly bool _mz;
        private readonly Action<Hit> _emit;
        private bool _inComment;
        private readonly string _table;
        private readonly bool _isMap;
        private readonly List<PathSegment> _path;

        public Walker(ChangeOp op, SiblingOps siblings, bool dualWield, bool mz, Action<Hit> emit)
        {
            _op = op;
            _siblings = siblings;
            _dualWield = dualWield;
            _mz = mz;
            _emit = emit;
            _table = RpgDataSchema.TableOf(op.Key.File);
            _isMap = RpgDataSchema.IsMapFile(op.Key.File);
            _path = MergePath.Parse(op.Path).ToList();
        }

        public void Walk(JToken value)
        {
            if (_op.Key.File == RpgDataSchema.PluginListKey)
            {
                WalkPluginList(value);
                return;
            }

            bool recordsFile = RpgDataSchema.DatabaseKindOf(_op.Key.File) is not null || _op.Key.File == RpgDataSchema.MapInfosKey;
            if (_op.Key.IsWholeFile && _path.Count == 0 && recordsFile && value is JArray records)
            {
                // A whole records file: each element is a record, and paths start again inside it.
                foreach (JToken record in records)
                {
                    Visit(record, null, null, isRoot: false);
                }

                return;
            }

            Visit(value, LastName(_path), null, isRoot: true);
        }

        /// <summary>Plugin parameters the mod sets are free text (GR-R9), whether the op names one or the whole entry.</summary>
        private void WalkPluginList(JToken value)
        {
            if (_path.Count > 0 && _path[0].Name == ParametersProperty)
            {
                _emit(new Hit(HitUse.FreeText, default, value));
                return;
            }

            foreach (JToken parameters in value.SelectTokens("$.." + ParametersProperty))
            {
                _emit(new Hit(HitUse.FreeText, default, parameters));
            }
        }

        /// <param name="token">The token at <see cref="_path"/>.</param>
        /// <param name="name">The property name of the token, or of its array for an element.</param>
        /// <param name="parent">The object that holds the token as a property, when it is part of this value.</param>
        /// <param name="isRoot">True for the op's own value, whose siblings are other ops.</param>
        private void Visit(JToken token, string? name, JObject? parent, bool isRoot)
        {
            switch (token)
            {
                case JObject item:
                    bool wasComment = _inComment;
                    if (TryCommand(item, out int code, out JArray? parameters))
                    {
                        VisitCommand(code, parameters);
                        _inComment = code is CommentCode or CommentMoreCode;
                    }

                    foreach (JProperty property in item.Properties())
                    {
                        _path.Add(PathSegment.Property(property.Name));
                        Visit(property.Value, property.Name, item, isRoot: false);
                        _path.RemoveAt(_path.Count - 1);
                    }

                    _inComment = wasComment;
                    break;
                case JArray list when _isMap && _op.Key.IsWholeFile && _path.Count == 1 && _path[0].Name == RpgDataSchema.EventsProperty:
                    // A whole map's events are records: paths start again inside each one.
                    _path.Clear();
                    foreach (JToken record in list)
                    {
                        Visit(record, null, null, isRoot: false);
                    }

                    _path.Add(PathSegment.Property(RpgDataSchema.EventsProperty));
                    break;
                case JArray list:
                    for (int index = 0; index < list.Count; index++)
                    {
                        _path.Add(PathSegment.At(index));
                        Visit(list[index], name, null, isRoot: false);
                        _path.RemoveAt(_path.Count - 1);
                    }

                    break;
                case JValue { Type: JTokenType.String } text:
                    if (name == NoteProperty || (name == FormulaProperty && _path.Count > 1 && _path[^2].Name == DamageProperty))
                    {
                        _emit(new Hit(HitUse.FreeText, default, text));
                    }

                    _emit(new Hit(_inComment ? HitUse.Comment : HitUse.Text, default, text));
                    if (name is not null && ReferenceTable.MayBeField(name))
                    {
                        VisitField(text, parent, isRoot);
                    }

                    break;
                case JValue { Type: JTokenType.Integer or JTokenType.Float } number when name is not null && ReferenceTable.MayBeField(name):
                    VisitField(number, parent, isRoot);
                    break;
                case JValue { Type: JTokenType.Integer } number when isRoot && name is not null && ReferenceTable.MayBeSibling(name):
                    VisitSibling(number);
                    break;
            }
        }

        private void VisitCommand(int code, JArray parameters)
        {
            foreach (CommandPlace row in ReferenceTable.Commands(code))
            {
                if (!row.When.All(test => Holds(parameters, test)))
                {
                    continue;
                }

                JToken? place = At(parameters, row.Index);
                switch (row.Use)
                {
                    case PlaceUse.Id when place is JValue value && row.Kind is { } kind && row.MzOnly && !_mz:
                        // MV reads the parameter another way (285 p[2] 2 is by variable there), so its kind is unknown.
                        _emit(new Hit(HitUse.Ambiguous, kind, value));
                        break;
                    case PlaceUse.Id when place is JValue value && row.Kind is { } kind:
                        // Outside a map the event commands act on whatever map is loaded, so their ids are no map's (3.11).
                        EmitId(kind, value, row.SecondSlot, scoped: kind != IdKind.MapEvent || _isMap);
                        break;
                    case PlaceUse.FreeText when place is not null:
                        _emit(new Hit(HitUse.FreeText, default, place));
                        break;
                    case PlaceUse.Constant when place is not null:
                        _emit(new Hit(HitUse.Constant, default, place));
                        break;
                    case PlaceUse.RandomRange when place is JValue low && At(parameters, row.Index + 1) is JValue high:
                        _emit(new Hit(HitUse.RandomRange, default, new JArray(low.DeepClone(), high.DeepClone())));
                        break;
                    case PlaceUse.ByVariable when row.Kind is { } kind:
                        _emit(new Hit(HitUse.ByVariable, kind, parameters));
                        break;
                }
            }
        }

        private void VisitField(JValue number, JObject? parent, bool isRoot)
        {
            IReadOnlyList<FieldPlace> rows = ReferenceTable.Fields(_table, Pattern(_path));
            if (rows.Count == 0)
            {
                return;
            }

            int last = _path[^1].Name is null ? _path[^1].Index : 0;
            foreach (FieldPlace row in rows.Where(item => last >= item.MinIndex && last <= item.MaxIndex))
            {
                if (row.When is null)
                {
                    EmitId(row.Kind, number, row.SecondSlot, scoped: true);
                }
                else if (!TrySibling(row.When.Sibling, parent, isRoot, out int? sibling))
                {
                    // The sibling is vanilla's, which this walk cannot see: the id may be of any kind the rows name.
                    _emit(new Hit(HitUse.Ambiguous, row.Kind, number));
                }
                else if (sibling is { } known && row.When.Values.Contains(known))
                {
                    EmitId(row.Kind, number, row.SecondSlot, scoped: true);
                }
            }
        }

        /// <summary>
        /// A leaf op that sets only a deciding sibling (a drop's <c>kind</c>): when no op sets the id beside it, the id
        /// is vanilla's, and it now names the row's kind without this walk seeing which id it is.
        /// </summary>
        private void VisitSibling(JValue number)
        {
            IReadOnlyList<FieldPlace> rows = ReferenceTable.FieldsBySibling(_table, Pattern(_path));
            if (rows.Count == 0 || !TryInt(number, out int sibling))
            {
                return;
            }

            string parentPath = MergePath.Format(_path.Take(_path.Count - 1));
            foreach (FieldPlace row in rows.Where(item => item.When!.Values.Contains(sibling)))
            {
                if (!_siblings.TryGet(_op.Key, MergePath.Child(parentPath, ReferenceTable.FieldName(row)), out _))
                {
                    _emit(new Hit(HitUse.UnknownId, row.Kind, number));
                }
            }
        }

        /// <summary>
        /// An id of a known kind, unless the place is no map's, the slot may hold either kind, or the id is not stored
        /// as an integer, which a remap does not rewrite.
        /// </summary>
        private void EmitId(IdKind kind, JValue value, bool secondSlot, bool scoped)
        {
            if (!scoped || value.Type != JTokenType.Integer)
            {
                _emit(new Hit(HitUse.Ambiguous, kind, value));
            }
            else if (secondSlot && _dualWield)
            {
                _emit(new Hit(HitUse.Ambiguous, IdKind.Weapon, value));
                _emit(new Hit(HitUse.Ambiguous, IdKind.Armor, value));
            }
            else
            {
                _emit(new Hit(HitUse.Id, kind, value));
            }
        }

        /// <summary>
        /// The whole number in the sibling property <paramref name="name"/>, <c>null</c> when it holds none. False
        /// when the leaf is the op's whole value and no other op sets the sibling.
        /// </summary>
        private bool TrySibling(string name, JObject? parent, bool isRoot, out int? sibling)
        {
            JToken? token = parent?[name];
            if (parent is null && isRoot && _path.Count > 0)
            {
                string path = MergePath.Child(MergePath.Format(_path.Take(_path.Count - 1)), name);
                if (!_siblings.TryGet(_op.Key, path, out token))
                {
                    sibling = null;
                    return false;
                }
            }

            sibling = token is JValue value && TryInt(value, out int whole) ? whole : null;
            return true;
        }

        private static string? LastName(List<PathSegment> path)
        {
            for (int index = path.Count - 1; index >= 0; index--)
            {
                if (path[index].Name is { } name)
                {
                    return name;
                }
            }

            return null;
        }

        private static string Pattern(List<PathSegment> path)
        {
            var builder = new StringBuilder();
            foreach (PathSegment segment in path)
            {
                if (segment.Name is null)
                {
                    builder.Append("[*]");
                }
                else
                {
                    builder.Append(builder.Length == 0 ? "" : ".").Append(segment.Name);
                }
            }

            return builder.ToString();
        }
    }
}
