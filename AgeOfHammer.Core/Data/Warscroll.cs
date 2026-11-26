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

/// <summary>Data-driven effect used by spells, prayers and simple abilities.</summary>
public sealed class EffectDef
{
    /// <summary>"mortal" (Amount mortal damage), "heal" (Amount healed), or "buff" (Stat/Value until Expiry).</summary>
    public string Type { get; set; } = "mortal";
    public string Amount { get; set; } = "D3";
    public string? Stat { get; set; }
    public int Value { get; set; }
    public string AttackType { get; set; } = "Any";
    public string Expiry { get; set; } = "EndOfTurn";
}

/// <summary>A passive modifier an ability grants to its own unit.</summary>
public sealed class PassiveModifierDef
{
    /// <summary>Hit, Wound, Rend, Attacks, Damage, Save, Ward, Move, Control, Charge, StrikeFirst, StrikeLast.</summary>
    public string Stat { get; set; } = "Hit";
    public int Value { get; set; }
    public string AttackType { get; set; } = "Any";
    /// <summary>Optional condition: "charged" (this unit charged this turn), "targetHealth5Plus", "targetKeyword:X".</summary>
    public string? Condition { get; set; }
}

public sealed class AbilityDef
{
    public string Name { get; set; } = "";
    /// <summary>Timing as printed, e.g. "Passive", "Your Hero Phase", "Any Combat Phase".</summary>
    public string Timing { get; set; } = "Passive";
    public string Text { get; set; } = "";
    /// <summary>Optional: hook into a hand-written C# ability script.</summary>
    public string? ScriptId { get; set; }
    /// <summary>Optional: simple passive modifiers the engine applies automatically.</summary>
    public List<PassiveModifierDef> Modifiers { get; set; } = new();
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
    public string Source { get; set; } = "";
    public bool Verified { get; set; }

    [JsonIgnore] public string FactionId { get; set; } = "";
    [JsonIgnore] public float BaseRadius => BaseSizeMm / 25.4f / 2f;
    [JsonIgnore] public bool IsHero => HasKeyword("Hero");
    [JsonIgnore] public bool CanFly => HasKeyword("Fly");
    [JsonIgnore] public bool IsWizard => WizardLevel > 0;
    [JsonIgnore] public IEnumerable<WeaponProfile> RangedWeapons => Weapons.Where(w => !w.IsMelee);
    [JsonIgnore] public IEnumerable<WeaponProfile> MeleeWeapons => Weapons.Where(w => w.IsMelee);

    public bool HasKeyword(string keyword) => Keywords.Any(k => string.Equals(k, keyword, StringComparison.OrdinalIgnoreCase));
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
    /// <summary>Warscroll ids used when no army is picked (e.g. for the AI).</summary>
    public List<string> DefaultArmy { get; set; } = new();
    public List<Warscroll> Warscrolls { get; set; } = new();

    public Warscroll Get(string id) => Warscrolls.FirstOrDefault(w => w.Id == id)
        ?? throw new KeyNotFoundException($"Warscroll '{id}' not found in faction '{Id}'");
}
