using Godot;
using Godot.Collections;

public partial class Player : CharacterBody3D
{
    [Export] public float Speed = 6.0f;
    [Export] public Node3D CameraPivot;

    [ExportGroup("Trepar")]
    [Export] public float ClimbHoldTime = 0.35f;   // segundos empujando contra el escalón antes de trepar
    [Export] public float ClimbMaxHeight = 1.2f;   // escalón más alto que se puede trepar (1 nivel)
    [Export] public float ClimbDuration = 0.35f;   // duración de la animación de trepar

    private const float HalfHeight = 0.6f;  // la cápsula mide 1,2 m: el centro está a 0,6 m de los pies

    private float _gravity = ProjectSettings.GetSetting("physics/3d/default_gravity").AsSingle();
    private float _climbTimer;
    private bool _isClimbing;

    public override void _PhysicsProcess(double delta)
    {
        if (_isClimbing) return;  // durante la animación de trepar no hay física

        Vector3 velocity = Velocity;

        // Aplicar gravedad
        if (!IsOnFloor())
        {
            velocity.Y -= _gravity * (float)delta;
        }

        // Lectura directa de entradas (WASD + Flechas)
        Vector2 inputDir = Vector2.Zero;
        if (Input.IsKeyPressed(Key.W) || Input.IsKeyPressed(Key.Up)) inputDir.Y -= 1.0f;
        if (Input.IsKeyPressed(Key.S) || Input.IsKeyPressed(Key.Down)) inputDir.Y += 1.0f;
        if (Input.IsKeyPressed(Key.A) || Input.IsKeyPressed(Key.Left)) inputDir.X -= 1.0f;
        if (Input.IsKeyPressed(Key.D) || Input.IsKeyPressed(Key.Right)) inputDir.X += 1.0f;

        inputDir = inputDir.Normalized();

        // Calcular dirección relativa a la cámara
        Vector3 direction = new Vector3(inputDir.X, 0, inputDir.Y);

        if (CameraPivot != null && direction != Vector3.Zero)
        {
            direction = direction.Rotated(Vector3.Up, CameraPivot.Rotation.Y);
        }

        // Aplicar velocidad
        if (direction != Vector3.Zero)
        {
            velocity.X = direction.X * Speed;
            velocity.Z = direction.Z * Speed;
        }
        else
        {
            velocity.X = Mathf.MoveToward(Velocity.X, 0, Speed);
            velocity.Z = Mathf.MoveToward(Velocity.Z, 0, Speed);
        }

        Velocity = velocity;
        MoveAndSlide();

        UpdateClimb(direction, (float)delta);
    }

    /// <summary>
    /// Trepar: si el personaje empuja contra un escalón de hasta 1 nivel durante un momento,
    /// sube encima con una pequeña animación. Así nunca se queda atrapado en un hoyo.
    /// </summary>
    private void UpdateClimb(Vector3 direction, float delta)
    {
        // "Empujar contra un escalón" = hay intención de moverse pero casi no se avanza
        // (más fiable que IsOnWall: el pie del acantilado es una curva, no una pared recta)
        Vector3 real = GetRealVelocity();
        bool pushingWall = direction != Vector3.Zero && real.Y > -1.0f
                           && new Vector2(real.X, real.Z).Length() < Speed * 0.3f;
        _climbTimer = pushingWall ? _climbTimer + delta : 0.0f;
        if (_climbTimer < ClimbHoldTime) return;
        _climbTimer = 0.0f;

        // Buscamos el suelo encima del escalón, un poco por delante del personaje
        Vector3 ahead = GlobalPosition + direction.Normalized() * 0.7f;
        var query = PhysicsRayQueryParameters3D.Create(ahead + Vector3.Up * 2.0f, ahead + Vector3.Down * 1.0f);
        query.CollisionMask = 1;  // solo el terreno (no la colisión del agua para el ratón)
        query.Exclude = new Array<Rid> { GetRid() };
        Dictionary hit = GetWorld3D().DirectSpaceState.IntersectRay(query);
        if (hit.Count == 0) return;

        Vector3 ground = (Vector3)hit["position"];
        float rise = ground.Y - (GlobalPosition.Y - HalfHeight);        if (rise < 0.3f || rise > ClimbMaxHeight) return;  // demasiado bajo (se sube andando) o demasiado alto

        // Animación: primero arriba, luego hacia delante
        _isClimbing = true;
        Velocity = Vector3.Zero;
        Vector3 top = new Vector3(GlobalPosition.X, ground.Y + HalfHeight + 0.05f, GlobalPosition.Z);
        Vector3 end = new Vector3(ground.X, ground.Y + HalfHeight + 0.05f, ground.Z);

        Tween tween = CreateTween();
        tween.TweenProperty(this, "global_position", top, ClimbDuration * 0.6f)
             .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
        tween.TweenProperty(this, "global_position", end, ClimbDuration * 0.4f);
        tween.Finished += () => _isClimbing = false;
    }
}
