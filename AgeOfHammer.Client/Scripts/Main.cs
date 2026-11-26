using System.Globalization;
using AgeOfHammer.Core.Data;
using Godot;

namespace AgeOfHammer.Client;

/// <summary>Choices made on the menu before a battle starts.</summary>
public sealed class MatchSetup
{
    public int[] FactionIndex { get; } = [0, 1];
    public bool[] IsAi { get; } = [false, true];
    public List<string>[] Armies { get; } = [new(), new()];
    public int BattleplanIndex { get; set; }
}

/// <summary>
/// Root node: loads game data and swaps between screens.
/// Command-line options (after "--"):
///   --watch                         start an AI vs AI battle with the default armies
///   --army-screen                   open player 1's army editor (for screenshots)
///   --smoke                         play an AI vs AI game instantly, print the result and quit (exit code 0 = ok)
///   --screenshot &lt;file.png&gt; [--after &lt;seconds&gt;]   save the window to a PNG and quit
/// </summary>
public partial class Main : Node
{
    public List<FactionData> Factions { get; private set; } = new();
    public List<Battleplan> Battleplans { get; private set; } = new();
    public MatchSetup Setup { get; } = new();
    public string? LoadError { get; private set; }
    public bool SmokeTest { get; private set; }

    private Node? _screen;
    private string? _screenshotPath;
    private double _screenshotAfter = 5;
    private double _elapsed;

    public override void _Ready()
    {
        DisplayServer.WindowSetTitle("Age of Hammer - Age of Sigmar 4th Edition Simulator");
        RenderingServer.SetDefaultClearColor(Palette.Background);

        try
        {
            var dir = DataLoader.FindDataDirectory(ProjectSettings.GlobalizePath("res://"));
            Factions = DataLoader.LoadAllFactions(dir);
            Battleplans = DataLoader.LoadAllBattleplans(dir);
            if (Factions.Count == 0 || Battleplans.Count == 0) LoadError = "No factions or battleplans found in the data folder.";
        }
        catch (Exception e)
        {
            LoadError = e.Message;
        }

        if (LoadError == null)
        {
            int sce = Factions.FindIndex(f => f.Id == "stormcast-eternals");
            int skv = Factions.FindIndex(f => f.Id == "skaven");
            Setup.FactionIndex[0] = Math.Max(0, sce);
            Setup.FactionIndex[1] = skv >= 0 ? skv : (Factions.Count > 1 ? 1 : 0);
            for (int p = 0; p < 2; p++) ResetArmy(p);
        }

        // Accept our flags after "--" (Godot's convention) or mixed in with engine args, because
        // PowerShell strips a bare "--" when calling tools/godot.ps1.
        var args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        int shot = Array.IndexOf(args, "--screenshot");
        if (shot >= 0 && shot + 1 < args.Length) _screenshotPath = args[shot + 1];
        int after = Array.IndexOf(args, "--after");
        if (after >= 0 && after + 1 < args.Length && double.TryParse(args[after + 1], CultureInfo.InvariantCulture, out var s))
            _screenshotAfter = s;
        SmokeTest = args.Contains("--smoke");

        if (LoadError != null && SmokeTest)
        {
            GD.PrintErr("Data failed to load: " + LoadError);
            GetTree().Quit(1);
            return;
        }

        if (LoadError == null && args.Contains("--army-screen"))
            ShowArmy(0);
        else if (LoadError == null && (args.Contains("--watch") || SmokeTest))
        {
            Setup.IsAi[0] = Setup.IsAi[1] = true;
            ShowBattle();
        }
        else ShowMenu();
    }

    public void ResetArmy(int player)
    {
        Setup.Armies[player].Clear();
        Setup.Armies[player].AddRange(Factions[Setup.FactionIndex[player]].DefaultArmy);
    }

    public void ShowMenu() => Show(new MenuScreen { App = this });
    public void ShowArmy(int player) => Show(new ArmyScreen { App = this, Player = player });
    public void ShowBattle() => Show(new BattleScreen { App = this });

    private void Show(Node screen)
    {
        if (_screen != null)
        {
            RemoveChild(_screen);
            _screen.QueueFree();
        }
        _screen = screen;
        AddChild(screen);
    }

    public override void _Process(double delta)
    {
        _elapsed += delta;
        if (_screenshotPath != null && _elapsed >= _screenshotAfter)
        {
            var image = GetViewport().GetTexture().GetImage();
            image.SavePng(_screenshotPath);
            GD.Print($"Saved screenshot to {_screenshotPath}");
            _screenshotPath = null;
            GetTree().Quit();
        }
    }
}
