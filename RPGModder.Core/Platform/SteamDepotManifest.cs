using System.Buffers.Binary;
using System.Text;

namespace RPGModder.Core.Platform;

/// <summary>
/// A strict reader of a Steam depot manifest as Steam keeps it in <c>depotcache</c>, written from
/// the file format (CNV-19). The file is a sequence of sections, each a little-endian 32-bit magic
/// and a little-endian 32-bit length followed by that many bytes of protocol-buffer data, closed by
/// an end magic that carries no length. The payload section lists the files; the metadata section
/// names the depot and manifest and says whether file names are encrypted. Only the fields TX-22
/// needs are kept. Any deviation is <see cref="InvalidDataException"/>, never a partial list.
/// </summary>
internal sealed class SteamDepotManifest
{
    public const uint PayloadMagic = 0x71F617D0;
    public const uint MetadataMagic = 0x1F4812BE;
    public const uint SignatureMagic = 0x1B81B817;
    public const uint EndMagic = 0x32C415AB;

    public const uint DirectoryFlag = 0x40;
    public const uint SymlinkFlag = 0x200;

    private SteamDepotManifest(ulong depotId, ulong manifestId, bool filenamesEncrypted, IReadOnlyList<Entry> entries)
    {
        DepotId = depotId;
        ManifestId = manifestId;
        FilenamesEncrypted = filenamesEncrypted;
        Entries = entries;
    }

    public ulong DepotId { get; }
    public ulong ManifestId { get; }
    public bool FilenamesEncrypted { get; }

    /// <summary>The non-directory entries, with names as the depot stores them.</summary>
    public IReadOnlyList<Entry> Entries { get; }

    public static SteamDepotManifest Parse(byte[] data)
    {
        int position = 0;
        byte[]? payload = null;
        byte[]? metadata = null;
        bool ended = false;

        while (!ended)
        {
            uint magic = ReadUInt32(data, ref position);
            switch (magic)
            {
                case EndMagic:
                    ended = true;
                    break;
                case PayloadMagic:
                    payload = TakeOnce(payload, ReadSection(data, ref position), "payload");
                    break;
                case MetadataMagic:
                    metadata = TakeOnce(metadata, ReadSection(data, ref position), "metadata");
                    break;
                case SignatureMagic:
                    ReadSection(data, ref position);
                    break;
                default:
                    throw new InvalidDataException($"Unknown section 0x{magic:X8} at offset {position - 4}.");
            }
        }

        if (position != data.Length)
        {
            throw new InvalidDataException("Bytes follow the end of the depot manifest.");
        }

        if (payload == null || metadata == null)
        {
            throw new InvalidDataException("The depot manifest lacks its payload or its metadata.");
        }

        (ulong depotId, ulong manifestId, bool encrypted) = ReadMetadata(metadata);
        return new SteamDepotManifest(depotId, manifestId, encrypted, encrypted ? Array.Empty<Entry>() : ReadPayload(payload));
    }

    private static byte[] TakeOnce(byte[]? existing, byte[] section, string name)
    {
        if (existing != null)
        {
            throw new InvalidDataException($"The depot manifest has two {name} sections.");
        }

        return section;
    }

    private static (ulong DepotId, ulong ManifestId, bool Encrypted) ReadMetadata(byte[] metadata)
    {
        ulong? depotId = null;
        ulong? manifestId = null;
        bool encrypted = false;
        var reader = new ProtoReader(metadata);
        while (reader.Next(out int field, out int wireType))
        {
            switch (field, wireType)
            {
                case (1, ProtoReader.Varint):
                    depotId = reader.ReadVarint();
                    break;
                case (2, ProtoReader.Varint):
                    manifestId = reader.ReadVarint();
                    break;
                case (4, ProtoReader.Varint):
                    encrypted = reader.ReadVarint() != 0;
                    break;
                case (1 or 2 or 4, _):
                    throw new InvalidDataException($"Metadata field {field} has wire type {wireType}.");
                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        if (depotId == null || manifestId == null)
        {
            throw new InvalidDataException("The depot manifest metadata does not name its depot and manifest.");
        }

        return (depotId.Value, manifestId.Value, encrypted);
    }

    private static List<Entry> ReadPayload(byte[] payload)
    {
        var entries = new List<Entry>();
        var reader = new ProtoReader(payload);
        while (reader.Next(out int field, out int wireType))
        {
            if (field == 1)
            {
                if (wireType != ProtoReader.LengthDelimited)
                {
                    throw new InvalidDataException("A file mapping is not length-delimited.");
                }

                Entry? entry = ReadMapping(reader.ReadBytes());
                if (entry != null)
                {
                    entries.Add(entry);
                }
            }
            else
            {
                reader.Skip(wireType);
            }
        }

        return entries;
    }

    private static Entry? ReadMapping(byte[] mapping)
    {
        string? name = null;
        ulong size = 0;
        ulong flags = 0;
        byte[]? sha = null;
        string? linkTarget = null;
        var reader = new ProtoReader(mapping);
        while (reader.Next(out int field, out int wireType))
        {
            switch (field, wireType)
            {
                case (1, ProtoReader.LengthDelimited):
                    name = Utf8(reader.ReadBytes());
                    break;
                case (2, ProtoReader.Varint):
                    size = reader.ReadVarint();
                    break;
                case (3, ProtoReader.Varint):
                    flags = reader.ReadVarint();
                    break;
                case (5, ProtoReader.LengthDelimited):
                    sha = reader.ReadBytes();
                    break;
                case (7, ProtoReader.LengthDelimited):
                    linkTarget = Utf8(reader.ReadBytes());
                    break;
                case (1 or 2 or 3 or 5 or 7, _):
                    throw new InvalidDataException($"File mapping field {field} has wire type {wireType}.");
                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        if (string.IsNullOrEmpty(name))
        {
            throw new InvalidDataException("A file mapping has no name.");
        }

        if ((flags & DirectoryFlag) != 0)
        {
            return null;
        }

        if ((flags & SymlinkFlag) != 0 || !string.IsNullOrEmpty(linkTarget))
        {
            throw new InvalidDataException($"The depot holds a link, which this reader does not prove: {name}");
        }

        if (size > long.MaxValue)
        {
            throw new InvalidDataException($"The size of {name} is out of range.");
        }

        // An empty file has one possible content, so its size is its proof whatever the hash field holds.
        if (size == 0)
        {
            return new Entry(name, 0, EmptySha1);
        }

        if (sha == null || sha.Length != 20)
        {
            throw new InvalidDataException($"The content hash of {name} is not a SHA-1.");
        }

        return new Entry(name, (long)size, Convert.ToHexString(sha));
    }

    private static string Utf8(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes).TrimEnd('\0');
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("A file name is not UTF-8.", ex);
        }
    }

    private static byte[] ReadSection(byte[] data, ref int position)
    {
        uint length = ReadUInt32(data, ref position);
        if (length > data.Length - position)
        {
            throw new InvalidDataException($"A section of {length} bytes runs past the end of the file.");
        }

        byte[] section = data.AsSpan(position, (int)length).ToArray();
        position += (int)length;
        return section;
    }

    private static uint ReadUInt32(byte[] data, ref int position)
    {
        if (data.Length - position < 4)
        {
            throw new InvalidDataException("The depot manifest ends inside a section header.");
        }

        uint value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position, 4));
        position += 4;
        return value;
    }

    /// <summary>The SHA-1 of zero bytes.</summary>
    public const string EmptySha1 = "DA39A3EE5E6B4B0D3255BFEF95601890AFD80709";

    /// <summary>One file of the depot: its name as stored (backslashes on Windows depots), size and SHA-1.</summary>
    public sealed record Entry(string Name, long Size, string Sha1);

    /// <summary>The protocol-buffer wire format, enough to walk fields and skip the ones not needed.</summary>
    private sealed class ProtoReader
    {
        public const int Varint = 0;
        public const int Fixed64 = 1;
        public const int LengthDelimited = 2;
        public const int Fixed32 = 5;

        private readonly byte[] _data;
        private int _position;

        public ProtoReader(byte[] data)
        {
            _data = data;
        }

        public bool Next(out int field, out int wireType)
        {
            if (_position >= _data.Length)
            {
                field = 0;
                wireType = 0;
                return false;
            }

            ulong key = ReadVarint();
            field = (int)(key >> 3);
            wireType = (int)(key & 7);
            if (field == 0 || key >> 3 > int.MaxValue)
            {
                throw new InvalidDataException("A field number is out of range.");
            }

            return true;
        }

        public ulong ReadVarint()
        {
            ulong value = 0;
            for (int shift = 0; shift < 64; shift += 7)
            {
                if (_position >= _data.Length)
                {
                    throw new InvalidDataException("A varint runs past the end of its section.");
                }

                byte current = _data[_position++];
                value |= (ulong)(current & 0x7F) << shift;
                if ((current & 0x80) == 0)
                {
                    return value;
                }
            }

            throw new InvalidDataException("A varint is longer than ten bytes.");
        }

        public byte[] ReadBytes()
        {
            ulong length = ReadVarint();
            if (length > (ulong)(_data.Length - _position))
            {
                throw new InvalidDataException("A length-delimited field runs past the end of its section.");
            }

            byte[] bytes = _data.AsSpan(_position, (int)length).ToArray();
            _position += (int)length;
            return bytes;
        }

        public void Skip(int wireType)
        {
            switch (wireType)
            {
                case Varint:
                    ReadVarint();
                    break;
                case Fixed64:
                    Advance(8);
                    break;
                case LengthDelimited:
                    ReadBytes();
                    break;
                case Fixed32:
                    Advance(4);
                    break;
                default:
                    throw new InvalidDataException($"Wire type {wireType} is not allowed here.");
            }
        }

        private void Advance(int count)
        {
            if (_data.Length - _position < count)
            {
                throw new InvalidDataException("A fixed-width field runs past the end of its section.");
            }

            _position += count;
        }
    }
}
