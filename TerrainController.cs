using Godot;

/// <summary>
/// Une las casillas con lo que se ve:
///   TileGrid (datos) → TileTerrainBuilder (forma) → TerrainHeightmap → trozos (chunks) + colisión + agua.
/// Al cambiar casillas solo se recalculan y redibujan las zonas afectadas.
/// No decide QUÉ se puede cambiar (eso es TerraformRules) ni CÓMO se pide (eso es TerraformTool).
/// </summary>
public partial class TerrainController : MeshInstance3D
{
    [ExportGroup("Tamaño")]
    [Export] public float SizeMeters = 128.0f;  // lado de la isla (1 casilla = 1 m)
    [Export] public float Spacing = 0.25f;      // metros entre vértices de la malla (4 por casilla)
    [Export] public int ChunkQuads = 64;        // cuadrados por lado de cada trozo (64 × 0,25 m = 16 m)

    [ExportGroup("Casillas")]
    [Export] public int MaxTileLevel = 4;
    [Export] public bool FillWithPrototype = true;  // true: empieza con la montaña y el río de prueba

    [ExportGroup("Colisión")]
    [Export] public CollisionShape3D TerrainCollisionShape;
    [Export] public float CollisionUpdateInterval = 0.1f;  // segundos entre actualizaciones

    public TileGrid Tiles { get; private set; }
    public TerrainHeightmap Heightmap { get; private set; }

    private TileTerrainBuilder _builder;
    private Color[] _vertexColors;  // material por vértice para el shader (ver TileTerrainBuilder)

    // Un trozo: su malla visible y el rango de vértices del heightmap que cubre
    private class Chunk
    {
        public MeshInstance3D Instance;
        public ArrayMesh Mesh;
        public int StartX, StartZ;   // primer vértice (coordenadas del heightmap)
        public Vector3[] Vertices;
        public Vector3[] Normals;
        public Vector2[] Uvs;
        public Color[] Colors;
        public int[] Indices;    // cada trozo elige sus diagonales según el relieve
    }

    private Chunk[,] _chunks;
    private int _chunksPerSide;

    private MeshInstance3D _waterSurface;
    private MeshInstance3D _waterfalls;

    private readonly HeightMapShape3D _heightShape = new HeightMapShape3D();
    private bool _collisionDirty;
    private double _collisionTimer;

    public override void _Ready()
    {
        int quads = Mathf.RoundToInt(SizeMeters / Spacing);
        _chunksPerSide = Mathf.CeilToInt((float)quads / ChunkQuads);
        quads = _chunksPerSide * ChunkQuads;  // ajustamos para que encajen trozos completos
        int size = quads + 1;
        int tilesPerSide = Mathf.RoundToInt(quads * Spacing);

        // 1) Datos: las casillas son la fuente de verdad
        Tiles = new TileGrid(tilesPerSide, tilesPerSide, MaxTileLevel, initialLevel: 1);
        if (FillWithPrototype) TilePrototype.Fill(Tiles);

        // 2) Forma: alturas y materiales calculados desde las casillas
        Heightmap = new TerrainHeightmap(size, Spacing);
        _vertexColors = new Color[size * size];
        _builder = new TileTerrainBuilder(Tiles, Heightmap, _vertexColors);
        _builder.RebuildAll();
        (MaterialOverride as ShaderMaterial)?.SetShaderParameter("use_biome_colors", true);

        // 3) Dibujo. Este nodo ya no dibuja nada (la malla de la escena es solo para el editor);
        //    lo dibujan sus trozos hijos con el mismo material.
        Mesh = null;
        BuildChunks();
        CreateWaterNodes();
        RebuildWater();

        // 4) Colisión
        if (TerrainCollisionShape != null)
        {
            _heightShape.MapWidth = size;
            _heightShape.MapDepth = size;
            TerrainCollisionShape.Shape = _heightShape;
            // HeightMapShape3D pone 1 m entre muestras; lo escalamos en X/Z para que coincida con Spacing
            TerrainCollisionShape.Scale = new Vector3(Spacing, 1.0f, Spacing);
            UpdateCollision();
        }
    }

    public override void _Process(double delta)
    {
        // La colisión cuesta más que la malla: la actualizamos como mucho cada CollisionUpdateInterval
        _collisionTimer += delta;
        if (_collisionDirty && _collisionTimer >= CollisionUpdateInterval)
            UpdateCollision();
    }

    // ---------- API para la herramienta ----------

    /// <summary>Casilla que contiene un punto del mundo.</summary>
    public Vector2I WorldToTile(Vector3 world)
    {
        Vector3 local = ToLocal(world);
        return new Vector2I(Mathf.FloorToInt(local.X + Tiles.Width * 0.5f),
                            Mathf.FloorToInt(local.Z + Tiles.Depth * 0.5f));
    }

    /// <summary>Centro de una casilla en el mundo, a la altura del suelo de su nivel.</summary>
    public Vector3 TileCenterWorld(Vector2I tile)
    {
        float y = TileTerrainBuilder.GroundHeight(Tiles.GetLevel(tile.X, tile.Y));
        return ToGlobal(new Vector3(tile.X + 0.5f - Tiles.Width * 0.5f, y, tile.Y + 0.5f - Tiles.Depth * 0.5f));
    }

    /// <summary>Altura de la superficie visible de una casilla: el suelo de su nivel, o el agua.</summary>
    public float TileSurfaceHeight(Vector2I tile)
    {
        int level = Tiles.GetLevel(tile.X, tile.Y);
        return Tiles.IsWater(tile)
            ? TileTerrainBuilder.WaterHeight(level)
            : TileTerrainBuilder.GroundHeight(level);
    }

    /// <summary>Hay que llamarlo después de cambiar casillas: redibuja solo lo afectado.</summary>
    public void OnTilesChanged(Rect2I tileRect)
    {
        Rect2I vertices = _builder.RebuildRegion(tileRect);
        RebuildChunksIn(vertices);
        RebuildWater();
        _collisionDirty = true;
    }

    /// <summary>Activa/desactiva una capa visual en todos los trozos (p. ej. la del cursor).</summary>
    public void SetVisualLayer(int layer, bool enabled)
    {
        SetLayerMaskValue(layer, enabled);
        foreach (Chunk chunk in _chunks)
            chunk.Instance.SetLayerMaskValue(layer, enabled);
    }

    // ---------- Trozos ----------

    private void BuildChunks()
    {
        int vertsPerSide = ChunkQuads + 1;
        _chunks = new Chunk[_chunksPerSide, _chunksPerSide];
        int size = Heightmap.Size;

        for (int cz = 0; cz < _chunksPerSide; cz++)
        for (int cx = 0; cx < _chunksPerSide; cx++)
        {
            var chunk = new Chunk
            {
                StartX = cx * ChunkQuads,
                StartZ = cz * ChunkQuads,
                Mesh = new ArrayMesh(),
                Vertices = new Vector3[vertsPerSide * vertsPerSide],
                Normals = new Vector3[vertsPerSide * vertsPerSide],
                Uvs = new Vector2[vertsPerSide * vertsPerSide],
                Colors = new Color[vertsPerSide * vertsPerSide],
                Indices = new int[ChunkQuads * ChunkQuads * 6]
            };

            // UV globales de la isla (0..1), iguales que con una sola malla
            for (int z = 0; z < vertsPerSide; z++)
            for (int x = 0; x < vertsPerSide; x++)
                chunk.Uvs[z * vertsPerSide + x] = new Vector2(
                    (float)(chunk.StartX + x) / (size - 1),
                    (float)(chunk.StartZ + z) / (size - 1));

            chunk.Instance = new MeshInstance3D
            {
                Name = $"Chunk_{cx}_{cz}",
                Mesh = chunk.Mesh,
                MaterialOverride = MaterialOverride,
                Layers = Layers
            };
            AddChild(chunk.Instance);

            _chunks[cx, cz] = chunk;
            RebuildChunk(chunk);
        }
    }

    /// <summary>Reconstruye los trozos que tocan una zona de vértices del heightmap.</summary>
    private void RebuildChunksIn(Rect2I dirty)
    {
        // +1 de margen: las normales de un vértice dependen de sus vecinos
        int minCx = Mathf.Clamp((dirty.Position.X - 1) / ChunkQuads, 0, _chunksPerSide - 1);
        int minCz = Mathf.Clamp((dirty.Position.Y - 1) / ChunkQuads, 0, _chunksPerSide - 1);
        int maxCx = Mathf.Clamp((dirty.End.X + 1) / ChunkQuads, 0, _chunksPerSide - 1);
        int maxCz = Mathf.Clamp((dirty.End.Y + 1) / ChunkQuads, 0, _chunksPerSide - 1);

        for (int cz = minCz; cz <= maxCz; cz++)
        for (int cx = minCx; cx <= maxCx; cx++)
            RebuildChunk(_chunks[cx, cz]);
    }

    private void RebuildChunk(Chunk chunk)
    {
        int vertsPerSide = ChunkQuads + 1;
        float half = (Heightmap.Size - 1) * 0.5f * Spacing;

        for (int z = 0; z < vertsPerSide; z++)
        for (int x = 0; x < vertsPerSide; x++)
        {
            int gx = chunk.StartX + x;  // coordenadas en el heightmap global
            int gz = chunk.StartZ + z;
            int i = z * vertsPerSide + x;

            chunk.Vertices[i] = new Vector3(gx * Spacing - half, Heightmap.Get(gx, gz), gz * Spacing - half);

            // Normal con filtro de Sobel (3×3) sobre el heightmap GLOBAL: más suave que usar solo
            // 2 vecinos (evita rayas de luz en las paredes) y sin costuras entre trozos
            float dx = (Heightmap.GetClamped(gx - 1, gz - 1) + 2 * Heightmap.GetClamped(gx - 1, gz) + Heightmap.GetClamped(gx - 1, gz + 1)
                      - Heightmap.GetClamped(gx + 1, gz - 1) - 2 * Heightmap.GetClamped(gx + 1, gz) - Heightmap.GetClamped(gx + 1, gz + 1)) * 0.25f;
            float dz = (Heightmap.GetClamped(gx - 1, gz - 1) + 2 * Heightmap.GetClamped(gx, gz - 1) + Heightmap.GetClamped(gx + 1, gz - 1)
                      - Heightmap.GetClamped(gx - 1, gz + 1) - 2 * Heightmap.GetClamped(gx, gz + 1) - Heightmap.GetClamped(gx + 1, gz + 1)) * 0.25f;
            chunk.Normals[i] = new Vector3(dx, 2.0f * Spacing, dz).Normalized();
            chunk.Colors[i] = _vertexColors[gz * Heightmap.Size + gx];
        }

        // Dos triángulos por cuadrado (orden horario = cara visible en Godot). Cada cuadrado se
        // parte por la diagonal cuyos extremos tienen alturas más parecidas: así las paredes
        // diagonales salen lisas en vez de en zigzag.
        int k = 0;
        for (int z = 0; z < ChunkQuads; z++)
        for (int x = 0; x < ChunkQuads; x++)
        {
            int a = z * vertsPerSide + x;  // arriba-izquierda
            int b = a + 1;                 // arriba-derecha
            int c = a + vertsPerSide;      // abajo-izquierda
            int d = c + 1;                 // abajo-derecha
            float diagAD = Mathf.Abs(chunk.Vertices[a].Y - chunk.Vertices[d].Y);
            float diagBC = Mathf.Abs(chunk.Vertices[b].Y - chunk.Vertices[c].Y);

            if (diagAD < diagBC)
            {
                chunk.Indices[k++] = a; chunk.Indices[k++] = b; chunk.Indices[k++] = d;
                chunk.Indices[k++] = a; chunk.Indices[k++] = d; chunk.Indices[k++] = c;
            }
            else
            {
                chunk.Indices[k++] = a; chunk.Indices[k++] = b; chunk.Indices[k++] = c;
                chunk.Indices[k++] = b; chunk.Indices[k++] = d; chunk.Indices[k++] = c;
            }
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = chunk.Vertices;
        arrays[(int)Mesh.ArrayType.Normal] = chunk.Normals;
        arrays[(int)Mesh.ArrayType.TexUV] = chunk.Uvs;
        arrays[(int)Mesh.ArrayType.Color] = chunk.Colors;
        arrays[(int)Mesh.ArrayType.Index] = chunk.Indices;

        chunk.Mesh.ClearSurfaces();
        chunk.Mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
    }

    // ---------- Agua ----------

    private void CreateWaterNodes()
    {
        var waterShader = GD.Load<Shader>("res://water.gdshader");
        var fallsMaterial = new ShaderMaterial { Shader = waterShader };
        fallsMaterial.SetShaderParameter("is_waterfall", true);

        _waterSurface = new MeshInstance3D
        {
            Name = "WaterSurface",
            MaterialOverride = new ShaderMaterial { Shader = waterShader },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
        _waterfalls = new MeshInstance3D
        {
            Name = "Waterfalls",
            MaterialOverride = fallsMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
        AddChild(_waterSurface);
        AddChild(_waterfalls);

        // Colisión del agua SOLO para el ratón de la herramienta: está en la capa física 2 y el
        // personaje solo choca con la capa 1, así que lo atraviesa (no camina sobre el agua).
        _waterPickShape = new CollisionShape3D { Shape = new ConcavePolygonShape3D { BackfaceCollision = true } };
        _waterPickBody = new StaticBody3D { Name = "WaterPick", CollisionLayer = WaterPickLayer, CollisionMask = 0 };
        _waterPickBody.AddChild(_waterPickShape);
        AddChild(_waterPickBody);
    }

    public const uint WaterPickLayer = 1u << 1;  // capa física 2
    private StaticBody3D _waterPickBody;
    private CollisionShape3D _waterPickShape;

    /// <summary>¿El rayo del ratón ha dado en la superficie del agua?</summary>
    public bool IsWaterPickBody(GodotObject collider) => collider == _waterPickBody;

    // El agua se regenera entera: son pocas casillas y tarda muy poco
    private void RebuildWater()
    {
        ArrayMesh surface = TileTerrainBuilder.BuildWaterSurface(Tiles);
        _waterSurface.Mesh = surface;
        _waterfalls.Mesh = TileTerrainBuilder.BuildWaterfalls(Tiles);
        ((ConcavePolygonShape3D)_waterPickShape.Shape).SetFaces(surface?.GetFaces() ?? new Vector3[0]);
    }

    private void UpdateCollision()
    {
        _heightShape.MapData = Heightmap.Data;
        _collisionDirty = false;
        _collisionTimer = 0.0;
    }
}
