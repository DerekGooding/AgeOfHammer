using Godot;

namespace AgeOfHammer.Client;

/// <summary>A simple bar chart drawn in a Control: one bar per value, optional per-bar colours and x labels.</summary>
public partial class BarChart : Control
{
    public string Title { get; set; } = "";
    public double[] Values { get; set; } = [];
    public string[] Labels { get; set; } = [];
    public Color[]? Colors { get; set; }
    public Color BarColor { get; set; } = Palette.Accent;
    /// <summary>Formats the value shown above the tallest bars (null = hidden).</summary>
    public Func<double, string>? ValueFormat { get; set; }
    /// <summary>Show only every Nth x label (0 = automatic).</summary>
    public int LabelEvery { get; set; }

    public override void _Draw()
    {
        var font = ThemeDB.FallbackFont;
        const int fs = 12;
        var size = Size;
        DrawRect(new Rect2(Vector2.Zero, size), Palette.Background);
        DrawString(font, new Vector2(8, 16), Title, HorizontalAlignment.Left, -1, 13, Palette.TextDim);
        if (Values.Length == 0) return;

        float left = 8, right = size.X - 8, top = 40, bottom = size.Y - 20;
        double max = Values.Max();
        if (max <= 0) max = 1;
        float slot = (right - left) / Values.Length;
        float bar = Math.Max(1, slot * 0.8f);
        int every = LabelEvery > 0 ? LabelEvery : Math.Max(1, (int)Math.Ceiling(Values.Length / ((right - left) / 34f)));
        for (int i = 0; i < Values.Length; i++)
        {
            float h = (float)(Values[i] / max) * (bottom - top);
            float x = left + i * slot + (slot - bar) / 2;
            var color = Colors != null && i < Colors.Length ? Colors[i] : BarColor;
            DrawRect(new Rect2(x, bottom - h, bar, h), color);
            if (ValueFormat != null && Values[i] >= max * 0.6 && Values.Length <= 30)
                DrawString(font, new Vector2(x - 4, bottom - h - 3), ValueFormat(Values[i]), HorizontalAlignment.Left, -1, fs - 1, Palette.Text);
            if (i < Labels.Length && i % every == 0)
                DrawString(font, new Vector2(x - 2, size.Y - 5), Labels[i], HorizontalAlignment.Left, -1, fs, Palette.TextDim);
        }
        DrawLine(new Vector2(left, bottom), new Vector2(right, bottom), Palette.Border, 1);
    }
}

/// <summary>A horizontal win/draw/loss bar with 95% confidence whiskers for both sides.</summary>
public partial class WinBar : Control
{
    public AgeOfHammer.Core.Lab.Rate A { get; set; }
    public AgeOfHammer.Core.Lab.Rate B { get; set; }
    public Color ColorA { get; set; } = Palette.Accent;
    public Color ColorB { get; set; } = Palette.Good;

    public override void _Draw()
    {
        var w = Size.X;
        float h = Size.Y - 10;
        float a = (float)A.Value * w, b = (float)B.Value * w;
        DrawRect(new Rect2(0, 0, w, h), Palette.PanelLight);
        DrawRect(new Rect2(0, 0, a, h), ColorA);
        DrawRect(new Rect2(w - b, 0, b, h), ColorB);
        // Whiskers: A's interval from the left, B's from the right.
        DrawLine(new Vector2((float)A.Low * w, h + 4), new Vector2((float)A.High * w, h + 4), ColorA, 3);
        DrawLine(new Vector2(w - (float)B.High * w, h + 8), new Vector2(w - (float)B.Low * w, h + 8), ColorB, 3);
        DrawLine(new Vector2(w / 2, 0), new Vector2(w / 2, h), new Color(1, 1, 1, 0.5f), 1);
    }
}
