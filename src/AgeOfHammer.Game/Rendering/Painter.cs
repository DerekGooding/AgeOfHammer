using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using NVec = System.Numerics.Vector2;

namespace AgeOfHammer.Game.Rendering;

public static class VecExtensions
{
    public static Vector2 X(this NVec v) => new(v.X, v.Y);
    public static NVec N(this Vector2 v) => new(v.X, v.Y);
}

/// <summary>Simple shape and text drawing on top of SpriteBatch using generated textures.</summary>
public sealed class Painter
{
    private const int CircleSize = 256;
    public SpriteBatch Batch { get; }
    public SpriteFont Font { get; }
    public SpriteFont Title { get; }
    public Texture2D Pixel { get; }
    public Texture2D Circle { get; }
    public GraphicsDevice Device { get; }

    public Painter(GraphicsDevice device, SpriteFont font, SpriteFont title)
    {
        Device = device;
        Batch = new SpriteBatch(device);
        Font = font;
        Title = title;
        Pixel = new Texture2D(device, 1, 1);
        Pixel.SetData([Color.White]);
        Circle = MakeCircle(device, CircleSize);
    }

    private static Texture2D MakeCircle(GraphicsDevice device, int size)
    {
        var tex = new Texture2D(device, size, size);
        var data = new Color[size * size];
        float r = size / 2f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = MathF.Sqrt((x + 0.5f - r) * (x + 0.5f - r) + (y + 0.5f - r) * (y + 0.5f - r));
                float a = Math.Clamp(r - d, 0f, 1f); // 1px anti-aliased edge
                data[y * size + x] = Color.White * a;
            }
        tex.SetData(data);
        return tex;
    }

    public void Rect(Vector2 pos, Vector2 size, Color color) =>
        Batch.Draw(Pixel, pos, null, color, 0, Vector2.Zero, size, SpriteEffects.None, 0);

    public void Rect(Rectangle r, Color color) => Batch.Draw(Pixel, r, color);

    public void RectOutline(Rectangle r, Color color, int t = 1)
    {
        Rect(new Rectangle(r.X, r.Y, r.Width, t), color);
        Rect(new Rectangle(r.X, r.Bottom - t, r.Width, t), color);
        Rect(new Rectangle(r.X, r.Y, t, r.Height), color);
        Rect(new Rectangle(r.Right - t, r.Y, t, r.Height), color);
    }

    public void RectOutline(Vector2 pos, Vector2 size, Color color, float t)
    {
        Rect(pos, new Vector2(size.X, t), color);
        Rect(pos + new Vector2(0, size.Y - t), new Vector2(size.X, t), color);
        Rect(pos, new Vector2(t, size.Y), color);
        Rect(pos + new Vector2(size.X - t, 0), new Vector2(t, size.Y), color);
    }

    public void Line(Vector2 a, Vector2 b, Color color, float thickness)
    {
        var d = b - a;
        float len = d.Length();
        if (len < 1e-4f) return;
        float angle = MathF.Atan2(d.Y, d.X);
        Batch.Draw(Pixel, a, null, color, angle, new Vector2(0, 0.5f), new Vector2(len, thickness), SpriteEffects.None, 0);
    }

    public void Disc(Vector2 center, float radius, Color color) =>
        Batch.Draw(Circle, center, null, color, 0, new Vector2(CircleSize / 2f), radius * 2 / CircleSize, SpriteEffects.None, 0);

    public void Ring(Vector2 center, float radius, Color color, float thickness, int segments = 64)
    {
        var prev = center + new Vector2(radius, 0);
        for (int i = 1; i <= segments; i++)
        {
            float a = i * MathF.Tau / segments;
            var next = center + new Vector2(MathF.Cos(a), MathF.Sin(a)) * radius;
            Line(prev, next, color, thickness);
            prev = next;
        }
    }

    public void Text(string text, Vector2 pos, Color color, float scale = 1f) =>
        Batch.DrawString(Font, text, new Vector2(MathF.Round(pos.X), MathF.Round(pos.Y)), color, 0, Vector2.Zero, scale, SpriteEffects.None, 0);

    public void TitleText(string text, Vector2 pos, Color color) =>
        Batch.DrawString(Title, text, new Vector2(MathF.Round(pos.X), MathF.Round(pos.Y)), color);

    public void TextCentered(string text, Vector2 center, Color color, float scale = 1f)
    {
        var size = Font.MeasureString(text) * scale;
        Text(text, center - size / 2, color, scale);
    }

    public Vector2 Measure(string text) => Font.MeasureString(text);

    /// <summary>Word-wraps text to a pixel width.</summary>
    public List<string> Wrap(string text, float width)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Split('\n'))
        {
            var line = "";
            foreach (var word in paragraph.Split(' '))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (Font.MeasureString(candidate).X > width && line.Length > 0)
                {
                    lines.Add(line);
                    line = word;
                }
                else line = candidate;
            }
            lines.Add(line);
        }
        return lines;
    }

    public static Color ParseHex(string hex, Color fallback)
    {
        hex = hex.TrimStart('#');
        if (hex.Length != 6) return fallback;
        try
        {
            return new Color(Convert.ToInt32(hex[..2], 16), Convert.ToInt32(hex[2..4], 16), Convert.ToInt32(hex[4..], 16));
        }
        catch (FormatException) { return fallback; }
    }
}
