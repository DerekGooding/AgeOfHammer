using AgeOfHammer.Core.Engine;
using AgeOfHammer.Core.Geometry;
using AgeOfHammer.Core.State;
using Godot;
using NVec = System.Numerics.Vector2;

namespace AgeOfHammer.Client;

/// <summary>Draws the battlefield in world space (1 inch = <see cref="BattleScreen.Px"/> pixels) and handles board mouse input.</summary>
public partial class BoardView : Node2D
{
    public BattleScreen Screen { get; set; } = null!;

    private const float Px = BattleScreen.Px;
    private static Vector2 G(NVec v) => BattleScreen.G(v);

    /// <summary>One screen pixel in board units, so line widths stay constant while zooming.</summary>
    private float OnePixel => 1f / Screen.Camera.Zoom.X;

    // ------------------------------------------------------------------ input

    public override void _UnhandledInput(InputEvent e)
    {
        var cam = Screen.Camera;
        switch (e)
        {
            case InputEventMouseButton { Pressed: true } mb when mb.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown:
                if (!Screen.MouseOverBoard) return;
                ZoomAt(mb.Position, mb.ButtonIndex == MouseButton.WheelUp ? 1.15f : 1 / 1.15f);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }:
                Screen.OnBoardClick();
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right }:
                Screen.CancelMode();
                break;
            case InputEventMouseMotion mm when (mm.ButtonMask & MouseButtonMask.Right) != 0:
                cam.Position -= mm.Relative / cam.Zoom.X;
                break;
            default:
                return;
        }
        GetViewport().SetInputAsHandled();
    }

    private void ZoomAt(Vector2 screenPos, float factor)
    {
        var cam = Screen.Camera;
        var half = GetViewport().GetVisibleRect().Size / 2;
        float z = cam.Zoom.X;
        var world = cam.Position + (screenPos - half) / z;
        float nz = Mathf.Clamp(z * factor, 0.2f, 4f);
        cam.Zoom = new Vector2(nz, nz);
        cam.Position = world - (screenPos - half) / nz;
    }

    // ------------------------------------------------------------------ drawing

    public override void _Draw()
    {
        if (Screen?.Engine == null) return;
        DrawBattlefield();
        DrawObjectives();
        DrawMarkers();
        DrawUnits();
        DrawOverlays();
    }

    /// <summary>Outline of a base (circle or stadium) in board pixels, optionally grown by <paramref name="growPx"/> screen pixels.</summary>
    private Vector2[] BaseOutline(Base b, float growPx = 0)
    {
        float r = b.Radius * Px + growPx * OnePixel;
        var a = G(b.A);
        var c = G(b.B);
        float angle = b.IsRound ? 0 : Mathf.Atan2(b.Half.Y, b.Half.X);
        const int n = 14;
        var pts = new Vector2[2 * (n + 1)];
        for (int i = 0; i <= n; i++)
        {
            float t1 = angle - Mathf.Pi / 2 + Mathf.Pi * i / n;   // around B
            float t2 = angle + Mathf.Pi / 2 + Mathf.Pi * i / n;   // around A
            pts[i] = c + new Vector2(Mathf.Cos(t1), Mathf.Sin(t1)) * r;
            pts[n + 1 + i] = a + new Vector2(Mathf.Cos(t2), Mathf.Sin(t2)) * r;
        }
        return pts;
    }

    private void FillBase(Base b, Color color, float growPx = 0)
    {
        if (b.IsRound) DrawCircle(G(b.Center), b.Radius * Px + growPx * OnePixel, color, true, -1, true);
        else DrawColoredPolygon(BaseOutline(b, growPx), color);
    }

    private void OutlineBase(Base b, Color color, float widthPx, float growPx)
    {
        if (b.IsRound) { Ring(G(b.Center), b.Radius * Px + growPx * OnePixel, color, widthPx, 32); return; }
        var pts = BaseOutline(b, growPx);
        var closed = new Vector2[pts.Length + 1];
        pts.CopyTo(closed, 0);
        closed[^1] = pts[0];
        DrawPolyline(closed, color, widthPx * OnePixel, true);
    }

    private void Ring(Vector2 center, float radius, Color color, float widthPx, int points = 48) =>
        DrawArc(center, radius, 0, Mathf.Tau, points, color, widthPx * OnePixel, true);

    private void DrawBattlefield()
    {
        var s = Screen.S;
        var plan = s.Battleplan;
        float w = plan.Width * Px, h = plan.Height * Px;
        DrawRect(new Rect2(-6, -6, w + 12, h + 12), Color.Color8(10, 12, 10));
        DrawRect(new Rect2(0, 0, w, h), Palette.Board);
        var grid = new Color(0, 0, 0, 0.12f);
        for (int x = 6; x < plan.Width; x += 6) DrawLine(new Vector2(x * Px, 0), new Vector2(x * Px, h), grid, OnePixel);
        for (int y = 6; y < plan.Height; y += 6) DrawLine(new Vector2(0, y * Px), new Vector2(w, y * Px), grid, OnePixel);

        for (int p = 0; p < 2; p++)
        {
            var t = s.TerritoryOf(p);
            var rect = new Rect2(t.X * Px, t.Y * Px, t.W * Px, t.H * Px);
            DrawRect(rect, Screen.Colors[p] with { A = 0.10f });
            DrawRect(rect, Screen.Colors[p] with { A = 0.5f }, false, 2 * OnePixel);
        }
        foreach (var terrain in plan.Terrain)
        {
            var r = terrain.Area.ToRect();
            var rect = new Rect2(r.X * Px, r.Y * Px, r.W * Px, r.H * Px);
            DrawRect(rect, Palette.Terrain);
            DrawRect(rect, Color.Color8(40, 34, 28), false, 2 * OnePixel);
        }
    }

    private void DrawObjectives()
    {
        foreach (var o in Screen.S.Objectives)
        {
            var c = o.ControlledBy >= 0 ? Screen.Colors[o.ControlledBy] : Colors.LightGray;
            var pos = G(o.Position);
            Ring(pos, (Objective.MarkerRadius + Objective.ControlRange) * Px, c with { A = 0.55f }, 2, 64);
            DrawCircle(pos, Objective.MarkerRadius * Px, new Color(0, 0, 0, 0.6f), true, -1, true);
            DrawCircle(pos, Objective.MarkerRadius * 0.8f * Px, c, true, -1, true);
        }
    }

    private void DrawMarkers()
    {
        foreach (var m in Screen.S.Markers)
        {
            var pos = G(m.Position);
            DrawCircle(pos, m.Radius * Px, new Color(0.1f, 0.08f, 0.05f, 0.95f), true, -1, true);
            Ring(pos, m.Radius * Px, Screen.Colors[m.Owner], 2.5f, 32);
            Ring(pos, m.Radius * 0.55f * Px, Screen.Colors[m.Owner] with { A = 0.5f }, 1.5f, 24);
        }
    }

    private void DrawUnits()
    {
        var s = Screen.S;
        var targets = Screen.ValidTargets().ToHashSet();
        var pendingMove = Screen.Engine.Pending as PendingMove;
        var reaction = Screen.HumanDecides ? Screen.Engine.Pending as PendingReaction : null;
        float px = OnePixel;
        foreach (var u in s.Units.Where(u => u.OnBattlefield))
        {
            var baseColor = Screen.Colors[u.Owner];
            bool dim = u.Owner == s.ActivePlayer && Screen.HasActed(u);
            float r = u.BaseRadius * Px;
            foreach (var m in u.Alive)
            {
                var pos = G(m.Position);
                if (pendingMove?.Unit == u && Screen.HumanDecides)
                {
                    FillBase(u.BaseOf(m), baseColor with { A = 0.25f });
                    continue;
                }
                var b = u.BaseOf(m);
                FillBase(b, Colors.Black);
                FillBase(b, dim ? baseColor.Lerp(Colors.Gray, 0.55f) : baseColor, -1.5f);
                if (u.Warscroll.IsHero) Ring(pos, r * 0.6f, new Color(0, 0, 0, 0.7f), 2, 24);
                if (m.Damage > 0)
                {
                    float frac = m.Damage / (float)u.Warscroll.Health;
                    DrawCircle(pos, r * 0.55f * Mathf.Sqrt(frac), Palette.Bad, true, -1, true);
                }
                if (u == Screen.Selected) OutlineBase(b, Colors.White, 2, 3);
                else if (targets.Contains(u)) OutlineBase(b, Palette.Bad, 2.5f, 3);
                else if (reaction != null && reaction.Eligible.Contains(u)) OutlineBase(b, Palette.Accent, 2.5f, 3);
                else if (u == Screen.Hovered) OutlineBase(b, new Color(1, 1, 1, 0.5f), 1.5f, 2);
            }
        }
    }

    private void DrawOverlays()
    {
        var engine = Screen.Engine;
        var s = Screen.S;

        // Deployment ghost
        if (s.Phase == Phase.Deployment && Screen.HumanDecides && Screen.DeployUnit is { } deploy && Screen.MouseOverBoard)
        {
            var pos = Screen.DeploymentPositions(deploy);
            bool ok = engine.ValidateDeployment(deploy, pos) == null;
            foreach (var p in pos) FillBase(deploy.BaseAt(p), (ok ? Palette.Good : Palette.Bad) with { A = 0.6f });
        }

        // Marker placement ghost
        if (engine.Pending is PendingMarker pmk && Screen.HumanDecides && Screen.MouseOverBoard)
        {
            bool ok = engine.ValidateMarker(pmk.Decider, pmk.Def, Screen.MouseWorld) == null;
            DrawCircle(G(Screen.MouseWorld), pmk.Def.Radius * Px, (ok ? Palette.Good : Palette.Bad) with { A = 0.6f }, true, -1, true);
        }

        // Reserve arrival ghost
        if (Screen.ArrivingUnit is { } arriving && Screen.MouseOverBoard)
        {
            var pos = Screen.ArrivalPositions(arriving);
            bool ok = engine.ValidateArrival(arriving, pos) == null;
            foreach (var p in pos) FillBase(arriving.BaseAt(p), (ok ? Palette.Good : Palette.Bad) with { A = 0.6f });
        }

        // Pending move: allowance rings and the ghost formation following the mouse
        if (engine.Pending is PendingMove pm && Screen.HumanDecides)
        {
            var unit = pm.Unit;
            foreach (var m in unit.Alive)
                Ring(G(m.Position), (pm.Allowance + unit.BaseReach) * Px, new Color(1, 1, 1, 0.12f), 1.5f, 64);
            var ghost = Screen.GhostPositions(pm);
            if (ghost != null && Screen.MouseOverBoard)
            {
                bool ok = engine.ValidateMove(pm, ghost) == null;
                var alive = unit.Alive.ToList();
                for (int i = 0; i < ghost.Count && i < alive.Count; i++)
                {
                    DrawLine(G(alive[i].Position), G(ghost[i]), new Color(1, 1, 1, 0.35f), 1.5f * OnePixel);
                    FillBase(unit.BaseAt(ghost[i]), (ok ? Palette.Good : Palette.Bad) with { A = 0.65f });
                }
            }
        }

        // Range ring for the selected unit
        var sel = Screen.Selected;
        if (sel != null && sel.OnBattlefield && engine.Pending == null)
        {
            float range = s.Phase switch
            {
                Phase.Movement => engine.MoveCharacteristic(sel),
                Phase.Shooting => sel.Warscroll.RangedWeapons.Select(w => (float)w.Range).DefaultIfEmpty(0).Max(),
                Phase.Charge => GameEngine.MaxChargeDistance,
                _ => 0,
            };
            if (range > 0) Ring(G(sel.Centroid), range * Px, new Color(1, 1, 1, 0.2f), 1.5f, 96);
        }

        // Measuring line between the selected and hovered units
        if (sel != null && Screen.Hovered is { } hov && hov != sel && sel.OnBattlefield)
        {
            var (a, b, _) = Placement.ClosestPair(sel, hov);
            DrawLine(G(a), G(b), new Color(1, 1, 1, 0.7f), 1.5f * OnePixel);
        }
    }
}
