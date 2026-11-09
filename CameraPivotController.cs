using Godot;

public partial class CameraPivotController : Node3D
{
    [Export] public Node3D Target;
    [Export] public float FollowSpeed = 5.0f;
    [Export] public float RotationDuration = 0.35f;

    private Tween _rotationTween;

    public override void _Ready()
    {
        if (Target == null && HasNode("../Player"))
        {
            Target = GetNode<Node3D>("../Player");
        }
    }

    public override void _Process(double delta)
    {
        if (Target != null)
        {
            GlobalPosition = GlobalPosition.Lerp(Target.GlobalPosition, (float)delta * FollowSpeed);
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey keyEvent && keyEvent.Pressed && !keyEvent.Echo)
        {
            if (keyEvent.Keycode == Key.Q)
            {
                RotateCamera(90.0f);
            }
            else if (keyEvent.Keycode == Key.E)
            {
                RotateCamera(-90.0f);
            }
        }
    }

    private void RotateCamera(float angleDegrees)
    {
        if (_rotationTween != null && _rotationTween.IsRunning())
            _rotationTween.Kill();

        // 1. Normalizar la rotación Y actual del nodo al rango [-PI, PI]
        float currentY = Mathf.Wrap(Rotation.Y, -Mathf.Pi, Mathf.Pi);
        
        Vector3 currentRot = Rotation;
        currentRot.Y = currentY;
        Rotation = currentRot;

        // 2. Calcular el ángulo de destino relativo a la rotación actual
        float targetY = currentY + Mathf.DegToRad(angleDegrees);

        // 3. Interpolar hacia el nuevo ángulo
        _rotationTween = CreateTween();
        _rotationTween.TweenProperty(this, "rotation:y", targetY, RotationDuration)
                      .SetTrans(Tween.TransitionType.Cubic)
                      .SetEase(Tween.EaseType.Out);
    }
}