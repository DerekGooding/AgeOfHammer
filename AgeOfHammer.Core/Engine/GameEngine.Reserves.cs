using System.Numerics;
using AgeOfHammer.Core.Combat;
using AgeOfHammer.Core.Data;
using AgeOfHammer.Core.Geometry;
using AgeOfHammer.Core.State;

namespace AgeOfHammer.Core.Engine;

/// <summary>
/// Reserves (deep strike, tunnels...) and battlefield markers (Gnawholes...), driven by
/// <see cref="ReserveDef"/> and <see cref="MarkerDef"/> entries in ability scripts.
/// Core rules: at most half of an army's units can start in reserve, units cannot arrive in the first battle round,
/// and units still in reserve at the start of the fourth battle round are destroyed.
/// </summary>
public sealed partial class GameEngine
{
    /// <summary>Units still in reserve after this battle round are destroyed.</summary>
    public const int LastReserveRound = 3;

    /// <summary>Reserve pools a unit may be deployed into (from its own abilities and its faction's battle traits).</summary>
    public List<string> ReservePools(Unit unit) =>
        Modifiers.AbilitiesOf(State, unit)
            .Where(a => a.Reserve is { Declare: true } r && r.Keywords.All(unit.Warscroll.HasKeyword))
            .Select(a => a.Reserve!.Pool).Distinct().ToList();

    public int ReserveLimit(int player) => State.UnitsOf(player).Count() / 2;
    public int ReservedCount(int player) => State.UnitsOf(player).Count(u => u.ReservePool != null);

    public bool CanDeployInReserve(Unit unit, string pool) =>
        State.Phase == Phase.Deployment && Pending == null && Decider == unit.Owner && !unit.Deployed
        && ReservePools(unit).Contains(pool) && ReservedCount(unit.Owner) < ReserveLimit(unit.Owner);

    private ActionResult DoDeployInReserve(DeployInReserve r)
    {
        var unit = State.GetUnit(r.UnitId);
        if (unit.Owner != r.Player) return ActionResult.Fail("Not your unit.");
        if (!CanDeployInReserve(unit, r.Pool))
            return ActionResult.Fail($"{unit.Name} cannot be set up in {r.Pool} (at most half your units can start in reserve).");
        unit.Deployed = true;
        unit.ReservePool = r.Pool;
        State.Write($"{State.Players[r.Player].Name} sets up {unit.Name} in reserve ({r.Pool}).");
        AfterDeploy(r.Player);
        return ActionResult.Success;
    }

    /// <summary>The arrival rule for a reserve unit right now, if one applies (timing and battle round included).</summary>
    public ArrivalDef? ArrivalFor(Unit unit)
    {
        if (!unit.InReserve) return null;
        foreach (var a in Modifiers.AbilitiesOf(State, unit))
        {
            if (a.Reserve?.Arrive is not { } arrive || a.Reserve.Pool != unit.ReservePool) continue;
            if (a.Phase != null && a.Phase != "movement") continue;
            bool myTurn = unit.Owner == State.ActivePlayer;
            if (a.Perspective == "your" && !myTurn || a.Perspective == "enemy" && myTurn) continue;
            if (State.BattleRound < arrive.FromRound) continue;
            return arrive;
        }
        return null;
    }

    public bool CanArrive(Unit unit) =>
        State.Phase == Phase.Movement && Pending == null && unit.Owner == State.ActivePlayer && Decider == unit.Owner
        && ArrivalFor(unit) != null;

    public IEnumerable<Unit> ArrivableUnits(int player) => State.UnitsOf(player).Where(CanArrive);

    /// <summary>Checks a set-up position for a reserve unit. Returns null if legal.</summary>
    public string? ValidateArrival(Unit unit, IReadOnlyList<Vector2> positions)
    {
        var arrive = ArrivalFor(unit);
        if (arrive == null) return $"{unit.Name} cannot arrive now.";
        if (positions.Count != unit.AliveCount) return $"Expected {unit.AliveCount} positions.";
        foreach (var p in positions)
        {
            var b = unit.BaseAt(p);
            if (!Geo.Inside(State.Bounds, b)) return "Models must be set up on the battlefield.";
            if (OverlapsTerrain(b)) return "Models cannot be set up on impassable terrain.";
            foreach (var e in State.EnemiesOf(unit))
                foreach (var m in e.Alive)
                    if (Geo.Distance(b, e.BaseOf(m)) <= arrive.MinEnemyDistance)
                        return $"Models must be set up more than {arrive.MinEnemyDistance}\" from all enemy units.";
        }
        if (arrive.Marker != null)
        {
            var markers = State.Markers.Where(m => m.Owner == unit.Owner && m.Name == arrive.Marker).ToList();
            if (markers.Count == 0) return $"You have no {arrive.Marker} on the battlefield.";
            bool ok = markers.Any(mk => positions.All(p => Vector2.Distance(p, mk.Position) + unit.BaseReach <= arrive.MarkerRange + Geo.Epsilon));
            if (!ok) return $"Every model must be wholly within {arrive.MarkerRange}\" of a friendly {arrive.Marker}.";
        }
        if (Overlaps(unit, positions)) return "Models cannot overlap other models.";
        if (!Geo.IsCoherent(positions.Select(unit.BaseAt).ToList(), CoherencyRange(unit))) return "Unit must be set up in coherency.";
        return null;
    }

    private ActionResult DoArrive(ArriveFromReserve a)
    {
        var unit = State.GetUnit(a.UnitId);
        if (unit.Owner != a.Player || !CanArrive(unit)) return ActionResult.Fail($"{unit.Name} cannot arrive now.");
        float previousFacing = unit.Facing;
        if (a.Facing is float facing) unit.Facing = facing;
        var error = ValidateArrival(unit, a.Positions);
        if (error != null) unit.Facing = previousFacing;
        if (error != null) return ActionResult.Fail(error);
        var alive = unit.Alive.ToList();
        for (int i = 0; i < alive.Count; i++) alive[i].Position = a.Positions[i];
        string pool = unit.ReservePool!;
        unit.ReservePool = null;
        unit.SetUpThisTurn = true;
        unit.Moved = true; // it cannot also make a normal move this phase
        State.Write($"{unit.Name} arrives from {pool}!");
        return ActionResult.Success;
    }

    private void DestroyLateReserves()
    {
        foreach (var u in State.Units.Where(u => u.InReserve).ToList())
        {
            foreach (var m in u.Models) m.Slain = true;
            State.Write($"{u.Name} never arrived from {u.ReservePool} and is destroyed.");
            u.ReservePool = null;
        }
        CheckWipeout();
    }

    // ------------------------------------------------------------------ markers

    /// <summary>Asks each player in turn to place their start-of-battle-round markers, then continues.</summary>
    private void OfferRoundStartMarkers(List<int> players, Action then)
    {
        foreach (int p in players)
        {
            var source = State.UnitsOf(p).FirstOrDefault();
            if (source == null) continue;
            var ability = Modifiers.AbilitiesOf(State, source)
                .FirstOrDefault(a => a.Marker != null && a.Phase == "battle-round-start"
                                     && !State.UsedAbilities.Contains($"round{State.BattleRound}|P{p}|{a.Name}"));
            if (ability == null) continue;
            State.UsedAbilities.Add($"round{State.BattleRound}|P{p}|{ability.Name}");
            var rest = players.SkipWhile(x => x != p).Skip(1).ToList();
            Pending = new PendingMarker
            {
                Decider = p, Def = ability.Marker!, Source = ability.Name,
                Then = () => OfferRoundStartMarkers(rest, then),
            };
            return;
        }
        then();
    }

    /// <summary>Checks a marker position. Returns null if legal.</summary>
    public string? ValidateMarker(int owner, MarkerDef def, Vector2 p)
    {
        var mb = Base.Circle(p, def.Radius);
        if (!State.Bounds.ContainsCircle(p, def.Radius)) return "The marker must be on the battlefield.";
        foreach (var e in State.Units.Where(u => u.Owner != owner && u.OnBattlefield))
            foreach (var m in e.Alive)
                if (Geo.Distance(mb, e.BaseOf(m)) <= def.MinEnemyDistance)
                    return $"Must be more than {def.MinEnemyDistance}\" from all enemy units.";
        foreach (var o in State.Objectives)
            if (Geo.Distance(p, def.Radius, o.Position, Objective.MarkerRadius) <= def.MinObjectiveDistance)
                return $"Must be more than {def.MinObjectiveDistance}\" from all objectives.";
        foreach (var t in State.Battleplan.Terrain)
        {
            var rect = t.Area.ToRect();
            float d = Vector2.Distance(rect.Clamp(p), p) - def.Radius;
            if (d <= def.MinTerrainDistance) return $"Must be more than {def.MinTerrainDistance}\" from terrain.";
        }
        foreach (var mk in State.Markers)
            if (Vector2.Distance(mk.Position, p) - mk.Radius - def.Radius <= def.MinTerrainDistance)
                return $"Must be more than {def.MinTerrainDistance}\" from other terrain features.";
        foreach (var u in State.Units.Where(u => u.OnBattlefield))
            foreach (var m in u.Alive)
                if (Geo.Overlaps(mb, u.BaseOf(m))) return "Cannot be placed on top of models.";
        return null;
    }

    private ActionResult DoPlaceMarker(PendingMarker pm, PlaceMarker pl)
    {
        if (pl.Position is Vector2 p)
        {
            var error = ValidateMarker(pm.Decider, pm.Def, p);
            if (error != null) return ActionResult.Fail(error);
            State.Markers.Add(new Marker { Owner = pm.Decider, Name = pm.Def.Name, Position = p, Radius = pm.Def.Radius });
            State.Write($"{State.Players[pm.Decider].Name} places a {pm.Def.Name} ({pm.Source}).");
        }
        else State.Write($"{State.Players[pm.Decider].Name} does not place a {pm.Def.Name}.");
        Pending = null;
        pm.Then?.Invoke();
        return ActionResult.Success;
    }
}
