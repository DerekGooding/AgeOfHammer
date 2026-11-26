using Godot;

namespace AgeOfHammer.Client;

/// <summary>Add or remove warscrolls from one player's army, with points.</summary>
public partial class ArmyScreen : Control
{
    private const int MaxUnits = 30;
    private const int PointsLimit = 2000;

    public Main App { get; set; } = null!;
    public int Player { get; set; }
    private MarginContainer _root = null!;
    private float _leftScroll, _rightScroll;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _root = new MarginContainer();
        _root.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        foreach (var side in new[] { "left", "top", "right", "bottom" }) _root.AddThemeConstantOverride("margin_" + side, 32);
        AddChild(_root);
        Build();
    }

    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Keycode: Key.Escape }) App.ShowMenu();
    }

    private void Build()
    {
        Ui.Clear(_root);
        var faction = App.Factions[App.Setup.FactionIndex[Player]];
        var army = App.Setup.Armies[Player];
        int total = army.Sum(id => faction.Warscrolls.FirstOrDefault(w => w.Id == id)?.Points ?? 0);

        var page = Ui.Column(12);
        _root.AddChild(page);
        page.AddChild(Ui.Label($"Player {Player + 1} army - {faction.Name}", Palette.Accent, 28));
        page.AddChild(Ui.Label("Click a warscroll to add it. Click a unit in your army to remove it. Hover for its profile.", Palette.TextDim, 14));

        var lists = Ui.Row(40);
        lists.SizeFlagsVertical = SizeFlags.ExpandFill;
        page.AddChild(lists);

        var (leftScroll, left) = ScrollColumn(lists);
        left.AddChild(Ui.Label($"WARSCROLLS ({faction.Warscrolls.Count})", Palette.TextDim, 14));
        foreach (var group in faction.Warscrolls.GroupBy(w => w.IsHero ? "Heroes" : "Units").OrderBy(g => g.Key == "Heroes" ? 0 : 1))
        {
            left.AddChild(Ui.Label(group.Key, Palette.Accent, 15));
            foreach (var w in group.OrderBy(w => w.Name))
            {
                var id = w.Id;
                left.AddChild(Ui.Button($"+  {w.Name}  ({w.UnitSize}) - {w.Points} pts", () => { army.Add(id); Rebuild(leftScroll); },
                    army.Count < MaxUnits, tooltip: Tooltip(w)));
            }
        }

        var (rightScroll, right) = ScrollColumn(lists);
        var totalColor = total > PointsLimit ? Palette.Bad : Palette.TextDim;
        right.AddChild(Ui.Label($"ARMY: {army.Count} units, {total} / {PointsLimit} pts", totalColor, 14));
        for (int i = 0; i < army.Count; i++)
        {
            int index = i;
            var w = faction.Warscrolls.FirstOrDefault(x => x.Id == army[i]);
            right.AddChild(Ui.Button($"-  {w?.Name ?? army[i]}  ({w?.Points ?? 0} pts)", () => { army.RemoveAt(index); Rebuild(leftScroll); },
                tooltip: w == null ? null : Tooltip(w)));
        }

        page.AddChild(Ui.Row(12,
            Ui.Button("Reset to default", () => { App.ResetArmy(Player); Build(); }, minWidth: 200),
            Ui.Button("Clear", () => { army.Clear(); Build(); }, minWidth: 120),
            Ui.Button("Done", App.ShowMenu, minWidth: 200)));

        // Keep the scroll positions across rebuilds.
        Callable.From(() =>
        {
            leftScroll.ScrollVertical = (int)_leftScroll;
            rightScroll.ScrollVertical = (int)_rightScroll;
        }).CallDeferred();
        leftScroll.GetVScrollBar().ValueChanged += v => _leftScroll = (float)v;
        rightScroll.GetVScrollBar().ValueChanged += v => _rightScroll = (float)v;
    }

    private void Rebuild(ScrollContainer left)
    {
        _leftScroll = left.ScrollVertical;
        Build();
    }

    private static (ScrollContainer Scroll, VBoxContainer Column) ScrollColumn(Container parent)
    {
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        scroll.CustomMinimumSize = new Vector2(480, 0);
        scroll.SizeFlagsVertical = SizeFlags.ExpandFill;
        parent.AddChild(scroll);
        var col = Ui.Column(6);
        col.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(col);
        return (scroll, col);
    }

    private static string Tooltip(AgeOfHammer.Core.Data.Warscroll w)
    {
        var lines = new List<string>
        {
            w.Name,
            $"Move {w.Move}\"  Health {w.Health}  Save {(w.Save >= 7 ? "-" : w.Save + "+")}  Control {w.Control}" + (w.Ward is int ward ? $"  Ward {ward}+" : ""),
            $"{w.UnitSize} model(s), {w.BaseSize ?? w.BaseSizeMm + "mm"} bases, {w.Points} pts",
            "Weapons: " + string.Join(", ", w.Weapons.Select(x => x.Name)),
        };
        if (w.Abilities.Count > 0) lines.Add("Abilities: " + string.Join(", ", w.Abilities.Select(a => a.Name)));
        if (w.Spells.Count > 0) lines.Add("Spells: " + string.Join(", ", w.Spells.Select(s => s.Name)));
        lines.AddRange(w.Notes.Select(n => "Note: " + n));
        return string.Join("\n", lines);
    }
}
