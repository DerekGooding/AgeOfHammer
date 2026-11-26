using Godot;

namespace AgeOfHammer.Client;

/// <summary>Pick factions, who controls each side, and the battleplan.</summary>
public partial class MenuScreen : Control
{
    public Main App { get; set; } = null!;
    private VBoxContainer _content = null!;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var center = new CenterContainer();
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(center);
        _content = Ui.Column(14);
        center.AddChild(_content);
        Build();
    }

    private void Build()
    {
        Ui.Clear(_content);
        var title = Ui.Label("AGE OF HAMMER", Palette.Accent, 40);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        _content.AddChild(title);
        var subtitle = Ui.Label("An Age of Sigmar 4th edition tabletop simulator", Palette.TextDim, 15);
        subtitle.HorizontalAlignment = HorizontalAlignment.Center;
        _content.AddChild(subtitle);

        if (App.LoadError != null)
        {
            var err = Ui.Label("Could not load game data:\n" + App.LoadError, Palette.Bad, 15, wrap: true);
            err.CustomMinimumSize = new Vector2(800, 0);
            _content.AddChild(err);
            return;
        }

        var setup = App.Setup;
        var players = Ui.Row(24);
        for (int p = 0; p < 2; p++) players.AddChild(PlayerPanel(p));
        _content.AddChild(players);

        var plan = App.Battleplans[setup.BattleplanIndex];
        var planLabel = Ui.Label($"Battleplan: {plan.Name}  ({plan.Width}\" x {plan.Height}\", {plan.BattleRounds} rounds)");
        planLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _content.AddChild(planLabel);
        if (App.Battleplans.Count > 1)
            _content.AddChild(Centered(Ui.Button("Change battleplan", () =>
            {
                setup.BattleplanIndex = (setup.BattleplanIndex + 1) % App.Battleplans.Count;
                Build();
            }, minWidth: 200)));

        var start = Ui.Button("START BATTLE", App.ShowBattle, setup.Armies[0].Count > 0 && setup.Armies[1].Count > 0, minWidth: 280);
        start.CustomMinimumSize = new Vector2(280, 48);
        start.AddThemeFontSizeOverride("font_size", 18);
        _content.AddChild(Centered(start));

        foreach (var line in new[]
                 {
                     "Left-click: select / place / target.   Right-drag: pan.   Mouse wheel: zoom.",
                     "Enter: end phase.   Esc: cancel.   Tab: next unit.   R: deployment formation.   F: fit board.   +/-: AI speed.   P: pause AI.",
                     "Unit stats were entered without access to Wahapedia - check data/factions/*.json (\"verified\": false).",
                 })
        {
            var l = Ui.Label(line, Palette.TextDim, 14);
            l.HorizontalAlignment = HorizontalAlignment.Center;
            _content.AddChild(l);
        }
    }

    private static CenterContainer Centered(Control c)
    {
        var cc = new CenterContainer();
        cc.AddChild(c);
        return cc;
    }

    private Control PlayerPanel(int player)
    {
        var setup = App.Setup;
        var faction = App.Factions[setup.FactionIndex[player]];
        var panel = Ui.Panel(margin: 16);
        panel.CustomMinimumSize = new Vector2(400, 320);
        var col = Ui.Column(10);
        panel.AddChild(col);

        col.AddChild(Ui.Label($"PLAYER {player + 1}", Palette.TextDim, 14));
        var swatch = new ColorRect { Color = Palette.Faction(faction.Color, Colors.Gray), CustomMinimumSize = new Vector2(18, 18) };
        col.AddChild(Ui.Row(8, swatch, Ui.Label(faction.Name, size: 18)));

        col.AddChild(Ui.Row(8,
            Grow(Ui.Button("< Faction", () => CycleFaction(player, -1))),
            Grow(Ui.Button("Faction >", () => CycleFaction(player, +1)))));
        col.AddChild(Ui.Row(8,
            Grow(Ui.Button("Human", () => { setup.IsAi[player] = false; Build(); }, on: !setup.IsAi[player])),
            Grow(Ui.Button("AI", () => { setup.IsAi[player] = true; Build(); }, on: setup.IsAi[player]))));

        var armyLabel = Ui.Label($"Army: {setup.Armies[player].Count} units");
        armyLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        col.AddChild(Ui.Row(8, armyLabel, Ui.Button("Edit army...", () => App.ShowArmy(player), minWidth: 140)));
        foreach (var group in setup.Armies[player].GroupBy(id => id))
        {
            var name = faction.Warscrolls.FirstOrDefault(w => w.Id == group.Key)?.Name ?? group.Key;
            col.AddChild(Ui.Label($"   {group.Count()} x {name}", Palette.TextDim, 14));
        }
        return panel;
    }

    private static Control Grow(Control c)
    {
        c.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        return c;
    }

    private void CycleFaction(int player, int dir)
    {
        int n = App.Factions.Count;
        App.Setup.FactionIndex[player] = ((App.Setup.FactionIndex[player] + dir) % n + n) % n;
        App.ResetArmy(player);
        Build();
    }
}
