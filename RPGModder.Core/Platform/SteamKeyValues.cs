using System.Text;

namespace RPGModder.Core.Platform;

/// <summary>
/// A strict reader of Valve's text key-values format, as used by <c>appmanifest_*.acf</c>: quoted
/// keys, each followed by a quoted value or a braced block. Anything else, an unbalanced brace or
/// trailing text, is <see cref="InvalidDataException"/>. Keys compare without regard to case.
/// </summary>
internal sealed class SteamKeyValues
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SteamKeyValues> _blocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _blockOrder = new();

    /// <summary>The names of the child blocks, in file order.</summary>
    public IReadOnlyList<string> BlockNames => _blockOrder;

    public string? Value(string key)
    {
        return _values.TryGetValue(key, out string? value) ? value : null;
    }

    public SteamKeyValues? Block(string key)
    {
        return _blocks.TryGetValue(key, out SteamKeyValues? block) ? block : null;
    }

    /// <summary>Parses a document of one top-level key and its block, and returns that key and block.</summary>
    public static (string Key, SteamKeyValues Root) Parse(string text)
    {
        var reader = new Tokenizer(text);
        string key = reader.ExpectString();
        reader.Expect('{');
        SteamKeyValues root = ParseBlock(reader);
        if (!reader.AtEnd())
        {
            throw new InvalidDataException("Text follows the closing brace of the key-values document.");
        }

        return (key, root);
    }

    private static SteamKeyValues ParseBlock(Tokenizer reader)
    {
        var block = new SteamKeyValues();
        while (true)
        {
            if (reader.TryConsume('}'))
            {
                return block;
            }

            string key = reader.ExpectString();
            if (reader.TryConsume('{'))
            {
                SteamKeyValues child = ParseBlock(reader);
                if (!block._blocks.TryAdd(key, child))
                {
                    throw new InvalidDataException($"The block '{key}' appears twice.");
                }

                block._blockOrder.Add(key);
            }
            else if (!block._values.TryAdd(key, reader.ExpectString()))
            {
                throw new InvalidDataException($"The key '{key}' appears twice.");
            }
        }
    }

    private sealed class Tokenizer
    {
        private readonly string _text;
        private int _position;

        public Tokenizer(string text)
        {
            _text = text;
        }

        public bool AtEnd()
        {
            SkipSpace();
            return _position >= _text.Length;
        }

        public bool TryConsume(char symbol)
        {
            SkipSpace();
            if (_position < _text.Length && _text[_position] == symbol)
            {
                _position++;
                return true;
            }

            return false;
        }

        public void Expect(char symbol)
        {
            if (!TryConsume(symbol))
            {
                throw new InvalidDataException($"Expected '{symbol}' at offset {_position}.");
            }
        }

        public string ExpectString()
        {
            SkipSpace();
            if (_position >= _text.Length || _text[_position] != '"')
            {
                throw new InvalidDataException($"Expected a quoted string at offset {_position}.");
            }

            _position++;
            var value = new StringBuilder();
            while (_position < _text.Length)
            {
                char current = _text[_position++];
                if (current == '"')
                {
                    return value.ToString();
                }

                if (current == '\\')
                {
                    if (_position >= _text.Length)
                    {
                        break;
                    }

                    char escaped = _text[_position++];
                    value.Append(escaped switch
                    {
                        '\\' => '\\',
                        '"' => '"',
                        'n' => '\n',
                        't' => '\t',
                        _ => throw new InvalidDataException($"Unknown escape '\\{escaped}' at offset {_position - 1}.")
                    });
                    continue;
                }

                value.Append(current);
            }

            throw new InvalidDataException("A quoted string is not closed.");
        }

        private void SkipSpace()
        {
            while (_position < _text.Length)
            {
                if (char.IsWhiteSpace(_text[_position]))
                {
                    _position++;
                }
                else if (_position + 1 < _text.Length && _text[_position] == '/' && _text[_position + 1] == '/')
                {
                    while (_position < _text.Length && _text[_position] != '\n')
                    {
                        _position++;
                    }
                }
                else
                {
                    return;
                }
            }
        }
    }
}
