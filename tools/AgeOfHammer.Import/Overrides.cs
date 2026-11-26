using System.Text.Json.Nodes;

namespace AgeOfHammer.Import;

/// <summary>
/// Applies a hand-written override file to a generated faction (JSON merge patch, RFC 7386, plus keyed lists):
/// <code>
/// {
///   "faction": { "defaultArmy": ["liberators", "lord-imperatant"] },
///   "warscrolls": {
///     "liberators": {
///       "verified": true,
///       "weapons": { "Grandhammer": { "modelsWithWeapon": 1 } },   // keyed by name: merge into that weapon
///       "abilities": { "Stalwart Defenders": { "modifiers": [ ... ] } }
///     },
///     "some-unit": null                                            // remove a warscroll
///   }
/// }
/// </code>
/// Objects merge, null removes, arrays replace. "weapons", "abilities" and "spells" may be given as an object keyed by
/// name to patch single entries (an entry not found is added).
/// </summary>
public static class Overrides
{
    private static readonly HashSet<string> KeyedLists = ["weapons", "abilities", "spells"];

    public static void Apply(JsonObject faction, JsonObject overrides)
    {
        if (overrides["faction"] is JsonObject factionPatch) Merge(faction, factionPatch);

        if (overrides["warscrolls"] is JsonObject scrolls)
        {
            var list = faction["warscrolls"]?.AsArray() ?? throw new InvalidOperationException("faction has no warscrolls");
            foreach (var (id, patch) in scrolls)
            {
                var target = list.OfType<JsonObject>().FirstOrDefault(w => (string?)w["id"] == id);
                if (patch is null)
                {
                    if (target != null) list.Remove(target);
                    continue;
                }
                if (target == null)
                {
                    // A whole new warscroll (must be complete).
                    var added = patch.DeepClone().AsObject();
                    added["id"] = id;
                    list.Add(added);
                    continue;
                }
                Merge(target, patch.AsObject());
            }
        }
    }

    private static void Merge(JsonObject target, JsonObject patch)
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
                    var entry = array.OfType<JsonObject>().FirstOrDefault(e => (string?)e["name"] == name);
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
