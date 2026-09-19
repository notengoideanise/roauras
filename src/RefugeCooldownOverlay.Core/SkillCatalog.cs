using System.Text.Json;

namespace RefugeCooldownOverlay.Core;

/// <summary>Loads the static skill catalog artifact produced by tools/build_skill_catalog.py.</summary>
public static class SkillCatalog
{
    public sealed record CatalogResult(IReadOnlyList<SkillDefinition> Skills, int RowCount, int DuplicateSlugs);

    public static IReadOnlyList<SkillDefinition> SelectInOrder(
        IReadOnlyList<SkillDefinition> catalog, IEnumerable<string> slugs) =>
        slugs.Select(slug => catalog.FirstOrDefault(skill => skill.Slug == slug))
            .OfType<SkillDefinition>()
            .ToList();

    public static CatalogResult Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        var skills = new List<SkillDefinition>();
        foreach (var entry in root.GetProperty("skills").EnumerateArray())
        {
            var status = Enum.TryParse<SkillMappingStatus>(
                entry.GetProperty("mappingStatus").GetString(), ignoreCase: true, out var parsed)
                ? parsed
                : SkillMappingStatus.Unknown;

            var runtimeKey = entry.TryGetProperty("runtimeKey", out var keyElement)
                && keyElement.ValueKind == JsonValueKind.Number
                ? keyElement.GetInt32()
                : (int?)null;

            var iconFile = entry.TryGetProperty("iconFile", out var iconFileElement)
                && iconFileElement.ValueKind == JsonValueKind.String
                ? iconFileElement.GetString()
                : null;

            skills.Add(new SkillDefinition(
                Slug: entry.GetProperty("slug").GetString() ?? string.Empty,
                Name: entry.GetProperty("name").GetString() ?? string.Empty,
                ClassName: entry.GetProperty("className").GetString() ?? string.Empty,
                IconHandle: entry.TryGetProperty("iconHandle", out var icon)
                    ? icon.GetString() ?? string.Empty
                    : string.Empty,
                RuntimeKey: runtimeKey,
                MappingStatus: status,
                Evidence: entry.TryGetProperty("evidence", out var evidence)
                    ? evidence.GetString()
                    : null,
                IconFile: iconFile));
        }

        return new CatalogResult(
            skills,
            root.TryGetProperty("rowCount", out var rowCount) ? rowCount.GetInt32() : skills.Count,
            root.TryGetProperty("duplicateSlugs", out var dupes) ? dupes.GetInt32() : 0);
    }
}
