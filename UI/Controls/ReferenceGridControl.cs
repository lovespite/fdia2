using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace Fdia2.UI.Controls;

public sealed class ReferenceGridControl : IDisposable
{
    public static readonly Vector3 Origin = new(-0.5f, -0.5f, -0.5f);
    const float AxisLengthMin = 4f;
    const float AxisLengthFactor = 6f;
    const float GridExtentMin = 10f;
    const float GridExtentFactor = 10f;
    const float GridMinorCell = 0.05f;
    const float GridMajorCell = 0.25f;

    int axisShaderProgram;
    int axisVao;
    int axisVbo;
    int axisVertexCount;

    int gridShaderProgram;
    int gridVao;
    int gridVbo;
    int gridVertexCount;

    public void Initialize()
    {
        axisShaderProgram = ShaderUtils.CreateProgramFromResources("reference.vert", "reference.frag", "reference");
        axisVao = GL.GenVertexArray();
        axisVbo = GL.GenBuffer();
        axisVertexCount = 6;

        GL.BindVertexArray(axisVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, axisVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, axisVertexCount * 6 * sizeof(float), IntPtr.Zero, BufferUsageHint.DynamicDraw);

        var stride = 6 * sizeof(float);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        GL.BindVertexArray(0);

        gridShaderProgram = ShaderUtils.CreateProgramFromResources("reference_grid.vert", "reference_grid.frag", "reference grid");
        gridVao = GL.GenVertexArray();
        gridVbo = GL.GenBuffer();
        gridVertexCount = 6;

        GL.BindVertexArray(gridVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, gridVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, gridVertexCount * 3 * sizeof(float), IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
        GL.BindVertexArray(0);
    }

    public void Render(CameraState camera, Vector2i clientSize)
    {
        if (axisVertexCount <= 0 || axisVao == 0 || axisShaderProgram == 0)
            return;
        if (gridVertexCount <= 0 || gridVao == 0 || gridShaderProgram == 0)
            return;
        if (clientSize.X <= 0 || clientSize.Y <= 0)
            return;

        camera.GetPose(out var eye, out _, out _, out _);
        UpdateGeometry(eye);
        camera.GetMatrices(clientSize, out var view, out var projection);

        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.UseProgram(gridShaderProgram);
        GL.UniformMatrix4(GL.GetUniformLocation(gridShaderProgram, "uView"), false, ref view);
        GL.UniformMatrix4(GL.GetUniformLocation(gridShaderProgram, "uProjection"), false, ref projection);
        GL.Uniform3(GL.GetUniformLocation(gridShaderProgram, "uReferenceOrigin"), Origin);
        GL.Uniform3(GL.GetUniformLocation(gridShaderProgram, "uCameraPosition"), eye);
        GL.Uniform1(GL.GetUniformLocation(gridShaderProgram, "uMinorCell"), GridMinorCell);
        GL.Uniform1(GL.GetUniformLocation(gridShaderProgram, "uMajorCell"), GridMajorCell);
        GL.Uniform1(GL.GetUniformLocation(gridShaderProgram, "uFadeDistance"), MathF.Max(GridExtentMin, (eye - Origin).Length * 1.2f));
        GL.Uniform3(GL.GetUniformLocation(gridShaderProgram, "uMinorColor"), new Vector3(0.15f, 0.15f, 0.17f));
        GL.Uniform3(GL.GetUniformLocation(gridShaderProgram, "uMajorColor"), new Vector3(0.30f, 0.30f, 0.33f));
        GL.BindVertexArray(gridVao);
        GL.DrawArrays(PrimitiveType.Triangles, 0, gridVertexCount);
        GL.BindVertexArray(0);
        GL.UseProgram(0);
        GL.Disable(EnableCap.Blend);

        GL.LineWidth(2f);
        GL.UseProgram(axisShaderProgram);
        GL.UniformMatrix4(GL.GetUniformLocation(axisShaderProgram, "uView"), false, ref view);
        GL.UniformMatrix4(GL.GetUniformLocation(axisShaderProgram, "uProjection"), false, ref projection);
        GL.BindVertexArray(axisVao);
        GL.DrawArrays(PrimitiveType.Lines, 0, axisVertexCount);
        GL.BindVertexArray(0);
        GL.UseProgram(0);
        GL.LineWidth(1f);
    }

    void UpdateGeometry(Vector3 eye)
    {
        var distanceToOrigin = (eye - Origin).Length;
        var axisLength = MathF.Max(AxisLengthMin, distanceToOrigin * AxisLengthFactor);
        var axisEndX = Origin + new Vector3(axisLength, 0f, 0f);
        var axisEndY = Origin + new Vector3(0f, axisLength, 0f);
        var axisEndZ = Origin + new Vector3(0f, 0f, axisLength);

        float[] axisVertices =
        [
          Origin.X, Origin.Y, Origin.Z, 0.95f, 0.25f, 0.25f,
          axisEndX.X, axisEndX.Y, axisEndX.Z, 0.95f, 0.25f, 0.25f,
          Origin.X, Origin.Y, Origin.Z, 0.30f, 0.95f, 0.30f,
          axisEndY.X, axisEndY.Y, axisEndY.Z, 0.30f, 0.95f, 0.30f,
          Origin.X, Origin.Y, Origin.Z, 0.30f, 0.55f, 0.98f,
          axisEndZ.X, axisEndZ.Y, axisEndZ.Z, 0.30f, 0.55f, 0.98f,
        ];

        GL.BindBuffer(BufferTarget.ArrayBuffer, axisVbo);
        GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, axisVertices.Length * sizeof(float), axisVertices);

        var gridExtent = MathF.Max(GridExtentMin, distanceToOrigin * GridExtentFactor);
        var centerX = MathF.Floor((eye.X - Origin.X) / GridMajorCell) * GridMajorCell + Origin.X;
        var centerZ = MathF.Floor((eye.Z - Origin.Z) / GridMajorCell) * GridMajorCell + Origin.Z;
        var y = Origin.Y;
        var x0 = centerX - gridExtent;
        var x1 = centerX + gridExtent;
        var z0 = centerZ - gridExtent;
        var z1 = centerZ + gridExtent;

        float[] gridVertices =
        [
          x0, y, z0,
          x1, y, z0,
          x1, y, z1,
          x0, y, z0,
          x1, y, z1,
          x0, y, z1,
        ];

        GL.BindBuffer(BufferTarget.ArrayBuffer, gridVbo);
        GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, gridVertices.Length * sizeof(float), gridVertices);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
    }

    public void Dispose()
    {
        if (axisVbo != 0) { GL.DeleteBuffer(axisVbo); axisVbo = 0; }
        if (axisVao != 0) { GL.DeleteVertexArray(axisVao); axisVao = 0; }
        if (axisShaderProgram != 0) { GL.DeleteProgram(axisShaderProgram); axisShaderProgram = 0; }
        if (gridVbo != 0) { GL.DeleteBuffer(gridVbo); gridVbo = 0; }
        if (gridVao != 0) { GL.DeleteVertexArray(gridVao); gridVao = 0; }
        if (gridShaderProgram != 0) { GL.DeleteProgram(gridShaderProgram); gridShaderProgram = 0; }
    }
}
