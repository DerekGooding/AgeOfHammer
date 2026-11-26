using AgeOfHammer.Core.Data;
using AgeOfHammer.Core.Lab;

// Headless balance sweep: plays AI vs AI battles in parallel and reports win rates (95% confidence) and unit stats.
//
//   dotnet run -c Release --project tools/AgeOfHammer.Balance -- [options]
//
//   --games <n>            number of games (default 1000; with side swapping, n/2 seeds each played both ways)
//   --data <dir>           alternative data directory (e.g. an edited copy of data/) to compare balance changes
//   --battleplan <id>      battleplan id (default: the first one)
//   --p1 <faction>         army A faction id (default stormcast-eternals); --p2 likewise for army B (default skaven)
//   --army1 <ids>          comma-separated warscroll ids for army A (default: the faction's defaultArmy); --army2 likewise
//   --no-swap              don't swap sides (swapping cancels out any first-player or territory bias)
//   --no-reserves          the AI never uses deep strike / tunnels (to measure their effect)
//   --threads <n>          limit parallelism (default: all cores)
//   --details              per-warscroll damage dealt, survival and damage per 100 points
//   --coverage             how much of each faction's rules are automated (data/scripts), then exit

int games = 1000, threads = -1;
string? dataDir = null, planId = null, army1 = null, army2 = null;
string p1 = "stormcast-eternals", p2 = "skaven";
bool swap = true, details = false, coverage = false, reserves = true;

for (int i = 0; i < args.Length; i++)
{
    string arg = args[i];
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{arg} needs a value.");
    switch (arg)
    {
        case "--games": games = int.Parse(Next()); break;
        case "--seeds": games = int.Parse(Next()) * 2; break; // backwards compatible
        case "--data": dataDir = Next(); break;
        case "--battleplan": planId = Next(); break;
        case "--p1": p1 = Next(); break;
        case "--p2": p2 = Next(); break;
        case "--army1": army1 = Next(); break;
        case "--army2": army2 = Next(); break;
        case "--no-swap": swap = false; break;
        case "--no-reserves": reserves = false; break;
        case "--threads": threads = int.Parse(Next()); break;
        case "--details": details = true; break;
        case "--coverage": coverage = true; break;
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

if (coverage)
{
    // How much of each army's rules the engine automates (data/scripts).
    Console.WriteLine($"{"Faction",-24} {"warscroll abilities",20} {"spells",10} {"battle traits",14}");
    foreach (var f in factions)
    {
        var abilities = f.Warscrolls.SelectMany(w => w.Abilities).ToList();
        var spells = f.Warscrolls.SelectMany(w => w.Spells).ToList();
        Console.WriteLine($"{f.Name,-24} {Frac(abilities.Count(a => a.IsScripted), abilities.Count),20} " +
                          $"{Frac(spells.Count(sp => sp.IsScripted), spells.Count),10} {Frac(f.BattleTraits.Count(a => a.IsScripted), f.BattleTraits.Count),14}");
        foreach (var w in f.LoadWarnings) Console.WriteLine($"  warning: {w}");
    }
    return 0;
}

ArmyList a, b;
try
{
    a = Army(p1, army1);
    b = Army(p2, army2);
}
catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

Console.WriteLine($"Battleplan: {plan.Name}   Data: {dataDir}");
Console.WriteLine($"A = {a}: {string.Join(", ", a.Units)}");
Console.WriteLine($"B = {b}: {string.Join(", ", b.Units)}");

var settings = new MatchupSettings { Battleplan = plan, Games = games, SwapSides = swap, UseReserves = reserves, MaxParallelism = threads };
var result = MatchupRunner.Run(a, b, settings);

Console.WriteLine();
Console.WriteLine($"{result.N} games in {result.Elapsed.TotalSeconds:0.0}s{(swap ? " (half with sides swapped)" : "")}" +
                  (result.Failures > 0 ? $", {result.Failures} FAILED" : ""));
Console.WriteLine($"  {a.Faction.Name,-22} wins {result.WinA}   avg VP {result.AvgVpA:0.0}");
Console.WriteLine($"  {b.Faction.Name,-22} wins {result.WinB}   avg VP {result.AvgVpB:0.0}");
Console.WriteLine($"  Draws {result.Draw.Value:P1}   wipeouts {result.Wipeouts.Value:P1}   avg rounds {result.AvgRounds:0.0}");
foreach (var g in result.Games.Where(g => g.Error != null).Take(5))
    Console.Error.WriteLine($"  seed {g.Seed}{(g.Swapped ? " (swapped)" : "")}: {g.Error}");

if (details)
{
    Console.WriteLine();
    Console.WriteLine($"  {"Unit",-36} {"pts",4} {"survived",9} {"dmg/game",9} {"dmg/100pts",11} {"taken/game",11}");
    foreach (var u in result.Units)
        Console.WriteLine($"  {u.Side} {u.Name,-34} {u.Points,4} {u.SurvivalRate,9:P0} {u.DamagePerGame,9:0.0} {u.DamagePer100Points,11:0.0} {u.DamageTaken / Math.Max(1, u.Instances),11:0.0}");
}

return result.Failures > 0 ? 1 : 0;

ArmyList Army(string factionId, string? csv)
{
    var f = factions.FirstOrDefault(x => x.Id == factionId)
            ?? throw new ArgumentException($"No faction '{factionId}'. Known: {string.Join(", ", factions.Select(x => x.Id))}");
    var units = csv is null ? f.DefaultArmy : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    foreach (var id in units) f.Get(id);
    return new ArmyList(f, units);
}

static string Frac(int n, int total) => total == 0 ? "-" : $"{n}/{total} ({100.0 * n / total:0}%)";
