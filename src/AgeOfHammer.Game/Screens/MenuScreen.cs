using AgeOfHammer.Game.UI;
using Microsoft.Xna.Framework;

namespace AgeOfHammer.Game.Screens;

public sealed class MenuScreen : IScreen
{
    private readonly HammerGame _game;
    public MenuScreen(HammerGame game) => _game = game;

    public void Update(GameTime time) { }

    public void Draw(GameTime time)
    {
        var p = _game.Painter;
        var ui = _game.Ui;
        var setup = _game.Setup;
        p.Batch.Begin(blendState: Microsoft.Xna.Framework.Graphics.BlendState.AlphaBlend);

        int cx = _game.Width / 2;
        p.TitleText("AGE OF HAMMER", new Vector2(cx - p.Title.MeasureString("AGE OF HAMMER").X / 2, 60), Theme.Accent);
        p.TextCentered("An Age of Sigmar 4th edition tabletop simulator", new Vector2(cx, 110), Theme.TextDim);

        if (_game.LoadError != null)
        {
            int y = 170;
            foreach (var line in p.Wrap("Could not load game data:\n" + _game.LoadError, 800))
            {
                p.TextCentered(line, new Vector2(cx, y), Theme.Bad);
                y += 20;
            }
            p.Batch.End();
            return;
        }

        int top = 170;
        for (int player = 0; player < 2; player++)
        {
            int x = cx - 420 + player * 440;
            var box = new Rectangle(x, top, 400, 300);
            p.Rect(box, Theme.Panel);
            p.RectOutline(box, Theme.Border);
            var faction = _game.Factions[setup.FactionIndex[player]];
            var color = Rendering.Painter.ParseHex(faction.Color, Color.Gray);
            p.Text($"PLAYER {player + 1}", new Vector2(x + 16, top + 12), Theme.TextDim);
            p.Rect(new Rectangle(x + 16, top + 40, 18, 18), color);
            p.Text(faction.Name, new Vector2(x + 42, top + 40), Theme.Text);

            if (ui.Button(new Rectangle(x + 16, top + 70, 180, 30), "< Faction"))
                CycleFaction(player, -1);
            if (ui.Button(new Rectangle(x + 204, top + 70, 180, 30), "Faction >"))
                CycleFaction(player, +1);

            if (ui.Button(new Rectangle(x + 16, top + 110, 180, 30), "Human", on: !setup.IsAi[player])) setup.IsAi[player] = false;
            if (ui.Button(new Rectangle(x + 204, top + 110, 180, 30), "AI", on: setup.IsAi[player])) setup.IsAi[player] = true;

            p.Text($"Army: {setup.Armies[player].Count} units", new Vector2(x + 16, top + 152), Theme.Text);
            int ly = top + 174;
            foreach (var group in setup.Armies[player].GroupBy(id => id).Take(7))
            {
                var name = faction.Warscrolls.FirstOrDefault(w => w.Id == group.Key)?.Name ?? group.Key;
                p.Text($"{group.Count()} x {name}", new Vector2(x + 24, ly), Theme.TextDim);
                ly += 17;
            }
            if (ui.Button(new Rectangle(x + 230, top + 148, 154, 26), "Edit army..."))
            {
                _game.Show(new ArmyScreen(_game, player, this));
                p.Batch.End();
                return;
            }
        }

        int by = top + 320;
        var plan = _game.Battleplans[setup.BattleplanIndex];
        p.TextCentered($"Battleplan: {plan.Name}  ({plan.Width}\" x {plan.Height}\", {plan.BattleRounds} rounds)", new Vector2(cx, by + 10), Theme.Text);
        if (_game.Battleplans.Count > 1 && ui.Button(new Rectangle(cx - 90, by + 30, 180, 28), "Change battleplan"))
            setup.BattleplanIndex = (setup.BattleplanIndex + 1) % _game.Battleplans.Count;

        bool canStart = setup.Armies[0].Count > 0 && setup.Armies[1].Count > 0;
        if (ui.Button(new Rectangle(cx - 140, by + 80, 280, 48), "START BATTLE", canStart))
            _game.Show(new BattleScreen(_game));

        int hy = by + 150;
        string[] help =
        [
            "Left-click: select / place / target.   Right-drag: pan.   Mouse wheel: zoom.",
            "Enter: end phase.   Esc: cancel.   Tab: next unit.   R: change deployment formation.   +/-: AI speed.",
            "Unit stats were entered without access to Wahapedia - check data/factions/*.json (\"verified\": false).",
        ];
        foreach (var h in help)
        {
            p.TextCentered(h, new Vector2(cx, hy), Theme.TextDim);
            hy += 20;
        }

        ui.DrawTooltip();
        p.Batch.End();
    }

    private void CycleFaction(int player, int dir)
    {
        int n = _game.Factions.Count;
        _game.Setup.FactionIndex[player] = ((_game.Setup.FactionIndex[player] + dir) % n + n) % n;
        _game.ResetArmy(player);
    }
}
