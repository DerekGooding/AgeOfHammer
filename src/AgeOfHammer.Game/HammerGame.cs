using AgeOfHammer.Core.Data;
using AgeOfHammer.Game.Rendering;
using AgeOfHammer.Game.Screens;
using AgeOfHammer.Game.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace AgeOfHammer.Game;

public interface IScreen
{
    void Update(GameTime time);
    void Draw(GameTime time);
}

/// <summary>Choices made on the menu before a battle starts.</summary>
public sealed class MatchSetup
{
    public int[] FactionIndex { get; } = [0, 1];
    public bool[] IsAi { get; } = [false, true];
    public List<string>[] Armies { get; } = [new(), new()];
    public int BattleplanIndex { get; set; }
}

public sealed class HammerGame : Microsoft.Xna.Framework.Game
{
    private readonly GraphicsDeviceManager _graphics;
    private IScreen? _screen;

    public Painter Painter { get; private set; } = null!;
    public Ui Ui { get; private set; } = null!;
    public List<FactionData> Factions { get; private set; } = new();
    public List<Battleplan> Battleplans { get; private set; } = new();
    public MatchSetup Setup { get; } = new();
    public string? LoadError { get; private set; }

    private readonly bool _watch;

    public HammerGame(string[]? args = null)
    {
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 1600,
            PreferredBackBufferHeight = 900,
            SynchronizeWithVerticalRetrace = true,
            PreferMultiSampling = true,
        };
        _watch = args?.Contains("--watch") == true;
        if (args != null) ParseScreenshotArgs(args);
        Content.RootDirectory = "Content";
        IsMouseVisible = true;
        IsFixedTimeStep = false; // keeps Update and Draw 1:1 for the immediate-mode UI
        Window.AllowUserResizing = true;
        Window.Title = "Age of Hammer - Age of Sigmar 4th Edition Simulator";
    }

    public int Width => GraphicsDevice.Viewport.Width;
    public int Height => GraphicsDevice.Viewport.Height;

    protected override void LoadContent()
    {
        Painter = new Painter(GraphicsDevice, Content.Load<SpriteFont>("Fonts/Default"), Content.Load<SpriteFont>("Fonts/Title"));
        Ui = new Ui(Painter);
        try
        {
            var dir = DataLoader.FindDataDirectory();
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
        if (_watch && LoadError == null)
        {
            Setup.IsAi[0] = Setup.IsAi[1] = true;
            Show(new BattleScreen(this));
        }
        else Show(new MenuScreen(this));
    }

    public void ResetArmy(int player)
    {
        Setup.Armies[player].Clear();
        Setup.Armies[player].AddRange(Factions[Setup.FactionIndex[player]].DefaultArmy);
    }

    public void Show(IScreen screen) => _screen = screen;

    protected override void Update(GameTime gameTime)
    {
        Ui.Update();
        _screen?.Update(gameTime);
        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(Theme.Background);
        _screen?.Draw(gameTime);
        base.Draw(gameTime);
        if (_screenshotPath != null && gameTime.TotalGameTime.TotalSeconds >= _screenshotAfter) SaveScreenshot();
    }

    // Dev aid: "--screenshot <file.png> [--after <seconds>]" saves the frame and exits.
    private string? _screenshotPath;
    private double _screenshotAfter = 5;

    private void ParseScreenshotArgs(string[] args)
    {
        int i = Array.IndexOf(args, "--screenshot");
        if (i >= 0 && i + 1 < args.Length) _screenshotPath = args[i + 1];
        int a = Array.IndexOf(args, "--after");
        if (a >= 0 && a + 1 < args.Length && double.TryParse(args[a + 1], System.Globalization.CultureInfo.InvariantCulture, out var s))
            _screenshotAfter = s;
    }

    private void SaveScreenshot()
    {
        int w = GraphicsDevice.PresentationParameters.BackBufferWidth;
        int h = GraphicsDevice.PresentationParameters.BackBufferHeight;
        var data = new Color[w * h];
        GraphicsDevice.GetBackBufferData(data);
        using var tex = new Texture2D(GraphicsDevice, w, h);
        tex.SetData(data);
        using (var stream = File.Create(_screenshotPath!)) tex.SaveAsPng(stream, w, h);
        _screenshotPath = null;
        Exit();
    }
}
