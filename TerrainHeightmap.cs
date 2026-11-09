/// <summary>
/// Alturas de RENDER del terreno: una por vértice en una cuadrícula regular (más fina que las casillas).
/// No es la fuente de verdad: la calcula TileTerrainBuilder a partir de TileGrid.
/// El formato (fila a fila, z * Size + x) es el mismo que usa HeightMapShape3D para la colisión.
/// </summary>
public class TerrainHeightmap
{
    public int Size { get; }       // vértices por lado
    public float Spacing { get; }  // metros entre vértices

    private readonly float[] _h;
    public float[] Data => _h;

    public TerrainHeightmap(int size, float spacing)
    {
        Size = size;
        Spacing = spacing;
        _h = new float[size * size];
    }

    public float Get(int x, int z) => _h[z * Size + x];

    /// <summary>Altura con los bordes repetidos (para calcular normales en el borde del mapa).</summary>
    public float GetClamped(int x, int z) =>
        _h[System.Math.Clamp(z, 0, Size - 1) * Size + System.Math.Clamp(x, 0, Size - 1)];
}
