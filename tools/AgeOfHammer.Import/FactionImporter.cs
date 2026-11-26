using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgeOfHammer.Core.Data;
using AgeOfHammer.Core.Dice;

namespace AgeOfHammer.Import;

/// <summary>What happened to one faction during an import, for the report.</summary>
public sealed class FactionReport
{
    public required string Name { get; init; }
    public int Imported { get; set; }
    public List<string> Skipped { get; } = new();
    public List<string> Assumptions { get; } = new();
    public List<string> DroppedWeapons { get; } = new();
}

/// <summary>Converts one catalog faction into our <see cref="FactionData"/>.</summary>
public sealed partial class FactionImporter(Catalog catalog, int defaultArmyPoints)
{
    /// <summary>Content-group types imported as faction rules (others, e.g. Regiments of Renown, come later).</summary>
    private static readonly HashSet<string> FactionGroupTypes =
    [
        "battle-formation", "heroic-trait", "artefact-of-power", "spell-lore", "prayer-lore", "manifestation-lore",
        "monstrous-traits", "rules-module",
    ];

    private static readonly HashSet<string> NonFactionGroupTypes =
        ["regiment-of-renown", "army-of-renown", "general-rules", "supplemental-content"];

    /// <summary>Display colours (hex) per faction; anything missing falls back to grey.</summary>
    private static readonly Dictionary<string, string> Colors = new()
    {
        ["stormcast-eternals"] = "#D4A62A", ["cities-of-sigmar"] = "#3E6FB0", ["daughters-of-khaine"] = "#9E1B32",
        ["fyreslayers"] = "#E0702A", ["idoneth-deepkin"] = "#2AA6A0", ["kharadron-overlords"] = "#8A6A3A",
        ["lumineth-realm-lords"] = "#E8E4D8", ["seraphon"] = "#2E9E6A", ["sylvaneth"] = "#6E8B3D",
        ["khazalite-holds"] = "#B8860B", ["blades-of-khorne"] = "#B22222", ["disciples-of-tzeentch"] = "#3B6FD6",
        ["hedonites-of-slaanesh"] = "#C45AB3", ["maggotkin-of-nurgle"] = "#8A9A3A", ["skaven"] = "#3F9E4D",
        ["slaves-to-darkness"] = "#6A6A72", ["beasts-of-chaos"] = "#7A5A2A", ["helsmiths-of-hashut"] = "#C0502A",
        ["flesh-eater-courts"] = "#8B3A3A", ["nighthaunt"] = "#7FD6C8", ["ossiarch-bonereapers"] = "#D8CBA8",
        ["soulblight-gravelords"] = "#5A2A6A", ["gloomspite-gitz"] = "#C8C84A", ["ironjawz"] = "#D4A020",
        ["kruleboyz"] = "#5A7A3A", ["bonesplitterz"] = "#C86A3A", ["ogor-mawtribes"] = "#B07A5A",
        ["sons-of-behemat"] = "#8A7A6A",
    };

    public (FactionData Faction, FactionReport Report) Import(JsonElement factionEntity)
    {
        string name = factionEntity.GetProperty("name").GetString()!;
        string factionId = Slug(name);
        string entityId = factionEntity.GetProperty("id").GetString()!;
        var report = new FactionReport { Name = name };

        var faction = new FactionData
        {
            Id = factionId,
            Name = name,
            Color = Colors.GetValueOrDefault(factionId, "#888888"),
            Source = $"AoS Reminders AoS4 catalog (github.com/daviseford/aos-reminders), generated {catalog.GeneratedAt}",
            Attribution = "Data: AoS Reminders (MIT, github.com/daviseford/aos-reminders), compiled from Games Workshop publications " +
                          "and Wahapedia (Powered by Wahapedia). Rules text, names and points are the property of Games Workshop.",
            Verified = false,
        };

        var warscrolls = catalog.OfKind("warscroll")
            .Where(w => Catalog.Strings(w, "factionIds").Contains(entityId) && Catalog.InContext(w, Catalog.CurrentContext))
            .OrderBy(w => w.GetProperty("name").GetString(), StringComparer.Ordinal)
            .ToList();

        var usedIds = new HashSet<string>();
        foreach (var w in warscrolls)
        {
            var ws = ImportWarscroll(w, report);
            if (ws == null) continue;
            string id = ws.Id;
            for (int n = 2; !usedIds.Add(id); n++) id = $"{ws.Id}-{n}";
            ws.Id = id;
            faction.Warscrolls.Add(ws);
            report.Imported++;
        }

        ImportFactionRules(entityId, faction);
        faction.GrandAlliance = GrandAlliance(faction);
        faction.DefaultArmy = DefaultArmy(faction);
        return (faction, report);
    }

    // ------------------------------------------------------------------ warscrolls

    private Warscroll? ImportWarscroll(JsonElement w, FactionReport report)
    {
        string name = w.GetProperty("name").GetString()!;
        var keywords = Catalog.Strings(w, "keywords").ToList();
        if (keywords.Contains("MANIFESTATION") || keywords.Contains("FACTION TERRAIN"))
        {
            report.Skipped.Add($"{name}: manifestation or faction terrain (not supported by the engine yet)");
            return null;
        }

        var ch = w.GetProperty("characteristics");
        string? move = Catalog.Str(ch, "move"), health = Catalog.Str(ch, "health"), save = Catalog.Str(ch, "save"),
            control = Catalog.Str(ch, "control"), ward = Catalog.Str(ch, "ward");
        int? moveValue = ParseMove(move, out string? moveNote);
        if (moveValue is null || !int.TryParse(health, out int hp) || !int.TryParse(control, out int ctl))
        {
            report.Skipped.Add($"{name}: unsupported characteristics (Move '{move}', Health '{health}', Control '{control}')");
            return null;
        }

        string id = w.GetProperty("id").GetString()!;
        var profile = BestProfile(id);
        if (profile is not JsonElement bp)
        {
            report.Skipped.Add($"{name}: no matched-play battle profile (points/unit size)");
            return null;
        }

        int unitSize = bp.TryGetProperty("unitSize", out var us) && us.ValueKind == JsonValueKind.Number ? us.GetInt32() : 1;
        var ws = new Warscroll
        {
            Id = Slug(name),
            Name = name,
            Move = moveValue.Value,
            Health = hp,
            Save = SaveValue(save),
            Control = ctl,
            Ward = ward != null && WardRegex().Match(ward) is { Success: true } wm ? int.Parse(wm.Groups[1].Value) : null,
            UnitSize = Math.Max(1, unitSize),
            Points = bp.TryGetProperty("points", out var pts) && pts.ValueKind == JsonValueKind.Number ? pts.GetInt32() : 0,
            RegimentOptions = Catalog.Strings(bp, "regimentOptions").ToList(),
            Sources = catalog.SourceLinks(w),
            Verified = false,
        };
        if (moveNote != null) ws.Notes.Add(moveNote);
        ws.Source = ws.Sources.FirstOrDefault(s => s.Contains("wahapedia", StringComparison.OrdinalIgnoreCase)) ?? ws.Sources.FirstOrDefault() ?? "";

        foreach (var kw in keywords)
        {
            var level = LevelRegex().Match(kw);
            if (level.Success)
            {
                string baseKeyword = level.Groups[1].Value;
                if (baseKeyword == "WIZARD") ws.WizardLevel = int.Parse(level.Groups[2].Value);
                if (baseKeyword == "PRIEST") ws.PriestLevel = int.Parse(level.Groups[2].Value);
                if (baseKeyword is "WIZARD" or "PRIEST") ws.Keywords.Add(baseKeyword);
            }
            ws.Keywords.Add(kw);
        }

        var baseSizes = Catalog.Strings(bp, "baseSizes").ToList();
        ws.BaseSize = baseSizes.FirstOrDefault();
        ws.BaseSizeMm = ParseBase(ws.BaseSize) ?? EstimateBase(ws);
        if (ParseBase(ws.BaseSize) == null)
            ws.Notes.Add($"Base size '{ws.BaseSize ?? "none"}' not given in mm; using an estimated {ws.BaseSizeMm}mm.");
        foreach (var note in Catalog.Strings(bp, "notes")) ws.Notes.Add(note);

        foreach (var weapon in catalog.Linked(id, "includes").Where(e => e.GetProperty("kind").GetString() == "weapon"))
        {
            var wp = ImportWeapon(weapon, out string? reason);
            if (wp != null) ws.Weapons.Add(wp);
            else report.DroppedWeapons.Add($"{name} / {weapon.GetProperty("name").GetString()}: {reason}");
        }
        if (ws.Weapons.Count == 0)
        {
            report.Skipped.Add($"{name}: no usable weapon profiles");
            return null;
        }
        AssumeLoadout(ws, report);

        foreach (var ability in catalog.Linked(id, "includes").Where(e => e.GetProperty("kind").GetString() == "ability"))
        {
            var def = ImportAbility(ability);
            if (def.CostKind == "spell") ws.Spells.Add(ToSpell(def));
            else ws.Abilities.Add(def);
        }
        return ws;
    }

    /// <summary>The battle profile for the current season if there is one, else the standard one.</summary>
    private JsonElement? BestProfile(string warscrollId)
    {
        var profiles = catalog.Linked(warscrollId, "includes").Where(e => e.GetProperty("kind").GetString() == "battle-profile").ToList();
        foreach (var context in new[] { Catalog.SeasonContext, Catalog.CurrentContext })
            foreach (var p in profiles)
                if (Catalog.InContext(p, context)) return p;
        return null;
    }

    private static WeaponProfile? ImportWeapon(JsonElement weapon, out string? reason)
    {
        reason = null;
        var p = weapon.GetProperty("profile");
        string attacks = (Catalog.Str(p, "attacks") ?? "").Trim();
        string damage = (Catalog.Str(p, "damage") ?? "").Trim();
        string? hit = Catalog.Str(p, "hit"), wound = Catalog.Str(p, "wound"), rend = Catalog.Str(p, "rend");
        if (!TryDice(attacks) || !TryDice(damage) || !TryRoll(hit, out int h) || !TryRoll(wound, out int wnd))
        {
            reason = $"characteristics not numeric (Atk '{attacks}', Hit '{hit}', Wnd '{wound}', Dmg '{damage}') - usually 'See below'";
            return null;
        }
        bool ranged = weapon.GetProperty("weaponType").GetString() == "ranged";
        int range = 0;
        if (ranged)
        {
            if (!p.TryGetProperty("rangeInches", out var r) || r.ValueKind != JsonValueKind.Number)
            {
                reason = "ranged weapon without a range";
                return null;
            }
            range = (int)Math.Round(r.GetDouble());
        }
        return new WeaponProfile
        {
            Name = weapon.GetProperty("name").GetString()!,
            Range = range,
            Attacks = attacks,
            Hit = h,
            Wound = wnd,
            Rend = int.TryParse(rend, out int rd) ? rd : 0,
            Damage = damage,
            Abilities = weapon.GetProperty("keywords").EnumerateArray().Select(k => Catalog.Str(k, "raw") ?? "").Where(s => s.Length > 0).ToList(),
        };
    }

    /// <summary>
    /// The catalog has no wargear distribution. In multi-model units with several weapons of one type, assume the
    /// weakest is carried by every model and the others by 1 model in 5 (typical champion/special weapons), and say so.
    /// </summary>
    private static void AssumeLoadout(Warscroll ws, FactionReport report)
    {
        if (ws.UnitSize <= 1) return;
        foreach (var group in ws.Weapons.Where(w => !w.Companion).GroupBy(w => w.IsMelee))
        {
            var list = group.ToList();
            if (list.Count < 2) continue;
            var main = list.OrderBy(Strength).First();
            int carriers = Math.Max(1, ws.UnitSize / 5);
            var others = list.Where(w => w != main).ToList();
            foreach (var w in others) w.ModelsWithWeapon = carriers;
            string note = $"Loadout assumed: every model uses {main.Name}; {string.Join(", ", others.Select(o => o.Name))} " +
                          $"used by {carriers} model(s). Check the warscroll's wargear options and fix in data/overrides if wrong.";
            ws.Notes.Add(note);
            report.Assumptions.Add($"{ws.Name}: {note}");
        }
    }

    private static double Strength(WeaponProfile w) =>
        w.AttacksExpr.Average * w.DamageExpr.Average * (7 - w.Hit) * (7 - w.Wound) * (1 + w.Rend);

    private static AbilityDef ImportAbility(JsonElement a)
    {
        var def = new AbilityDef
        {
            Name = TitleCase(a.GetProperty("name").GetString()!),
            Kind = Catalog.Str(a, "abilityKind"),
            Keywords = Catalog.Strings(a, "keywords").ToList(),
        };
        if (a.TryGetProperty("text", out var text))
        {
            def.Text = Catalog.Str(text, "effect") ?? "";
            def.Declare = Catalog.Str(text, "declare");
            def.ReactionTrigger = Catalog.Str(text, "reactionTrigger");
        }
        if (a.TryGetProperty("timings", out var timings) && timings.GetArrayLength() > 0)
        {
            var t = timings[0];
            def.Timing = Catalog.Str(t, "raw") ?? def.Kind ?? "Passive";
            def.Perspective = Catalog.Str(t, "perspective");
            if (t.TryGetProperty("window", out var window))
                def.Phase = Catalog.Str(window, "phase") ?? Catalog.Str(window, "kind");
        }
        if (a.TryGetProperty("cost", out var cost))
        {
            def.CostKind = Catalog.Str(cost, "kind");
            def.CostValue = cost.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
        }
        return def;
    }

    private static SpellDef ToSpell(AbilityDef a)
    {
        string declare = a.Declare ?? "";
        var range = RangeRegex().Match(declare);
        string target = Regex.IsMatch(declare, @"\benemy\b", RegexOptions.IgnoreCase) ? "enemy"
            : declare.Contains("to be the target", StringComparison.OrdinalIgnoreCase) ? "friendly"
            : "self";
        return new SpellDef
        {
            Name = a.Name,
            CastingValue = a.CostValue ?? 7,
            Range = range.Success ? int.Parse(range.Groups[1].Value) : 12,
            Target = target,
            Text = (declare.Length > 0 ? "Declare: " + declare + "\n" : "") + "Effect: " + a.Text,
            Effect = null, // the effect still needs scripting
        };
    }

    // ------------------------------------------------------------------ faction rules

    private void ImportFactionRules(string factionEntityId, FactionData faction)
    {
        var seen = new HashSet<string>();
        foreach (var group in catalog.Linked(factionEntityId, "includes", "offers").Where(e => e.GetProperty("kind").GetString() == "content-group"))
        {
            if (!Catalog.InContext(group, Catalog.CurrentContext) && !Catalog.InContext(group, Catalog.SeasonContext)) continue;
            string type = Catalog.Str(group, "groupType") ?? "";
            if (NonFactionGroupTypes.Contains(type)) continue;
            if (!seen.Add(group.GetProperty("id").GetString()!)) continue;

            var abilities = GroupAbilities(group, 0).Select(ImportAbility).ToList();
            if (abilities.Count == 0) continue;
            if (type == "battle-trait") faction.BattleTraits.AddRange(abilities);
            else if (FactionGroupTypes.Contains(type) || !type.Contains("renown"))
                faction.ContentGroups.Add(new ContentGroupDef { Type = type, Name = group.GetProperty("name").GetString()!, Abilities = abilities });
        }
    }

    private IEnumerable<JsonElement> GroupAbilities(JsonElement group, int depth)
    {
        foreach (var e in catalog.Linked(group.GetProperty("id").GetString()!, "includes"))
        {
            string kind = e.GetProperty("kind").GetString()!;
            bool current = Catalog.InContext(e, Catalog.CurrentContext) || Catalog.InContext(e, Catalog.SeasonContext);
            if (kind == "ability" && current) yield return e;
            else if (kind == "content-group" && depth < 2 && current)
                foreach (var nested in GroupAbilities(e, depth + 1)) yield return nested;
        }
    }

    private static string GrandAlliance(FactionData f)
    {
        string[] alliances = ["ORDER", "CHAOS", "DEATH", "DESTRUCTION"];
        var best = alliances.Select(a => (a, n: f.Warscrolls.Count(w => w.Keywords.Contains(a)))).OrderByDescending(x => x.n).First();
        return best.n == 0 ? "" : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(best.a.ToLowerInvariant());
    }

    /// <summary>A small playable default list: the cheapest hero, then the cheapest units, up to the points target.</summary>
    private List<string> DefaultArmy(FactionData f)
    {
        var army = new List<string>();
        int total = 0;
        var playable = f.Warscrolls.Where(w => w.Points > 0).ToList();
        var hero = playable.Where(w => w.IsHero).OrderBy(w => w.Points).FirstOrDefault();
        if (hero != null) { army.Add(hero.Id); total += hero.Points; }
        foreach (var w in playable.Where(w => !w.IsHero).OrderBy(w => w.Points).ThenBy(w => w.Name))
        {
            if (army.Count >= 6) break;
            if (total + w.Points > defaultArmyPoints) continue;
            army.Add(w.Id);
            total += w.Points;
        }
        if (army.Count == 0 && playable.Count > 0) army.Add(playable.OrderBy(w => w.Points).First().Id);
        return army;
    }

    // ------------------------------------------------------------------ parsing helpers

    public static string Slug(string name)
    {
        var s = name.ToLowerInvariant().Replace("’", "").Replace("'", "");
        s = Regex.Replace(s, "[^a-z0-9]+", "-").Trim('-');
        return s;
    }

    private static string TitleCase(string name) =>
        name.Any(char.IsLower) ? name : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.ToLowerInvariant());

    /// <summary>"6\"" -> 6. Random moves ("2D6\"", "D6+8\"") use their average, rounded, with a note.</summary>
    private static int? ParseMove(string? move, out string? note)
    {
        note = null;
        var m = MoveRegex().Match(move ?? "");
        if (m.Success) return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        string dice = (move ?? "").TrimEnd('"').Trim();
        if (!TryDice(dice)) return null;
        int avg = (int)Math.Round(DiceExpression.Parse(dice).Average);
        note = $"Random Move {move} approximated as {avg}\" (the engine does not roll for Move yet).";
        return avg;
    }

    private static int SaveValue(string? save) =>
        save != null && Regex.Match(save, @"^(\d)\+$") is { Success: true } m ? int.Parse(m.Groups[1].Value) : 7;

    private static bool TryRoll(string? value, out int result)
    {
        result = 0;
        var m = Regex.Match(value ?? "", @"^(\d)\+$");
        if (!m.Success) return false;
        result = int.Parse(m.Groups[1].Value);
        return true;
    }

    private static bool TryDice(string value)
    {
        if (value.Length == 0) return false;
        try { DiceExpression.Parse(value); return true; }
        catch (FormatException) { return false; }
    }

    /// <summary>"40mm", "25mm [6]", "28.5mm [3] or 40mm [1]" -> first size; ovals "105 x 70mm" -> area-equivalent circle.</summary>
    public static int? ParseBase(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var oval = Regex.Match(text, @"(\d+(?:\.\d+)?)\s*[×xX]\s*(\d+(?:\.\d+)?)\s*mm");
        if (oval.Success)
        {
            double a = double.Parse(oval.Groups[1].Value, CultureInfo.InvariantCulture);
            double b = double.Parse(oval.Groups[2].Value, CultureInfo.InvariantCulture);
            return (int)Math.Round(Math.Sqrt(a * b));
        }
        var round = Regex.Match(text, @"(\d+(?:\.\d+)?)\s*mm");
        return round.Success ? (int)Math.Round(double.Parse(round.Groups[1].Value, CultureInfo.InvariantCulture)) : null;
    }

    private static int EstimateBase(Warscroll w) =>
        w.HasKeyword("MONSTER") ? 100 : w.HasKeyword("WAR MACHINE") || w.HasKeyword("CAVALRY") || w.HasKeyword("BEAST") ? 60
        : w.IsHero ? 40 : 32;

    [GeneratedRegex("^(\\d+)\"$")] private static partial Regex MoveRegex();
    [GeneratedRegex("(\\d)\\+")] private static partial Regex WardRegex();
    [GeneratedRegex("^(WIZARD|PRIEST) \\((\\d+)\\)$")] private static partial Regex LevelRegex();
    [GeneratedRegex("within (\\d+)\"")] private static partial Regex RangeRegex();
}
