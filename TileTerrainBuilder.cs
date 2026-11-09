using Godot;

/// <summary>
/// Convierte las casillas (TileGrid) en la forma que se ve:
///   - Alturas del heightmap con acantilados de borde redondeado y orillas en ladera.
///   - Un color por vértice con el "material" para el shader (B = colina de orilla).
///   - Mallas de agua (superficie plana por nivel) y de cascadas.
///
/// Cómo salen las curvas: para cada nivel se calcula, en cada vértice, la DISTANCIA CON SIGNO
/// al borde de la zona de casillas que alcanzan ese nivel (positiva dentro, negativa fuera).
/// Esa distancia se suaviza un poco y se pasa por un perfil de acantilado. Las esquinas
/// quedan redondeadas y, a diferencia de un simple difuminado, una sola casilla no desaparece.
/// </summary>
public class TileTerrainBuilder
{
    public const float LevelHeight = 1.0f;        // altura de cada nivel (m): algo menos que el personaje
    public const float WaterSurfaceDepth = 0.5f;  // la superficie del agua queda por debajo del suelo
    public const float BedDepth = 1.0f;           // profundidad del cauce bajo el suelo
    // Perfil del acantilado respecto al borde de la casilla (m): empieza a bajar un poco DENTRO
    // de la casilla y termina FUERA. Así incluso una casilla suelta tiene la parte de arriba plana.
    private const float CliffInner = 0.05f;
    private const float CliffOuter = 0.3f;
    // Orilla del agua: pared empinada de colina, más un pequeño reborde de colina por fuera
    private const float BankInner = 0.15f;
    private const float BankOuter = 0.2f;
    private const float BankRim = 0.12f;           // franja de colina plana entre la hierba y la pared
    // Mar: la playa entra en el agua con una pendiente suave de arena (2 m de ancho)
    private const float SeaShoreOuter = 1.0f;
    private const float SeaShoreInner = 1.0f;
    private const float SeaDepth = 1.0f;
    private const float SmoothRadiusMeters = 0.25f; // suavizado de la distancia: redondea esquinas
    private const int SearchRadius = 2;           // casillas a buscar alrededor para la distancia

    private int SmoothSteps => System.Math.Max(1, Mathf.RoundToInt(SmoothRadiusMeters / _heightmap.Spacing));

    public static float GroundHeight(int level) => (level - 1) * LevelHeight;
    public static float WaterHeight(int level) => GroundHeight(level) - WaterSurfaceDepth;

    private readonly TileGrid _tiles;
    private readonly TerrainHeightmap _heightmap;
    private readonly Color[] _colors;

    // Distancias con signo sin suavizar, por vértice. Campos 0..MaxLevel-1 = niveles 1..MaxLevel;
    // después, el agua interior y el mar.
    private readonly float[][] _sdf;
    private int WaterField => _tiles.MaxLevel;
    private int SeaField => _tiles.MaxLevel + 1;

    // Forma de cada casilla en cada campo: fuera, dentro, o MEDIA casilla en diagonal.
    // Las medias casillas convierten las escaleras de casillas en bordes a 45°.
    private const byte Out = 0, In = 1, HalfNW = 2, HalfNE = 3, HalfSW = 4, HalfSE = 5;
    private readonly byte[][] _shape;

    public TileTerrainBuilder(TileGrid tiles, TerrainHeightmap heightmap, Color[] colors)
    {
        _tiles = tiles;
        _heightmap = heightmap;
        _colors = colors;
        _sdf = new float[tiles.MaxLevel + 2][];
        _shape = new byte[tiles.MaxLevel + 2][];
        for (int f = 0; f < _sdf.Length; f++)
        {
            _sdf[f] = new float[heightmap.Size * heightmap.Size];
            _shape[f] = new byte[tiles.Width * tiles.Depth];
        }
    }

    public Rect2I RebuildAll() => RebuildRegion(new Rect2I(0, 0, _tiles.Width, _tiles.Depth));

    /// <summary>
    /// Recalcula alturas y colores de los vértices afectados por un cambio en 'tileRect'.
    /// Devuelve la zona de VÉRTICES que cambió (para redibujar solo esos trozos).
    /// </summary>
    public Rect2I RebuildRegion(Rect2I tileRect)
    {
        float sp = _heightmap.Spacing;
        int size = _heightmap.Size;
        int margin = SearchRadius + 1;  // un cambio influye hasta esta distancia (en casillas)

        int vx0 = Mathf.Clamp(Mathf.FloorToInt((tileRect.Position.X - margin) / sp), 0, size - 1);
        int vz0 = Mathf.Clamp(Mathf.FloorToInt((tileRect.Position.Y - margin) / sp), 0, size - 1);
        int vx1 = Mathf.Clamp(Mathf.CeilToInt((tileRect.End.X + margin) / sp), 0, size - 1);
        int vz1 = Mathf.Clamp(Mathf.CeilToInt((tileRect.End.Y + margin) / sp), 0, size - 1);

        // 0) Forma de las casillas cambiadas y sus vecinas (la media casilla depende de las vecinas)
        for (int tz = Mathf.Max(tileRect.Position.Y - 1, 0); tz <= Mathf.Min(tileRect.End.Y, _tiles.Depth - 1); tz++)
        for (int tx = Mathf.Max(tileRect.Position.X - 1, 0); tx <= Mathf.Min(tileRect.End.X, _tiles.Width - 1); tx++)
            for (int f = 0; f < _shape.Length; f++)
                _shape[f][tz * _tiles.Width + tx] = ComputeShape(f, tx, tz);

        // 1) Distancias sin suavizar (con margen extra para el suavizado)
        int s = SmoothSteps;
        for (int vz = Mathf.Max(vz0 - s, 0); vz <= Mathf.Min(vz1 + s, size - 1); vz++)
        for (int vx = Mathf.Max(vx0 - s, 0); vx <= Mathf.Min(vx1 + s, size - 1); vx++)
            for (int f = 0; f < _sdf.Length; f++)
                _sdf[f][vz * size + vx] = SignedDistance(f, vx * sp, vz * sp);

        // 2) Alturas y colores
        float[] heights = _heightmap.Data;
        for (int vz = vz0; vz <= vz1; vz++)
        for (int vx = vx0; vx <= vx1; vx++)
        {
            // Cada nivel aporta su altura; 't' va de 0 (abajo) a 1 (arriba del acantilado).
            // Si algún 't' está a medias, el vértice está en una PARED → material colina.
            float h = -LevelHeight;
            float wall = 0.0f;
            for (int f = 0; f < _tiles.MaxLevel; f++)
            {
                float t = Mathf.SmoothStep(-CliffOuter, CliffInner, Smoothed(f, vx, vz));
                h += LevelHeight * t;
                wall = Mathf.Max(wall, WallAmount(t));
            }

            // Cauce: se hunde con una pared empinada; las paredes y el fondo son colina
            float waterDist = Smoothed(WaterField, vx, vz);
            float carve = Mathf.SmoothStep(-BankOuter, BankInner, waterDist);
            h -= BedDepth * carve;
            float bank = waterDist > -(BankOuter + BankRim) ? 1.0f : 0.0f;

            // Mar: pendiente suave de arena hacia el fondo
            float seaCarve = Mathf.SmoothStep(-SeaShoreOuter, SeaShoreInner, Smoothed(SeaField, vx, vz));
            h -= SeaDepth * seaCarve;

            // Arena: en la playa y en el fondo del mar
            float px = vx * sp, pz = vz * sp;
            int tx = Mathf.Clamp(Mathf.FloorToInt(px), 0, _tiles.Width - 1);
            int tz = Mathf.Clamp(Mathf.FloorToInt(pz), 0, _tiles.Depth - 1);
            float sand = _tiles.IsBeach(tx, tz) || _tiles.IsSea(tx, tz) || seaCarve > 0.05f ? 1.0f : 0.0f;

            heights[vz * size + vx] = h;
            // R = arena, G = pared de acantilado, B = pared/fondo del cauce (G y B → colina)
            _colors[vz * size + vx] = new Color(sand, wall, bank, 1.0f);
        }

        return new Rect2I(vx0, vz0, vx1 - vx0 + 1, vz1 - vz0 + 1);
    }

    /// <summary>1 si 't' está a media pared, 0 si está arriba (≈1) o abajo (≈0) del todo.</summary>
    private static float WallAmount(float t) => t > 0.02f && t < 0.97f ? 1.0f : 0.0f;

    private bool InField(int field, int x, int z)
    {
        if (field < _tiles.MaxLevel) return _tiles.GetLevel(x, z) >= field + 1;
        if (field == WaterField) return _tiles.IsInlandWater(x, z);
        return _tiles.IsSea(x, z);
    }


    /// <summary>
    /// Una casilla de fuera se rellena a MEDIAS (triángulo) si dos vecinas en ángulo recto están dentro
    /// y las otras dos fuera: es el escalón de una escalera diagonal o el rincón de una L.
    /// N = z−1, S = z+1, W = x−1, E = x+1.
    /// </summary>
    private byte ComputeShape(int field, int x, int z)
    {
        if (InField(field, x, z)) return In;
        bool n = InField(field, x, z - 1), s = InField(field, x, z + 1);
        bool w = InField(field, x - 1, z), e = InField(field, x + 1, z);
        if (n && w && !s && !e) return HalfNW;
        if (n && e && !s && !w) return HalfNE;
        if (s && w && !n && !e) return HalfSW;
        if (s && e && !n && !w) return HalfSE;
        return Out;
    }

    /// <summary>Distancia con signo del punto (en metros = casillas) al borde del campo.</summary>
    private float SignedDistance(int field, float px, float pz)
    {
        int cx = Mathf.FloorToInt(px), cz = Mathf.FloorToInt(pz);
        float dInside = float.MaxValue, dOutside = float.MaxValue;
        Vector2 p = new Vector2(px, pz);
        byte[] shapes = _shape[field];

        for (int tz = cz - SearchRadius; tz <= cz + SearchRadius; tz++)
        for (int tx = cx - SearchRadius; tx <= cx + SearchRadius; tx++)
        {
            int sx = Mathf.Clamp(tx, 0, _tiles.Width - 1), sz = Mathf.Clamp(tz, 0, _tiles.Depth - 1);
            byte shape = shapes[sz * _tiles.Width + sx];

            if (shape == In || shape == Out)
            {
                // Distancia del punto al cuadrado de la casilla [tx, tx+1] × [tz, tz+1]
                float dx = Mathf.Max(Mathf.Max(tx - px, 0.0f), px - (tx + 1));
                float dz = Mathf.Max(Mathf.Max(tz - pz, 0.0f), pz - (tz + 1));
                float d = Mathf.Sqrt(dx * dx + dz * dz);
                if (shape == In) dInside = Mathf.Min(dInside, d);
                else dOutside = Mathf.Min(dOutside, d);
            }
            else
            {
                // Media casilla: un triángulo dentro y el otro fuera
                Vector2 c00 = new(tx, tz), c10 = new(tx + 1, tz), c01 = new(tx, tz + 1), c11 = new(tx + 1, tz + 1);
                (Vector2 a, Vector2 b, Vector2 c, Vector2 oa, Vector2 ob, Vector2 oc) = shape switch
                {
                    HalfNW => (c00, c10, c01, c10, c11, c01),
                    HalfNE => (c00, c10, c11, c00, c11, c01),
                    HalfSW => (c00, c01, c11, c00, c10, c11),
                    _      => (c10, c11, c01, c00, c10, c01)   // HalfSE
                };
                dInside = Mathf.Min(dInside, DistanceToTriangle(p, a, b, c));
                dOutside = Mathf.Min(dOutside, DistanceToTriangle(p, oa, ob, oc));
            }
        }

        if (dInside <= 0.0f) return Mathf.Min(dOutside, SearchRadius);   // dentro: + distancia a fuera
        return -Mathf.Min(dInside, SearchRadius);                         // fuera: − distancia a dentro
    }

    private static float DistanceToTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        float d1 = (b - a).Cross(p - a), d2 = (c - b).Cross(p - b), d3 = (a - c).Cross(p - c);
        bool hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
        bool hasPos = d1 > 0 || d2 > 0 || d3 > 0;
        if (!(hasNeg && hasPos)) return 0.0f;  // dentro del triángulo
        return Mathf.Min(DistanceToSegment(p, a, b), Mathf.Min(DistanceToSegment(p, b, c), DistanceToSegment(p, c, a)));
    }

    private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float t = Mathf.Clamp((p - a).Dot(ab) / ab.LengthSquared(), 0.0f, 1.0f);
        return p.DistanceTo(a + ab * t);
    }

    /// <summary>Media de la distancia en ±SmoothRadiusMeters: redondea esquinas sin borrar casillas sueltas.</summary>
    private float Smoothed(int field, int vx, int vz)
    {
        int size = _heightmap.Size;
        int s = SmoothSteps;
        float sum = 0.0f;
        for (int oz = -s; oz <= s; oz++)
        for (int ox = -s; ox <= s; ox++)
        {
            int x = Mathf.Clamp(vx + ox, 0, size - 1);
            int z = Mathf.Clamp(vz + oz, 0, size - 1);
            sum += _sdf[field][z * size + x];
        }
        return sum / ((2 * s + 1) * (2 * s + 1));
    }

    // ---------- Agua ----------

    /// <summary>Superficie del agua: un cuadrado plano por casilla de agua, a la altura de su nivel.</summary>
    public static ArrayMesh BuildWaterSurface(TileGrid tiles)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        float half = tiles.Width * 0.5f;
        bool any = false;

        for (int z = 0; z < tiles.Depth; z++)
        for (int x = 0; x < tiles.Width; x++)
        {
            if (!tiles.IsWater(x, z)) continue;
            int level = tiles.GetLevel(x, z);
            float y = WaterHeight(level);

            // El plano se extiende un poco bajo la orilla: el borde visible es donde el terreno
            // (curvo) corta el agua. Hacia una casilla más baja NO se extiende: ahí va una cascada.
            float x0 = x - Extension(tiles, x, z, -1, 0, level);
            float x1 = x + 1 + Extension(tiles, x, z, 1, 0, level);
            float z0 = z - Extension(tiles, x, z, 0, -1, level);
            float z1 = z + 1 + Extension(tiles, x, z, 0, 1, level);

            AddQuad(st,
                new Vector3(x0 - half, y, z0 - half), new Vector3(x1 - half, y, z0 - half),
                new Vector3(x1 - half, y, z1 - half), new Vector3(x0 - half, y, z1 - half),
                Vector3.Up, new Vector2(x0, z0), new Vector2(x1, z1));
            any = true;
        }

        return any ? st.Commit() : null;
    }

    /// <summary>
    /// Cuánto puede extenderse el agua hacia (dx, dz): 0,6 si la vecina está a su nivel o más alta
    /// y no está al borde de un acantilado (ahí asomaría como una repisa); 0 en otro caso.
    /// </summary>
    private static float Extension(TileGrid tiles, int x, int z, int dx, int dz, int level)
    {
        int cx = x + dx, cz = z + dz;
        if (!tiles.IsInside(cx, cz)) return 0.0f;
        for (int oz = -1; oz <= 1; oz++)
        for (int ox = -1; ox <= 1; ox++)
            if (tiles.GetLevel(cx + ox, cz + oz) < level) return 0.0f;
        return 0.6f;
    }

    /// <summary>Cascadas: una cortina vertical en cada borde entre agua alta y agua más baja.</summary>
    public static ArrayMesh BuildWaterfalls(TileGrid tiles)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        float half = tiles.Width * 0.5f;
        bool any = false;

        Vector2I[] dirs = { new(1, 0), new(-1, 0), new(0, 1), new(0, -1) };

        for (int z = 0; z < tiles.Depth; z++)
        for (int x = 0; x < tiles.Width; x++)
        {
            if (!tiles.IsWater(x, z)) continue;
            int level = tiles.GetLevel(x, z);

            foreach (Vector2I dir in dirs)
            {
                int nx = x + dir.X, nz = z + dir.Y;
                if (!tiles.IsInside(nx, nz) || !tiles.IsWater(nx, nz)) continue;
                int lower = tiles.GetLevel(nx, nz);
                if (lower >= level) continue;

                float top = WaterHeight(level);
                float bottom = WaterHeight(lower);

                // Borde compartido entre las dos casillas
                Vector3 edge = new Vector3(x + 0.5f + dir.X * 0.5f - half, 0, z + 0.5f + dir.Y * 0.5f - half);
                Vector3 along = new Vector3(Mathf.Abs(dir.Y), 0, Mathf.Abs(dir.X)) * 0.5f;

                AddQuad(st,
                    edge - along + Vector3.Up * top, edge + along + Vector3.Up * top,
                    edge + along + Vector3.Up * bottom, edge - along + Vector3.Up * bottom,
                    new Vector3(dir.X, 0, dir.Y), new Vector2(0, 0), new Vector2(1, 1));
                any = true;
            }
        }

        return any ? st.Commit() : null;
    }

    private static void AddQuad(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c, Vector3 d,
                                Vector3 normal, Vector2 uvMin, Vector2 uvMax)
    {
        // a = (min,min)  b = (max,min)  c = (max,max)  d = (min,max)
        Vector2 ua = uvMin, ub = new(uvMax.X, uvMin.Y), uc = uvMax, ud = new(uvMin.X, uvMax.Y);
        st.SetNormal(normal); st.SetUV(ua); st.AddVertex(a);
        st.SetNormal(normal); st.SetUV(ub); st.AddVertex(b);
        st.SetNormal(normal); st.SetUV(ud); st.AddVertex(d);
        st.SetNormal(normal); st.SetUV(ub); st.AddVertex(b);
        st.SetNormal(normal); st.SetUV(uc); st.AddVertex(c);
        st.SetNormal(normal); st.SetUV(ud); st.AddVertex(d);
    }
}
