using System.Numerics;

namespace AgeOfHammer.Core.Geometry;

/// <summary>
/// A model's base as a stadium (capsule): every point within <see cref="Radius"/> of the segment
/// <see cref="Center"/> ± <see cref="Half"/>. A round base has a zero-length segment; an oval base (e.g. 60 x 35mm)
/// has a segment of (length - width) and radius width / 2. The stadium is a close, slightly generous stand-in for
/// an ellipse, and distances between stadiums are cheap to compute exactly.
/// </summary>
public readonly record struct Base(Vector2 Center, Vector2 Half, float Radius)
{
    public Vector2 A => Center - Half;
    public Vector2 B => Center + Half;
    /// <summary>Distance from the centre to the farthest point of the base.</summary>
    public float Reach => Half.Length() + Radius;
    public bool IsRound => Half == Vector2.Zero;

    public static Base Circle(Vector2 center, float radius) => new(center, Vector2.Zero, radius);

    /// <summary>Size of the base's axis-aligned bounding box (inches).</summary>
    public Vector2 Extent => new(MathF.Abs(Half.X) * 2 + Radius * 2, MathF.Abs(Half.Y) * 2 + Radius * 2);

    public Base MovedTo(Vector2 center) => this with { Center = center };

    public bool Contains(Vector2 p) => Geo.SegmentPointDistance(A, B, p) <= Radius;
}

public static partial class Geo
{
    /// <summary>Edge-to-edge gap between two bases (negative when they overlap).</summary>
    public static float Gap(Base a, Base b) =>
        (a.IsRound && b.IsRound ? Vector2.Distance(a.Center, b.Center) : SegmentSegmentDistance(a.A, a.B, b.A, b.B)) - a.Radius - b.Radius;

    public static float Distance(Base a, Base b) => MathF.Max(0, Gap(a, b));

    public static bool Overlaps(Base a, Base b) => Gap(a, b) < -Epsilon;

    /// <summary>The whole base is inside the rectangle.</summary>
    public static bool Inside(RectF rect, Base b) => rect.ContainsCircle(b.A, b.Radius) && rect.ContainsCircle(b.B, b.Radius);

    /// <summary>Any part of the base touches the rectangle.</summary>
    public static bool Touches(Base b, RectF rect) =>
        b.IsRound ? CircleIntersectsRect(b.Center, b.Radius, rect) : SweptCircleHitsRect(b.A, b.B, b.Radius, rect);

    /// <summary>Moving a base from <paramref name="from"/> to <paramref name="to"/> touches the rectangle at any point.</summary>
    public static bool SweepTouches(Base from, Base to, RectF rect) =>
        SweptCircleHitsRect(from.A, to.A, from.Radius, rect) || SweptCircleHitsRect(from.B, to.B, from.Radius, rect)
        || SweptCircleHitsRect(from.Center, to.Center, from.Radius, rect);

    /// <summary>Coherency for bases of any shape: one group, each within <paramref name="range"/> of another.</summary>
    public static bool IsCoherent(IReadOnlyList<Base> bases, float range = CoherencyRange)
    {
        int n = bases.Count;
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
                if (seen[j] || Gap(bases[i], bases[j]) > range + Epsilon) continue;
                seen[j] = true;
                count++;
                stack.Push(j);
            }
        }
        return count == n;
    }

    /// <summary>Shortest distance between segments p1-p2 and q1-q2.</summary>
    public static float SegmentSegmentDistance(Vector2 p1, Vector2 p2, Vector2 q1, Vector2 q2)
    {
        if (SegmentsIntersect(p1, p2, q1, q2)) return 0;
        return MathF.Min(MathF.Min(SegmentPointDistance(p1, p2, q1), SegmentPointDistance(p1, p2, q2)),
                         MathF.Min(SegmentPointDistance(q1, q2, p1), SegmentPointDistance(q1, q2, p2)));
    }

    private static bool SegmentsIntersect(Vector2 p1, Vector2 p2, Vector2 q1, Vector2 q2)
    {
        static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        float d1 = Cross(q1, q2, p1), d2 = Cross(q1, q2, p2), d3 = Cross(p1, p2, q1), d4 = Cross(p1, p2, q2);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    /// <summary>Lays out <paramref name="count"/> bases of the given bounding-box size in rows around a centre point.</summary>
    public static List<Vector2> Formation(int count, Vector2 size, Vector2 center, int perRow = 5, float gap = 0.25f)
    {
        var result = new List<Vector2>(count);
        float sx = size.X + gap, sy = size.Y + gap;
        int rows = (int)MathF.Ceiling(count / (float)perRow);
        for (int i = 0; i < count; i++)
        {
            int row = i / perRow, col = i % perRow;
            int inRow = Math.Min(perRow, count - row * perRow);
            result.Add(center + new Vector2((col - (inRow - 1) / 2f) * sx, (row - (rows - 1) / 2f) * sy));
        }
        return result;
    }
}
