using AgeOfHammer.Core.AI;
using AgeOfHammer.Core.Data;
using AgeOfHammer.Core.Dice;
using AgeOfHammer.Core.Engine;
using AgeOfHammer.Core.State;

// Headless balance sweep: plays AI vs AI battles across many seeds and reports win rates and unit performance.
//
//   dotnet run --project tools/AgeOfHammer.Balance -- [options]
//
//   --seeds <n>            games per pairing (default 200)
//   --data <dir>           alternative data directory (e.g. an edited copy of data/) to compare balance changes
//   --battleplan <id>      battleplan id (default: the first one)
//   --p1 <faction>         player 1 faction id (default stormcast-eternals); --p2 likewise (default skaven)
//   --army1 <ids>          comma-separated warscroll ids for player 1 (default: the faction's defaultArmy); --army2 likewise
//   --no-swap              don't also play every pairing with the sides swapped (swapping cancels out territory bias)
//   --details              per-warscroll damage dealt, damage taken and survival

int seeds = 200;
string? dataDir = null, planId = null, army1 = null, army2 = null;
string p1 = "stormcast-eternals", p2 = "skaven";
bool swap = true, details = false;

for (int i = 0; i < args.Length; i++)
{
    string arg = args[i];
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{arg} needs a value.");
    switch (arg)
    {
        case "--seeds": seeds = int.Parse(Next()); break;
        case "--data": dataDir = Next(); break;
        case "--battleplan": planId = Next(); break;
        case "--p1": p1 = Next(); break;
        case "--p2": p2 = Next(); break;
        case "--army1": army1 = Next(); break;
        case "--army2": army2 = Next(); break;
        case "--no-swap": swap = false; break;
        case "--details": details = true; break;
        default:
            Console.Error.WriteLine($"Unknown argument '{arg}'.");
            return 2;
    }
}

dataDir ??= DataLoader.FindDataDirectory(Directory.GetCurrentDirectory());
List<FactionData> factions;
Battleplan plan;
try
{
    factions = DataLoader.LoadAllFactions(dataDir);
    var plans = DataLoader.LoadAllBattleplans(dataDir);
    plan = planId is null ? plans[0] : plans.FirstOrDefault(p => p.Id == planId)
        ?? throw new ArgumentException($"No battleplan '{planId}'. Known: {string.Join(", ", plans.Select(p => p.Id))}");
}
catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

FactionData Faction(string id) => factions.FirstOrDefault(f => f.Id == id)
    ?? throw new ArgumentException($"No faction '{id}'. Known: {string.Join(", ", factions.Select(f => f.Id))}");

List<string> Army(FactionData f, string? csv) =>
    csv is null ? f.DefaultArmy : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

FactionData fa, fb;
List<string> armyA, armyB;
try
{
    fa = Faction(p1);
    fb = Faction(p2);
    armyA = Army(fa, army1);
    armyB = Army(fb, army2);
    foreach (var id in armyA) fa.Get(id);
    foreach (var id in armyB) fb.Get(id);
}
catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

Console.WriteLine($"Battleplan: {plan.Name}   Data: {dataDir}");
Console.WriteLine($"A = {fa.Name}: {string.Join(", ", armyA)}");
Console.WriteLine($"B = {fb.Name}: {string.Join(", ", armyB)}");
Console.WriteLine();

var results = new List<GameResult>();
var unitStats = new Dictionary<string, UnitStats>();
int failures = 0;

foreach (bool swapped in swap ? new[] { false, true } : [false])
{
    for (int seed = 1; seed <= seeds; seed++)
    {
        // Side 0 deploys in the bottom territory; swapping puts army A on top.
        var (f0, a0, f1, a1) = swapped ? (fb, armyB, fa, armyA) : (fa, armyA, fb, armyB);
        var engine = GameEngine.Create(plan, f0, a0, f1, a1, new SeededDiceRoller(seed));
        var ai = new[] { new AiPlayer(0), new AiPlayer(1) };
        try
        {
            int guard = 0;
            while (engine.State.Phase != Phase.GameOver)
            {
                if (++guard > 50_000) throw new InvalidOperationException("game did not finish");
                ai[engine.Decider].Act(engine);
            }
        }
        catch (Exception ex)
        {
            failures++;
            Console.Error.WriteLine($"  seed {seed}{(swapped ? " (swapped)" : "")}: {ex.Message}");
            continue;
        }

        var s = engine.State;
        int sideA = swapped ? 1 : 0;
        results.Add(new GameResult(
            AWon: s.Winner == sideA, BWon: s.Winner == 1 - sideA, Draw: s.IsDraw,
            VpA: s.Players[sideA].VictoryPoints, VpB: s.Players[1 - sideA].VictoryPoints,
            Rounds: s.BattleRound, Wipeout: s.Log.Last().Contains("wiped out")));

        if (details)
            // Units with the same name can't be told apart in the log, so they're pooled per game.
            foreach (var group in s.Units.GroupBy(u => (u.Owner, u.Name)))
            {
                string key = $"{(group.Key.Owner == sideA ? "A" : "B")} {group.Key.Name}";
                var st = unitStats.TryGetValue(key, out var existing) ? existing : unitStats[key] = new UnitStats();
                foreach (var u in group)
                {
                    st.Games++;
                    st.Survived += u.IsDestroyed ? 0 : 1;
                    st.WoundsLostFraction += 1 - u.WoundsRemaining / (double)(u.Warscroll.Health * u.Models.Count);
                }
                // (In a mirror match both sides' units share names, so damage dealt is pooled across sides.)
                st.DamageDealt += s.Log.Where(l => l.StartsWith(group.Key.Name + " attacks ")).Sum(DamageFromLog);
            }
    }
}

int n = results.Count;
if (n == 0)
{
    Console.Error.WriteLine("No games finished.");
    return 1;
}

Console.WriteLine($"{n} games{(swap ? " (half with sides swapped)" : "")}{(failures > 0 ? $", {failures} FAILED" : "")}");
Console.WriteLine($"  {fa.Name,-22} wins {Pct(results.Count(r => r.AWon), n)}   avg VP {results.Average(r => r.VpA):0.0}");
Console.WriteLine($"  {fb.Name,-22} wins {Pct(results.Count(r => r.BWon), n)}   avg VP {results.Average(r => r.VpB):0.0}");
Console.WriteLine($"  Draws {Pct(results.Count(r => r.Draw), n)}   wipeouts {Pct(results.Count(r => r.Wipeout), n)}   avg rounds {results.Average(r => r.Rounds):0.0}");

if (details)
{
    Console.WriteLine();
    Console.WriteLine($"  {"Unit",-34} {"survived",9} {"wounds lost",12} {"dmg dealt/game",15}");
    foreach (var (key, st) in unitStats.OrderBy(k => k.Key))
        Console.WriteLine($"  {key,-34} {Pct(st.Survived, st.Games),9} {st.WoundsLostFraction / st.Games,12:P0} {st.DamageDealt / (double)st.Games,15:0.0}");
    Console.WriteLine("  (several units of the same warscroll are pooled)");
}

return failures > 0 ? 1 : 0;

static string Pct(int count, int total) => $"{100.0 * count / total,5:0.0}%";

// Attack summaries end with ". <n> damage, <m> slain".
static int DamageFromLog(string line)
{
    int idx = line.LastIndexOf(" damage, ", StringComparison.Ordinal);
    if (idx < 0) return 0;
    int start = line.LastIndexOf(' ', idx - 1) + 1;
    return int.TryParse(line[start..idx], out var dmg) ? dmg : 0;
}

internal sealed record GameResult(bool AWon, bool BWon, bool Draw, int VpA, int VpB, int Rounds, bool Wipeout);

internal sealed class UnitStats
{
    public int Games;
    public int Survived;
    public double WoundsLostFraction;
    public int DamageDealt;
}
