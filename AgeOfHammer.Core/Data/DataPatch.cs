using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgeOfHammer.Core.Data;

/// <summary>
/// Applies a patch file to a faction's JSON. Used for data/overrides (data fixes, applied by the importer) and
/// data/scripts (ability automation, applied when factions are loaded).
/// <code>
/// {
///   "faction":    { ... },                        // merged into the faction object
///   "abilities":  { "Extremis Chamber": { ... } }, // merged into EVERY ability with that name (warscrolls, battle traits, rule groups)
///   "warscrolls": {
///     "liberators": { "weapons": { "Grandhammer": { "modelsWithWeapon": 1 } } },
///     "some-unit": null                             // remove a warscroll
///   }
/// }
/// </code>
/// Objects merge, null removes, arrays replace. Inside an object, "weapons", "abilities", "spells" and "battleTraits"
/// may be given as an object keyed by name to patch single entries (an entry that does not exist is added).
/// </summary>
public static class DataPatch
{
    private static readonly HashSet<string> KeyedLists = ["weapons", "abilities", "spells", "battleTraits"];

    public static readonly JsonDocumentOptions DocumentOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static JsonObject ParseObject(string json) =>
        JsonNode.Parse(json, documentOptions: DocumentOptions)?.AsObject() ?? throw new InvalidDataException("Empty JSON");

    /// <summary>Applies <paramref name="patch"/> to <paramref name="faction"/>. Returns names that matched nothing (likely typos).</summary>
    public static List<string> Apply(JsonObject faction, JsonObject patch)
    {
        var unmatched = new List<string>();
        if (patch["faction"] is JsonObject factionPatch) Merge(faction, factionPatch);

        if (patch["abilities"] is JsonObject byName)
            foreach (var (name, abilityPatch) in byName)
            {
                int hits = 0;
                foreach (var ability in AllAbilities(faction).Where(a => string.Equals((string?)a["name"], name, StringComparison.OrdinalIgnoreCase)))
                {
                    if (abilityPatch is JsonObject p) Merge(ability, p);
                    hits++;
                }
                if (hits == 0) unmatched.Add($"ability '{name}'");
            }

        if (patch["warscrolls"] is JsonObject scrolls)
        {
            var list = faction["warscrolls"]?.AsArray() ?? throw new InvalidOperationException("faction has no warscrolls");
            foreach (var (id, scrollPatch) in scrolls)
            {
                var target = list.OfType<JsonObject>().FirstOrDefault(w => (string?)w["id"] == id);
                if (scrollPatch is null)
                {
                    if (target != null) list.Remove(target);
                    continue;
                }
                if (target == null)
                {
                    if (scrollPatch is JsonObject full && full["name"] != null)
                    {
                        var added = full.DeepClone().AsObject();
                        added["id"] = id;
                        list.Add(added);
                    }
                    else unmatched.Add($"warscroll '{id}'");
                    continue;
                }
                Merge(target, scrollPatch.AsObject());
            }
        }
        return unmatched;
    }

    private static IEnumerable<JsonObject> AllAbilities(JsonObject faction)
    {
        foreach (var w in faction["warscrolls"]?.AsArray().OfType<JsonObject>() ?? [])
            foreach (var a in w["abilities"]?.AsArray().OfType<JsonObject>() ?? [])
                yield return a;
        foreach (var a in faction["battleTraits"]?.AsArray().OfType<JsonObject>() ?? [])
            yield return a;
        foreach (var g in faction["contentGroups"]?.AsArray().OfType<JsonObject>() ?? [])
            foreach (var a in g["abilities"]?.AsArray().OfType<JsonObject>() ?? [])
                yield return a;
    }

    public static void Merge(JsonObject target, JsonObject patch)
    {
        foreach (var (key, value) in patch)
        {
            if (value is null)
            {
                target.Remove(key);
            }
            else if (KeyedLists.Contains(key) && value is JsonObject byName && target[key] is JsonArray array)
            {
                foreach (var (name, entryPatch) in byName)
                {
                    var entry = array.OfType<JsonObject>().FirstOrDefault(e => string.Equals((string?)e["name"], name, StringComparison.OrdinalIgnoreCase));
                    if (entryPatch is null) { if (entry != null) array.Remove(entry); }
                    else if (entry == null) { var add = entryPatch.DeepClone().AsObject(); add["name"] = name; array.Add(add); }
                    else Merge(entry, entryPatch.AsObject());
                }
            }
            else if (value is JsonObject obj && target[key] is JsonObject existing)
            {
                Merge(existing, obj);
            }
            else
            {
                target[key] = value.DeepClone();
            }
        }
    }
}
