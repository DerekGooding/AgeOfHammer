using System.Collections.Concurrent;
using System.Diagnostics;
using AgeOfHammer.Core.AI;
using AgeOfHammer.Core.Data;
using AgeOfHammer.Core.Dice;
using AgeOfHammer.Core.Engine;
using AgeOfHammer.Core.State;

namespace AgeOfHammer.Core.Lab;

/// <summary>An army list: a faction and warscroll ids (repeats allowed).</summary>
public sealed record ArmyList(FactionData Faction, IReadOnlyList<string> Units)
{
    public int Points => Units.Sum(id => Faction.Warscrolls.FirstOrDefault(w => w.Id == id)?.Points ?? 0);
    public override string ToString() => $"{Faction.Name} ({Points} pts)";
}

public sealed record MatchupSettings
{
    public required Battleplan Battleplan { get; init; }
    /// <summary>Total games. With <see cref="SwapSides"/>, each seed is played twice (A as player 1, then as player 2).</summary>
    public int Games { get; init; } = 1000;
    public bool SwapSides { get; init; } = true;
    public bool UseReserves { get; init; } = true;
    public int FirstSeed { get; init; } = 1;
    /// <summary>Threads to use; -1 = all cores.</summary>
    public int MaxParallelism { get; init; } = -1;
}

/// <summary>One simulated game. <see cref="Winner"/>: 0 = army A, 1 = army B, null = draw.</summary>
public sealed record GameSummary(int Seed, bool Swapped, int? Winner, int VpA, int VpB, int Rounds, bool Wipeout, string? Error = null);

/// <summary>Results for one warscroll on one side, pooled over every copy in the list and every game.</summary>
public sealed class UnitSummary
{
    public required string Side { get; init; }
    public required string WarscrollId { get; init; }
    public required string Name { get; init; }
    public int Points { get; init; }
    public int Instances { get; set; }
    public int Survived { get; set; }
    public double DamageDealt { get; set; }
    public double ModelsSlain { get; set; }
    public double DamageTaken { get; set; }

    public double SurvivalRate => Instances == 0 ? 0 : (double)Survived / Instances;
    public double DamagePerGame => Instances == 0 ? 0 : DamageDealt / Instances;
    public double DamagePer100Points => Points == 0 ? 0 : DamagePerGame * 100 / Points;
}

/// <summary>A proportion with its 95% Wilson score interval.</summary>
public readonly record struct Rate(double Value, double Low, double High)
{
    public static Rate Wilson(int successes, int n, double z = 1.96)
    {
        if (n == 0) return new Rate(0, 0, 1);
        double p = (double)successes / n, z2 = z * z;
        double denom = 1 + z2 / n;
        double centre = (p + z2 / (2 * n)) / denom;
        double half = z * Math.Sqrt(p * (1 - p) / n + z2 / (4.0 * n * n)) / denom;
        return new Rate(p, Math.Max(0, centre - half), Math.Min(1, centre + half));
    }

    public override string ToString() => $"{Value:P1} ({Low:P1}-{High:P1})";
}

public sealed class MatchupResult
{
    public required ArmyList A { get; init; }
    public required ArmyList B { get; init; }
    public required MatchupSettings Settings { get; init; }
    public List<GameSummary> Games { get; } = new();
    public List<UnitSummary> Units { get; } = new();
    public TimeSpan Elapsed { get; set; }

    public IEnumerable<GameSummary> Completed => Games.Where(g => g.Error == null);
    public int Failures => Games.Count(g => g.Error != null);
    public int N => Completed.Count();
    public Rate WinA => Rate.Wilson(Completed.Count(g => g.Winner == 0), N);
    public Rate WinB => Rate.Wilson(Completed.Count(g => g.Winner == 1), N);
    public Rate Draw => Rate.Wilson(Completed.Count(g => g.Winner == null), N);
    public double AvgVpA => N == 0 ? 0 : Completed.Average(g => g.VpA);
    public double AvgVpB => N == 0 ? 0 : Completed.Average(g => g.VpB);
    public double AvgRounds => N == 0 ? 0 : Completed.Average(g => g.Rounds);
    public Rate Wipeouts => Rate.Wilson(Completed.Count(g => g.Wipeout), N);

    /// <summary>Distribution of the VP margin (A minus B): margin -> number of games.</summary>
    public SortedDictionary<int, int> MarginHistogram()
    {
        var h = new SortedDictionary<int, int>();
        foreach (var g in Completed) h[g.VpA - g.VpB] = h.GetValueOrDefault(g.VpA - g.VpB) + 1;
        return h;
    }
}

/// <summary>Plays many seeded AI-vs-AI games in parallel. Every game can be replayed exactly from its seed.</summary>
public static class MatchupRunner
{
    /// <summary>Creates the engine and AI players for one game exactly as the runner does (used for replays).</summary>
    public static (GameEngine Engine, AiPlayer[] Ai) CreateGame(ArmyList a, ArmyList b, MatchupSettings s, int seed, bool swapped)
    {
        var (first, second) = swapped ? (b, a) : (a, b);
        var engine = GameEngine.Create(s.Battleplan, first.Faction, first.Units, second.Faction, second.Units, new SeededDiceRoller(seed));
        var ai = new[] { new AiPlayer(0) { UseReserves = s.UseReserves }, new AiPlayer(1) { UseReserves = s.UseReserves } };
        return (engine, ai);
    }

    public static MatchupResult Run(ArmyList a, ArmyList b, MatchupSettings s, IProgress<int>? progress = null, CancellationToken cancel = default)
    {
        var watch = Stopwatch.StartNew();
        var games = new GameSummary[s.Games];
        var unitStats = new ConcurrentBag<(string Side, Unit Unit, UnitStats Stats)>();
        int done = 0;
        var options = new ParallelOptions { MaxDegreeOfParallelism = s.MaxParallelism, CancellationToken = cancel };
        try
        {
            Parallel.For(0, s.Games, options, i =>
            {
                int seed = s.FirstSeed + (s.SwapSides ? i / 2 : i);
                bool swapped = s.SwapSides && i % 2 == 1;
                games[i] = Play(a, b, s, seed, swapped, unitStats);
                progress?.Report(Interlocked.Increment(ref done));
            });
        }
        catch (OperationCanceledException) { }

        var result = new MatchupResult { A = a, B = b, Settings = s };
        result.Games.AddRange(games.Where(g => g != null));
        foreach (var group in unitStats.GroupBy(x => (x.Side, x.Unit.Warscroll.Id)).OrderBy(g => g.Key.Side).ThenBy(g => g.Key.Id))
        {
            var ws = group.First().Unit.Warscroll;
            result.Units.Add(new UnitSummary
            {
                Side = group.Key.Side, WarscrollId = ws.Id, Name = ws.Name, Points = ws.Points,
                Instances = group.Count(),
                Survived = group.Count(x => !x.Unit.IsDestroyed),
                DamageDealt = group.Sum(x => x.Stats.DamageDealt),
                ModelsSlain = group.Sum(x => x.Stats.ModelsSlain),
                DamageTaken = group.Sum(x => x.Stats.DamageTaken),
            });
        }
        result.Elapsed = watch.Elapsed;
        return result;
    }

    private static GameSummary Play(ArmyList a, ArmyList b, MatchupSettings s, int seed, bool swapped,
        ConcurrentBag<(string, Unit, UnitStats)> unitStats)
    {
        var (engine, ai) = CreateGame(a, b, s, seed, swapped);
        var st = engine.State;
        try
        {
            int guard = 0;
            while (st.Phase != Phase.GameOver)
            {
                if (++guard > 50_000) throw new InvalidOperationException("game did not finish");
                ai[engine.Decider].Act(engine);
            }
        }
        catch (Exception ex)
        {
            return new GameSummary(seed, swapped, null, 0, 0, st.BattleRound, false, ex.Message);
        }

        int sideA = swapped ? 1 : 0;
        foreach (var u in st.Units)
            unitStats.Add((u.Owner == sideA ? "A" : "B", u, st.StatsOf(u)));
        int? winner = st.IsDraw || st.Winner == null ? null : st.Winner == sideA ? 0 : 1;
        bool wipeout = st.Log.Count > 0 && st.Log[^1].Contains("wiped out");
        return new GameSummary(seed, swapped, winner, st.Players[sideA].VictoryPoints, st.Players[1 - sideA].VictoryPoints, st.BattleRound, wipeout);
    }
}
