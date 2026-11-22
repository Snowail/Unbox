using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace UnzipProxy.Core;

public enum ArchiveKind { Archive, Stream, TarWrapper, Split }

public sealed record ArchiveFormat(string Suffix, string[] HandlerHints, ArchiveKind Kind,
    bool Associate, string DisplayName, string IconKey, string BadgeLabel, string PickerGroup);

public static class ArchiveFormatCatalog
{
    private sealed record Catalog(int SchemaVersion, ArchiveFormat[] Formats);

    private static readonly ArchiveFormat[] Formats = Load();

    private static ArchiveFormat[] Load()
    {
        using var stream = typeof(ArchiveFormatCatalog).Assembly.GetManifestResourceStream("Unbox.ArchiveFormats.json")
            ?? throw new InvalidDataException("Missing embedded archive format catalog.");
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new JsonStringEnumConverter<ArchiveKind>(allowIntegerValues: false));
        var catalog = JsonSerializer.Deserialize<Catalog>(stream, options);
        if (catalog is not { SchemaVersion: 1, Formats.Length: > 0 })
            throw new InvalidDataException("Invalid archive format catalog schema.");

        var suffixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var format in catalog.Formats)
        {
            if (format?.Suffix is null || !Regex.IsMatch(format.Suffix, @"^\.[a-z0-9]+(?:\.[a-z0-9]+)*\z") ||
                !suffixes.Add(format.Suffix) || (format.Associate && format.Suffix.Count(c => c == '.') != 1) ||
                format.IconKey is null || !Regex.IsMatch(format.IconKey, @"^[a-z0-9]+\z") ||
                format.HandlerHints is not { Length: > 0 } ||
                format.HandlerHints.Any(hint => hint is null || !Regex.IsMatch(hint, @"^[a-zA-Z0-9]+\z")) ||
                string.IsNullOrWhiteSpace(format.DisplayName) || string.IsNullOrWhiteSpace(format.BadgeLabel) ||
                format.PickerGroup is not ("Archive" or "Stream"))
                throw new InvalidDataException($"Invalid archive catalog entry {format?.Suffix}.");
        }

        return catalog.Formats.OrderByDescending(format => format.Suffix.Length).ToArray();
    }

    public static ArchiveFormat? Match(string fileName) =>
        Formats.FirstOrDefault(format => fileName.EndsWith(format.Suffix, StringComparison.OrdinalIgnoreCase));

    public static string BuildOpenFileFilter()
    {
        var archive = string.Join(';', Formats.Where(f => f.PickerGroup == "Archive").Select(f => "*" + f.Suffix));
        var stream = string.Join(';', Formats.Where(f => f.PickerGroup == "Stream").Select(f => "*" + f.Suffix));
        return $"支持的压缩文件|{archive};{stream}|归档与分卷|{archive}|压缩流与复合 TAR|{stream}|所有文件 (*.*)|*.*";
    }
}
