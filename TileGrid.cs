using Godot;

public enum TileType : byte
{
    Land,    // tierra con hierba
    Water,   // agua interior (río o estanque)
    Sea,     // mar: rodea la isla, no se puede modificar
    Beach,   // tierra con arena de playa
    Spring   // manantial: agua que alimenta ríos, no se puede modificar
}

/// <summary>
/// Lo que el juego GUARDA del terreno: una cuadrícula de casillas de 1 m,
/// cada una con un nivel entero y un tipo (tierra, agua, mar, playa, manantial).
/// Es la fuente de verdad para biomas, pesca, animales, guardado y cooperativo.
/// La forma curva que se ve en pantalla se calcula a partir de aquí (TileTerrainBuilder).
/// </summary>
public class TileGrid
{
    public int Width { get; }
    public int Depth { get; }
    public int MaxLevel { get; }

    private readonly byte[] _levels;
    private readonly TileType[] _types;

    public TileGrid(int width, int depth, int maxLevel, int initialLevel)
    {
        Width = width;
        Depth = depth;
        MaxLevel = maxLevel;
        _levels = new byte[width * depth];
        _types = new TileType[width * depth];
        for (int i = 0; i < _levels.Length; i++) _levels[i] = (byte)initialLevel;
    }

    public bool IsInside(int x, int z) => x >= 0 && x < Width && z >= 0 && z < Depth;
    public bool IsInside(Vector2I t) => IsInside(t.X, t.Y);

    private int Index(int x, int z) =>
        System.Math.Clamp(z, 0, Depth - 1) * Width + System.Math.Clamp(x, 0, Width - 1);

    // Las lecturas fuera del mapa devuelven la casilla del borde más cercana
    public int GetLevel(int x, int z) => _levels[Index(x, z)];
    public TileType GetTileType(int x, int z) => _types[Index(x, z)];

    /// <summary>Cualquier agua: río, estanque, manantial o mar.</summary>
    public bool IsWater(int x, int z) => GetTileType(x, z) is TileType.Water or TileType.Sea or TileType.Spring;
    /// <summary>Agua de interior (la que se rige por las reglas de ríos y estanques).</summary>
    public bool IsInlandWater(int x, int z) => GetTileType(x, z) is TileType.Water or TileType.Spring;
    public bool IsSea(int x, int z) => GetTileType(x, z) == TileType.Sea;
    public bool IsSpring(int x, int z) => GetTileType(x, z) == TileType.Spring;
    public bool IsBeach(int x, int z) => GetTileType(x, z) == TileType.Beach;

    public int GetLevel(Vector2I t) => GetLevel(t.X, t.Y);
    public bool IsWater(Vector2I t) => IsWater(t.X, t.Y);
    public bool IsInlandWater(Vector2I t) => IsInlandWater(t.X, t.Y);
    public bool IsSea(Vector2I t) => IsSea(t.X, t.Y);
    public bool IsSpring(Vector2I t) => IsSpring(t.X, t.Y);
    public bool IsBeach(Vector2I t) => IsBeach(t.X, t.Y);

    public void SetLevel(int x, int z, int level)
    {
        if (IsInside(x, z)) _levels[Index(x, z)] = (byte)System.Math.Clamp(level, 0, MaxLevel);
    }

    public void SetTileType(int x, int z, TileType type)
    {
        if (IsInside(x, z)) _types[Index(x, z)] = type;
    }

    public void SetLevel(Vector2I t, int level) => SetLevel(t.X, t.Y, level);
    public void SetTileType(Vector2I t, TileType type) => SetTileType(t.X, t.Y, type);
}
