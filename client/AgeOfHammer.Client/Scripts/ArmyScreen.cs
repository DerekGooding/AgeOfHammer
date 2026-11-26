using Godot;

namespace AgeOfHammer.Client;

/// <summary>Add or remove warscrolls from one player's army.</summary>
public partial class ArmyScreen : Control
{
    private const int MaxUnits = 12;

    public Main App { get; set; } = null!;
    public int Player { get; set; }
    private MarginContainer _root = null!;

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

        var page = Ui.Column(12);
        _root.AddChild(page);
        page.AddChild(Ui.Label($"Player {Player + 1} army - {faction.Name}", Palette.Accent, 28));
        page.AddChild(Ui.Label("Click a warscroll to add it. Click a unit in your army to remove it.", Palette.TextDim, 14));

        var lists = Ui.Row(40);
        lists.SizeFlagsVertical = SizeFlags.ExpandFill;
        page.AddChild(lists);

        var left = Ui.Column(6);
        left.CustomMinimumSize = new Vector2(440, 0);
        lists.AddChild(left);
        left.AddChild(Ui.Label("WARSCROLLS", Palette.TextDim, 14));
        foreach (var w in faction.Warscrolls)
        {
            string tip = $"{w.Name}\nMove {w.Move}\"  Health {w.Health}  Save {w.Save}+  Control {w.Control}\n" +
                         $"{w.UnitSize} model(s) on {w.BaseSizeMm}mm bases\nWeapons: {string.Join(", ", w.Weapons.Select(x => x.Name))}\n" +
                         (w.Verified ? "Verified against Wahapedia" : "NOT yet verified against Wahapedia");
            var id = w.Id;
            left.AddChild(Ui.Button($"+  {w.Name}  ({w.UnitSize})", () => { army.Add(id); Build(); }, army.Count < MaxUnits, tooltip: tip));
        }

        var right = Ui.Column(6);
        right.CustomMinimumSize = new Vector2(440, 0);
        lists.AddChild(right);
        right.AddChild(Ui.Label($"ARMY ({army.Count}/{MaxUnits})", Palette.TextDim, 14));
        for (int i = 0; i < army.Count; i++)
        {
            int index = i;
            var name = faction.Warscrolls.FirstOrDefault(x => x.Id == army[i])?.Name ?? army[i];
            right.AddChild(Ui.Button($"-  {name}", () => { army.RemoveAt(index); Build(); }));
        }

        page.AddChild(Ui.Row(12,
            Ui.Button("Reset to default", () => { App.ResetArmy(Player); Build(); }, minWidth: 200),
            Ui.Button("Done", App.ShowMenu, minWidth: 200)));
    }
}
