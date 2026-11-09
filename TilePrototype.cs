using Godot;

/// <summary>
/// PROTOTIPO: rellena la cuadrícula con una isla de prueba: mar alrededor, anillo de playa,
/// pradera, una montaña de 3 niveles con un manantial en la cima y un río que baja por
/// cascadas, pasa por un estanque y desemboca en el mar.
/// </summary>
public static class TilePrototype
{
    public static void Fill(TileGrid grid)
    {
        Vector2 center = new Vector2(grid.Width, grid.Depth) * 0.5f;

        // 1) Forma de la isla: un círculo "ondulado" para que la costa no sea perfecta
        for (int z = 0; z < grid.Depth; z++)
        for (int x = 0; x < grid.Width; x++)
        {
            Vector2 c = new Vector2(x + 0.5f, z + 0.5f);
            Vector2 fromCenter = c - center;
            float angle = Mathf.Atan2(fromCenter.Y, fromCenter.X);
            float coast = 50.0f + 4.0f * Mathf.Sin(3 * angle) + 2.5f * Mathf.Sin(7 * angle + 1.0f);
            float dist = fromCenter.Length();

            if (dist > coast)
            {
                grid.SetTileType(x, z, TileType.Sea);
                grid.SetLevel(x, z, 0);
            }
            else if (dist > coast - 4.0f)
            {
                grid.SetTileType(x, z, TileType.Beach);
                grid.SetLevel(x, z, 0);
            }
            else
            {
                // Pradera (nivel 1) con una meseta de nivel 2 y una cima de nivel 3
                int level = 1;
                if (InEllipse(c, new Vector2(64, 40), new Vector2(24, 15))) level = 2;
                if (InEllipse(c, new Vector2(60, 36), new Vector2(12, 8))) level = 3;
                grid.SetLevel(x, z, level);
            }
        }

        // 2) Río: nace en la cima, baja por cascadas, cruza la playa y llega al mar.
        //    El agua conserva el nivel de la casilla: al cruzar un acantilado sale una cascada.
        Vector2[] river =
        {
            new(57, 33), new(59, 40), new(64, 47), new(69, 53), new(70, 60),
            new(67, 68), new(62, 75), new(64, 83), new(72, 88), new(80, 90),
            new(88, 98), new(96, 108), new(102, 116)
        };
        for (int z = 0; z < grid.Depth; z++)
        for (int x = 0; x < grid.Width; x++)
        {
            if (grid.IsSea(x, z)) continue;
            if (DistanceToPolyline(new Vector2(x + 0.5f, z + 0.5f), river) <= 1.05f)
                grid.SetTileType(x, z, TileType.Water);
        }

        // 3) Estanque junto al río
        for (int z = 80; z < 100; z++)
        for (int x = 72; x < 92; x++)
        {
            if (new Vector2(x + 0.5f, z + 0.5f).DistanceTo(new Vector2(82, 91)) <= 4.5f)
                grid.SetTileType(x, z, TileType.Water);
        }

        // 4) Manantial: el nacimiento del río en la cima
        for (int z = 31; z <= 35; z++)
        for (int x = 55; x <= 59; x++)
        {
            if (grid.GetTileType(x, z) == TileType.Water &&
                new Vector2(x + 0.5f, z + 0.5f).DistanceTo(new Vector2(57, 33)) <= 1.5f)
                grid.SetTileType(x, z, TileType.Spring);
        }
    }

    private static bool InEllipse(Vector2 p, Vector2 center, Vector2 radii)
    {
        Vector2 d = (p - center) / radii;
        return d.LengthSquared() <= 1.0f;
    }

    private static float DistanceToPolyline(Vector2 p, Vector2[] points)
    {
        float best = float.MaxValue;
        for (int i = 0; i < points.Length - 1; i++)
        {
            Vector2 a = points[i], b = points[i + 1];
            Vector2 ab = b - a;
            float t = Mathf.Clamp((p - a).Dot(ab) / ab.LengthSquared(), 0.0f, 1.0f);
            best = Mathf.Min(best, p.DistanceTo(a + ab * t));
        }
        return best;
    }
}
