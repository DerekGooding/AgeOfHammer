using Godot;

namespace AgeOfHammer.Client;

public static class Palette
{
    public static readonly Color Background = Color.Color8(18, 20, 24);
    public static readonly Color Panel = Color.Color8(30, 33, 40);
    public static readonly Color PanelLight = Color.Color8(44, 48, 58);
    public static readonly Color Border = Color.Color8(70, 76, 90);
    public static readonly Color Text = Color.Color8(225, 228, 235);
    public static readonly Color TextDim = Color.Color8(150, 156, 170);
    public static readonly Color Accent = Color.Color8(230, 180, 60);
    public static readonly Color Good = Color.Color8(90, 200, 110);
    public static readonly Color Bad = Color.Color8(225, 80, 70);
    public static readonly Color Board = Color.Color8(46, 66, 44);
    public static readonly Color Terrain = Color.Color8(92, 82, 70);

    public static Color Faction(string hex, Color fallback) => Color.HtmlIsValid(hex) ? Color.FromHtml(hex) : fallback;

    public static string Hex(Color c) => c.ToHtml(false);
}

/// <summary>Helpers for building Godot UI in code.</summary>
public static class Ui
{
    public static StyleBoxFlat Box(Color bg, Color? border = null, int margin = 10)
    {
        var sb = new StyleBoxFlat { BgColor = bg };
        if (border is Color b)
        {
            sb.BorderColor = b;
            sb.SetBorderWidthAll(1);
        }
        sb.SetContentMarginAll(margin);
        sb.SetCornerRadiusAll(3);
        return sb;
    }

    public static PanelContainer Panel(Color? bg = null, int margin = 10)
    {
        var p = new PanelContainer();
        p.AddThemeStyleboxOverride("panel", Box(bg ?? Palette.Panel, Palette.Border, margin));
        return p;
    }

    public static Label Label(string text, Color? color = null, int size = 15, bool wrap = false)
    {
        var l = new Label { Text = text };
        l.AddThemeColorOverride("font_color", color ?? Palette.Text);
        l.AddThemeFontSizeOverride("font_size", size);
        if (wrap)
        {
            l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            l.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        }
        return l;
    }

    public static Button Button(string text, Action onPressed, bool enabled = true, bool on = false, string? tooltip = null, int minWidth = 0)
    {
        var b = new Button
        {
            Text = text,
            Disabled = !enabled,
            FocusMode = Control.FocusModeEnum.None, // keep Enter/Space for game shortcuts
            CustomMinimumSize = new Vector2(minWidth, 30),
            TooltipText = tooltip ?? "",
        };
        b.AddThemeFontSizeOverride("font_size", 15);
        if (on)
        {
            b.AddThemeStyleboxOverride("normal", Box(Color.Color8(120, 96, 40), Palette.Accent, 6));
            b.AddThemeStyleboxOverride("hover", Box(Color.Color8(140, 112, 48), Palette.Accent, 6));
        }
        b.Pressed += onPressed;
        return b;
    }

    public static void Clear(Node node)
    {
        foreach (var child in node.GetChildren())
        {
            node.RemoveChild(child);
            child.QueueFree();
        }
    }

    public static HBoxContainer Row(int separation = 8, params Control[] children)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", separation);
        foreach (var c in children) row.AddChild(c);
        return row;
    }

    public static VBoxContainer Column(int separation = 6)
    {
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", separation);
        return col;
    }

    /// <summary>Escapes text for a BBCode RichTextLabel.</summary>
    public static string Bb(string text) => text.Replace("[", "[lb]");
}
