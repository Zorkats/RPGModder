namespace RPGModder.Core.Merge;

/// <summary>
/// A vanilla file, or the live config save (GR-M10). <c>Text</c> is set when
/// <see cref="RpgDataSchema.KindOf"/> is not <see cref="MergeFileKind.Opaque"/>; <c>Sha256</c> is lower-case hex.
/// </summary>
public sealed record BaseFile(
    string Path,
    string? Text,
    string Sha256);
