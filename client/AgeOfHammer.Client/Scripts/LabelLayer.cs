using AgeOfHammer.Core.Engine;
using AgeOfHammer.Core.State;
using Godot;

namespace AgeOfHammer.Client;

/// <summary>Draws unit names, terrain names and cursor hints in screen space so text stays crisp at any zoom.</summary>
public partial class LabelLayer : Control
{
    public BattleScreen Screen { get; set; } = null!;
    private const int FontSize = 13;

    public override void _Draw()
    {
        if (Screen?.Engine == null) return;
        var font = ThemeDB.FallbackFont;
        var s = Screen.S;
        var vp = GetViewportRect().Size;
        var board = new Rect2(0, BattleScreen.TopBarHeight, vp.X - BattleScreen.PanelWidth, vp.Y - BattleScreen.TopBarHeight);

        foreach (var t in s.Battleplan.Terrain)
        {
            var c = t.Area.ToRect().Center;
            var pos = Screen.ToScreen(c);
            if (board.HasPoint(pos)) Centered(font, t.Name, pos, Color.Color8(220, 210, 190, 200), null);
        }

        foreach (var u in s.Units.Where(u => u.OnBattlefield))
        {
            float top = u.Alive.Min(m => m.Position.Y) - u.BaseRadius;
            var anchor = Screen.ToScreen(new System.Numerics.Vector2(u.Centroid.X, top));
            if (!board.HasPoint(anchor)) continue;
            string text = u.Models.Count > 1
                ? $"{u.Name} {u.AliveCount}/{u.Models.Count}"
                : $"{u.Name} {u.WoundsRemaining}/{u.Warscroll.Health}";
            Centered(font, text, anchor - new Vector2(0, 12), u == Screen.Selected ? Palette.Accent : Colors.White, new Color(0, 0, 0, 0.55f));
        }

        var mouse = GetViewport().GetMousePosition();
        if (!Screen.MouseOverBoard) return;

        if (Screen.Selected is { } sel && Screen.Hovered is { } hov && hov != sel && sel.OnBattlefield)
            Boxed(font, $"{GameState.Distance(sel, hov):0.0}\"", mouse + new Vector2(16, -22), Colors.White);

        if (Screen.Engine.Pending is PendingMove pm && Screen.HumanDecides)
        {
            var ghost = Screen.GhostPositions(pm);
            string? error = ghost == null ? "Cannot reach" : Screen.Engine.ValidateMove(pm, ghost);
            Boxed(font, error ?? "Click to confirm", mouse + new Vector2(16, 14), error == null ? Palette.Good : Palette.Bad);
        }
    }

    private void Centered(Font font, string text, Vector2 center, Color color, Color? background)
    {
        var size = font.GetStringSize(text, HorizontalAlignment.Left, -1, FontSize);
        var topLeft = center - size / 2;
        if (background is Color bg) DrawRect(new Rect2(topLeft - new Vector2(3, 1), size + new Vector2(6, 2)), bg);
        DrawString(font, topLeft + new Vector2(0, font.GetAscent(FontSize)), text, HorizontalAlignment.Left, -1, FontSize, color);
    }

    private void Boxed(Font font, string text, Vector2 topLeft, Color color)
    {
        var size = font.GetStringSize(text, HorizontalAlignment.Left, -1, FontSize);
        DrawRect(new Rect2(topLeft - new Vector2(4, 2), size + new Vector2(8, 4)), new Color(0, 0, 0, 0.75f));
        DrawString(font, topLeft + new Vector2(0, font.GetAscent(FontSize)), text, HorizontalAlignment.Left, -1, FontSize, color);
    }
}
