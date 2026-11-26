using AgeOfHammer.Core.AI;
using AgeOfHammer.Core.Dice;
using AgeOfHammer.Core.Engine;
using AgeOfHammer.Core.Geometry;
using AgeOfHammer.Core.State;
using Godot;
using NVec = System.Numerics.Vector2;

namespace AgeOfHammer.Client;

public enum TargetMode { None, Shoot, Fight, Spell, PowerThrough, Ability }

/// <summary>
/// The battle: owns the <see cref="GameEngine"/>, drives the AI, handles selection/targeting and builds the HUD.
/// The battlefield itself is drawn by <see cref="BoardView"/>; unit labels by <see cref="LabelLayer"/>.
/// </summary>
public partial class BattleScreen : Node
{
    /// <summary>Board pixels per battlefield inch (before camera zoom).</summary>
    public const float Px = 20f;
    public const int PanelWidth = 440;
    public const int TopBarHeight = 40;

    public Main App { get; set; } = null!;
    public GameEngine Engine { get; private set; } = null!;
    public GameState S => Engine.State;
    public Color[] Colors { get; } = new Color[2];
    public Camera2D Camera { get; private set; } = null!;
    public BoardView Board { get; private set; } = null!;

    public Unit? Selected { get; private set; }
    public Unit? Hovered { get; private set; }
    public Unit? DeployUnit { get; private set; }
    public TargetMode Mode { get; private set; }
    public NVec MouseWorld { get; private set; }
    public bool MouseOverBoard { get; private set; }

    private readonly AiPlayer?[] _ai = new AiPlayer?[2];
    private readonly int[] _perRowOptions = [5, 10, 3, 2, 1];
    private int _perRowIndex;
    private int _spellIndex;
    private string _abilityName = "";
    private bool _allOutAttack;
    private double _aiTimer;
    private double _aiDelay = 0.45;
    private bool _aiPaused;

    // HUD
    private CanvasLayer _hudLayer = null!;
    private LabelLayer _labels = null!;
    private Label _topLeft = null!;
    private RichTextLabel _topRight = null!;
    private Label _status = null!;
    private VBoxContainer _dynamic = null!;
    private RichTextLabel _log = null!;
    private Label _flash = null!;
    private double _flashUntil;
    private PanelContainer? _gameOver;
    private int _logShown;
    private string _panelSignature = "";

    public static Vector2 G(NVec v) => new(v.X * Px, v.Y * Px);
    public Vector2 ToScreen(NVec inches) => Board.GetGlobalTransformWithCanvas() * G(inches);
    public int Decider => Engine.Decider;
    public bool HumanDecides => Decider >= 0 && _ai[Decider] == null;

    public override void _Ready()
    {
        var setup = App.Setup;
        var f0 = App.Factions[setup.FactionIndex[0]];
        var f1 = App.Factions[setup.FactionIndex[1]];
        Engine = GameEngine.Create(App.Battleplans[setup.BattleplanIndex], f0, setup.Armies[0], f1, setup.Armies[1],
            new SeededDiceRoller(System.Environment.TickCount));
        Colors[0] = Palette.Faction(f0.Color, Godot.Colors.SteelBlue);
        Colors[1] = Palette.Faction(f1.Color, Godot.Colors.IndianRed);
        if (f0.Id == f1.Id)
        {
            S.Players[0].Name += " (P1)";
            S.Players[1].Name += " (P2)";
            Colors[1] = Color.Color8(200, 70, 70);
        }
        for (int p = 0; p < 2; p++)
        {
            if (setup.IsAi[p]) _ai[p] = new AiPlayer(p);
            else if (setup.IsAi[1 - p]) S.Players[p].Name += " (You)";
        }

        Board = new BoardView { Screen = this };
        AddChild(Board);
        Camera = new Camera2D();
        AddChild(Camera);
        Camera.MakeCurrent();
        FitCamera();
        BuildHud();
    }

    // ================================================================== HUD construction

    private void BuildHud()
    {
        var layer = _hudLayer = new CanvasLayer();
        AddChild(layer);

        _labels = new LabelLayer { Screen = this, MouseFilter = Control.MouseFilterEnum.Ignore };
        _labels.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(_labels);

        var top = Ui.Panel(Palette.Panel, 8);
        top.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopWide);
        top.OffsetBottom = TopBarHeight;
        layer.AddChild(top);
        var topRow = Ui.Row(12);
        top.AddChild(topRow);
        _topLeft = Ui.Label("", size: 15);
        _topLeft.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        topRow.AddChild(_topLeft);
        _topRight = new RichTextLabel { BbcodeEnabled = true, FitContent = true, AutowrapMode = TextServer.AutowrapMode.Off, ScrollActive = false };
        _topRight.CustomMinimumSize = new Vector2(620, 0);
        _topRight.AddThemeFontSizeOverride("normal_font_size", 15);
        topRow.AddChild(_topRight);

        var side = Ui.Panel(Palette.Panel, 10);
        side.AnchorLeft = 1;
        side.AnchorRight = 1;
        side.AnchorTop = 0;
        side.AnchorBottom = 1;
        side.OffsetLeft = -PanelWidth;
        side.OffsetTop = TopBarHeight;
        layer.AddChild(side);
        var col = Ui.Column(8);
        side.AddChild(col);
        _status = Ui.Label("", size: 15, wrap: true);
        col.AddChild(_status);

        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        scroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        scroll.SizeFlagsStretchRatio = 1f;
        col.AddChild(scroll);
        _dynamic = Ui.Column(6);
        _dynamic.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scroll.AddChild(_dynamic);

        _log = new RichTextLabel { BbcodeEnabled = true, ScrollFollowing = true, SelectionEnabled = true };
        _log.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _log.SizeFlagsStretchRatio = 1.2f;
        _log.AddThemeFontSizeOverride("normal_font_size", 13);
        _log.AddThemeStyleboxOverride("normal", Ui.Box(Palette.Background, null, 6));
        col.AddChild(_log);

        _flash = Ui.Label("", Godot.Colors.White, 15);
        _flash.AddThemeStyleboxOverride("normal", Ui.Box(Color.Color8(60, 16, 16, 235), null, 8));
        _flash.Visible = false;
        _flash.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterTop);
        _flash.OffsetTop = TopBarHeight + 12;
        layer.AddChild(_flash);
    }

    private void Flash(string? message)
    {
        if (string.IsNullOrEmpty(message)) return;
        _flash.Text = message;
        _flash.Visible = true;
        _flash.ResetSize();
        var vp = GetViewport().GetVisibleRect().Size;
        _flash.Position = new Vector2((vp.X - PanelWidth - _flash.Size.X) / 2, TopBarHeight + 12);
        _flashUntil = Time.GetTicksMsec() / 1000.0 + 3.5;
    }

    // ================================================================== Actions

    private void Do(GameAction action)
    {
        var r = Engine.Submit(action);
        if (!r.Ok) Flash(r.Error);
        else
        {
            Mode = TargetMode.None;
            _allOutAttack = false;
        }
    }

    private void TryEndPhase()
    {
        if (!HumanDecides || Engine.Pending != null || S.Phase is Phase.Deployment or Phase.GameOver) return;
        Do(new EndPhase(Decider));
    }

    public void FitCamera()
    {
        var vp = GetViewport().GetVisibleRect().Size;
        var plan = S.Battleplan;
        float zoom = Mathf.Min((vp.X - PanelWidth - 30) / (plan.Width * Px), (vp.Y - TopBarHeight - 30) / (plan.Height * Px));
        zoom = Mathf.Max(zoom, 0.2f); // headless runs have a zero-size viewport
        Camera.Zoom = new Vector2(zoom, zoom);
        var boardCenter = new Vector2(plan.Width * Px / 2, plan.Height * Px / 2);
        Camera.Position = boardCenter + new Vector2(PanelWidth / 2f, -TopBarHeight / 2f) / zoom;
    }

    // ================================================================== Per-frame update

    public override void _Process(double delta)
    {
        if (App.SmokeTest)
        {
            RunSmokeTest();
            return;
        }

        var vp = GetViewport().GetVisibleRect().Size;
        var mouse = GetViewport().GetMousePosition();
        MouseOverBoard = mouse.X < vp.X - PanelWidth && mouse.Y > TopBarHeight && _gameOver == null;
        var world = Board.GetGlobalMousePosition() / Px;
        MouseWorld = new NVec(world.X, world.Y);
        Hovered = MouseOverBoard ? UnitAt(MouseWorld) : null;

        if (S.Phase == Phase.Deployment && HumanDecides &&
            (DeployUnit == null || DeployUnit.Deployed || DeployUnit.Owner != Decider))
            DeployUnit = S.UnitsOf(Decider).FirstOrDefault(u => !u.Deployed);
        if (Selected != null && !Selected.OnBattlefield && S.Phase != Phase.Deployment) Selected = null;

        if (S.Phase != Phase.GameOver && Decider >= 0 && _ai[Decider] is { } ai && !_aiPaused)
        {
            _aiTimer += delta;
            double wait = S.Phase == Phase.Deployment ? _aiDelay * 0.4 : _aiDelay;
            if (_aiTimer >= wait)
            {
                _aiTimer = 0;
                try { ai.Act(Engine); }
                catch (Exception e)
                {
                    _aiPaused = true;
                    Flash($"AI error (paused): {e.Message}");
                    GD.PrintErr(e);
                }
            }
        }

        if (_flash.Visible && Time.GetTicksMsec() / 1000.0 > _flashUntil) _flash.Visible = false;
        RefreshHud();
        Board.QueueRedraw();
        _labels.QueueRedraw();
    }

    /// <summary>--smoke: play the whole AI vs AI game immediately, then quit with the result.</summary>
    private void RunSmokeTest()
    {
        try
        {
            int guard = 0;
            while (S.Phase != Phase.GameOver)
            {
                if (++guard > 20000) throw new InvalidOperationException("Game did not finish.");
                _ai[Decider]!.Act(Engine);
            }
            GD.Print(S.Log.Last());
            GD.Print($"SMOKE OK: {guard} actions, {S.BattleRound} rounds.");
            GetTree().Quit(0);
        }
        catch (Exception e)
        {
            GD.PrintErr("SMOKE FAILED: " + e);
            GetTree().Quit(1);
        }
        SetProcess(false);
    }

    private Unit? UnitAt(NVec world)
    {
        foreach (var u in S.Units.Where(u => u.OnBattlefield))
            foreach (var m in u.Alive)
                if (NVec.Distance(m.Position, world) <= u.BaseRadius) return u;
        return null;
    }

    // ================================================================== Input

    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false } key) return;
        switch (key.Keycode)
        {
            case Key.Escape:
                if (Mode != TargetMode.None) Mode = TargetMode.None;
                else Selected = null;
                break;
            case Key.Enter:
            case Key.KpEnter:
                TryEndPhase();
                break;
            case Key.Tab:
                CycleSelection();
                break;
            case Key.R:
                _perRowIndex = (_perRowIndex + 1) % _perRowOptions.Length;
                break;
            case Key.F:
                FitCamera();
                break;
            case Key.Equal:
            case Key.KpAdd:
                _aiDelay = Math.Max(0.02, _aiDelay / 1.6);
                Flash($"AI delay {_aiDelay:0.00}s");
                break;
            case Key.Minus:
            case Key.KpSubtract:
                _aiDelay = Math.Min(3, _aiDelay * 1.6);
                Flash($"AI delay {_aiDelay:0.00}s");
                break;
            case Key.P:
                _aiPaused = !_aiPaused;
                break;
        }
        GetViewport().SetInputAsHandled();
    }

    private void CycleSelection()
    {
        int owner = Decider >= 0 ? Decider : 0;
        var mine = S.LiveUnitsOf(owner).ToList();
        if (mine.Count == 0) return;
        int idx = Selected == null ? -1 : mine.IndexOf(Selected);
        Selected = mine[(idx + 1) % mine.Count];
    }

    public void CancelMode() => Mode = TargetMode.None;

    public void OnBoardClick()
    {
        if (!MouseOverBoard || S.Phase == Phase.GameOver) return;
        int d = Decider;

        if (HumanDecides && S.Phase == Phase.Deployment && Engine.Pending == null && DeployUnit != null)
        {
            Do(new DeployUnit(d, DeployUnit.Id, DeploymentPositions(DeployUnit)));
            return;
        }
        if (HumanDecides && Engine.Pending is PendingMove pm)
        {
            var ghost = GhostPositions(pm);
            if (ghost == null) Flash("Cannot reach that unit with this charge roll.");
            else Do(new CompleteMove(d, ghost));
            return;
        }
        if (HumanDecides && Engine.Pending is PendingReaction pr)
        {
            if (Hovered != null && pr.Eligible.Contains(Hovered)) Do(new UseReaction(d, Hovered.Id));
            return;
        }
        if (HumanDecides && Mode != TargetMode.None && Selected != null)
        {
            if (Hovered == null || !ValidTargets().Contains(Hovered))
            {
                Flash("Not a valid target.");
                return;
            }
            var t = Hovered;
            switch (Mode)
            {
                case TargetMode.Shoot: Do(new Shoot(d, Selected.Id, t.Id, _allOutAttack)); break;
                case TargetMode.Fight: Do(new Fight(d, Selected.Id, t.Id, _allOutAttack)); break;
                case TargetMode.Spell: Do(new CastSpell(d, Selected.Id, _spellIndex, t.Id)); break;
                case TargetMode.PowerThrough: Do(new PowerThrough(d, Selected.Id, t.Id)); break;
                case TargetMode.Ability: Do(new UseAbility(d, Selected.Id, _abilityName, [t.Id])); break;
            }
            return;
        }
        Selected = Hovered;
        _allOutAttack = false;
    }

    // ================================================================== Queries used by the board

    public List<NVec> DeploymentPositions(Unit unit)
    {
        int perRow = Math.Max(1, Math.Min(_perRowOptions[_perRowIndex], unit.Models.Count));
        return Geo.Formation(unit.Models.Count, unit.BaseRadius, MouseWorld, perRow);
    }

    public List<NVec>? GhostPositions(PendingMove pm)
    {
        if (pm.Kind == MoveKind.Charge && Hovered != null && Hovered.Owner != pm.Unit.Owner)
            return Placement.AutoCharge(Engine, pm, Hovered);
        return Placement.PreviewMove(pm, MouseWorld);
    }

    public IEnumerable<Unit> ValidTargets()
    {
        if (Selected == null || !HumanDecides) return [];
        return Mode switch
        {
            TargetMode.Shoot => Engine.ShootTargets(Selected),
            TargetMode.Fight => Engine.FightTargets(Selected),
            TargetMode.Spell when _spellIndex < Selected.Warscroll.Spells.Count =>
                Engine.SpellTargets(Selected, Selected.Warscroll.Spells[_spellIndex]),
            TargetMode.PowerThrough => Engine.PowerThroughTargets(Selected),
            TargetMode.Ability when Engine.ActivatedAbilities(Selected).FirstOrDefault(a => a.Name == _abilityName) is { } ab =>
                Engine.AbilityTargets(Selected, ab),
            _ => [],
        };
    }

    public bool HasActed(Unit u) => S.Phase switch
    {
        Phase.Movement => u.Moved,
        Phase.Shooting => u.Shot,
        Phase.Charge => u.ChargeAttempted,
        Phase.Combat => u.Fought,
        _ => false,
    };

    public static string PhaseName(Phase p) => p switch
    {
        Phase.Hero => "Hero Phase",
        Phase.Movement => "Movement Phase",
        Phase.Shooting => "Shooting Phase",
        Phase.Charge => "Charge Phase",
        Phase.Combat => "Combat Phase",
        Phase.EndOfTurn => "End of Turn",
        _ => p.ToString(),
    };

    // ================================================================== HUD refresh

    private void RefreshHud()
    {
        _topLeft.Text = S.Phase switch
        {
            Phase.Deployment => "DEPLOYMENT",
            Phase.GameOver => "GAME OVER",
            _ => $"Round {S.BattleRound}/{S.Battleplan.BattleRounds}   |   {S.Active.Name}'s turn   |   {PhaseName(S.Phase)}",
        };
        _topRight.Text = "[right]" + string.Join("      ", S.Players.Select(p =>
            $"[color=#{Palette.Hex(Colors[p.Index])}]{Ui.Bb(p.Name)}   CP {p.CommandPoints}   VP {p.VictoryPoints}[/color]")) + "[/right]";

        _status.Text = S.Phase == Phase.GameOver ? "" :
            HumanDecides ? $"{S.Players[Decider].Name}: your decision" :
            Decider >= 0 ? $"{S.Players[Decider].Name} (AI) is thinking..." + (_aiPaused ? "  [PAUSED - press P]" : "") : "";
        _status.AddThemeColorOverride("font_color", Decider >= 0 ? Colors[Decider] : Palette.Text);

        // Append new log lines.
        for (; _logShown < S.Log.Count; _logShown++)
        {
            var entry = S.Log[_logShown];
            var color = entry.StartsWith("===") || entry.StartsWith("---") ? Palette.Accent
                : entry.Contains("DESTROYED") ? Palette.Bad
                : entry.EndsWith("phase.") ? Palette.TextDim : Palette.Text;
            _log.AppendText($"[color=#{Palette.Hex(color)}]{Ui.Bb(entry)}[/color]\n");
        }

        // Rebuild the action panel only when something it shows has changed.
        var pending = Engine.Pending;
        string sig = string.Join("|", S.Phase, Decider, pending?.GetType().Name, (pending as PendingMove)?.Allowance,
            (pending as PendingMove)?.CanReroll, Selected?.Id, Selected?.AliveCount, Selected?.WoundsRemaining, Mode, _allOutAttack,
            S.Log.Count, DeployUnit?.Id, S.Players[0].CommandPoints, S.Players[1].CommandPoints);
        if (sig != _panelSignature)
        {
            _panelSignature = sig;
            BuildPanel();
        }

        if (S.Phase == Phase.GameOver && _gameOver == null) ShowGameOver();
    }

    private void BuildPanel()
    {
        Ui.Clear(_dynamic);
        if (HumanDecides) BuildPrompt();
        BuildUnitCard();
        BuildActions();
        if (HumanDecides && Engine.Pending == null && S.Phase is not (Phase.Deployment or Phase.GameOver))
        {
            string label = S.Phase == Phase.Combat
                ? (Engine.PickableFighters(Decider).Any() ? "Pass (remaining units don't fight)" : "End Combat Phase")
                : $"End {PhaseName(S.Phase)}  [Enter]";
            _dynamic.AddChild(Ui.Button(label, TryEndPhase));
        }
    }

    private void Text(string text, Color? color = null, int size = 14) => _dynamic.AddChild(Ui.Label(text, color, size, wrap: true));
    private void Btn(string text, Action action, bool enabled = true, bool on = false, string? tip = null) =>
        _dynamic.AddChild(Ui.Button(text, action, enabled, on, tip));

    private void BuildPrompt()
    {
        int d = Decider;
        switch (Engine.Pending)
        {
            case null when S.Phase == Phase.Deployment:
                Text("Deploy your units wholly within your territory (shaded). Click the board to place the selected unit. R changes formation.", Palette.TextDim);
                foreach (var u in S.UnitsOf(d).Where(u => !u.Deployed).Take(12))
                {
                    var unit = u;
                    Btn($"{u.Name} ({u.Models.Count})", () => { DeployUnit = unit; _panelSignature = ""; }, on: u == DeployUnit);
                }
                Btn("Auto-place this unit", () =>
                {
                    if (DeployUnit == null) return;
                    var pos = Placement.DeployNear(Engine, DeployUnit, S.TerritoryOf(d).Center);
                    if (pos == null) Flash("No room to deploy that unit.");
                    else Do(new DeployUnit(d, DeployUnit.Id, pos));
                });
                break;

            case PendingDefence pd:
                Text($"{pd.Attacker.Name} is attacking your {pd.Target.Name}.", Palette.Accent);
                Text("Use All-out Defence (+1 to save rolls) for 1 CP?");
                Btn("Yes (1 CP)", () => Do(new RespondDefence(d, true)));
                Btn("No", () => Do(new RespondDefence(d, false)));
                break;

            case PendingReaction pr:
                string cmd = Commands.ForReaction(pr.Kind);
                string desc = pr.Kind switch
                {
                    ReactionKind.Redeploy => "move a unit up to D6\"",
                    ReactionKind.CoveringFire => "a unit shoots the closest enemy with -1 to hit",
                    _ => "a unit attempts a charge",
                };
                Text($"Reaction: use {cmd} ({Commands.Cost(cmd)} CP) - {desc}? Click a highlighted unit or pick below.", Palette.Accent);
                foreach (var u in pr.Eligible.Take(8))
                {
                    var unit = u;
                    Btn(u.Name, () => Do(new UseReaction(d, unit.Id)));
                }
                Btn("Decline", () => Do(new DeclineReaction(d)));
                break;

            case PendingAbilityWindow aw:
                Text($"Your opponent ended the {PhaseName(aw.Phase)}. You can use your units' abilities now.", Palette.Accent);
                foreach (var (unit, ability) in Engine.UsableAbilities(d).Take(10))
                {
                    var (uu, ab) = (unit, ability);
                    Btn($"{uu.Name}: {ab.Name}", () => StartAbility(uu, ab), tip: AbilityTip(ab));
                }
                Btn("Done", () => Do(new DoneAbilities(d)));
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
                Text($"{pm.Unit.Name}: {kind} up to {pm.Allowance:0.#}\"", Palette.Accent, 15);
                Text(pm.Kind == MoveKind.Charge
                    ? "Click an enemy unit to charge it (models end within 1/2\"), or click a point to move the formation there."
                    : "Click where the unit should move. The formation follows the cursor; green = legal.", Palette.TextDim);
                if (pm.Kind == MoveKind.Charge && pm.CanReroll)
                    Btn("Re-roll charge: Forward to Victory (1 CP)", () => Do(new RerollCharge(d)));
                Btn(pm.Kind switch
                {
                    MoveKind.Charge => "Give up the charge",
                    MoveKind.Normal => "Cancel move",
                    _ => "Stay in place",
                }, () => Do(new CancelMove(d)));
                break;
        }
    }

    private void BuildUnitCard()
    {
        var u = Selected;
        if (u == null)
        {
            Text("Click a unit to see its warscroll. Tab cycles units.", Palette.TextDim);
            return;
        }
        var ws = u.Warscroll;
        string c = Palette.Hex(Colors[u.Owner]);
        var sb = new System.Text.StringBuilder();
        sb.Append($"[color=#{c}][b]{Ui.Bb(ws.Name)}[/b][/color]");
        if (!ws.Verified) sb.Append($" [color=#{Palette.Hex(Palette.TextDim)}](unverified)[/color]");
        sb.Append('\n');
        sb.Append($"Move {Engine.MoveCharacteristic(u)}\"   Health {ws.Health}   Save {(ws.Save >= 7 ? "-" : ws.Save + "+")}   Control {ws.Control}");
        if (ws.Ward is int ward) sb.Append($"   Ward {ward}+");
        sb.Append($"\n[color=#{Palette.Hex(Palette.TextDim)}]Models {u.AliveCount}/{u.Models.Count}   Wounds left {u.WoundsRemaining}   {Ui.Bb(S.Players[u.Owner].Name)}[/color]\n");

        sb.Append("[table=7]");
        foreach (var h in new[] { "Weapon", "Rng", "Atk", "Hit", "Wnd", "Rnd", "Dmg" })
            sb.Append($"[cell][color=#{Palette.Hex(Palette.TextDim)}]{h}  [/color][/cell]");
        foreach (var wp in ws.Weapons)
        {
            string name = Ui.Bb(wp.Name);
            if (wp.Abilities.Count > 0)
                name = $"[hint={Ui.Bb(string.Join(", ", wp.Abilities))}]{name}[/hint][color=#{Palette.Hex(Palette.Accent)}]*[/color]";
            sb.Append($"[cell]{name}  [/cell]");
            foreach (var v in new[] { wp.IsMelee ? "-" : wp.Range + "\"", wp.Attacks, wp.Hit + "+", wp.Wound + "+", wp.Rend == 0 ? "-" : wp.Rend.ToString(), wp.Damage })
                sb.Append($"[cell]{v}  [/cell]");
        }
        sb.Append("[/table]\n");
        foreach (var a in ws.Abilities.Where(a => a.IsScripted))
            sb.Append($"[color=#{Palette.Hex(Palette.Good)}][hint={Ui.Bb(AbilityTip(a))}]+ {Ui.Bb(a.Name)} ({Ui.Bb(a.Timing)})[/hint][/color]\n");
        foreach (var a in ws.Abilities.Where(a => !a.IsScripted))
            sb.Append($"[hint={Ui.Bb(a.Text)}]* {Ui.Bb(a.Name)} ({Ui.Bb(a.Timing)})[/hint]\n");
        foreach (var sp in ws.Spells)
            sb.Append($"[hint={Ui.Bb(sp.Text)}]~ {Ui.Bb(sp.Name)} (cast {sp.CastingValue}+)[/hint]\n");

        var flags = new List<string>();
        if (u.Ran) flags.Add("Ran");
        if (u.Retreated) flags.Add("Retreated");
        if (u.Charged) flags.Add("Charged");
        if (S.InCombat(u)) flags.Add("In combat");
        flags.AddRange(u.Buffs.Select(b => $"{b.Source}: {b.Stat} {b.Value:+#;-#;0}"));
        if (flags.Count > 0) sb.Append($"[color=#{Palette.Hex(Palette.Accent)}]{Ui.Bb(string.Join("  |  ", flags))}[/color]");

        var card = new RichTextLabel { BbcodeEnabled = true, FitContent = true, Text = sb.ToString(), ScrollActive = false };
        card.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        card.AddThemeFontSizeOverride("normal_font_size", 14);
        card.AddThemeFontSizeOverride("bold_font_size", 16);
        _dynamic.AddChild(card);
    }

    private void BuildActions()
    {
        var u = Selected;
        if (u == null || !HumanDecides || u.Owner != Decider || !u.OnBattlefield) return;
        if (Engine.Pending is PendingAbilityWindow) { AbilityButtons(u); return; }
        if (Engine.Pending != null) return;
        int me = Decider;

        void AoaToggle()
        {
            if (Engine.CanUseCommand(me, Commands.AllOutAttack, u))
                Btn($"All-out Attack (+1 hit, 1 CP): {(_allOutAttack ? "ON" : "off")}", () => _allOutAttack = !_allOutAttack, on: _allOutAttack);
        }

        switch (S.Phase)
        {
            case Phase.Hero:
                if (Engine.CanRally(u)) Btn("Rally (1 CP): 6 dice, 4+ heals / returns models", () => Do(new Rally(me, u.Id)));
                if (Engine.CanCast(u))
                    for (int i = 0; i < u.Warscroll.Spells.Count; i++)
                    {
                        int index = i;
                        var sp = u.Warscroll.Spells[i];
                        bool used = S.Players[me].SpellsCastThisTurn.Contains(sp.Name);
                        Btn($"Cast {sp.Name} ({sp.CastingValue}+)", () =>
                        {
                            if (sp.Target == "self") Do(new CastSpell(me, u.Id, index, u.Id));
                            else { Mode = TargetMode.Spell; _spellIndex = index; }
                        }, !used, Mode == TargetMode.Spell && _spellIndex == i, sp.Text);
                    }
                break;
            case Phase.Movement:
                if (Engine.CanNormalMove(u))
                {
                    Btn($"Normal move ({Engine.MoveCharacteristic(u)}\")", () => Do(new DeclareMove(me, u.Id, MoveKind.Normal)));
                    Btn("Run (Move + D6, no shooting/charging)", () => Do(new DeclareMove(me, u.Id, MoveKind.Run)));
                    if (Engine.CanUseCommand(me, Commands.AtTheDouble, u))
                        Btn("Run - At the Double (1 CP): auto 6", () => Do(new DeclareMove(me, u.Id, MoveKind.Run, AtTheDouble: true)));
                }
                if (Engine.CanRetreat(u)) Btn("Retreat (suffer D3 mortal damage)", () => Do(new DeclareMove(me, u.Id, MoveKind.Retreat)));
                break;
            case Phase.Shooting:
                if (Engine.CanShoot(u))
                {
                    AoaToggle();
                    Btn("Shoot - then click a target", () => Mode = TargetMode.Shoot, on: Mode == TargetMode.Shoot);
                }
                break;
            case Phase.Charge:
                if (Engine.CanCharge(u)) Btn("Declare charge (2D6\")", () => Do(new DeclareCharge(me, u.Id)));
                break;
            case Phase.Combat:
                if (Engine.CanFight(u))
                {
                    AoaToggle();
                    Btn("Fight - pile in 3\" then click a target", () => Mode = TargetMode.Fight, on: Mode == TargetMode.Fight);
                }
                break;
            case Phase.EndOfTurn:
                if (Engine.CanPowerThrough(u))
                    Btn("Power Through (2 CP): D3 mortal - click target", () => Mode = TargetMode.PowerThrough, on: Mode == TargetMode.PowerThrough);
                break;
        }
        AbilityButtons(u);
        if (Mode != TargetMode.None) Text("Click a target highlighted in red (Esc / right-click to cancel).", Palette.Accent);
    }

    /// <summary>Buttons for the selected unit's scripted abilities that can be used right now.</summary>
    private void AbilityButtons(Unit u)
    {
        foreach (var ability in Engine.ActivatedAbilities(u).Where(a => Engine.CanUseAbility(Decider, u, a)).ToList())
        {
            var ab = ability;
            Btn($"Use: {ab.Name}", () => StartAbility(u, ab), on: Mode == TargetMode.Ability && _abilityName == ab.Name, tip: AbilityTip(ab));
        }
    }

    private void StartAbility(Unit unit, AgeOfHammer.Core.Data.AbilityDef ability)
    {
        Selected = unit;
        if (ability.Activate!.Target == "self") Do(new UseAbility(Decider, unit.Id, ability.Name));
        else
        {
            Mode = TargetMode.Ability;
            _abilityName = ability.Name;
            _panelSignature = "";
        }
    }

    private static string AbilityTip(AgeOfHammer.Core.Data.AbilityDef a) =>
        $"{a.Name} ({a.Timing})\n" + (a.Declare != null ? a.Declare + "\n" : "") + a.Text +
        (a.IsScripted ? "\n[Automated by the engine]" : "");

    private void ShowGameOver()
    {
        _gameOver = Ui.Panel(Palette.Panel, 24);
        _gameOver.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center);
        var col = Ui.Column(16);
        _gameOver.AddChild(col);
        string title = S.IsDraw ? "DRAW" : $"{S.Players[S.Winner!.Value].Name} WINS";
        var t = Ui.Label(title, S.IsDraw ? Palette.Text : Colors[S.Winner!.Value], 30);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        col.AddChild(t);
        var vp = Ui.Label($"Victory points {S.Players[0].VictoryPoints} - {S.Players[1].VictoryPoints}");
        vp.HorizontalAlignment = HorizontalAlignment.Center;
        col.AddChild(vp);
        col.AddChild(Ui.Row(16, Ui.Button("Main menu", App.ShowMenu, minWidth: 180), Ui.Button("Rematch", App.ShowBattle, minWidth: 180)));
        _hudLayer.AddChild(_gameOver);
        _gameOver.ResetSize();
        var size = GetViewport().GetVisibleRect().Size;
        _gameOver.Position = new Vector2((size.X - PanelWidth - _gameOver.Size.X) / 2, (size.Y - _gameOver.Size.Y) / 2);
    }
}
