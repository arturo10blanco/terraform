using Godot;

public partial class Player : CharacterBody3D
{
    [Export] public float Speed = 6.0f;
    [Export] public Node3D CameraPivot;

    private float _gravity = ProjectSettings.GetSetting("physics/3d/default_gravity").AsSingle();

    public override void _PhysicsProcess(double delta)
    {
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
    }
}