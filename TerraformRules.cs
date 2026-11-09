using Godot;
using System.Collections.Generic;

/// <summary>
/// Reglas de la terraformación sobre casillas. Cada Try* comprueba si la acción es válida y,
/// si lo es, la aplica. Si no, devuelve false y una razón legible para mostrar al jugador.
/// Están juntas aquí para poder revisarlas y cambiarlas fácilmente.
/// </summary>
public static class TerraformRules
{
    public const int MinLevel = 0;
    public const int BorderTiles = 2;                   // franja del borde del mapa que no se toca
    public const int MaxPondTiles = 12;                 // un estanque sin conexión no puede ser mayor
    public const float MaxInlandWaterFraction = 0.15f;  // tope de agua interior: 15 % de la isla

    private static readonly Vector2I[] Neighbors8 =
    {
        new(-1, -1), new(0, -1), new(1, -1),
        new(-1, 0),              new(1, 0),
        new(-1, 1),  new(0, 1),  new(1, 1)
    };

    private static readonly Vector2I[] Neighbors4 = { new(1, 0), new(-1, 0), new(0, 1), new(0, -1) };

    /// <summary>Comprobaciones comunes a todas las acciones.</summary>
    private static bool CanTouch(TileGrid g, Vector2I t, Rect2I protectedTiles, out string reason)
    {
        reason = null;
        if (t.X < BorderTiles || t.Y < BorderTiles || t.X >= g.Width - BorderTiles || t.Y >= g.Depth - BorderTiles)
            reason = "No se puede modificar el borde del mapa";
        else if (protectedTiles.HasPoint(t))
            reason = "No puedes modificar el suelo donde estás";
        else if (g.IsSea(t))
            reason = "El mar no se puede modificar";
        else if (g.IsSpring(t))
            reason = "El manantial no se puede modificar";
        return reason == null;
    }

    // ---------- Terreno ----------

    public static bool TryRaise(TileGrid g, Vector2I t, Rect2I protectedTiles, out string reason)
    {
        if (!CanTouch(g, t, protectedTiles, out reason)) return false;
        if (g.IsWater(t)) { reason = "Primero rellena el agua"; return false; }
        int level = g.GetLevel(t);
        if (level >= g.MaxLevel) { reason = "Altura máxima alcanzada"; return false; }

        if (g.IsBeach(t)) g.SetTileType(t, TileType.Land);  // al subirla deja de ser playa
        g.SetLevel(t, level + 1);
        return true;
    }

    public static bool TryLower(TileGrid g, Vector2I t, Rect2I protectedTiles, out string reason)
    {
        if (!CanTouch(g, t, protectedTiles, out reason)) return false;
        if (g.IsWater(t)) { reason = "Primero rellena el agua"; return false; }
        int level = g.GetLevel(t);
        if (level <= MinLevel) { reason = "Altura mínima alcanzada"; return false; }

        // El agua vecina se saldría por el hueco
        foreach (Vector2I n in Neighbors8)
        {
            if (g.IsWater(t + n) && g.GetLevel(t + n) >= level)
            {
                reason = "El agua de al lado se saldría";
                return false;
            }
        }

        g.SetLevel(t, level - 1);
        return true;
    }

    // ---------- Agua ----------

    public static bool TryDigWater(TileGrid g, Vector2I t, Rect2I protectedTiles, out string reason)
    {
        if (!CanTouch(g, t, protectedTiles, out reason)) return false;
        if (g.IsWater(t)) { reason = "Ya hay agua aquí"; return false; }
        int level = g.GetLevel(t);

        // El agua no puede quedar junto a tierra más baja (se saldría).
        // Sí puede quedar junto a agua más baja en línea recta: eso forma una cascada.
        foreach (Vector2I n in Neighbors8)
        {
            if (g.GetLevel(t + n) >= level) continue;
            bool straight = n.X == 0 || n.Y == 0;
            if (!(straight && g.IsWater(t + n)))
            {
                reason = "El agua se saldría por el desnivel";
                return false;
            }
        }

        // Probamos a poner el agua y comprobamos las reglas de conexión; si fallan, se deshace
        TileType previous = g.GetTileType(t.X, t.Y);
        g.SetTileType(t, TileType.Water);

        WaterBody body = Explore(g, t);
        if (!body.HasSource && body.Size > MaxPondTiles)
        {
            g.SetTileType(t, previous);
            reason = $"Un estanque sin conexión no puede pasar de {MaxPondTiles} casillas: únelo a un río o al mar";
            return false;
        }

        if (CountInlandWater(g, out int islandTiles) > islandTiles * MaxInlandWaterFraction)
        {
            g.SetTileType(t, previous);
            reason = "La isla ya tiene demasiada agua interior";
            return false;
        }

        return true;
    }

    public static bool TryFillWater(TileGrid g, Vector2I t, Rect2I protectedTiles, out string reason)
    {
        if (!CanTouch(g, t, protectedTiles, out reason)) return false;
        if (!g.IsWater(t)) { reason = "Aquí no hay agua"; return false; }
        int level = g.GetLevel(t);

        // Si cae una cascada desde arriba sobre esta casilla, rellenarla dejaría el agua de arriba
        // junto a tierra más baja
        foreach (Vector2I n in Neighbors4)
        {
            if (g.IsWater(t + n) && g.GetLevel(t + n) > level)
            {
                reason = "Primero rellena el agua de arriba";
                return false;
            }
        }

        // Si está junto a la playa o al mar, vuelve a ser arena; si no, hierba
        bool nearBeach = false;
        foreach (Vector2I n in Neighbors4) nearBeach |= g.IsBeach(t + n) || g.IsSea(t + n);
        g.SetTileType(t, nearBeach ? TileType.Beach : TileType.Land);

        // Rellenar puede partir un río: lo que quede suelto no puede ser un estanque demasiado grande
        foreach (Vector2I n in Neighbors4)
        {
            if (!g.IsInlandWater(t + n)) continue;
            WaterBody body = Explore(g, t + n);
            if (!body.HasSource && body.Size > MaxPondTiles)
            {
                g.SetTileType(t, TileType.Water);
                reason = $"Quedaría un estanque suelto de más de {MaxPondTiles} casillas";
                return false;
            }
        }

        return true;
    }

    // ---------- Conexión del agua ----------

    private struct WaterBody
    {
        public bool HasSource;  // conectada al mar o a un manantial
        public int Size;        // casillas de agua interior recorridas
    }

    /// <summary>
    /// Recorre el agua interior conectada (en línea recta) desde 'start'. Se detiene en cuanto
    /// encuentra el mar o un manantial: entonces es un río y no hace falta seguir contando.
    /// </summary>
    private static WaterBody Explore(TileGrid g, Vector2I start)
    {
        var body = new WaterBody();
        var visited = new HashSet<Vector2I> { start };
        var queue = new Queue<Vector2I>();
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            Vector2I t = queue.Dequeue();
            body.Size++;
            if (g.IsSpring(t)) { body.HasSource = true; return body; }

            foreach (Vector2I n in Neighbors4)
            {
                Vector2I next = t + n;
                if (!g.IsInside(next) || visited.Contains(next)) continue;
                if (g.IsSea(next)) { body.HasSource = true; return body; }
                if (g.IsInlandWater(next))
                {
                    visited.Add(next);
                    queue.Enqueue(next);
                }
            }
        }
        return body;
    }

    private static int CountInlandWater(TileGrid g, out int islandTiles)
    {
        int water = 0;
        islandTiles = 0;
        for (int z = 0; z < g.Depth; z++)
        for (int x = 0; x < g.Width; x++)
        {
            if (g.IsSea(x, z)) continue;
            islandTiles++;
            if (g.GetTileType(x, z) == TileType.Water) water++;
        }
        return water;
    }
}
