namespace AgeOfHammer.Core.Dice;

/// <summary>Source of random dice results. Swappable so tests and replays are deterministic.</summary>
public interface IDiceRoller
{
    int Roll(int sides);
}

public static class DiceExtensions
{
    public static int D6(this IDiceRoller r) => r.Roll(6);
    public static int D3(this IDiceRoller r) => r.Roll(3);
    public static int Roll2D6(this IDiceRoller r) => r.Roll(6) + r.Roll(6);
}

public sealed class SeededDiceRoller : IDiceRoller
{
    private readonly Random _rng;
    public SeededDiceRoller(int seed) => _rng = new Random(seed);
    public SeededDiceRoller() => _rng = new Random();
    public int Roll(int sides) => _rng.Next(1, sides + 1);
}

/// <summary>A dice value such as "D6", "2D3+1", "D3-1" or "3".</summary>
public readonly record struct DiceExpression(int Count, int Sides, int Modifier)
{
    public static DiceExpression Fixed(int value) => new(0, 0, value);

    public static DiceExpression Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new FormatException("Empty dice expression");
        var s = text.Trim().ToUpperInvariant().Replace(" ", "");
        int d = s.IndexOf('D');
        if (d < 0) return Fixed(int.Parse(s));
        var countPart = s[..d];
        int count = countPart.Length == 0 ? 1 : int.Parse(countPart);
        var rest = s[(d + 1)..];
        int sign = rest.IndexOfAny(['+', '-']);
        int sides = int.Parse(sign < 0 ? rest : rest[..sign]);
        int mod = sign < 0 ? 0 : int.Parse(rest[sign..]);
        if (count < 1 || sides < 1) throw new FormatException($"Invalid dice expression '{text}'");
        return new DiceExpression(count, sides, mod);
    }

    public bool IsFixed => Count == 0;

    public int Roll(IDiceRoller dice)
    {
        int total = Modifier;
        for (int i = 0; i < Count; i++) total += dice.Roll(Sides);
        return Math.Max(0, total);
    }

    public double Average => Count * (Sides + 1) / 2.0 + Modifier;

    public override string ToString()
    {
        if (IsFixed) return Modifier.ToString();
        var core = (Count == 1 ? "" : Count.ToString()) + "D" + Sides;
        return Modifier switch { > 0 => $"{core}+{Modifier}", < 0 => $"{core}{Modifier}", _ => core };
    }
}
