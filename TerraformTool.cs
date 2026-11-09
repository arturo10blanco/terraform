using Godot;
using Godot.Collections;

/// <summary>
/// Herramienta de terraformar del personaje (va como hijo del Player).
/// Se apunta con el ratón a una CASILLA, que solo se puede cambiar si está al alcance.
///   [1] Terreno → clic izquierdo sube un nivel, clic derecho baja un nivel
///   [2] Agua    → clic izquierdo cava agua, clic derecho la rellena
/// Mantener pulsado y arrastrar aplica la acción a cada casilla nueva (útil para trazar ríos).
/// Si una regla lo impide, se muestra el motivo en pantalla.
/// </summary>
public partial class TerraformTool : Node3D
{
    [Export] public TerrainController Terrain;
    [Export] public float Reach = 3.0f;             // distancia horizontal máxima al centro de la casilla
    [Export] public float PlayerFootprint = 0.35f;  // radio del personaje: sus casillas no se tocan

    private enum ToolMode { Terrain, Water }
    private ToolMode _mode = ToolMode.Terrain;

    private CharacterBody3D _player;
    private Node3D _cursor;
    private StandardMaterial3D _cursorMaterial;
    private Label _hud;
    private string _message = "";
    private double _messageTimer;

    private bool _wasLeft, _wasRight;
    private Vector2I _lastAppliedTile = new(-1, -1);
    private Vector2 _lastApplyMouse;

    private static readonly Color InReachColor = new Color(1.0f, 1.0f, 1.0f, 0.95f);
    private static readonly Color OutOfReachColor = new Color(0.3f, 0.3f, 0.3f, 0.4f);

    public override void _Ready()
    {
        _player = GetParent<CharacterBody3D>();
        CreateCursor();
        CreateHud();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey key || !key.Pressed || key.Echo) return;

        switch (key.Keycode)
        {
            case Key.Key1: _mode = ToolMode.Terrain; break;
            case Key.Key2: _mode = ToolMode.Water; break;
            default: return;
        }
        UpdateHud();
    }

    public override void _Process(double delta)
    {
        if (Terrain == null) return;

        if (_messageTimer > 0 && (_messageTimer -= delta) <= 0)
        {
            _message = "";
            UpdateHud();
        }

        bool left = Input.IsMouseButtonPressed(MouseButton.Left);
        bool right = Input.IsMouseButtonPressed(MouseButton.Right);

        if (!TryGetMouseTile(out Vector2I tile))
        {
            _cursor.Visible = false;
            _wasLeft = left;
            _wasRight = right;
            return;
        }

        // ¿Está la casilla al alcance del personaje? (distancia horizontal a su centro)
        Vector3 center = Terrain.TileCenterWorld(tile);
        Vector3 toCenter = center - _player.GlobalPosition;
        bool inReach = new Vector2(toCenter.X, toCenter.Z).Length() <= Reach;

        // El cursor se apoya plano sobre la superficie de la casilla (suelo o agua), un pelín por encima
        _cursor.Visible = true;
        _cursor.GlobalPosition = new Vector3(center.X, Terrain.GlobalPosition.Y + Terrain.TileSurfaceHeight(tile) + 0.03f, center.Z);
        SetCursorColor(inReach ? InReachColor : OutOfReachColor);

        // Un clic aplica la acción una vez; al arrastrar se aplica a cada casilla nueva.
        // "Arrastrar" exige mover el ratón: si la casilla cambia solo porque el terreno ha cambiado
        // bajo el cursor, no se vuelve a aplicar.
        Vector2 mouse = GetViewport().GetMousePosition();
        bool newPress = (left && !_wasLeft) || (right && !_wasRight);
        bool dragged = tile != _lastAppliedTile && mouse.DistanceTo(_lastApplyMouse) > 2.0f;
        if ((left || right) && inReach && (newPress || dragged))
        {
            Apply(tile, primary: left);
            _lastAppliedTile = tile;
            _lastApplyMouse = mouse;
        }
        if (!left && !right) _lastAppliedTile = new Vector2I(-1, -1);

        _wasLeft = left;
        _wasRight = right;
    }

    private void Apply(Vector2I tile, bool primary)
    {
        TileGrid grid = Terrain.Tiles;
        Rect2I protectedTiles = PlayerTiles();
        string reason;

        bool ok = _mode switch
        {
            ToolMode.Terrain when primary => TerraformRules.TryRaise(grid, tile, protectedTiles, out reason),
            ToolMode.Terrain => TerraformRules.TryLower(grid, tile, protectedTiles, out reason),
            _ when primary => TerraformRules.TryDigWater(grid, tile, protectedTiles, out reason),
            _ => TerraformRules.TryFillWater(grid, tile, protectedTiles, out reason)
        };

        if (ok) Terrain.OnTilesChanged(new Rect2I(tile, 1, 1));
        else ShowMessage(reason);
    }

    /// <summary>Casillas que pisa el personaje (su círculo puede tocar hasta 4).</summary>
    private Rect2I PlayerTiles()
    {
        Vector3 p = _player.GlobalPosition;
        Vector2I min = Terrain.WorldToTile(p - new Vector3(PlayerFootprint, 0, PlayerFootprint));
        Vector2I max = Terrain.WorldToTile(p + new Vector3(PlayerFootprint, 0, PlayerFootprint));
        return new Rect2I(min, max - min + Vector2I.One);
    }

    private bool TryGetMouseTile(out Vector2I tile)
    {
        tile = default;
        var camera = GetViewport().GetCamera3D();
        if (camera == null) return false;

        Vector2 mouse = GetViewport().GetMousePosition();
        Vector3 from = camera.ProjectRayOrigin(mouse);
        Vector3 to = from + camera.ProjectRayNormal(mouse) * 1000.0f;

        var query = PhysicsRayQueryParameters3D.Create(from, to);
        query.Exclude = new Array<Rid> { _player.GetRid() };  // el rayo atraviesa al personaje

        Dictionary hit = GetWorld3D().DirectSpaceState.IntersectRay(query);
        if (hit.Count == 0) return false;

        Vector3 point = (Vector3)hit["position"];
        Vector3 normal = (Vector3)hit["normal"];

        // El agua tiene una colisión solo para este rayo: si da en ella, es esa casilla de agua.
        // El agua visible de la orilla puede asomar unos centímetros sobre la casilla vecina:
        // en ese caso elegimos la casilla de agua más cercana.
        if (Terrain.IsWaterPickBody((GodotObject)hit["collider"]))
        {
            tile = NearestWaterTile(point);
            return Terrain.Tiles.IsInside(tile.X, tile.Y);
        }

        // Si el rayo da en la pared de un acantilado, nos metemos un poco hacia dentro:
        // así se elige la casilla de arriba (la que "se ve") y no la de abajo.
        Vector3 inward = new Vector3(-normal.X, 0, -normal.Z) * 0.3f;
        tile = Terrain.WorldToTile(point + inward);
        return Terrain.Tiles.IsInside(tile.X, tile.Y);
    }

    private Vector2I NearestWaterTile(Vector3 point)
    {
        Vector2I center = Terrain.WorldToTile(point);
        if (Terrain.Tiles.IsWater(center)) return center;

        Vector2I best = center;
        float bestDist = float.MaxValue;
        for (int dz = -1; dz <= 1; dz++)
        for (int dx = -1; dx <= 1; dx++)
        {
            Vector2I t = center + new Vector2I(dx, dz);
            if (!Terrain.Tiles.IsWater(t)) continue;
            Vector3 c = Terrain.TileCenterWorld(t);
            float dist = new Vector2(c.X - point.X, c.Z - point.Z).Length();
            if (dist < bestDist) { bestDist = dist; best = t; }
        }
        return best;
    }

    private void ShowMessage(string text)
    {
        _message = text;
        _messageTimer = 2.0;
        UpdateHud();
    }

    // ---------- Cursor y HUD (creados por código para no tocar la escena) ----------

    // El cursor es un "cubo" de 1 casilla:
    //   - abajo, un cuadrado plano a la altura de la casilla (suelo o agua);
    //   - alrededor, paredes que se desvanecen hacia arriba, con las aristas verticales marcadas.
    // Las paredes asoman por encima de los bordes de hoyos y ríos, así la casilla se ve aunque
    // el borde tape el cuadrado. Además, una segunda pasada "rayos X" muy tenue lo deja intuir
    // a través del terreno cuando queda completamente oculto.
    private const float CursorHalf = 0.48f;    // medio lado (un pelín menos que la casilla)
    private const float CursorHeight = 1.0f;   // altura de las paredes (1 nivel)
    private const float XRayStrength = 0.3f;   // opacidad de la pasada a través del terreno

    private StandardMaterial3D _cursorXRay;
    private StandardMaterial3D _wallMaterial;
    private StandardMaterial3D _wallXRay;

    private void CreateCursor()
    {
        // 1) Textura del cuadrado de abajo: borde marcado e interior suave
        const int res = 64;
        var image = Image.CreateEmpty(res, res, false, Image.Format.Rgba8);
        for (int y = 0; y < res; y++)
        for (int x = 0; x < res; x++)
        {
            // Coordenadas -1..1 y distancia con signo a un cuadrado de esquinas casi rectas
            Vector2 p = new Vector2((x + 0.5f) / res * 2 - 1, (y + 0.5f) / res * 2 - 1);
            const float radius = 0.12f;
            Vector2 q = p.Abs() - new Vector2(1 - radius - 0.04f, 1 - radius - 0.04f);
            float d = new Vector2(Mathf.Max(q.X, 0), Mathf.Max(q.Y, 0)).Length()
                      + Mathf.Min(Mathf.Max(q.X, q.Y), 0) - radius;

            float alpha = d > 0 ? 0.0f : (d > -0.12f ? 1.0f : 0.15f);
            image.SetPixel(x, y, new Color(1, 1, 1, alpha));
        }

        _cursorXRay = CursorMaterial(noDepthTest: true);
        _cursorXRay.AlbedoTexture = ImageTexture.CreateFromImage(image);
        _cursorMaterial = CursorMaterial(noDepthTest: false);
        _cursorMaterial.AlbedoTexture = _cursorXRay.AlbedoTexture;
        _cursorMaterial.NextPass = _cursorXRay;

        // 2) Paredes y aristas (el degradado va en el color de cada vértice)
        _wallXRay = CursorMaterial(noDepthTest: true);
        _wallXRay.VertexColorUseAsAlbedo = true;
        _wallMaterial = CursorMaterial(noDepthTest: false);
        _wallMaterial.VertexColorUseAsAlbedo = true;
        _wallMaterial.NextPass = _wallXRay;

        _cursor = new Node3D { TopLevel = true, Visible = false };  // no hereda la posición del personaje
        _cursor.AddChild(new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(CursorHalf * 2, CursorHalf * 2) },
            MaterialOverride = _cursorMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        });
        _cursor.AddChild(new MeshInstance3D
        {
            Mesh = BuildCursorWalls(),
            MaterialOverride = _wallMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        });
        AddChild(_cursor);
        SetCursorColor(InReachColor);
    }

    private static StandardMaterial3D CursorMaterial(bool noDepthTest) => new StandardMaterial3D
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        NoDepthTest = noDepthTest
    };

    private void SetCursorColor(Color color)
    {
        _cursorMaterial.AlbedoColor = color;
        _wallMaterial.AlbedoColor = color;
        Color xray = new Color(color.R, color.G, color.B, color.A * XRayStrength);
        _cursorXRay.AlbedoColor = xray;
        _wallXRay.AlbedoColor = xray;
    }

    private static ArrayMesh BuildCursorWalls()
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);

        Vector3[] corners =
        {
            new(-CursorHalf, 0, -CursorHalf), new(CursorHalf, 0, -CursorHalf),
            new(CursorHalf, 0, CursorHalf), new(-CursorHalf, 0, CursorHalf)
        };

        // Paredes: semitransparentes abajo, invisibles arriba
        for (int i = 0; i < 4; i++)
            AddFadingQuad(st, corners[i], corners[(i + 1) % 4], 0.28f);

        // Aristas verticales: dos tiras finas cruzadas en cada esquina, más opacas
        const float w = 0.025f;
        foreach (Vector3 c in corners)
        {
            AddFadingQuad(st, c + new Vector3(-w, 0, 0), c + new Vector3(w, 0, 0), 0.9f);
            AddFadingQuad(st, c + new Vector3(0, 0, -w), c + new Vector3(0, 0, w), 0.9f);
        }

        return st.Commit();
    }

    /// <summary>Rectángulo vertical de 'a' a 'b' (abajo) que se desvanece hasta 0 arriba.</summary>
    private static void AddFadingQuad(SurfaceTool st, Vector3 a, Vector3 b, float bottomAlpha)
    {
        Vector3 up = Vector3.Up * CursorHeight;
        Color bottom = new Color(1, 1, 1, bottomAlpha), top = new Color(1, 1, 1, 0);
        st.SetColor(bottom); st.AddVertex(a);
        st.SetColor(bottom); st.AddVertex(b);
        st.SetColor(top); st.AddVertex(b + up);
        st.SetColor(bottom); st.AddVertex(a);
        st.SetColor(top); st.AddVertex(b + up);
        st.SetColor(top); st.AddVertex(a + up);
    }

    private void CreateHud()
    {
        var layer = new CanvasLayer();
        _hud = new Label { Position = new Vector2(16, 16) };
        _hud.AddThemeFontSizeOverride("font_size", 20);
        _hud.AddThemeColorOverride("font_outline_color", Colors.Black);
        _hud.AddThemeConstantOverride("outline_size", 6);
        layer.AddChild(_hud);
        AddChild(layer);
        UpdateHud();
    }

    private void UpdateHud()
    {
        string Mark(ToolMode m) => _mode == m ? "▶ " : "   ";
        _hud.Text =
            $"{Mark(ToolMode.Terrain)}[1] Terreno: clic izq. subir / clic der. bajar\n" +
            $"{Mark(ToolMode.Water)}[2] Agua: clic izq. cavar / clic der. rellenar" +
            (string.IsNullOrEmpty(_message) ? "" : $"\n\n{_message}");
    }
}
