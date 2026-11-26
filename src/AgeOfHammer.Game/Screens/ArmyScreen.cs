using AgeOfHammer.Game.UI;
using Microsoft.Xna.Framework;

namespace AgeOfHammer.Game.Screens;

/// <summary>Add or remove warscrolls from one player's army.</summary>
public sealed class ArmyScreen : IScreen
{
    private const int MaxUnits = 12;
    private readonly HammerGame _game;
    private readonly int _player;
    private readonly IScreen _back;

    public ArmyScreen(HammerGame game, int player, IScreen back)
    {
        _game = game;
        _player = player;
        _back = back;
    }

    public void Update(GameTime time)
    {
        if (_game.Ui.KeyPressed(Microsoft.Xna.Framework.Input.Keys.Escape)) _game.Show(_back);
    }

    public void Draw(GameTime time)
    {
        var p = _game.Painter;
        var ui = _game.Ui;
        var faction = _game.Factions[_game.Setup.FactionIndex[_player]];
        var army = _game.Setup.Armies[_player];
        p.Batch.Begin();

        p.TitleText($"Player {_player + 1} army - {faction.Name}", new Vector2(40, 24), Theme.Accent);
        p.Text("Click a warscroll to add it. Click a unit in your army to remove it.", new Vector2(40, 66), Theme.TextDim);

        p.Text("WARSCROLLS", new Vector2(40, 100), Theme.TextDim);
        int y = 124;
        foreach (var w in faction.Warscrolls)
        {
            string melee = string.Join(", ", w.Weapons.Select(x => x.Name));
            string tip = $"{w.Name}\nMove {w.Move}\"  Health {w.Health}  Save {w.Save}+  Control {w.Control}\n" +
                         $"{w.UnitSize} model(s) on {w.BaseSizeMm}mm bases\nWeapons: {melee}\n" +
                         (w.Verified ? "Verified against Wahapedia" : "NOT yet verified against Wahapedia");
            if (ui.Button(new Rectangle(40, y, 420, 30), $"+  {w.Name}  ({w.UnitSize})", army.Count < MaxUnits, tooltip: tip))
                army.Add(w.Id);
            y += 36;
        }

        p.Text($"ARMY ({army.Count}/{MaxUnits})", new Vector2(520, 100), Theme.TextDim);
        y = 124;
        for (int i = 0; i < army.Count; i++)
        {
            var w = faction.Warscrolls.FirstOrDefault(x => x.Id == army[i]);
            if (ui.Button(new Rectangle(520, y, 420, 30), $"-  {w?.Name ?? army[i]}"))
            {
                army.RemoveAt(i);
                break;
            }
            y += 36;
        }

        if (ui.Button(new Rectangle(40, _game.Height - 70, 200, 40), "Reset to default")) _game.ResetArmy(_player);
        if (ui.Button(new Rectangle(260, _game.Height - 70, 200, 40), "Done")) _game.Show(_back);

        ui.DrawTooltip();
        p.Batch.End();
    }
}
