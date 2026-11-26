using AgeOfHammer.Game.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace AgeOfHammer.Game.UI;

public static class Theme
{
    public static readonly Color Background = new(18, 20, 24);
    public static readonly Color Panel = new(30, 33, 40);
    public static readonly Color PanelLight = new(44, 48, 58);
    public static readonly Color Border = new(70, 76, 90);
    public static readonly Color Text = new(225, 228, 235);
    public static readonly Color TextDim = new(150, 156, 170);
    public static readonly Color Accent = new(230, 180, 60);
    public static readonly Color Good = new(90, 200, 110);
    public static readonly Color Bad = new(225, 80, 70);
    public static readonly Color Button = new(56, 62, 76);
    public static readonly Color ButtonHover = new(76, 84, 102);
    public static readonly Color ButtonOn = new(120, 96, 40);
}

/// <summary>
/// Tiny immediate-mode UI. Input is sampled once per frame in <see cref="Update"/>; widgets are drawn and
/// clicked during Draw (the game runs with a variable timestep so Update and Draw are 1:1).
/// </summary>
public sealed class Ui
{
    private readonly Painter _p;
    private MouseState _mouse, _prevMouse;
    private KeyboardState _keys, _prevKeys;

    public Ui(Painter painter) => _p = painter;

    public Point Mouse => _mouse.Position;
    public bool LeftClicked { get; private set; }
    public bool RightDown => _mouse.RightButton == ButtonState.Pressed;
    public bool RightClicked => _mouse.RightButton == ButtonState.Pressed && _prevMouse.RightButton == ButtonState.Released;
    public Point MouseDelta => _mouse.Position - _prevMouse.Position;
    public int WheelDelta => _mouse.ScrollWheelValue - _prevMouse.ScrollWheelValue;
    /// <summary>Set when a widget used this frame's click, so the board ignores it.</summary>
    public bool ClickConsumed { get; set; }

    public void Update()
    {
        _prevMouse = _mouse;
        _prevKeys = _keys;
        _mouse = Microsoft.Xna.Framework.Input.Mouse.GetState();
        _keys = Keyboard.GetState();
        LeftClicked = _mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released;
        ClickConsumed = false;
    }

    public bool KeyPressed(Keys k) => _keys.IsKeyDown(k) && _prevKeys.IsKeyUp(k);
    public bool KeyDown(Keys k) => _keys.IsKeyDown(k);

    public bool Hover(Rectangle r) => r.Contains(_mouse.Position);

    public bool Button(Rectangle r, string text, bool enabled = true, bool on = false, string? tooltip = null)
    {
        bool hover = enabled && Hover(r);
        var bg = !enabled ? Theme.Panel : on ? Theme.ButtonOn : hover ? Theme.ButtonHover : Theme.Button;
        _p.Rect(r, bg);
        _p.RectOutline(r, enabled ? Theme.Border : Theme.PanelLight);
        var size = _p.Measure(text);
        _p.Text(text, new Vector2(r.X + (r.Width - size.X) / 2, r.Y + (r.Height - size.Y) / 2), enabled ? Theme.Text : Theme.TextDim);
        if (hover && tooltip != null) _pendingTooltip = tooltip;
        if (hover && LeftClicked && !ClickConsumed)
        {
            ClickConsumed = true;
            return true;
        }
        return false;
    }

    private string? _pendingTooltip;

    /// <summary>Shows a tooltip when the mouse is over the given area.</summary>
    public void TooltipArea(Rectangle r, string text)
    {
        if (Hover(r)) _pendingTooltip = text;
    }

    /// <summary>Draws the tooltip of the hovered button (call last).</summary>
    public void DrawTooltip(int maxWidth = 320)
    {
        if (_pendingTooltip == null) return;
        var lines = _p.Wrap(_pendingTooltip, maxWidth);
        int h = lines.Count * 18 + 8;
        int w = (int)lines.Max(l => _p.Measure(l).X) + 12;
        var pos = new Point(Math.Max(4, Mouse.X - w - 10), Mouse.Y + 12);
        var r = new Rectangle(pos.X, pos.Y, w, h);
        _p.Rect(r, new Color(10, 10, 14) * 0.95f);
        _p.RectOutline(r, Theme.Accent);
        for (int i = 0; i < lines.Count; i++) _p.Text(lines[i], new Vector2(r.X + 6, r.Y + 4 + i * 18), Theme.Text);
        _pendingTooltip = null;
    }
}
