using System.Numerics;
using System.Text.Json.Serialization;
using AgeOfHammer.Core.Geometry;

namespace AgeOfHammer.Core.Data;

public sealed class RectDef
{
    public float X { get; set; }
    public float Y { get; set; }
    public float W { get; set; }
    public float H { get; set; }
    public RectF ToRect() => new(X, Y, W, H);
}

public sealed class PointDef
{
    public float X { get; set; }
    public float Y { get; set; }
    public Vector2 ToVector() => new(X, Y);
}

public sealed class TerrainDef
{
    public string Name { get; set; } = "Ruins";
    public RectDef Area { get; set; } = new();
    /// <summary>Impassable terrain blocks non-FLY movement.</summary>
    public bool Impassable { get; set; } = true;
    /// <summary>Obscuring terrain blocks visibility through it.</summary>
    public bool Obscuring { get; set; } = true;
}

/// <summary>Battlefield layout and scoring rules.</summary>
public sealed class Battleplan
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public float Width { get; set; } = 60;
    public float Height { get; set; } = 44;
    public int BattleRounds { get; set; } = 5;
    /// <summary>Territory for player 0 and player 1.</summary>
    public List<RectDef> Territories { get; set; } = new();
    public List<PointDef> Objectives { get; set; } = new();
    public List<TerrainDef> Terrain { get; set; } = new();
    /// <summary>VP at end of your turn for controlling at least one objective.</summary>
    public int VpForOne { get; set; } = 1;
    /// <summary>Additional VP at end of your turn for controlling two or more objectives.</summary>
    public int VpForTwoPlus { get; set; } = 1;
    /// <summary>Additional VP at end of your turn for controlling more objectives than your opponent.</summary>
    public int VpForMoreThanOpponent { get; set; } = 1;

    [JsonIgnore] public RectF Bounds => new(0, 0, Width, Height);
}
