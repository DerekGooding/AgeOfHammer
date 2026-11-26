using Microsoft.Xna.Framework;

namespace AgeOfHammer.Game.Rendering;

/// <summary>Maps battlefield inches to screen pixels. Position is the world point at the centre of the view.</summary>
public sealed class Camera
{
    public Vector2 Position { get; set; }
    /// <summary>Pixels per inch.</summary>
    public float Zoom { get; set; } = 15f;
    public Rectangle View { get; set; }

    public Vector2 ViewCenter => new(View.X + View.Width / 2f, View.Y + View.Height / 2f);

    public Matrix Transform =>
        Matrix.CreateTranslation(-Position.X, -Position.Y, 0) * Matrix.CreateScale(Zoom, Zoom, 1) *
        Matrix.CreateTranslation(ViewCenter.X, ViewCenter.Y, 0);

    public Vector2 WorldToScreen(Vector2 w) => (w - Position) * Zoom + ViewCenter;
    public Vector2 ScreenToWorld(Vector2 s) => (s - ViewCenter) / Zoom + Position;

    public void Fit(float width, float height)
    {
        Position = new Vector2(width / 2, height / 2);
        Zoom = MathF.Min(View.Width / (width + 2), View.Height / (height + 2));
    }

    /// <summary>Zooms keeping the world point under the cursor fixed.</summary>
    public void ZoomAt(Vector2 screen, float factor)
    {
        var before = ScreenToWorld(screen);
        Zoom = Math.Clamp(Zoom * factor, 4f, 80f);
        var after = ScreenToWorld(screen);
        Position += before - after;
    }
}
