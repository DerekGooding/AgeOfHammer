using System.Text.Json;

namespace AgeOfHammer.Core.Data;

public static class DataLoader
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static FactionData LoadFaction(string path) => ParseFaction(File.ReadAllText(path));

    public static FactionData ParseFaction(string json)
    {
        var faction = JsonSerializer.Deserialize<FactionData>(json, Options) ?? throw new InvalidDataException("Empty faction file");
        Validate(faction);
        foreach (var w in faction.Warscrolls) w.FactionId = faction.Id;
        return faction;
    }

    public static Battleplan LoadBattleplan(string path) =>
        JsonSerializer.Deserialize<Battleplan>(File.ReadAllText(path), Options) ?? throw new InvalidDataException("Empty battleplan file");

    public static List<FactionData> LoadAllFactions(string directory) =>
        Directory.GetFiles(Path.Combine(directory, "factions"), "*.json").OrderBy(f => f).Select(LoadFaction).ToList();

    public static List<Battleplan> LoadAllBattleplans(string directory) =>
        Directory.GetFiles(Path.Combine(directory, "battleplans"), "*.json").OrderBy(f => f).Select(LoadBattleplan).ToList();

    /// <summary>Finds a "data" folder in <paramref name="startDirectory"/> (default: next to the executable) or any parent.</summary>
    public static string FindDataDirectory(string? startDirectory = null)
    {
        var dir = new DirectoryInfo(startDirectory ?? AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "data");
            if (Directory.Exists(Path.Combine(candidate, "factions"))) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not find a 'data/factions' directory.");
    }

    private static void Validate(FactionData f)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(f.Id)) errors.Add("Faction id missing");
        var ids = new HashSet<string>();
        foreach (var w in f.Warscrolls)
        {
            if (!ids.Add(w.Id)) errors.Add($"Duplicate warscroll id '{w.Id}'");
            if (w.UnitSize < 1) errors.Add($"{w.Id}: unitSize must be >= 1");
            if (w.Health < 1) errors.Add($"{w.Id}: health must be >= 1");
            if (w.BaseSizeMm < 20) errors.Add($"{w.Id}: baseSizeMm looks wrong");
            foreach (var wp in w.Weapons)
            {
                try { _ = wp.AttacksExpr; _ = wp.DamageExpr; }
                catch (Exception e) { errors.Add($"{w.Id}/{wp.Name}: {e.Message}"); }
            }
        }
        foreach (var id in f.DefaultArmy)
            if (!ids.Contains(id)) errors.Add($"defaultArmy references unknown warscroll '{id}'");
        if (errors.Count > 0) throw new InvalidDataException($"Faction '{f.Id}' is invalid:\n" + string.Join("\n", errors));
    }
}
