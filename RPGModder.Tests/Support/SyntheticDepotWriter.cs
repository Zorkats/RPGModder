using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace RPGModder.Tests.Support;

/// <summary>
/// Writes a Steam depot manifest in the binary layout of <c>depotcache/*.manifest</c> (TST-19): sections
/// of a little-endian magic, a little-endian length and protocol-buffer bytes, closed by an end magic.
/// The constants are written here from the format, independently of the reader under test.
/// </summary>
public static class SyntheticDepotWriter
{
    public const uint PayloadMagic = 0x71F617D0;
    public const uint MetadataMagic = 0x1F4812BE;
    public const uint SignatureMagic = 0x1B81B817;
    public const uint EndMagic = 0x32C415AB;
    public const uint DirectoryFlag = 0x40;

    /// <summary>One depot entry: its name as Steam stores it (backslashes), size, flags and content.</summary>
    public sealed record Entry(string Name, long Size, uint Flags, byte[]? Sha1);

    /// <summary>Every file and folder under <paramref name="installFolder"/>, as Steam lists them.</summary>
    public static List<Entry> EntriesOf(string installFolder)
    {
        var entries = new List<Entry>();
        foreach (string folder in Directory.GetDirectories(installFolder, "*", SearchOption.AllDirectories))
        {
            if (!Relative(installFolder, folder).StartsWith(".rpgmodder", StringComparison.Ordinal))
            {
                entries.Add(new Entry(Relative(installFolder, folder), 0, DirectoryFlag, null));
            }
        }

        foreach (string file in Directory.GetFiles(installFolder, "*", SearchOption.AllDirectories))
        {
            if (!Relative(installFolder, file).StartsWith(".rpgmodder", StringComparison.Ordinal))
            {
                byte[] bytes = File.ReadAllBytes(file);
                entries.Add(new Entry(Relative(installFolder, file), bytes.LongLength, 0, SHA1.HashData(bytes)));
            }
        }

        return entries.OrderBy(entry => entry.Name, StringComparer.Ordinal).ToList();
    }

    public static byte[] Build(
        ulong depotId,
        ulong manifestId,
        IEnumerable<Entry> entries,
        bool filenamesEncrypted = false,
        uint? extraSectionMagic = null)
    {
        var payload = new MemoryStream();
        foreach (Entry entry in entries)
        {
            var mapping = new MemoryStream();
            WriteBytesField(mapping, 1, Encoding.UTF8.GetBytes(entry.Name));
            WriteVarintField(mapping, 2, (ulong)entry.Size);
            WriteVarintField(mapping, 3, entry.Flags);
            WriteBytesField(mapping, 4, SHA1.HashData(Encoding.UTF8.GetBytes(entry.Name.ToLowerInvariant())));
            if (entry.Sha1 != null)
            {
                WriteBytesField(mapping, 5, entry.Sha1);
            }

            WriteBytesField(payload, 1, mapping.ToArray());
        }

        var metadata = new MemoryStream();
        WriteVarintField(metadata, 1, depotId);
        WriteVarintField(metadata, 2, manifestId);
        WriteVarintField(metadata, 3, 1_700_000_000);
        WriteVarintField(metadata, 4, filenamesEncrypted ? 1UL : 0UL);
        WriteVarintField(metadata, 5, 1234);

        var file = new MemoryStream();
        WriteSection(file, PayloadMagic, payload.ToArray());
        WriteSection(file, MetadataMagic, metadata.ToArray());
        if (extraSectionMagic != null)
        {
            WriteSection(file, extraSectionMagic.Value, new byte[] { 0x08, 0x01 });
        }

        WriteSection(file, SignatureMagic, new byte[] { 0x0A, 0x02, 0x00, 0x00 });
        WriteUInt32(file, EndMagic);
        return file.ToArray();
    }

    private static string Relative(string root, string path)
    {
        return Path.GetRelativePath(root, path).Replace('/', '\\');
    }

    private static void WriteSection(Stream stream, uint magic, byte[] body)
    {
        WriteUInt32(stream, magic);
        WriteUInt32(stream, (uint)body.Length);
        stream.Write(body);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteVarintField(Stream stream, int field, ulong value)
    {
        WriteVarint(stream, (ulong)(field << 3));
        WriteVarint(stream, value);
    }

    private static void WriteBytesField(Stream stream, int field, byte[] value)
    {
        WriteVarint(stream, (ulong)((field << 3) | 2));
        WriteVarint(stream, (ulong)value.Length);
        stream.Write(value);
    }

    private static void WriteVarint(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        stream.WriteByte((byte)value);
    }
}
