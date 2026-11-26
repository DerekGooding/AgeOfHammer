using AgeOfHammer.Core.AI;
using AgeOfHammer.Core.Data;
using AgeOfHammer.Core.Dice;
using AgeOfHammer.Core.Engine;
using AgeOfHammer.Core.Geometry;
using AgeOfHammer.Core.State;
using AgeOfHammer.Game.Rendering;
using AgeOfHammer.Game.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using NVec = System.Numerics.Vector2;

namespace AgeOfHammer.Game.Screens;

public sealed class BattleScreen : IScreen
{
    private enum TargetMode { None, Shoot, Fight, Spell, PowerThrough }

    private const int PanelWidth = 440;
    private const int TopBar = 40;
    private const int LineH = 18;

    private readonly HammerGame _game;
    private readonly GameEngine _engine;
    private readonly AiPlayer?[] _ai = new AiPlayer?[2];
    private readonly Camera _cam = new();
    private readonly Color[] _colors = new Color[2];
    private readonly int[] _perRowOptions = [5, 10, 3, 2, 1];

    private Unit? _selected, _hovered, _deployUnit;
    private TargetMode _mode;
    private int _spellIndex;
    private bool _allOutAttack;
    private int _perRowIndex;
    private float _aiTimer;
    private float _aiDelay = 0.45f;
    private bool _aiPaused;
    private string? _flash;
    private double _flashUntil;
    private double _now;
    private int _logScroll;
    private Rectangle _lastView;
    private NVec _mouseWorld;

    private GameState S => _engine.State;
    private Painter P => _game.Painter;
    private Ui U => _game.Ui;

    public BattleScreen(HammerGame game)
    {
        _game = game;
        var setup = game.Setup;
        var f0 = game.Factions[setup.FactionIndex[0]];
        var f1 = game.Factions[setup.FactionIndex[1]];
        _engine = GameEngine.Create(game.Battleplans[setup.BattleplanIndex], f0, setup.Armies[0], f1, setup.Armies[1],
            new SeededDiceRoller(Environment.TickCount));
        _colors[0] = Painter.ParseHex(f0.Color, Color.SteelBlue);
        _colors[1] = Painter.ParseHex(f1.Color, Color.IndianRed);
        if (f0.Id == f1.Id)
        {
            S.Players[0].Name += " (P1)";
            S.Players[1].Name += " (P2)";
            _colors[1] = new Color(200, 70, 70);
        }
        for (int p = 0; p < 2; p++)
        {
            if (setup.IsAi[p]) _ai[p] = new AiPlayer(p);
            else S.Players[p].Name += setup.IsAi[1 - p] ? " (You)" : "";
        }
    }

    private Rectangle BoardView => new(0, TopBar, _game.Width - PanelWidth, _game.Height - TopBar);
    private int Decider => _engine.Decider;
    private bool HumanDecides => Decider >= 0 && _ai[Decider] == null;

    private void Flash(string? message)
    {
        _flash = message;
        _flashUntil = _now + 3.5;
    }

    private void Do(GameAction action)
    {
        var r = _engine.Submit(action);
        if (!r.Ok) Flash(r.Error);
        else
        {
            _mode = TargetMode.None;
            _allOutAttack = false;
        }
    }

    // ================================================================== UPDATE

    public void Update(GameTime time)
    {
        float dt = (float)time.ElapsedGameTime.TotalSeconds;
        _now = time.TotalGameTime.TotalSeconds;

        if (_lastView != BoardView)
        {
            _cam.View = BoardView;
            if (_lastView == Rectangle.Empty) _cam.Fit(S.Battleplan.Width, S.Battleplan.Height);
            _lastView = BoardView;
        }

        var mouse = U.Mouse;
        bool overBoard = BoardView.Contains(mouse);
        if (U.WheelDelta != 0)
        {
            if (overBoard) _cam.ZoomAt(mouse.ToVector2(), U.WheelDelta > 0 ? 1.15f : 1 / 1.15f);
            else _logScroll = Math.Max(0, _logScroll + (U.WheelDelta > 0 ? 3 : -3));
        }
        if (U.RightDown && overBoard && !U.RightClicked) _cam.Position -= U.MouseDelta.ToVector2() / _cam.Zoom;
        if (U.RightClicked) _mode = TargetMode.None;

        if (U.KeyPressed(Keys.Escape))
        {
            if (_mode != TargetMode.None) _mode = TargetMode.None;
            else _selected = null;
        }
        if (U.KeyPressed(Keys.Enter)) TryEndPhase();
        if (U.KeyPressed(Keys.Tab)) CycleSelection();
        if (U.KeyPressed(Keys.R)) _perRowIndex = (_perRowIndex + 1) % _perRowOptions.Length;
        if (U.KeyPressed(Keys.F)) _cam.Fit(S.Battleplan.Width, S.Battleplan.Height);
        if (U.KeyPressed(Keys.OemPlus) || U.KeyPressed(Keys.Add)) _aiDelay = MathF.Max(0.02f, _aiDelay / 1.6f);
        if (U.KeyPressed(Keys.OemMinus) || U.KeyPressed(Keys.Subtract)) _aiDelay = MathF.Min(3f, _aiDelay * 1.6f);
        if (U.KeyPressed(Keys.P)) _aiPaused = !_aiPaused;

        _mouseWorld = _cam.ScreenToWorld(mouse.ToVector2()).N();
        _hovered = overBoard ? UnitAt(_mouseWorld) : null;

        if (S.Phase == Phase.Deployment && HumanDecides)
        {
            if (_deployUnit == null || _deployUnit.Deployed || _deployUnit.Owner != Decider)
                _deployUnit = S.UnitsOf(Decider).FirstOrDefault(u => !u.Deployed);
        }
        if (_selected != null && !_selected.OnBattlefield && S.Phase != Phase.Deployment) _selected = null;

        // AI turns
        if (S.Phase != Phase.GameOver && Decider >= 0 && _ai[Decider] is { } ai && !_aiPaused)
        {
            _aiTimer += dt;
            float delay = S.Phase == Phase.Deployment ? _aiDelay * 0.4f : _aiDelay;
            if (_aiTimer >= delay)
            {
                _aiTimer = 0;
                try { ai.Act(_engine); }
                catch (Exception e)
                {
                    _aiPaused = true;
                    Flash($"AI error (paused): {e.Message}");
                }
            }
        }
    }

    private Unit? UnitAt(NVec world)
    {
        foreach (var u in S.Units.Where(u => u.OnBattlefield))
            foreach (var m in u.Alive)
                if (NVec.Distance(m.Position, world) <= u.BaseRadius) return u;
        return null;
    }

    private void CycleSelection()
    {
        int owner = Decider >= 0 ? Decider : 0;
        var mine = S.LiveUnitsOf(owner).ToList();
        if (mine.Count == 0) return;
        int idx = _selected == null ? -1 : mine.IndexOf(_selected);
        _selected = mine[(idx + 1) % mine.Count];
    }

    private void TryEndPhase()
    {
        if (!HumanDecides || _engine.Pending != null || S.Phase == Phase.Deployment) return;
        Do(new EndPhase(Decider));
    }

    // ================================================================== DRAW

    public void Draw(GameTime time)
    {
        P.Batch.Begin(transformMatrix: _cam.Transform, samplerState: SamplerState.LinearClamp, blendState: BlendState.AlphaBlend);
        DrawBoard();
        DrawObjectives();
        DrawUnits();
        DrawOverlays();
        P.Batch.End();

        P.Batch.Begin(blendState: BlendState.AlphaBlend);
        DrawLabels();
        DrawTopBar();
        DrawPanel();
        DrawFlash();
        DrawGameOver();
        HandleBoardClick();
        U.DrawTooltip();
        P.Batch.End();
    }

    private float Px => 1f / _cam.Zoom; // one screen pixel in inches

    private void DrawBoard()
    {
        var plan = S.Battleplan;
        P.Rect(new Vector2(-0.3f, -0.3f), new Vector2(plan.Width + 0.6f, plan.Height + 0.6f), new Color(10, 12, 10));
        P.Rect(Vector2.Zero, new Vector2(plan.Width, plan.Height), new Color(46, 66, 44));
        for (int x = 6; x < plan.Width; x += 6) P.Line(new Vector2(x, 0), new Vector2(x, plan.Height), Color.Black * 0.12f, Px);
        for (int y = 6; y < plan.Height; y += 6) P.Line(new Vector2(0, y), new Vector2(plan.Width, y), Color.Black * 0.12f, Px);

        for (int p = 0; p < 2; p++)
        {
            var t = S.TerritoryOf(p);
            P.Rect(new Vector2(t.X, t.Y), new Vector2(t.W, t.H), _colors[p] * 0.10f);
            P.RectOutline(new Vector2(t.X, t.Y), new Vector2(t.W, t.H), _colors[p] * 0.5f, 2 * Px);
        }
        foreach (var terrain in plan.Terrain)
        {
            var r = terrain.Area.ToRect();
            P.Rect(new Vector2(r.X, r.Y), new Vector2(r.W, r.H), new Color(92, 82, 70));
            P.RectOutline(new Vector2(r.X, r.Y), new Vector2(r.W, r.H), new Color(40, 34, 28), 2 * Px);
        }
    }

    private void DrawObjectives()
    {
        foreach (var o in S.Objectives)
        {
            var c = o.ControlledBy >= 0 ? _colors[o.ControlledBy] : Color.LightGray;
            var pos = o.Position.X();
            P.Ring(pos, Objective.MarkerRadius + Objective.ControlRange, c * 0.55f, 2 * Px);
            P.Disc(pos, Objective.MarkerRadius, Color.Black * 0.6f);
            P.Disc(pos, Objective.MarkerRadius * 0.8f, c);
        }
    }

    private bool HasActed(Unit u) => S.Phase switch
    {
        Phase.Movement => u.Moved,
        Phase.Shooting => u.Shot,
        Phase.Charge => u.ChargeAttempted,
        Phase.Combat => u.Fought,
        _ => false,
    };

    private void DrawUnits()
    {
        var targets = ValidTargets().ToHashSet();
        var pendingMove = _engine.Pending as PendingMove;
        foreach (var u in S.Units.Where(u => u.OnBattlefield))
        {
            var baseColor = _colors[u.Owner];
            bool dim = u.Owner == S.ActivePlayer && HasActed(u);
            float r = u.BaseRadius;
            foreach (var m in u.Alive)
            {
                var pos = m.Position.X();
                if (pendingMove?.Unit == u && HumanDecides)
                {
                    P.Disc(pos, r, baseColor * 0.25f);
                    continue;
                }
                P.Disc(pos, r, Color.Black);
                P.Disc(pos, r - 1.5f * Px, dim ? Color.Lerp(baseColor, Color.Gray, 0.55f) : baseColor);
                if (u.Warscroll.IsHero) P.Ring(pos, r * 0.6f, Color.Black * 0.7f, 2 * Px, 24);
                if (m.Damage > 0)
                {
                    float frac = m.Damage / (float)u.Warscroll.Health;
                    P.Disc(pos, r * 0.55f * MathF.Sqrt(frac), Theme.Bad);
                }
                if (u == _selected) P.Ring(pos, r + 3 * Px, Color.White, 2 * Px, 32);
                else if (targets.Contains(u)) P.Ring(pos, r + 3 * Px, Theme.Bad, 2.5f * Px, 32);
                else if (_engine.Pending is PendingReaction pr && HumanDecides && pr.Eligible.Contains(u))
                    P.Ring(pos, r + 3 * Px, Theme.Accent, 2.5f * Px, 32);
                else if (u == _hovered) P.Ring(pos, r + 2 * Px, Color.White * 0.5f, 1.5f * Px, 32);
            }
        }
    }

    private void DrawOverlays()
    {
        // Deployment ghost
        if (S.Phase == Phase.Deployment && HumanDecides && _deployUnit != null && BoardView.Contains(U.Mouse))
        {
            var pos = DeploymentPositions(_deployUnit);
            bool ok = _engine.ValidateDeployment(_deployUnit, pos) == null;
            foreach (var p in pos) P.Disc(p.X(), _deployUnit.BaseRadius, (ok ? Theme.Good : Theme.Bad) * 0.6f);
        }

        // Pending move ghost
        if (_engine.Pending is PendingMove pm && HumanDecides)
        {
            var unit = pm.Unit;
            foreach (var m in unit.Alive) P.Ring(m.Position.X(), pm.Allowance + unit.BaseRadius, Color.White * 0.12f, 1.5f * Px, 64);
            var ghost = GhostPositions(pm);
            if (ghost != null && BoardView.Contains(U.Mouse))
            {
                bool ok = _engine.ValidateMove(pm, ghost) == null;
                var alive = unit.Alive.ToList();
                for (int i = 0; i < ghost.Count && i < alive.Count; i++)
                {
                    P.Line(alive[i].Position.X(), ghost[i].X(), Color.White * 0.35f, 1.5f * Px);
                    P.Disc(ghost[i].X(), unit.BaseRadius, (ok ? Theme.Good : Theme.Bad) * 0.65f);
                }
            }
        }

        // Range rings for the selected unit
        if (_selected != null && _selected.OnBattlefield && _engine.Pending == null)
        {
            var c = _selected.Centroid.X();
            float range = S.Phase switch
            {
                Phase.Movement => _engine.MoveCharacteristic(_selected),
                Phase.Shooting => _selected.Warscroll.RangedWeapons.Select(w => (float)w.Range).DefaultIfEmpty(0).Max(),
                Phase.Charge => GameEngine.MaxChargeDistance,
                _ => 0,
            };
            if (range > 0) P.Ring(c, range, Color.White * 0.2f, 1.5f * Px, 96);
        }

        // Measuring line
        if (_selected != null && _hovered != null && _hovered != _selected && _selected.OnBattlefield)
        {
            var (a, b, _) = Placement.ClosestPair(_selected, _hovered);
            P.Line(a.X(), b.X(), Color.White * 0.7f, 1.5f * Px);
        }
    }

    private List<NVec> DeploymentPositions(Unit unit)
    {
        int perRow = Math.Min(_perRowOptions[_perRowIndex], unit.Models.Count);
        var pos = Geo.Formation(unit.Models.Count, unit.BaseRadius, _mouseWorld, Math.Max(1, perRow));
        return pos;
    }

    private List<NVec>? GhostPositions(PendingMove pm)
    {
        if (pm.Kind == MoveKind.Charge && _hovered != null && _hovered.Owner != pm.Unit.Owner)
            return Placement.AutoCharge(_engine, pm, _hovered);
        return Placement.PreviewMove(pm, _mouseWorld);
    }

    private void DrawLabels()
    {
        foreach (var u in S.Units.Where(u => u.OnBattlefield))
        {
            float top = u.Alive.Min(m => m.Position.Y) - u.BaseRadius;
            var anchor = _cam.WorldToScreen(new Vector2(u.Centroid.X, top));
            if (!BoardView.Contains(anchor.ToPoint())) continue;
            string text = u.Models.Count > 1
                ? $"{u.Name} {u.AliveCount}/{u.Models.Count}"
                : $"{u.Name} {u.WoundsRemaining}/{u.Warscroll.Health}";
            var size = P.Measure(text) * 0.85f;
            var pos = new Vector2(anchor.X - size.X / 2, anchor.Y - size.Y - 3);
            P.Rect(pos - new Vector2(3, 1), size + new Vector2(6, 2), Color.Black * 0.55f);
            P.Text(text, pos, u == _selected ? Theme.Accent : Color.White, 0.85f);
        }

        foreach (var t in S.Battleplan.Terrain)
        {
            var c = _cam.WorldToScreen(t.Area.ToRect().Center.X());
            if (BoardView.Contains(c.ToPoint())) P.TextCentered(t.Name, c, new Color(220, 210, 190) * 0.8f, 0.8f);
        }

        if (_selected != null && _hovered != null && _hovered != _selected && _selected.OnBattlefield)
        {
            float d = GameState.Distance(_selected, _hovered);
            var m = U.Mouse.ToVector2() + new Vector2(16, -22);
            var text = $"{d:0.0}\"";
            P.Rect(m - new Vector2(3, 1), P.Measure(text) + new Vector2(6, 2), Color.Black * 0.7f);
            P.Text(text, m, Color.White);
        }

        if (_engine.Pending is PendingMove pm && HumanDecides && BoardView.Contains(U.Mouse))
        {
            var ghost = GhostPositions(pm);
            string text = ghost == null ? "Cannot reach" : _engine.ValidateMove(pm, ghost) ?? "Click to confirm";
            var m = U.Mouse.ToVector2() + new Vector2(16, 12);
            P.Rect(m - new Vector2(3, 1), P.Measure(text) + new Vector2(6, 2), Color.Black * 0.75f);
            P.Text(text, m, ghost != null && _engine.ValidateMove(pm, ghost) == null ? Theme.Good : Theme.Bad);
        }
    }

    private void DrawTopBar()
    {
        P.Rect(new Rectangle(0, 0, _game.Width, TopBar), Theme.Panel);
        P.Rect(new Rectangle(0, TopBar - 1, _game.Width, 1), Theme.Border);
        string left = S.Phase switch
        {
            Phase.Deployment => "DEPLOYMENT",
            Phase.GameOver => "GAME OVER",
            _ => $"Round {S.BattleRound}/{S.Battleplan.BattleRounds}   |   {S.Active.Name}'s turn   |   {PhaseName(S.Phase)}",
        };
        P.Text(left, new Vector2(12, 11), Theme.Text);

        float x = _game.Width - 12;
        for (int p = 1; p >= 0; p--)
        {
            var pl = S.Players[p];
            string text = $"{pl.Name}   CP {pl.CommandPoints}   VP {pl.VictoryPoints}";
            x -= P.Measure(text).X;
            P.Text(text, new Vector2(x, 11), _colors[p]);
            x -= 30;
        }
    }

    private static string PhaseName(Phase p) => p switch
    {
        Phase.Hero => "Hero Phase",
        Phase.Movement => "Movement Phase",
        Phase.Shooting => "Shooting Phase",
        Phase.Charge => "Charge Phase",
        Phase.Combat => "Combat Phase",
        Phase.EndOfTurn => "End of Turn",
        _ => p.ToString(),
    };

    private void DrawFlash()
    {
        if (_flash == null || _now > _flashUntil) return;
        var size = P.Measure(_flash);
        var pos = new Vector2(BoardView.Center.X - size.X / 2, TopBar + 12);
        P.Rect(new Rectangle((int)pos.X - 10, (int)pos.Y - 6, (int)size.X + 20, (int)size.Y + 12), new Color(60, 16, 16) * 0.92f);
        P.Text(_flash, pos, Color.White);
    }

    // ================================================================== PANEL

    private void DrawPanel()
    {
        int x0 = _game.Width - PanelWidth;
        P.Rect(new Rectangle(x0, TopBar, PanelWidth, _game.Height - TopBar), Theme.Panel);
        P.Rect(new Rectangle(x0, TopBar, 1, _game.Height - TopBar), Theme.Border);
        int x = x0 + 12, w = PanelWidth - 24;
        int y = TopBar + 10;

        // Status
        if (S.Phase != Phase.GameOver)
        {
            string who = HumanDecides ? $"{S.Players[Decider].Name}: your decision" :
                Decider >= 0 ? $"{S.Players[Decider].Name} (AI) is thinking..." + (_aiPaused ? " [PAUSED - press P]" : "") : "";
            P.Text(who, new Vector2(x, y), Decider >= 0 ? _colors[Decider] : Theme.Text);
            y += LineH + 6;
        }

        if (HumanDecides) y = DrawPrompt(x, y, w);
        y = DrawSelectedUnit(x, y, w);
        y = DrawActions(x, y, w);

        // End phase button
        if (HumanDecides && _engine.Pending == null && S.Phase is not (Phase.Deployment or Phase.GameOver))
        {
            string label = S.Phase == Phase.Combat
                ? (_engine.PickableFighters(Decider).Any() ? "Pass (remaining units don't fight)" : "End Combat Phase")
                : $"End {PhaseName(S.Phase)}  [Enter]";
            if (U.Button(new Rectangle(x, y + 4, w, 32), label)) TryEndPhase();
            y += 44;
        }

        DrawLog(x, y + 6, w, _game.Height - (y + 6) - 10);
    }

    private int DrawPrompt(int x, int y, int w)
    {
        void Para(string text, Color c)
        {
            foreach (var line in P.Wrap(text, w))
            {
                P.Text(line, new Vector2(x, y), c);
                y += LineH;
            }
        }

        switch (_engine.Pending)
        {
            case null when S.Phase == Phase.Deployment:
                Para("Deploy your units wholly within your territory (shaded). Click the board to place the selected unit. R changes formation.", Theme.TextDim);
                y += 4;
                foreach (var u in S.UnitsOf(Decider).Where(u => !u.Deployed).Take(10))
                {
                    if (U.Button(new Rectangle(x, y, w, 26), $"{u.Name} ({u.Models.Count})", on: u == _deployUnit)) _deployUnit = u;
                    y += 30;
                }
                if (U.Button(new Rectangle(x, y, w, 26), "Auto-place this unit"))
                {
                    var unit = _deployUnit;
                    if (unit != null)
                    {
                        var center = S.TerritoryOf(Decider).Center;
                        var pos = Placement.DeployNear(_engine, unit, center);
                        if (pos == null) Flash("No room to deploy that unit.");
                        else Do(new DeployUnit(Decider, unit.Id, pos));
                    }
                }
                y += 36;
                break;

            case PendingDefence pd:
                Para($"{pd.Attacker.Name} is attacking your {pd.Target.Name}.", Theme.Accent);
                Para("Use All-out Defence (+1 to save rolls) for 1 CP?", Theme.Text);
                if (U.Button(new Rectangle(x, y + 4, w / 2 - 4, 30), "Yes (1 CP)")) Do(new RespondDefence(Decider, true));
                if (U.Button(new Rectangle(x + w / 2 + 4, y + 4, w / 2 - 4, 30), "No")) Do(new RespondDefence(Decider, false));
                y += 42;
                break;

            case PendingReaction pr:
                string cmd = Commands.ForReaction(pr.Kind);
                string desc = pr.Kind switch
                {
                    ReactionKind.Redeploy => "move a unit up to D6\"",
                    ReactionKind.CoveringFire => "a unit shoots the closest enemy with -1 to hit",
                    _ => "a unit attempts a charge",
                };
                Para($"Reaction: use {cmd} ({Commands.Cost(cmd)} CP) - {desc}? Click a highlighted unit or pick below.", Theme.Accent);
                foreach (var u in pr.Eligible.Take(6))
                {
                    if (U.Button(new Rectangle(x, y + 2, w, 26), u.Name)) Do(new UseReaction(Decider, u.Id));
                    y += 30;
                }
                if (U.Button(new Rectangle(x, y + 4, w, 28), "Decline")) Do(new DeclineReaction(Decider));
                y += 40;
                break;

            case PendingMove pm:
                string kind = pm.Kind switch
                {
                    MoveKind.Normal => "Normal move",
                    MoveKind.Run => "Run",
                    MoveKind.Retreat => "Retreat",
                    MoveKind.Charge => "Charge",
                    _ => "Redeploy",
                };
                Para($"{pm.Unit.Name}: {kind} up to {pm.Allowance:0.#}\"", Theme.Accent);
                Para(pm.Kind == MoveKind.Charge
                    ? "Click an enemy unit to charge it (models end within 1/2\"), or click a point to move the formation there."
                    : "Click where the unit should move. The formation follows the cursor; green = legal.", Theme.TextDim);
                if (pm.Kind == MoveKind.Charge && pm.CanReroll &&
                    U.Button(new Rectangle(x, y + 4, w, 28), "Re-roll charge: Forward to Victory (1 CP)"))
                    Do(new RerollCharge(Decider));
                if (pm.Kind == MoveKind.Charge && pm.CanReroll) y += 34;
                string cancel = pm.Kind switch
                {
                    MoveKind.Charge => "Give up the charge",
                    MoveKind.Normal => "Cancel move",
                    _ => "Stay in place",
                };
                if (U.Button(new Rectangle(x, y + 4, w, 28), cancel)) Do(new CancelMove(Decider));
                y += 40;
                break;
        }
        return y;
    }

    private int DrawSelectedUnit(int x, int y, int w)
    {
        var u = _selected;
        if (u == null)
        {
            P.Text("Click a unit to see its warscroll. Tab cycles units.", new Vector2(x, y), Theme.TextDim);
            return y + LineH + 8;
        }
        var ws = u.Warscroll;
        P.Rect(new Rectangle(x - 4, y - 2, w + 8, 2), _colors[u.Owner]);
        y += 4;
        P.Text(ws.Name + (ws.Verified ? "" : "  (unverified)"), new Vector2(x, y), _colors[u.Owner]);
        y += LineH + 2;
        P.Text($"Move {_engine.MoveCharacteristic(u)}\"   Health {ws.Health}   Save {(ws.Save >= 7 ? "-" : ws.Save + "+")}   Control {ws.Control}" +
               (ws.Ward is int ward ? $"   Ward {ward}+" : ""), new Vector2(x, y), Theme.Text);
        y += LineH;
        P.Text($"Models {u.AliveCount}/{u.Models.Count}   Wounds left {u.WoundsRemaining}   {S.Players[u.Owner].Name}", new Vector2(x, y), Theme.TextDim);
        y += LineH + 4;

        P.Text("Weapon", new Vector2(x, y), Theme.TextDim);
        string[] heads = ["Rng", "Atk", "Hit", "Wnd", "Rnd", "Dmg"];
        for (int i = 0; i < heads.Length; i++) P.Text(heads[i], new Vector2(x + 200 + i * 36, y), Theme.TextDim);
        y += LineH;
        foreach (var wp in ws.Weapons)
        {
            string name = wp.Name.Length > 24 ? wp.Name[..23] + "." : wp.Name;
            P.Text(name, new Vector2(x, y), Theme.Text);
            string[] vals = [wp.IsMelee ? "-" : wp.Range + "\"", wp.Attacks, wp.Hit + "+", wp.Wound + "+", wp.Rend == 0 ? "-" : wp.Rend.ToString(), wp.Damage];
            for (int i = 0; i < vals.Length; i++) P.Text(vals[i], new Vector2(x + 200 + i * 36, y), Theme.Text);
            if (wp.Abilities.Count > 0) U.TooltipArea(new Rectangle(x, y, w, LineH), string.Join(", ", wp.Abilities));
            y += LineH;
            if (wp.Abilities.Count > 0)
            {
                P.Text("  " + string.Join(", ", wp.Abilities), new Vector2(x, y), Theme.Accent * 0.9f, 0.85f);
                y += LineH - 2;
            }
        }
        if (ws.Abilities.Count > 0 || ws.Spells.Count > 0)
        {
            y += 2;
            foreach (var a in ws.Abilities)
            {
                P.Text($"* {a.Name} ({a.Timing})", new Vector2(x, y), Theme.TextDim);
                U.TooltipArea(new Rectangle(x, y, w, LineH), a.Text);
                y += LineH;
            }
            foreach (var sp in ws.Spells)
            {
                P.Text($"~ {sp.Name} (cast {sp.CastingValue}+)", new Vector2(x, y), Theme.TextDim);
                U.TooltipArea(new Rectangle(x, y, w, LineH), sp.Text);
                y += LineH;
            }
        }
        var flags = new List<string>();
        if (u.Ran) flags.Add("Ran");
        if (u.Retreated) flags.Add("Retreated");
        if (u.Charged) flags.Add("Charged");
        if (S.InCombat(u)) flags.Add("In combat");
        if (u.Buffs.Count > 0) flags.AddRange(u.Buffs.Select(b => $"{b.Source}: {b.Stat} {b.Value:+#;-#;0}"));
        if (flags.Count > 0)
        {
            foreach (var line in P.Wrap(string.Join("  |  ", flags), w))
            {
                P.Text(line, new Vector2(x, y), Theme.Accent);
                y += LineH;
            }
        }
        return y + 6;
    }

    private int DrawActions(int x, int y, int w)
    {
        var u = _selected;
        if (u == null || !HumanDecides || u.Owner != Decider || _engine.Pending != null || !u.OnBattlefield) return y;
        int me = Decider;

        bool Btn(string text, bool enabled = true, bool on = false, string? tip = null)
        {
            bool clicked = U.Button(new Rectangle(x, y, w, 28), text, enabled, on, tip);
            y += 32;
            return clicked;
        }

        void AoaToggle()
        {
            if (_engine.CanUseCommand(me, Commands.AllOutAttack, u))
                if (Btn($"All-out Attack (+1 hit, 1 CP): {(_allOutAttack ? "ON" : "off")}", on: _allOutAttack)) _allOutAttack = !_allOutAttack;
        }

        switch (S.Phase)
        {
            case Phase.Hero:
                if (_engine.CanRally(u) && Btn("Rally (1 CP): 6 dice, 4+ heals / returns models")) Do(new Rally(me, u.Id));
                if (_engine.CanCast(u))
                    for (int i = 0; i < u.Warscroll.Spells.Count; i++)
                    {
                        var sp = u.Warscroll.Spells[i];
                        bool used = S.Players[me].SpellsCastThisTurn.Contains(sp.Name);
                        if (Btn($"Cast {sp.Name} ({sp.CastingValue}+)", !used, _mode == TargetMode.Spell && _spellIndex == i, sp.Text))
                        {
                            if (sp.Target == "self") Do(new CastSpell(me, u.Id, i, u.Id));
                            else { _mode = TargetMode.Spell; _spellIndex = i; }
                        }
                    }
                break;
            case Phase.Movement:
                if (_engine.CanNormalMove(u))
                {
                    if (Btn($"Normal move ({_engine.MoveCharacteristic(u)}\")")) Do(new DeclareMove(me, u.Id, MoveKind.Normal));
                    if (Btn("Run (Move + D6, no shooting/charging)")) Do(new DeclareMove(me, u.Id, MoveKind.Run));
                    if (_engine.CanUseCommand(me, Commands.AtTheDouble, u) && Btn("Run - At the Double (1 CP): auto 6"))
                        Do(new DeclareMove(me, u.Id, MoveKind.Run, AtTheDouble: true));
                }
                if (_engine.CanRetreat(u) && Btn("Retreat (suffer D3 mortal damage)")) Do(new DeclareMove(me, u.Id, MoveKind.Retreat));
                break;
            case Phase.Shooting:
                if (_engine.CanShoot(u))
                {
                    AoaToggle();
                    if (Btn("Shoot - then click a target", on: _mode == TargetMode.Shoot)) _mode = TargetMode.Shoot;
                }
                break;
            case Phase.Charge:
                if (_engine.CanCharge(u) && Btn("Declare charge (2D6\")")) Do(new DeclareCharge(me, u.Id));
                break;
            case Phase.Combat:
                if (_engine.CanFight(u))
                {
                    AoaToggle();
                    if (Btn("Fight - pile in 3\" then click a target", on: _mode == TargetMode.Fight)) _mode = TargetMode.Fight;
                }
                break;
            case Phase.EndOfTurn:
                if (_engine.CanPowerThrough(u) && Btn("Power Through (2 CP): D3 mortal - click target", on: _mode == TargetMode.PowerThrough))
                    _mode = TargetMode.PowerThrough;
                break;
        }
        if (_mode != TargetMode.None)
        {
            P.Text("Click a target highlighted in red (Esc / right-click to cancel).", new Vector2(x, y), Theme.Accent);
            y += LineH + 4;
        }
        return y;
    }

    private IEnumerable<Unit> ValidTargets()
    {
        if (_selected == null || !HumanDecides) return [];
        return _mode switch
        {
            TargetMode.Shoot => _engine.ShootTargets(_selected),
            TargetMode.Fight => _engine.FightTargets(_selected),
            TargetMode.Spell when _spellIndex < _selected.Warscroll.Spells.Count =>
                _engine.SpellTargets(_selected, _selected.Warscroll.Spells[_spellIndex]),
            TargetMode.PowerThrough => _engine.PowerThroughTargets(_selected),
            _ => [],
        };
    }

    private void DrawLog(int x, int y, int w, int h)
    {
        P.Rect(new Rectangle(x - 4, y - 4, w + 8, h + 8), Theme.Background);
        var lines = new List<(string, Color)>();
        foreach (var entry in S.Log.TakeLast(80))
        {
            var color = entry.StartsWith("===") || entry.StartsWith("---") ? Theme.Accent
                : entry.Contains("DESTROYED") ? Theme.Bad
                : entry.EndsWith("phase.") ? Theme.TextDim : Theme.Text;
            foreach (var l in P.Wrap(entry, w)) lines.Add((l, color));
        }
        int visible = Math.Max(1, h / 16);
        _logScroll = Math.Clamp(_logScroll, 0, Math.Max(0, lines.Count - visible));
        int start = Math.Max(0, lines.Count - visible - _logScroll);
        for (int i = 0; i < visible && start + i < lines.Count; i++)
            P.Text(lines[start + i].Item1, new Vector2(x, y + i * 16), lines[start + i].Item2, 0.85f);
    }

    private void DrawGameOver()
    {
        if (S.Phase != Phase.GameOver) return;
        var view = BoardView;
        var box = new Rectangle(view.Center.X - 220, view.Center.Y - 110, 440, 220);
        P.Rect(box, Theme.Panel * 0.97f);
        P.RectOutline(box, Theme.Accent, 2);
        string title = S.IsDraw ? "DRAW" : $"{S.Players[S.Winner!.Value].Name} WINS";
        var size = P.Title.MeasureString(title);
        P.TitleText(title, new Vector2(box.Center.X - size.X / 2, box.Y + 24), S.IsDraw ? Theme.Text : _colors[S.Winner!.Value]);
        P.TextCentered($"Victory points {S.Players[0].VictoryPoints} - {S.Players[1].VictoryPoints}", new Vector2(box.Center.X, box.Y + 90), Theme.Text);
        if (U.Button(new Rectangle(box.X + 30, box.Bottom - 60, 180, 36), "Main menu")) _game.Show(new MenuScreen(_game));
        if (U.Button(new Rectangle(box.Right - 210, box.Bottom - 60, 180, 36), "Rematch")) _game.Show(new BattleScreen(_game));
    }

    // ================================================================== BOARD CLICKS

    private void HandleBoardClick()
    {
        if (!U.LeftClicked || U.ClickConsumed || !BoardView.Contains(U.Mouse) || S.Phase == Phase.GameOver) return;
        int d = Decider;

        if (HumanDecides && S.Phase == Phase.Deployment && _engine.Pending == null && _deployUnit != null)
        {
            Do(new DeployUnit(d, _deployUnit.Id, DeploymentPositions(_deployUnit)));
            return;
        }
        if (HumanDecides && _engine.Pending is PendingMove pm)
        {
            var ghost = GhostPositions(pm);
            if (ghost == null) Flash("Cannot reach that unit with this charge roll.");
            else Do(new CompleteMove(d, ghost));
            return;
        }
        if (HumanDecides && _engine.Pending is PendingReaction pr)
        {
            if (_hovered != null && pr.Eligible.Contains(_hovered)) Do(new UseReaction(d, _hovered.Id));
            return;
        }
        if (HumanDecides && _mode != TargetMode.None && _selected != null)
        {
            if (_hovered == null || !ValidTargets().Contains(_hovered))
            {
                Flash("Not a valid target.");
                return;
            }
            var t = _hovered;
            switch (_mode)
            {
                case TargetMode.Shoot: Do(new Shoot(d, _selected.Id, t.Id, _allOutAttack)); break;
                case TargetMode.Fight: Do(new Fight(d, _selected.Id, t.Id, _allOutAttack)); break;
                case TargetMode.Spell: Do(new CastSpell(d, _selected.Id, _spellIndex, t.Id)); break;
                case TargetMode.PowerThrough: Do(new PowerThrough(d, _selected.Id, t.Id)); break;
            }
            return;
        }
        _selected = _hovered;
        _allOutAttack = false;
    }
}
