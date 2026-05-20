using Fdia2.Core;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace Fdia2.UI.Controls;

public sealed class PointCloudControl : IDisposable
{
    int shaderProgram;
    int vao;
    int vbo;
    int pointCount;

    public int PointCount => pointCount;

    public void Initialize()
    {
        shaderProgram = ShaderUtils.CreateProgramFromResources("point_cloud.vert", "point_cloud.frag", "point cloud");
        vao = GL.GenVertexArray();
        vbo = GL.GenBuffer();

        GL.BindVertexArray(vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, 0, IntPtr.Zero, BufferUsageHint.DynamicDraw);

        var stride = 4 * sizeof(float);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 1, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        GL.BindVertexArray(0);
    }

    public void Upload(float[] vertices, int loadedPointCount)
    {
        pointCount = loadedPointCount;
        GL.BindVertexArray(vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.StaticDraw);
        GL.BindVertexArray(0);
    }

    public void Render(CameraState camera, Vector2i clientSize)
    {
        if (pointCount <= 0 || vao == 0 || shaderProgram == 0)
            return;
        if (clientSize.X <= 0 || clientSize.Y <= 0)
            return;

        camera.GetMatrices(clientSize, out var view, out var projection);
        GL.UseProgram(shaderProgram);
        GL.UniformMatrix4(GL.GetUniformLocation(shaderProgram, "uView"), false, ref view);
        GL.UniformMatrix4(GL.GetUniformLocation(shaderProgram, "uProjection"), false, ref projection);
        GL.BindVertexArray(vao);
        GL.DrawArrays(PrimitiveType.Points, 0, pointCount);
        GL.BindVertexArray(0);
        GL.UseProgram(0);
    }

    public static float[] BuildVertices(VolumeProcessor.VolumeData volumeData, out int loadedPointCount)
    {
        loadedPointCount = checked((int)volumeData.NonZeroVoxelCount);
        if (loadedPointCount == 0)
            return Array.Empty<float>();

        uint maxValue = 0;
        foreach (var v in volumeData.Voxels) if (v > maxValue) maxValue = v;
        float scale = maxValue == 0 ? 1f : 1f / maxValue;

        var vertices = new float[loadedPointCount * 4];
        var write = 0;
        var voxels = volumeData.Voxels;
        for (int z = 0; z < VolumeProcessor.AxisLength; z++)
        {
            var nz = z / (VolumeProcessor.AxisLength - 1f) - 0.5f;
            for (int y = 0; y < VolumeProcessor.AxisLength; y++)
            {
                var ny = y / (VolumeProcessor.AxisLength - 1f) - 0.5f;
                var yzBase = (z << 16) | (y << 8);
                for (int x = 0; x < VolumeProcessor.AxisLength; x++)
                {
                    var value = voxels[yzBase | x];
                    if (value == 0)
                        continue;

                    vertices[write++] = x / (VolumeProcessor.AxisLength - 1f) - 0.5f;
                    vertices[write++] = ny;
                    vertices[write++] = nz;
                    vertices[write++] = value * scale;
                }
            }
        }

        if (write == vertices.Length)
            return vertices;

        var resized = new float[write];
        Array.Copy(vertices, resized, write);
        loadedPointCount = write / 4;
        return resized;
    }

    public void Dispose()
    {
        if (vbo != 0) { GL.DeleteBuffer(vbo); vbo = 0; }
        if (vao != 0) { GL.DeleteVertexArray(vao); vao = 0; }
        if (shaderProgram != 0) { GL.DeleteProgram(shaderProgram); shaderProgram = 0; }
    }
}
