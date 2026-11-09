using Godot;

public partial class CameraPivotController : Node3D
{
    [Export] public Node3D Target;
    [Export] public float FollowSpeed = 5.0f;
    [Export] public float RotationDuration = 0.35f;

    [ExportGroup("Zoom (rueda del ratón)")]
    [Export] public float MinZoom = 0.6f;   // 0.6 = 60 % de la distancia inicial (más cerca)
    [Export] public float MaxZoom = 1.5f;   // 1.5 = 150 % (más lejos)
    [Export] public float ZoomStep = 0.1f;
    [Export] public float ZoomSpeed = 8.0f;

    private Tween _rotationTween;
    private Camera3D _camera;
    private Vector3 _baseCameraOffset;  // posición de la cámara respecto al pivote, tal como está en la escena
    private float _zoom = 1.0f;

    public override void _Ready()
    {
        if (Target == null && HasNode("../Player"))
        {
            Target = GetNode<Node3D>("../Player");
        }

        _camera = GetNodeOrNull<Camera3D>("Camera3D");
        if (_camera != null) _baseCameraOffset = _camera.Position;
    }

    public override void _Process(double delta)
    {
        if (Target != null)
        {
            GlobalPosition = GlobalPosition.Lerp(Target.GlobalPosition, (float)delta * FollowSpeed);
        }

        // El zoom acerca/aleja la cámara en su misma línea de visión, con suavizado
        if (_camera != null)
        {
            _camera.Position = _camera.Position.Lerp(_baseCameraOffset * _zoom, (float)delta * ZoomSpeed);
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton wheel && wheel.Pressed)
        {
            if (wheel.ButtonIndex == MouseButton.WheelUp)
                _zoom = Mathf.Clamp(_zoom - ZoomStep, MinZoom, MaxZoom);
            else if (wheel.ButtonIndex == MouseButton.WheelDown)
                _zoom = Mathf.Clamp(_zoom + ZoomStep, MinZoom, MaxZoom);
        }

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