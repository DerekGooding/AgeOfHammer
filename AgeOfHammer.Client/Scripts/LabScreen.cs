using AgeOfHammer.Core.Data;
using AgeOfHammer.Core.Lab;
using Godot;

namespace AgeOfHammer.Client;

/// <summary>What the lab remembers while the player edits armies or watches a replay.</summary>
public sealed class LabState
{
    public string Tab { get; set; } = "matchup";
    public int Games { get; set; } = 1000;
    public bool SwapSides { get; set; } = true;
    public bool UseReserves { get; set; } = true;
    public MatchupResult? Result { get; set; }
    public int EfficiencyFaction { get; set; } = -1;
    public int EfficiencyTarget { get; set; } = 1;
    public int SortColumn { get; set; } = 4;
    public string? SelectedWarscroll { get; set; }
}

/// <summary>A game to replay from a lab run.</summary>
public sealed record LabReplay(ArmyList A, ArmyList B, MatchupSettings Settings, int Seed, bool Swapped);

/// <summary>
/// The list-building lab: run many AI-vs-AI games between two lists (win rates with confidence intervals, VP margins,
/// unit performance, replays), and compare warscrolls' efficiency per 100 points.
/// </summary>
public partial class LabScreen : Control
{
    public Main App { get; set; } = null!;
    private LabState L => App.Lab;

    private MarginContainer _root = null!;
    private VBoxContainer _body = null!;

    // Running state
    private System.Threading.Tasks.Task<MatchupResult>? _task;
    private CancellationTokenSource? _cancel;
    private int _progress;
    private ProgressBar? _bar;
    private Label? _progressLabel;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _root = new MarginContainer();
        _root.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        foreach (var side in new[] { "left", "top", "right", "bottom" }) _root.AddThemeConstantOverride("margin_" + side, 24);
        AddChild(_root);
        if (L.EfficiencyFaction < 0) L.EfficiencyFaction = App.Setup.FactionIndex[0];
        Build();
    }

    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Keycode: Key.Escape } && _task == null) App.ShowMenu();
    }

    public override void _Process(double delta)
    {
        if (_task == null) return;
        if (_bar != null) _bar.Value = Volatile.Read(ref _progress);
        if (_progressLabel != null) _progressLabel.Text = $"Simulating... {Volatile.Read(ref _progress)} / {L.Games} games";
        if (!_task.IsCompleted) return;
        if (_task.IsCompletedSuccessfully) L.Result = _task.Result;
        _task = null;
        _cancel?.Dispose();
        _cancel = null;
        Build();
    }

    private void Build()
    {
        Ui.Clear(_root);
        var page = Ui.Column(12);
        _root.AddChild(page);
        var header = Ui.Row(10, Ui.Label("LIST LAB", Palette.Accent, 26));
        header.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        header.AddChild(Ui.Button("Matchup", () => { L.Tab = "matchup"; Build(); }, _task == null, L.Tab == "matchup", minWidth: 130));
        header.AddChild(Ui.Button("Unit efficiency", () => { L.Tab = "efficiency"; Build(); }, _task == null, L.Tab == "efficiency", minWidth: 150));
        header.AddChild(Ui.Button("Main menu", App.ShowMenu, _task == null, minWidth: 130));
        page.AddChild(header);

        _body = Ui.Column(10);
        _body.SizeFlagsVertical = SizeFlags.ExpandFill;
        page.AddChild(_body);
        if (L.Tab == "matchup") BuildMatchup();
        else BuildEfficiency();
    }

    // ================================================================== Matchup

    private ArmyList Army(int player)
    {
        var f = App.Factions[App.Setup.FactionIndex[player]];
        return new ArmyList(f, App.Setup.Armies[player].ToList());
    }

    private Color ColorOf(int player) => Palette.Faction(App.Factions[App.Setup.FactionIndex[player]].Color, player == 0 ? Palette.Accent : Palette.Good);

    private void BuildMatchup()
    {
        var row = Ui.Row(20);
        row.SizeFlagsVertical = SizeFlags.ExpandFill;
        _body.AddChild(row);

        // Left: armies and settings.
        var left = Ui.Column(10);
        left.CustomMinimumSize = new Vector2(420, 0);
        row.AddChild(left);
        for (int p = 0; p < 2; p++) left.AddChild(ArmyPanel(p));

        var settings = Ui.Panel(margin: 12);
        var sc = Ui.Column(8);
        settings.AddChild(sc);
        var plan = App.Battleplans[App.Setup.BattleplanIndex];
        sc.AddChild(Ui.Label($"Battleplan: {plan.Name}", size: 14));
        var gamesRow = Ui.Row(6, Ui.Label("Games:", size: 14));
        foreach (int n in new[] { 100, 500, 1000, 2000, 5000 })
        {
            int games = n;
            gamesRow.AddChild(Ui.Button(n.ToString(), () => { L.Games = games; Build(); }, _task == null, L.Games == n, minWidth: 56));
        }
        sc.AddChild(gamesRow);
        sc.AddChild(Ui.Row(6,
            Ui.Button($"Swap sides: {(L.SwapSides ? "on" : "off")}", () => { L.SwapSides = !L.SwapSides; Build(); }, _task == null, L.SwapSides,
                tooltip: "Play every seed twice with the armies on opposite sides, cancelling out first-turn and territory bias."),
            Ui.Button($"AI reserves: {(L.UseReserves ? "on" : "off")}", () => { L.UseReserves = !L.UseReserves; Build(); }, _task == null, L.UseReserves,
                tooltip: "Whether the AI uses deep strike / tunnels.")));
        if (_task == null)
        {
            var run = Ui.Button("RUN MATCHUP", StartRun, Army(0).Units.Count > 0 && Army(1).Units.Count > 0);
            run.CustomMinimumSize = new Vector2(0, 44);
            run.AddThemeFontSizeOverride("font_size", 17);
            sc.AddChild(run);
        }
        else
        {
            _bar = new ProgressBar { MinValue = 0, MaxValue = L.Games, Value = _progress, CustomMinimumSize = new Vector2(0, 24) };
            sc.AddChild(_bar);
            _progressLabel = Ui.Label("Simulating...", Palette.TextDim, 13);
            sc.AddChild(_progressLabel);
            sc.AddChild(Ui.Button("Cancel", () => _cancel?.Cancel()));
        }
        sc.AddChild(Ui.Label("Results are only as good as the AI, which plays both sides. Use them to compare lists, not as absolute truths.", Palette.TextDim, 12, wrap: true));
        left.AddChild(settings);

        // Right: results.
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.SizeFlagsVertical = SizeFlags.ExpandFill;
        row.AddChild(scroll);
        var right = Ui.Column(12);
        right.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(right);
        if (L.Result is { } r) BuildResults(right, r);
        else right.AddChild(Ui.Label("Pick two armies and run a matchup. 1000 games take a few seconds.", Palette.TextDim, 15));
    }

    private Control ArmyPanel(int player)
    {
        var army = Army(player);
        var panel = Ui.Panel(margin: 12);
        var col = Ui.Column(6);
        panel.AddChild(col);
        var swatch = new ColorRect { Color = ColorOf(player), CustomMinimumSize = new Vector2(16, 16) };
        col.AddChild(Ui.Row(8, swatch, Ui.Label($"{(player == 0 ? "A" : "B")}: {army.Faction.Name}", size: 16)));
        col.AddChild(Ui.Row(6,
            Ui.Button("<", () => CycleFaction(player, -1), _task == null, minWidth: 36),
            Ui.Button(">", () => CycleFaction(player, 1), _task == null, minWidth: 36),
            Ui.Button("Edit list...", () => App.ShowArmy(player, App.ShowLab), _task == null, minWidth: 120)));
        col.AddChild(Ui.Label($"{army.Units.Count} units, {army.Points} pts", Palette.TextDim, 14));
        var names = string.Join(", ", army.Units.GroupBy(id => id).Select(g =>
            (g.Count() > 1 ? $"{g.Count()}x " : "") + (army.Faction.Warscrolls.FirstOrDefault(w => w.Id == g.Key)?.Name ?? g.Key)));
        col.AddChild(Ui.Label(names, Palette.Text, 13, wrap: true));
        return panel;
    }

    private void CycleFaction(int player, int dir)
    {
        int n = App.Factions.Count;
        App.Setup.FactionIndex[player] = ((App.Setup.FactionIndex[player] + dir) % n + n) % n;
        App.ResetArmy(player);
        L.Result = null;
        Build();
    }

    /// <summary>Used by "--lab --run".</summary>
    public void StartRunFromCommandLine() => StartRun();

    private void StartRun()
    {
        var a = Army(0);
        var b = Army(1);
        var settings = new MatchupSettings
        {
            Battleplan = App.Battleplans[App.Setup.BattleplanIndex], Games = L.Games, SwapSides = L.SwapSides, UseReserves = L.UseReserves,
        };
        _progress = 0;
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        _task = System.Threading.Tasks.Task.Run(() => MatchupRunner.Run(a, b, settings, new CounterProgress(this), token));
        Build();
    }

    private sealed class CounterProgress(LabScreen screen) : IProgress<int>
    {
        public void Report(int value) => Interlocked.Exchange(ref screen._progress, value);
    }

    private void BuildResults(VBoxContainer right, MatchupResult r)
    {
        var cA = ColorOf(0);
        var cB = ColorOf(1);
        right.AddChild(Ui.Label($"{r.N} games in {r.Elapsed.TotalSeconds:0.0}s" + (r.Failures > 0 ? $" ({r.Failures} failed)" : "") +
                                $"   -   {r.A}  vs  {r.B}", Palette.TextDim, 13));
        var headline = new RichTextLabel { BbcodeEnabled = true, FitContent = true, ScrollActive = false };
        headline.AddThemeFontSizeOverride("normal_font_size", 20);
        headline.Text = $"[color=#{Palette.Hex(cA)}]{Ui.Bb(r.A.Faction.Name)} {r.WinA.Value:P1}[/color]   " +
                        $"[color=#{Palette.Hex(Palette.TextDim)}]draw {r.Draw.Value:P1}[/color]   " +
                        $"[color=#{Palette.Hex(cB)}]{r.WinB.Value:P1} {Ui.Bb(r.B.Faction.Name)}[/color]";
        right.AddChild(headline);
        right.AddChild(new WinBar { A = r.WinA, B = r.WinB, ColorA = cA, ColorB = cB, CustomMinimumSize = new Vector2(0, 34) });
        right.AddChild(Ui.Label($"95% confidence: A wins {r.WinA.Low:P1}-{r.WinA.High:P1}, B wins {r.WinB.Low:P1}-{r.WinB.High:P1}.   " +
                                $"Avg VP {r.AvgVpA:0.0} - {r.AvgVpB:0.0}.   Wipeouts {r.Wipeouts.Value:P0}.   Avg rounds {r.AvgRounds:0.0}.",
            Palette.Text, 13, wrap: true));

        // VP margin histogram.
        var hist = r.MarginHistogram();
        if (hist.Count > 0)
        {
            int lo = hist.Keys.Min(), hi = hist.Keys.Max();
            var values = new double[hi - lo + 1];
            var labels = new string[values.Length];
            var colors = new Color[values.Length];
            for (int m = lo; m <= hi; m++)
            {
                values[m - lo] = hist.GetValueOrDefault(m);
                labels[m - lo] = m.ToString("+0;-0;0");
                colors[m - lo] = m > 0 ? cA : m < 0 ? cB : Palette.TextDim;
            }
            right.AddChild(new BarChart
            {
                Title = "VP margin (A minus B), games", Values = values, Labels = labels, Colors = colors,
                CustomMinimumSize = new Vector2(0, 170), ValueFormat = v => v.ToString("0"),
            });
        }

        // Replays.
        var watch = Ui.Row(8, Ui.Label("Watch a game:", size: 14));
        void Add(string label, GameSummary? g)
        {
            if (g == null) return;
            watch.AddChild(Ui.Button(label, () => App.ShowReplay(new LabReplay(r.A, r.B, r.Settings, g.Seed, g.Swapped)),
                tooltip: $"Seed {g.Seed}{(g.Swapped ? " (sides swapped)" : "")}: VP {g.VpA}-{g.VpB}"));
        }
        var done = r.Completed.ToList();
        Add("A win", done.Where(g => g.Winner == 0).OrderBy(g => Math.Abs(g.VpA - g.VpB - (r.AvgVpA - r.AvgVpB))).FirstOrDefault());
        Add("B win", done.Where(g => g.Winner == 1).OrderBy(g => Math.Abs(g.VpA - g.VpB - (r.AvgVpA - r.AvgVpB))).FirstOrDefault());
        Add("Closest game", done.OrderBy(g => Math.Abs(g.VpA - g.VpB)).FirstOrDefault());
        Add("Biggest blowout", done.OrderByDescending(g => Math.Abs(g.VpA - g.VpB)).FirstOrDefault());
        right.AddChild(watch);

        // Units.
        var table = new RichTextLabel { BbcodeEnabled = true, FitContent = true, ScrollActive = false, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        table.AddThemeFontSizeOverride("normal_font_size", 13);
        var sb = new System.Text.StringBuilder("[table=7]");
        foreach (var h in new[] { "Unit", "Pts", "Survived", "Dmg/game", "Dmg/100pts", "Slain/game", "Taken/game" })
            sb.Append($"[cell][color=#{Palette.Hex(Palette.TextDim)}]{h}   [/color][/cell]");
        foreach (var u in r.Units.OrderBy(u => u.Side).ThenByDescending(u => u.DamagePer100Points))
        {
            var c = u.Side == "A" ? cA : cB;
            sb.Append($"[cell][color=#{Palette.Hex(c)}]{u.Side}[/color] {Ui.Bb(u.Name)}   [/cell][cell]{u.Points}[/cell][cell]{u.SurvivalRate:P0}[/cell]" +
                      $"[cell]{u.DamagePerGame:0.0}[/cell][cell]{u.DamagePer100Points:0.0}[/cell]" +
                      $"[cell]{u.ModelsSlain / Math.Max(1, u.Instances):0.0}[/cell][cell]{u.DamageTaken / Math.Max(1, u.Instances):0.0}[/cell]");
        }
        sb.Append("[/table]");
        table.Text = sb.ToString();
        right.AddChild(table);
    }

    // ================================================================== Unit efficiency

    private void BuildEfficiency()
    {
        var faction = App.Factions[L.EfficiencyFaction];
        var controls = Ui.Row(8,
            Ui.Button("<", () => { L.EfficiencyFaction = (L.EfficiencyFaction - 1 + App.Factions.Count) % App.Factions.Count; L.SelectedWarscroll = null; Build(); }, minWidth: 36),
            Ui.Label(faction.Name, Palette.Faction(faction.Color, Palette.Accent), 18),
            Ui.Button(">", () => { L.EfficiencyFaction = (L.EfficiencyFaction + 1) % App.Factions.Count; L.SelectedWarscroll = null; Build(); }, minWidth: 36));
        controls.AddChild(new Control { CustomMinimumSize = new Vector2(30, 0) });
        controls.AddChild(Ui.Label("Target:", size: 14));
        for (int t = 0; t < References.Targets.Count; t++)
        {
            int targetIndex = t;
            controls.AddChild(Ui.Button(References.Targets[t].Name, () => { L.EfficiencyTarget = targetIndex; Build(); }, on: L.EfficiencyTarget == t));
        }
        _body.AddChild(controls);
        _body.AddChild(Ui.Label("Expected damage at full strength with each warscroll's automated abilities, against the chosen target. " +
                                "Toughness = average number of reference attacks needed to destroy the unit. Click a column to sort, a row for its damage distribution.",
            Palette.TextDim, 13, wrap: true));

        var evals = faction.Warscrolls.Where(w => w.Points > 0).Select(w => UnitEfficiency.Evaluate(faction, w)).ToList();
        int t0 = L.EfficiencyTarget;
        var columns = new (string Title, Func<UnitEfficiency, double> Value, string Format)[]
        {
            ("Points", e => e.Warscroll.Points, "0"),
            ("Melee dmg", e => e.Melee[t0], "0.0"),
            ("Ranged dmg", e => e.Ranged[t0], "0.0"),
            ("Melee /100pts", e => e.Per100(e.Melee[t0]), "0.0"),
            ("Total /100pts", e => e.Per100(e.Melee[t0] + e.Ranged[t0]), "0.0"),
            ("Tough (basic)", e => e.AttacksToKill[0], "0"),
            ("Tough (elite)", e => e.AttacksToKill[1], "0"),
            ("Tough /100pts", e => e.Per100(e.AttacksToKill[1]), "0.0"),
        };

        var split = Ui.Row(16);
        split.SizeFlagsVertical = SizeFlags.ExpandFill;
        _body.AddChild(split);

        var tree = new Tree { Columns = columns.Length + 1, ColumnTitlesVisible = true, HideRoot = true, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        tree.SizeFlagsVertical = SizeFlags.ExpandFill;
        tree.SetColumnTitle(0, "Warscroll");
        tree.SetColumnExpand(0, true);
        tree.SetColumnCustomMinimumWidth(0, 220);
        for (int c = 0; c < columns.Length; c++)
        {
            tree.SetColumnTitle(c + 1, columns[c].Title + (L.SortColumn == c ? " v" : ""));
            tree.SetColumnExpand(c + 1, false);
            tree.SetColumnCustomMinimumWidth(c + 1, 92);
        }
        var root = tree.CreateItem();
        var sorted = evals.OrderByDescending(e => columns[Math.Clamp(L.SortColumn, 0, columns.Length - 1)].Value(e)).ToList();
        foreach (var e in sorted)
        {
            var item = tree.CreateItem(root);
            item.SetText(0, e.Warscroll.Name);
            item.SetMetadata(0, e.Warscroll.Id);
            for (int c = 0; c < columns.Length; c++)
            {
                double v = columns[c].Value(e);
                item.SetText(c + 1, double.IsInfinity(v) ? "-" : v.ToString(columns[c].Format));
                item.SetTextAlignment(c + 1, HorizontalAlignment.Right);
            }
            if (e.Warscroll.Id == L.SelectedWarscroll) item.Select(0);
        }
        tree.ColumnTitleClicked += (column, _) =>
        {
            if (column == 0) return;
            L.SortColumn = (int)column - 1;
            Build();
        };
        tree.ItemSelected += () =>
        {
            L.SelectedWarscroll = tree.GetSelected()?.GetMetadata(0).AsString();
            Build();
        };
        split.AddChild(tree);

        // Distribution of the selected unit.
        var detail = Ui.Column(10);
        detail.CustomMinimumSize = new Vector2(420, 0);
        split.AddChild(detail);
        var selected = faction.Warscrolls.FirstOrDefault(w => w.Id == L.SelectedWarscroll);
        if (selected == null)
        {
            detail.AddChild(Ui.Label("Select a warscroll to see how its damage is distributed.", Palette.TextDim, 14, wrap: true));
            return;
        }
        var target = References.Targets[t0];
        detail.AddChild(Ui.Label($"{selected.Name} vs {target.Name}", Palette.Accent, 16, wrap: true));
        foreach (bool melee in new[] { true, false })
        {
            if (!(melee ? selected.MeleeWeapons : selected.RangedWeapons).Any()) continue;
            var dist = UnitEfficiency.Distribution(faction, selected, target, melee);
            int last = Array.FindLastIndex(dist, p => p > 0.001);
            if (last < 0) continue;
            var values = dist.Take(last + 1).ToArray();
            detail.AddChild(new BarChart
            {
                Title = $"{(melee ? "Melee" : "Shooting")}: damage per activation (mean {CombatMath.Mean(dist):0.0}, " +
                        $"P(>= {Math.Max(1, (int)Math.Round(CombatMath.Mean(dist)))}) {CombatMath.AtLeast(dist, Math.Max(1, (int)Math.Round(CombatMath.Mean(dist)))):P0})",
                Values = values, Labels = Enumerable.Range(0, values.Length).Select(i => i.ToString()).ToArray(),
                BarColor = melee ? Palette.Accent : Palette.Good, ValueFormat = v => v.ToString("P0"),
                CustomMinimumSize = new Vector2(0, 190),
            });
        }
        var scripted = selected.Abilities.Where(a => a.IsScripted).Select(a => a.Name).ToList();
        detail.AddChild(Ui.Label(scripted.Count > 0 ? "Automated abilities included: " + string.Join(", ", scripted)
                                                    : "No automated abilities on this warscroll.", Palette.TextDim, 13, wrap: true));
    }
}
