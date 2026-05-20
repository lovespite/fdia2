using OpenTK.Mathematics;

namespace Fdia2.UI.Controls;

public sealed class CameraState
{
    public float Yaw { get; set; } = MathHelper.DegreesToRadians(45f);
    public float Pitch { get; set; } = MathHelper.DegreesToRadians(25f);
    public float Distance { get; set; } = 2.4f;

    public void Reset()
    {
        Yaw = MathHelper.DegreesToRadians(45f);
        Pitch = MathHelper.DegreesToRadians(25f);
        Distance = 2.4f;
    }

    public void GetPose(out Vector3 eye, out Vector3 forward, out Vector3 right, out Vector3 up)
    {
        eye = new Vector3(
          Distance * MathF.Cos(Pitch) * MathF.Cos(Yaw),
          Distance * MathF.Sin(Pitch),
          Distance * MathF.Cos(Pitch) * MathF.Sin(Yaw));

        forward = Vector3.Normalize(-eye);
        right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        up = Vector3.Normalize(Vector3.Cross(right, forward));
    }

    public void GetMatrices(Vector2i clientSize, out Matrix4 view, out Matrix4 projection)
    {
        var aspect = clientSize.X / (float)clientSize.Y;
        projection = Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(45f), aspect, 0.01f, 100f);
        GetPose(out var eye, out _, out _, out _);
        view = Matrix4.LookAt(eye, Vector3.Zero, Vector3.UnitY);
    }
}
