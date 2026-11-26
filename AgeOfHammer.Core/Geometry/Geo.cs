using System.Numerics;

namespace AgeOfHammer.Core.Geometry;

/// <summary>Axis-aligned rectangle in battlefield inches.</summary>
public readonly record struct RectF(float X, float Y, float W, float H)
{
    public float Right => X + W;
    public float Bottom => Y + H;
    public Vector2 Center => new(X + W / 2, Y + H / 2);
    public bool Contains(Vector2 p) => p.X >= X && p.X <= Right && p.Y >= Y && p.Y <= Bottom;
    /// <summary>True if a circular base is wholly within the rectangle.</summary>
    public bool ContainsCircle(Vector2 c, float r) => c.X - r >= X && c.X + r <= Right && c.Y - r >= Y && c.Y + r <= Bottom;
    public Vector2 Clamp(Vector2 p) => new(Math.Clamp(p.X, X, Right), Math.Clamp(p.Y, Y, Bottom));
}

/// <summary>Measurement helpers. All units are inches; models are circles (base radius).</summary>
public static class Geo
{
    public const float CombatRange = 3f;
    public const float CoherencyRange = 0.5f;
    public const float ChargeEndRange = 0.5f;
    public const float Epsilon = 0.01f;

    /// <summary>Base-to-base gap. Negative means the bases overlap.</summary>
    public static float Gap(Vector2 a, float ra, Vector2 b, float rb) => Vector2.Distance(a, b) - ra - rb;

    /// <summary>Base-to-base distance (never negative).</summary>
    public static float Distance(Vector2 a, float ra, Vector2 b, float rb) => MathF.Max(0, Gap(a, ra, b, rb));

    public static bool Overlaps(Vector2 a, float ra, Vector2 b, float rb) => Gap(a, ra, b, rb) < -Epsilon;

    public static float SegmentPointDistance(Vector2 a, Vector2 b, Vector2 p)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        if (len2 < 1e-9f) return Vector2.Distance(a, p);
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0, 1);
        return Vector2.Distance(a + ab * t, p);
    }

    public static bool CircleIntersectsRect(Vector2 c, float r, RectF rect)
    {
        var closest = rect.Clamp(c);
        return Vector2.DistanceSquared(closest, c) < r * r;
    }

    /// <summary>True if a circle of radius r swept from a to b touches the rectangle.</summary>
    public static bool SweptCircleHitsRect(Vector2 a, Vector2 b, float r, RectF rect)
    {
        float len = Vector2.Distance(a, b);
        int steps = Math.Max(1, (int)MathF.Ceiling(len / MathF.Max(0.1f, r * 0.5f)));
        for (int i = 0; i <= steps; i++)
            if (CircleIntersectsRect(Vector2.Lerp(a, b, i / (float)steps), r, rect)) return true;
        return false;
    }

    public static bool SegmentIntersectsRect(Vector2 a, Vector2 b, RectF rect) => SweptCircleHitsRect(a, b, 0.001f, rect);

    /// <summary>A unit is coherent if every model is within 1/2" of another and they form a single group.</summary>
    public static bool IsCoherent(IReadOnlyList<Vector2> positions, float radius, float range = CoherencyRange)
    {
        int n = positions.Count;
        if (n <= 1) return true;
        var seen = new bool[n];
        var stack = new Stack<int>();
        stack.Push(0);
        seen[0] = true;
        int count = 1;
        while (stack.Count > 0)
        {
            int i = stack.Pop();
            for (int j = 0; j < n; j++)
            {
                if (seen[j]) continue;
                if (Gap(positions[i], radius, positions[j], radius) <= range + Epsilon)
                {
                    seen[j] = true;
                    count++;
                    stack.Push(j);
                }
            }
        }
        return count == n;
    }

    public static Vector2 Centroid(IReadOnlyList<Vector2> points)
    {
        if (points.Count == 0) return Vector2.Zero;
        var sum = Vector2.Zero;
        foreach (var p in points) sum += p;
        return sum / points.Count;
    }

    /// <summary>Lays out <paramref name="count"/> bases in rows around a centre point.</summary>
    public static List<Vector2> Formation(int count, float radius, Vector2 center, int perRow = 5, float gap = 0.25f)
    {
        var result = new List<Vector2>(count);
        float spacing = radius * 2 + gap;
        int rows = (int)MathF.Ceiling(count / (float)perRow);
        for (int i = 0; i < count; i++)
        {
            int row = i / perRow, col = i % perRow;
            int inRow = Math.Min(perRow, count - row * perRow);
            float x = (col - (inRow - 1) / 2f) * spacing;
            float y = (row - (rows - 1) / 2f) * spacing;
            result.Add(center + new Vector2(x, y));
        }
        return result;
    }
}
