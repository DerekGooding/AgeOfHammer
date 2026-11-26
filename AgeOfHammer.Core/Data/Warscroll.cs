using System.Text.Json.Serialization;
using AgeOfHammer.Core.Dice;

namespace AgeOfHammer.Core.Data;

public enum CritEffect { None, TwoHits, AutoWound, Mortal }

public enum AttackType { Any, Melee, Ranged }

/// <summary>One weapon line on a warscroll (AoS4 characteristics).</summary>
public sealed class WeaponProfile
{
    public string Name { get; set; } = "";
    /// <summary>Range in inches. 0 means a melee weapon.</summary>
    public int Range { get; set; }
    public string Attacks { get; set; } = "1";
    public int Hit { get; set; } = 4;
    public int Wound { get; set; } = 4;
    public int Rend { get; set; }
    public string Damage { get; set; } = "1";
    /// <summary>Weapon abilities as printed, e.g. "Crit (Mortal)", "Anti-Charge (+1 Rend)", "Charge (+1 Damage)", "Shoot in Combat", "Companion".</summary>
    public List<string> Abilities { get; set; } = new();
    /// <summary>Optional: how many models in the unit carry this weapon (e.g. 1 for a champion's weapon). Null = every model.</summary>
    public int? ModelsWithWeapon { get; set; }

    [JsonIgnore] public bool IsMelee => Range <= 0;
    [JsonIgnore] public AttackType Type => IsMelee ? AttackType.Melee : AttackType.Ranged;
    [JsonIgnore] public DiceExpression AttacksExpr => DiceExpression.Parse(Attacks);
    [JsonIgnore] public DiceExpression DamageExpr => DiceExpression.Parse(Damage);

    [JsonIgnore]
    public CritEffect Crit
    {
        get
        {
            foreach (var a in Abilities)
            {
                var n = Normalize(a);
                if (n.Contains("crit(2hits)")) return CritEffect.TwoHits;
                if (n.Contains("crit(auto-wound)") || n.Contains("crit(autowound)")) return CritEffect.AutoWound;
                if (n.Contains("crit(mortal)")) return CritEffect.Mortal;
            }
            return CritEffect.None;
        }
    }

    [JsonIgnore] public bool ChargeBonusDamage => Abilities.Any(a => Normalize(a).StartsWith("charge(+1damage)"));
    [JsonIgnore] public bool ShootInCombat => Abilities.Any(a => Normalize(a) == "shootincombat");
    [JsonIgnore] public bool Companion => Abilities.Any(a => Normalize(a) == "companion");

    /// <summary>Anti-X (+N Rend) abilities as (keyword, bonus). Keyword "Charge" means "units that charged this turn".</summary>
    [JsonIgnore]
    public IEnumerable<(string Keyword, int Bonus)> AntiAbilities
    {
        get
        {
            foreach (var a in Abilities)
            {
                var t = a.Trim();
                if (!t.StartsWith("Anti-", StringComparison.OrdinalIgnoreCase)) continue;
                int paren = t.IndexOf('(');
                var kw = (paren < 0 ? t[5..] : t[5..paren]).Trim();
                int bonus = 1;
                if (paren >= 0)
                {
                    var inner = t[(paren + 1)..].TrimEnd(')').Replace("Rend", "", StringComparison.OrdinalIgnoreCase).Trim();
                    if (int.TryParse(inner, out var b)) bonus = b;
                }
                yield return (kw, bonus);
            }
        }
    }

    private static string Normalize(string s) => s.Replace(" ", "").ToLowerInvariant();
}

/// <summary>
/// One effect of a spell or activated ability (see data/scripts/README.md):
/// <list type="bullet">
/// <item>"mortal": <see cref="Amount"/> mortal damage ("D3", "2", or "roll" = the gate roll's result).</item>
/// <item>"mortalPerModel": roll a dice per model in the recipient; each roll of <see cref="Threshold"/>+ inflicts 1 mortal damage.</item>
/// <item>"slayRoll": roll <see cref="Amount"/> dice; each roll that equals or exceeds the recipient's Health (or <see cref="Threshold"/>) slays a model.</item>
/// <item>"mortalDice": roll <see cref="Amount"/> dice; each roll of <see cref="Threshold"/>+ inflicts 1 mortal damage.</item>
/// <item>"heal": heal <see cref="Amount"/>. "returnModels": return <see cref="Amount"/> slain models.</item>
/// <item>"buff": add <see cref="Value"/> to <see cref="Stat"/> until <see cref="Expiry"/>.</item>
/// </list>
/// </summary>
public sealed class EffectDef
{
    public string Type { get; set; } = "mortal";
    public string Amount { get; set; } = "D3";
    public int Threshold { get; set; }
    /// <summary>For "slayRoll": a roll must equal or exceed the recipient's Health plus this (1 = "exceeds").</summary>
    public int HealthOffset { get; set; }
    public string? Stat { get; set; }
    public int Value { get; set; }
    public string AttackType { get; set; } = "Any";
    /// <summary>Only affect attacks with weapons whose name contains this text.</summary>
    public string? Weapon { get; set; }
    /// <summary>EndOfPhase, EndOfTurn, EndOfRound, UntilYourNextTurn or Permanent.</summary>
    public string Expiry { get; set; } = "EndOfTurn";
    /// <summary>"target" (default) or "self" (the unit using the ability).</summary>
    public string To { get; set; } = "target";
}

/// <summary>Who an aura affects, measured from the unit that has the ability.</summary>
public sealed class AuraDef
{
    /// <summary>"friendly" or "enemy".</summary>
    public string Side { get; set; } = "friendly";
    /// <summary>Range in inches; null = the whole battlefield.</summary>
    public float? Range { get; set; }
    /// <summary>Every model must be within range (wholly within) rather than any model.</summary>
    public bool Wholly { get; set; }
    /// <summary>Affected units must have all of these keywords.</summary>
    public List<string> Keywords { get; set; } = new();
    /// <summary>Affected units must have at least one of these keywords (if any are listed).</summary>
    public List<string> KeywordsAny { get; set; } = new();
    /// <summary>Affected units must be one of these warscrolls (by name), if any are listed.</summary>
    public List<string> Names { get; set; } = new();
    /// <summary>Whether the unit with the aura is affected too (friendly auras).</summary>
    public bool IncludeSelf { get; set; } = true;
}

/// <summary>
/// A passive modifier from an ability. Without an <see cref="Aura"/> it affects the ability's own unit; with one it
/// affects every unit inside the aura.
/// </summary>
public sealed class PassiveModifierDef
{
    /// <summary>
    /// Hit, Wound, Rend, Attacks, Damage, Save, Ward, Move, Control, Charge, ChargeDice, Cast, StrikeFirst, StrikeLast,
    /// HitAgainst (attacks that target this unit), WoundAgainst, MaxControl, NoRetreatDamage, CoherencyRange.
    /// </summary>
    public string Stat { get; set; } = "Hit";
    public int Value { get; set; }
    /// <summary>Multiply the stat instead of adding (e.g. 2 = double Damage). Applied after additions.</summary>
    public int? Multiply { get; set; }
    public string AttackType { get; set; } = "Any";
    /// <summary>Only affect attacks with weapons whose name contains this text.</summary>
    public string? Weapon { get; set; }
    /// <summary>Condition terms joined with '&amp;'; see Modifiers.ConditionHolds for the vocabulary.</summary>
    public string? Condition { get; set; }
    public AuraDef? Aura { get; set; }
}

/// <summary>
/// Makes an ability usable by the engine and the AI: who it targets, an optional dice gate, and its effects.
/// Timing comes from the ability's Phase/Perspective; frequency from its printed Timing ("Once Per Turn (Army)"...).
/// </summary>
public sealed class ActivationDef
{
    /// <summary>"self", "enemy" or "friendly".</summary>
    public string Target { get; set; } = "self";
    /// <summary>Targets must be within this many inches of the user (null = no range limit unless InCombat).</summary>
    public float? Range { get; set; }
    /// <summary>Targets must be in combat with the user.</summary>
    public bool InCombat { get; set; }
    public bool Wholly { get; set; }
    public List<string> TargetKeywords { get; set; } = new();
    public List<string> TargetKeywordsAny { get; set; } = new();
    public List<string> TargetExcludeKeywords { get; set; } = new();
    /// <summary>Targets must be one of these warscrolls (by name), if any are listed.</summary>
    public List<string> TargetNames { get; set; } = new();
    /// <summary>Exclude the user itself from friendly targets ("another friendly unit").</summary>
    public bool Other { get; set; }
    /// <summary>Pick up to this many targets (the engine adds the extra targets itself). 0 = every eligible unit.</summary>
    public int MaxTargets { get; set; } = 1;
    /// <summary>Conditions on the user (same vocabulary as modifiers), e.g. "chargedThisPhase", "inCombat".</summary>
    public string? Requires { get; set; }
    /// <summary>Conditions on each target, e.g. "targetCharged", "targetDamaged".</summary>
    public string? TargetRequires { get; set; }
    /// <summary>Optional gate: roll this dice ("D6", "D3", "2D6") and continue only on <see cref="Success"/>+.</summary>
    public string? Roll { get; set; }
    public int Success { get; set; }
    /// <summary>"1" (default) or "models": roll one gate dice per model in the user's unit; the best roll counts.</summary>
    public string RollCount { get; set; } = "1";
    /// <summary>Roll the gate separately for each target.</summary>
    public bool PerTarget { get; set; }
    public List<EffectDef> Effects { get; set; } = new();
    /// <summary>No decision needed: the engine uses it automatically whenever it can (e.g. end-of-turn heals).</summary>
    public bool Auto { get; set; }
}

public sealed class AbilityDef
{
    public string Name { get; set; } = "";
    /// <summary>Timing as printed, e.g. "Passive", "Your Hero Phase", "Any Combat Phase".</summary>
    public string Timing { get; set; } = "Passive";
    public string Text { get; set; } = "";
    /// <summary>"passive", "active" or "reaction".</summary>
    public string? Kind { get; set; }
    /// <summary>Structured timing: hero, movement, shooting, charge, combat, end-of-turn, start-of-turn, deployment, battle-round-start...</summary>
    public string? Phase { get; set; }
    /// <summary>Whose turn: "your", "enemy", "any" or "neutral".</summary>
    public string? Perspective { get; set; }
    /// <summary>For reactions: what triggers it, as printed.</summary>
    public string? ReactionTrigger { get; set; }
    /// <summary>Declare step text, as printed (the Effect is in <see cref="Text"/>).</summary>
    public string? Declare { get; set; }
    /// <summary>Cost type: "spell" (casting value), "prayer" (chanting value) or "command-points".</summary>
    public string? CostKind { get; set; }
    public int? CostValue { get; set; }
    public List<string> Keywords { get; set; } = new();
    /// <summary>Optional: hook into a hand-written C# ability script.</summary>
    public string? ScriptId { get; set; }
    /// <summary>Optional: simple passive modifiers the engine applies automatically.</summary>
    public List<PassiveModifierDef> Modifiers { get; set; } = new();
    /// <summary>Optional: makes an active ability usable by the engine (see <see cref="ActivationDef"/>).</summary>
    public ActivationDef? Activate { get; set; }
    /// <summary>Optional: puts units into, or brings them out of, a reserve pool (deep strike, tunnels...).</summary>
    public ReserveDef? Reserve { get; set; }
    /// <summary>Optional: lets the player place a battlefield marker (e.g. a Gnawhole) at this ability's timing.</summary>
    public MarkerDef? Marker { get; set; }

    [JsonIgnore] public bool IsScripted => Modifiers.Count > 0 || Activate != null || Reserve != null || Marker != null || ScriptId != null;

    /// <summary>How often it can be used, from the printed timing.</summary>
    [JsonIgnore]
    public string Frequency =>
        Timing.Contains("Once Per Battle (Army)", StringComparison.OrdinalIgnoreCase) ? "battle-army"
        : Timing.Contains("Once Per Battle", StringComparison.OrdinalIgnoreCase) ? "battle-unit"
        : Timing.Contains("Once Per Battle Round (Army)", StringComparison.OrdinalIgnoreCase) ? "round-army"
        : Timing.Contains("Once Per Turn (Army)", StringComparison.OrdinalIgnoreCase) ? "turn-army"
        : "phase-unit";
}

public sealed class SpellDef
{
    public string Name { get; set; } = "";
    public int CastingValue { get; set; } = 7;
    public int Range { get; set; } = 12;
    /// <summary>"enemy", "friendly" or "self".</summary>
    public string Target { get; set; } = "enemy";
    public string Text { get; set; } = "";
    public EffectDef? Effect { get; set; }
    /// <summary>Further effects applied after <see cref="Effect"/>.</summary>
    public List<EffectDef> Effects { get; set; } = new();

    [JsonIgnore] public bool IsScripted => Effect != null || Effects.Count > 0;
    [JsonIgnore] public IEnumerable<EffectDef> AllEffects => Effect is null ? Effects : Effects.Prepend(Effect);
}

/// <summary>An AoS4 warscroll.</summary>
public sealed class Warscroll
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Move { get; set; } = 5;
    public int Health { get; set; } = 1;
    /// <summary>Save characteristic (e.g. 4 for 4+). 7 or more means no save.</summary>
    public int Save { get; set; } = 4;
    public int Control { get; set; } = 1;
    public int? Ward { get; set; }
    public int BaseSizeMm { get; set; } = 32;
    public int UnitSize { get; set; } = 1;
    public int Points { get; set; }
    public int WizardLevel { get; set; }
    public int PriestLevel { get; set; }
    public List<string> Keywords { get; set; } = new();
    public List<WeaponProfile> Weapons { get; set; } = new();
    public List<AbilityDef> Abilities { get; set; } = new();
    public List<SpellDef> Spells { get; set; } = new();
    /// <summary>Base size as printed (may be oval, e.g. "105 x 70mm"); <see cref="BaseSizeMm"/> is the circle the engine uses.</summary>
    public string? BaseSize { get; set; }
    /// <summary>Regiment options from the battle profile, e.g. "Any Stormcast Eternals", "0-1 Knight-Arcanum".</summary>
    public List<string> RegimentOptions { get; set; } = new();
    /// <summary>Importer notes, e.g. assumptions made about weapon loadouts.</summary>
    public List<string> Notes { get; set; } = new();
    /// <summary>Where the data came from (URLs or publication references).</summary>
    public List<string> Sources { get; set; } = new();
    public string Source { get; set; } = "";
    /// <summary>True once a person has checked this warscroll against the rules source.</summary>
    public bool Verified { get; set; }

    [JsonIgnore] public string FactionId { get; set; } = "";
    /// <summary>Half the base's width in inches (the radius of a round base).</summary>
    [JsonIgnore] public float BaseRadius => OvalMm is (_, float w) ? w / 25.4f / 2f : BaseSizeMm / 25.4f / 2f;
    /// <summary>For oval bases: half of (length - width) in inches, the half-length of the base's straight middle. 0 for round bases.</summary>
    [JsonIgnore] public float BaseHalfLength => OvalMm is (float l, float w) ? (l - w) / 25.4f / 2f : 0f;

    private (float Length, float Width)? _oval;
    private bool _ovalParsed;

    /// <summary>Length and width (mm) if <see cref="BaseSize"/> describes an oval such as "60 x 35mm".</summary>
    [JsonIgnore]
    public (float Length, float Width)? OvalMm
    {
        get
        {
            if (_ovalParsed) return _oval;
            _ovalParsed = true;
            var m = System.Text.RegularExpressions.Regex.Match(BaseSize ?? "", @"(\d+(?:\.\d+)?)\s*[×xX]\s*(\d+(?:\.\d+)?)\s*mm");
            if (m.Success)
            {
                float a = float.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                float b = float.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                _oval = (MathF.Max(a, b), MathF.Min(a, b));
            }
            return _oval;
        }
    }
    [JsonIgnore] public bool IsHero => HasKeyword("Hero");
    [JsonIgnore] public bool CanFly => HasKeyword("Fly");
    [JsonIgnore] public bool IsWizard => WizardLevel > 0;
    [JsonIgnore] public IEnumerable<WeaponProfile> RangedWeapons => Weapons.Where(w => !w.IsMelee);
    [JsonIgnore] public IEnumerable<WeaponProfile> MeleeWeapons => Weapons.Where(w => w.IsMelee);

    public bool HasKeyword(string keyword) => Keywords.Any(k => string.Equals(k, keyword, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Reserve rules: <see cref="Declare"/> lets units be set up in the named pool during deployment (they count as
/// deployed); <see cref="Arrive"/> lets units in that pool be set up on the battlefield in the movement phase.
/// </summary>
public sealed class ReserveDef
{
    /// <summary>Name of the reserve, e.g. "Celestial Realm" or "tunnels below".</summary>
    public string Pool { get; set; } = "reserve";
    public bool Declare { get; set; }
    public ArrivalDef? Arrive { get; set; }
    /// <summary>For declare abilities: only units with all of these keywords may go into the pool.</summary>
    public List<string> Keywords { get; set; } = new();
}

public sealed class ArrivalDef
{
    /// <summary>Every model must be set up more than this far from all enemy units.</summary>
    public float MinEnemyDistance { get; set; } = 9;
    /// <summary>If set, every model must be wholly within <see cref="MarkerRange"/> of a friendly marker with this name.</summary>
    public string? Marker { get; set; }
    public float MarkerRange { get; set; } = 6;
    /// <summary>First battle round in which units can arrive (the core rules forbid round 1).</summary>
    public int FromRound { get; set; } = 2;
}

/// <summary>A placeable battlefield marker, e.g. a Skaven Gnawhole.</summary>
public sealed class MarkerDef
{
    public string Name { get; set; } = "Marker";
    public float MinEnemyDistance { get; set; }
    public float MinObjectiveDistance { get; set; }
    public float MinTerrainDistance { get; set; }
    /// <summary>Size used for drawing and for "wholly within" measurements (inches).</summary>
    public float Radius { get; set; } = 1.5f;
}

/// <summary>A named group of faction rules, e.g. a battle formation or a spell lore.</summary>
public sealed class ContentGroupDef
{
    /// <summary>battle-formation, heroic-trait, artefact-of-power, spell-lore, prayer-lore, manifestation-lore, ...</summary>
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public List<AbilityDef> Abilities { get; set; } = new();
}

public sealed class FactionData
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string GrandAlliance { get; set; } = "";
    /// <summary>Display colour as "#RRGGBB".</summary>
    public string Color { get; set; } = "#888888";
    public string Source { get; set; } = "";
    public bool Verified { get; set; }
    public List<AbilityDef> BattleTraits { get; set; } = new();
    /// <summary>Other faction rules: battle formations, heroic traits, artefacts, spell/prayer/manifestation lores...</summary>
    public List<ContentGroupDef> ContentGroups { get; set; } = new();
    /// <summary>Credit line for the data source.</summary>
    public string Attribution { get; set; } = "";
    /// <summary>Warscroll ids used when no army is picked (e.g. for the AI).</summary>
    public List<string> DefaultArmy { get; set; } = new();
    public List<Warscroll> Warscrolls { get; set; } = new();

    /// <summary>Problems found while loading (e.g. a script names an ability that does not exist).</summary>
    [JsonIgnore] public List<string> LoadWarnings { get; } = new();

    public Warscroll Get(string id) => Warscrolls.FirstOrDefault(w => w.Id == id)
        ?? throw new KeyNotFoundException($"Warscroll '{id}' not found in faction '{Id}'");
}
