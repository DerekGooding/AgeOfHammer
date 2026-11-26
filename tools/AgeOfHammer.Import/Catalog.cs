using System.Text.Json;

namespace AgeOfHammer.Import;

/// <summary>
/// Read-only view of the AoS Reminders AoS4 catalog (github.com/daviseford/aos-reminders, data/aos4/catalog/catalog.json):
/// entities (factions, warscrolls, weapons, abilities, battle profiles, content groups) linked by relationships,
/// each traceable to source records (Games Workshop PDFs, Wahapedia pages).
/// </summary>
public sealed class Catalog
{
    public const string CurrentContext = "rules-context:90000000-0000-4000-8000-000000000001";
    public const string SeasonContext = "rules-context:90000000-0000-4000-8000-000000000006";

    public required JsonDocument Document { get; init; }
    public required Dictionary<string, JsonElement> Entities { get; init; }
    public required Dictionary<string, List<(string Kind, string To)>> Outgoing { get; init; }
    public required Dictionary<string, (string? ArtifactId, int? Page)> SourceRecords { get; init; }
    public required Dictionary<string, (string? Url, string? Title)> Artifacts { get; init; }
    public required string GeneratedAt { get; init; }
    public required string SeasonName { get; init; }

    public static Catalog Load(string path)
    {
        var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = doc.RootElement;

        var entities = new Dictionary<string, JsonElement>();
        foreach (var e in root.GetProperty("entities").EnumerateArray()) entities[e.GetProperty("id").GetString()!] = e;

        var outgoing = new Dictionary<string, List<(string, string)>>();
        foreach (var r in root.GetProperty("relationships").EnumerateArray())
        {
            string from = r.GetProperty("from").GetString()!;
            if (!outgoing.TryGetValue(from, out var list)) outgoing[from] = list = new();
            list.Add((r.GetProperty("kind").GetString()!, r.GetProperty("to").GetString()!));
        }

        var records = new Dictionary<string, (string?, int?)>();
        foreach (var s in root.GetProperty("sourceRecords").EnumerateArray())
        {
            int? page = s.TryGetProperty("locator", out var loc) && loc.TryGetProperty("page", out var p) && p.ValueKind == JsonValueKind.Number
                ? p.GetInt32() : null;
            records[s.GetProperty("id").GetString()!] = (Str(s, "artifactId"), page);
        }

        var artifacts = new Dictionary<string, (string?, string?)>();
        foreach (var a in root.GetProperty("sourceArtifacts").EnumerateArray())
            artifacts[a.GetProperty("id").GetString()!] = (Str(a, "sourceUrl"), Str(a, "title"));

        string season = root.GetProperty("rulesContexts").EnumerateArray()
            .Where(c => c.GetProperty("id").GetString() == SeasonContext)
            .Select(c => c.GetProperty("name").GetString()!).FirstOrDefault() ?? "current season";

        return new Catalog
        {
            Document = doc, Entities = entities, Outgoing = outgoing, SourceRecords = records, Artifacts = artifacts,
            GeneratedAt = Str(root, "generatedAt") ?? "unknown", SeasonName = season,
        };
    }

    public static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static IEnumerable<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)
            : [];

    public static bool InContext(JsonElement e, string context) => Strings(e, "rulesContextIds").Contains(context);

    public IEnumerable<JsonElement> OfKind(string kind) =>
        Entities.Values.Where(e => e.GetProperty("kind").GetString() == kind);

    /// <summary>Entities this one links to with the given relationship kinds.</summary>
    public IEnumerable<JsonElement> Linked(string fromId, params string[] relationshipKinds)
    {
        if (!Outgoing.TryGetValue(fromId, out var list)) yield break;
        foreach (var (kind, to) in list)
            if ((relationshipKinds.Length == 0 || relationshipKinds.Contains(kind)) && Entities.TryGetValue(to, out var e))
                yield return e;
    }

    /// <summary>Readable source links for an entity: Wahapedia URLs and Games Workshop PDFs (with page).</summary>
    public List<string> SourceLinks(JsonElement entity)
    {
        var links = new List<string>();
        if (!entity.TryGetProperty("sourceRefs", out var refs)) return links;
        foreach (var r in refs.EnumerateArray())
        {
            string? id = Str(r, "sourceRecordId");
            if (id == null) continue;
            if (Str(r, "transformation") is "normalized keyword") continue;
            string? link = null;
            const string wahapedia = "source-record:wahapedia:html:";
            string decoded = Uri.UnescapeDataString(id);
            if (decoded.StartsWith(wahapedia, StringComparison.Ordinal)) link = decoded[wahapedia.Length..];
            else if (SourceRecords.TryGetValue(id, out var rec) && rec.ArtifactId != null && Artifacts.TryGetValue(rec.ArtifactId, out var art))
                link = (art.Url ?? art.Title) + (rec.Page is int pg ? $"#page={pg}" : "");
            if (link != null && !links.Contains(link)) links.Add(link);
        }
        return links;
    }
}
