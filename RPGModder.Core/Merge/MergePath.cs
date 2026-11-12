using System.Globalization;
using System.Text;

namespace RPGModder.Core.Merge;

/// <summary>
/// File keys (GR-M2) and the leaf paths inside a record. A leaf path joins property names with <c>.</c> and
/// writes indexes as <c>[n]</c>: <c>pages[2].conditions.switch1Id</c>. A property name that is not a plain
/// identifier is written <c>['the name']</c>, so a path never reads two ways. The empty path is the record itself.
/// </summary>
public static class MergePath
{
    /// <summary>
    /// The order pseudo-leaf of a plugin record (seam 11.1): not a property of the entry but its place in the list.
    /// A pseudo-leaf starts with <c>@</c>, which <see cref="Child"/> never writes bare, so it cannot be mistaken
    /// for a real property (a property named <c>@after</c> is written <c>['@after']</c>).
    /// </summary>
    internal const string AfterLeaf = "@after";

    /// <summary>
    /// The place of an <c>OrderAmbiguous</c> collision among entries that more than one mod moves to the front of
    /// the list (seam 11.1). No op carries it: it only names that collision apart from the one for new entries.
    /// </summary>
    internal const string FirstLeaf = "@first";

    internal static bool IsPseudoLeaf(string path)
    {
        return path.Length > 0 && path[0] == '@';
    }

    /// <summary>Back slashes become forward slashes, a leading <c>./</c> or <c>/</c> is removed, the result is lower case.</summary>
    public static string ToKey(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        string key = path.Replace('\\', '/');
        while (true)
        {
            if (key.StartsWith("./", StringComparison.Ordinal))
            {
                key = key[2..];
            }
            else if (key.StartsWith('/'))
            {
                key = key[1..];
            }
            else
            {
                break;
            }
        }

        return key.ToLowerInvariant();
    }

    internal static string Child(string parent, string propertyName)
    {
        string segment = IsIdentifier(propertyName)
            ? propertyName
            : "['" + propertyName.Replace("\\", "\\\\").Replace("'", "\\'") + "']";

        if (parent.Length == 0)
        {
            return segment;
        }

        return segment[0] == '[' ? parent + segment : parent + "." + segment;
    }

    internal static string Index(string parent, int index)
    {
        return parent + "[" + index.ToString(CultureInfo.InvariantCulture) + "]";
    }

    /// <summary>
    /// Every path that contains <paramref name="path"/>, shortest first: the empty path, then each longer prefix,
    /// without the path itself. A pseudo-leaf has none, so it never joins its record's cluster.
    /// </summary>
    internal static IEnumerable<string> Ancestors(string path)
    {
        if (path.Length == 0 || IsPseudoLeaf(path))
        {
            yield break;
        }

        IReadOnlyList<PathSegment> segments = Parse(path);
        string prefix = "";
        yield return prefix;
        for (int index = 0; index < segments.Count - 1; index++)
        {
            prefix = Append(prefix, segments[index]);
            yield return prefix;
        }
    }

    /// <summary>The path with every index replaced by <c>[*]</c>, the form <see cref="RpgDataSchema"/> keys its policies by.</summary>
    internal static string Pattern(string path)
    {
        var builder = new StringBuilder();
        foreach (PathSegment segment in Parse(path))
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

    /// <summary>Reads a path written by <see cref="Child"/> and <see cref="Index"/>. Throws <see cref="FormatException"/> for anything else.</summary>
    internal static IReadOnlyList<PathSegment> Parse(string path)
    {
        var segments = new List<PathSegment>();
        int position = 0;
        while (position < path.Length)
        {
            char current = path[position];
            if (current == '[' && position + 1 < path.Length && path[position + 1] == '\'')
            {
                position = ReadQuotedName(path, position + 2, segments);
            }
            else if (current == '[')
            {
                position = ReadIndex(path, position + 1, segments);
            }
            else
            {
                if (segments.Count > 0)
                {
                    if (current != '.')
                    {
                        throw new FormatException($"Unexpected character in leaf path: {path}");
                    }

                    position++;
                }

                position = ReadIdentifier(path, position, segments);
            }
        }

        return segments;
    }

    internal static string Format(IEnumerable<PathSegment> segments)
    {
        string path = "";
        foreach (PathSegment segment in segments)
        {
            path = Append(path, segment);
        }

        return path;
    }

    private static string Append(string parent, PathSegment segment)
    {
        return segment.Name is null ? Index(parent, segment.Index) : Child(parent, segment.Name);
    }

    private static int ReadQuotedName(string path, int position, List<PathSegment> segments)
    {
        var name = new StringBuilder();
        while (position < path.Length)
        {
            char current = path[position];
            if (current == '\\' && position + 1 < path.Length)
            {
                name.Append(path[position + 1]);
                position += 2;
            }
            else if (current == '\'' && position + 1 < path.Length && path[position + 1] == ']')
            {
                segments.Add(PathSegment.Property(name.ToString()));
                return position + 2;
            }
            else
            {
                name.Append(current);
                position++;
            }
        }

        throw new FormatException($"Unterminated name in leaf path: {path}");
    }

    private static int ReadIndex(string path, int position, List<PathSegment> segments)
    {
        int end = path.IndexOf(']', position);
        if (end < 0 ||
            !int.TryParse(path.AsSpan(position, end - position), NumberStyles.None, CultureInfo.InvariantCulture, out int index))
        {
            throw new FormatException($"Bad index in leaf path: {path}");
        }

        segments.Add(PathSegment.At(index));
        return end + 1;
    }

    private static int ReadIdentifier(string path, int position, List<PathSegment> segments)
    {
        int start = position;
        while (position < path.Length && path[position] is not ('.' or '['))
        {
            position++;
        }

        string name = path[start..position];
        if (!IsIdentifier(name))
        {
            throw new FormatException($"Bad property name in leaf path: {path}");
        }

        segments.Add(PathSegment.Property(name));
        return position;
    }

    private static bool IsIdentifier(string name)
    {
        if (name.Length == 0 || !(char.IsAsciiLetter(name[0]) || name[0] is '_' or '$'))
        {
            return false;
        }

        foreach (char current in name)
        {
            if (!(char.IsAsciiLetterOrDigit(current) || current is '_' or '$'))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>One step of a leaf path: a property name, or an array index when <c>Name</c> is <c>null</c>.</summary>
internal readonly record struct PathSegment(string? Name, int Index)
{
    public static PathSegment Property(string name)
    {
        return new PathSegment(name, -1);
    }

    public static PathSegment At(int index)
    {
        return new PathSegment(null, index);
    }
}
