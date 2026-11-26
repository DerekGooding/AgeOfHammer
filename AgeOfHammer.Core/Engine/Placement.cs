using System.Numerics;
using AgeOfHammer.Core.Geometry;
using AgeOfHammer.Core.State;

namespace AgeOfHammer.Core.Engine;

/// <summary>Helpers that compute legal model positions. Used by both the UI and the AI.</summary>
public static class Placement
{
    public const float PileInDistance = 3f;
    private const float ContactGap = 0.05f;

    public static List<Vector2> Translate(Unit unit, Vector2 delta) => unit.AlivePositions.Select(p => p + delta).ToList();

    /// <summary>Moves the whole formation toward a point as far as is legal. Null if it cannot move at all.</summary>
    public static List<Vector2>? MoveToward(GameEngine engine, PendingMove pm, Vector2 point)
    {
        var unit = pm.Unit;
        var delta = point - unit.Centroid;
        float len = delta.Length();
        if (len < 0.05f) return null;
        var dir = delta / len;
        for (float d = MathF.Min(len, pm.Allowance); d > 0.05f; d -= 0.25f)
        {
            var pos = Translate(unit, dir * d);
            if (engine.ValidateMove(pm, pos) == null) return pos;
        }
        return null;
    }

    /// <summary>Formation moved directly by the mouse delta, clamped to the move allowance (may be illegal).</summary>
    public static List<Vector2> PreviewMove(PendingMove pm, Vector2 point)
    {
        var unit = pm.Unit;
        var delta = point - unit.Centroid;
        if (delta.Length() > pm.Allowance) delta = Vector2.Normalize(delta) * pm.Allowance;
        return Translate(unit, delta);
    }

    /// <summary>Finds legal charge end positions that bring the unit within 1/2" of <paramref name="target"/>.</summary>
    public static List<Vector2>? AutoCharge(GameEngine engine, PendingMove pm, Unit target)
    {
        var unit = pm.Unit;
        float r = unit.BaseRadius, rt = target.BaseRadius;

        // 1) Translate the formation along the closest model pair.
        var (ma, mb, gap) = ClosestPair(unit, target);
        if (gap <= pm.Allowance + Geo.ChargeEndRange)
        {
            var dir = Vector2.Normalize(mb - ma);
            foreach (var stop in new[] { ContactGap, 0.2f, 0.4f })
            {
                float d = gap - stop;
                if (d < 0 || d > pm.Allowance) continue;
                var pos = Translate(unit, dir * d);
                if (engine.ValidateMove(pm, pos) == null) return pos;
            }
        }

        // 2) Translate the formation toward the target's centre.
        var dir2 = target.Centroid - unit.Centroid;
        if (dir2.LengthSquared() > 1e-6f)
        {
            dir2 = Vector2.Normalize(dir2);
            for (float t = 0; t <= pm.Allowance + Geo.Epsilon; t += 0.1f)
            {
                var pos = Translate(unit, dir2 * t);
                float closest = pos.Min(p => target.Alive.Min(m => Geo.Gap(p, r, m.Position, rt)));
                if (closest < -Geo.Epsilon) break;
                if (closest <= Geo.ChargeEndRange - 0.05f && engine.ValidateMove(pm, pos) == null) return pos;
            }
        }

        // 3) Move each model individually toward its nearest target model.
        var state = engine.State;
        var positions = unit.AlivePositions;
        var order = Enumerable.Range(0, positions.Count)
            .OrderBy(i => target.Alive.Min(m => Geo.Gap(positions[i], r, m.Position, rt))).ToList();
        foreach (var i in order)
            positions[i] = StepToward(state, unit, positions, i, target, pm.Allowance);
        return engine.ValidateMove(pm, positions) == null ? positions : null;
    }

    /// <summary>Pile-in: each model may move up to 3" toward the target, ending no further away. Keeps coherency.</summary>
    public static List<Vector2> PileIn(GameState state, Unit unit, Unit target)
    {
        var original = unit.AlivePositions;
        var positions = new List<Vector2>(original);
        float r = unit.BaseRadius, rt = target.BaseRadius;
        var order = Enumerable.Range(0, positions.Count)
            .OrderBy(i => target.Alive.Min(m => Geo.Gap(positions[i], r, m.Position, rt))).ToList();
        foreach (var i in order)
            positions[i] = StepToward(state, unit, positions, i, target, PileInDistance);
        if (Geo.IsCoherent(positions, r)) return positions;

        // Fall back to moving the whole formation, which preserves coherency.
        var (ma, mb, gap) = ClosestPair(unit, target);
        if (gap > ContactGap)
        {
            var dir = Vector2.Normalize(mb - ma);
            for (float d = MathF.Min(PileInDistance, gap - ContactGap); d > 0.05f; d -= 0.25f)
            {
                var pos = original.Select(p => p + dir * d).ToList();
                if (!AnyOverlap(state, unit, pos)) return pos;
            }
        }
        return original;
    }

    private static Vector2 StepToward(GameState state, Unit unit, List<Vector2> positions, int i, Unit target, float maxMove)
    {
        float r = unit.BaseRadius, rt = target.BaseRadius;
        var from = positions[i];
        var nearest = target.Alive.OrderBy(m => Vector2.Distance(from, m.Position)).First();
        float gap = Geo.Gap(from, r, nearest.Position, rt);
        if (gap <= ContactGap) return from;
        var dir = Vector2.Normalize(nearest.Position - from);
        float move = MathF.Min(maxMove, gap - ContactGap);
        for (float d = move; d > 0.05f; d -= 0.2f)
        {
            var p = from + dir * d;
            if (!state.Bounds.ContainsCircle(p, r)) continue;
            if (!unit.Warscroll.CanFly && state.Battleplan.Terrain.Any(t => t.Impassable && Geo.SweptCircleHitsRect(from, p, r, t.Area.ToRect())))
                continue;
            if (OverlapsAt(state, unit, positions, i, p)) continue;
            return p;
        }
        return from;
    }

    private static bool OverlapsAt(GameState state, Unit unit, List<Vector2> positions, int index, Vector2 p)
    {
        float r = unit.BaseRadius;
        for (int j = 0; j < positions.Count; j++)
            if (j != index && Geo.Overlaps(p, r, positions[j], r)) return true;
        foreach (var other in state.Units)
        {
            if (other == unit || !other.OnBattlefield) continue;
            foreach (var m in other.Alive)
                if (Geo.Overlaps(p, r, m.Position, other.BaseRadius)) return true;
        }
        return false;
    }

    private static bool AnyOverlap(GameState state, Unit unit, List<Vector2> positions)
    {
        for (int i = 0; i < positions.Count; i++)
            if (OverlapsAt(state, unit, positions, i, positions[i])) return true;
        return false;
    }

    public static (Vector2 A, Vector2 B, float Gap) ClosestPair(Unit a, Unit b)
    {
        Vector2 ba = default, bb = default;
        float best = float.MaxValue;
        foreach (var ma in a.Alive)
            foreach (var mb in b.Alive)
            {
                float g = Geo.Gap(ma.Position, a.BaseRadius, mb.Position, b.BaseRadius);
                if (g < best) { best = g; ba = ma.Position; bb = mb.Position; }
            }
        return (ba, bb, best);
    }

    /// <summary>A legal deployment formation as close to <paramref name="center"/> as possible, or null.</summary>
    public static List<Vector2>? DeployNear(GameEngine engine, Unit unit, Vector2 center)
    {
        var zone = engine.State.TerritoryOf(unit.Owner);
        int perRow = unit.Models.Count >= 10 ? 10 : 5;
        for (float radius = 0; radius < 40; radius += 1f)
        {
            int steps = radius == 0 ? 1 : (int)(radius * 6);
            for (int s = 0; s < steps; s++)
            {
                float ang = s * MathF.Tau / steps;
                var c = zone.Clamp(center + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * radius);
                var pos = Geo.Formation(unit.Models.Count, unit.BaseRadius, c, perRow);
                if (engine.ValidateDeployment(unit, pos) == null) return pos;
            }
        }
        return null;
    }
}
