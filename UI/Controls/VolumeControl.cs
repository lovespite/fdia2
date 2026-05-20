using Fdia2.Core;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace Fdia2.UI.Controls;

public enum VolumeRenderMode
{
    Composite = 0,
    Mip = 1,
    Hybrid = 2,
}

public sealed class VolumeControl : IDisposable
{
    const float RayStepSize = 0.0065f;
    const float EarlyTerminateAlpha = 0.98f;

    int shaderProgram;
    int vao;
    int vbo;
    int volumeTextureId;
    int transferFunctionTextureId;
    bool hasVolumeTexture;
    float volumeValueScale = 1f;
    float autoOpacityMultiplier = 1f;

    public bool HasVolume => hasVolumeTexture;
    public float AutoOpacityMultiplier => autoOpacityMultiplier;

    public void Initialize()
    {
        shaderProgram = ShaderUtils.CreateProgramFromResources("volume.vert", "volume.frag", "volume");
        vao = GL.GenVertexArray();
        vbo = GL.GenBuffer();

        float[] quadVertices =
        [
          -1f, -1f, 0f, 0f,
           1f, -1f, 1f, 0f,
           1f,  1f, 1f, 1f,
          -1f, -1f, 0f, 0f,
           1f,  1f, 1f, 1f,
          -1f,  1f, 0f, 1f,
        ];

        GL.BindVertexArray(vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, quadVertices.Length * sizeof(float), quadVertices, BufferUsageHint.StaticDraw);
        var stride = 4 * sizeof(float);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 2 * sizeof(float));
        GL.BindVertexArray(0);

        transferFunctionTextureId = CreateTransferFunctionTexture();

        GL.UseProgram(shaderProgram);
        GL.Uniform1(GL.GetUniformLocation(shaderProgram, "uVolumeTex"), 0);
        GL.Uniform1(GL.GetUniformLocation(shaderProgram, "uTransferTex"), 1);
        GL.UseProgram(0);
    }

    public void UploadVolume(uint[] voxels)
    {
        if (voxels.Length != VolumeProcessor.VoxelCount)
            throw new ArgumentException($"Volume voxel length must be {VolumeProcessor.VoxelCount}.", nameof(voxels));

        if (volumeTextureId == 0)
            volumeTextureId = GL.GenTexture();

        var floatVoxels = new float[voxels.Length];
        for (int i = 0; i < voxels.Length; i++)
            floatVoxels[i] = voxels[i];

        GL.BindTexture(TextureTarget.Texture3D, volumeTextureId);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
        GL.TexImage3D(
          TextureTarget.Texture3D,
          0,
          PixelInternalFormat.R32f,
          VolumeProcessor.AxisLength,
          VolumeProcessor.AxisLength,
          VolumeProcessor.AxisLength,
          0,
          PixelFormat.Red,
          PixelType.Float,
          floatVoxels);
        GL.BindTexture(TextureTarget.Texture3D, 0);
        hasVolumeTexture = true;

        var (scale, autoOpacity) = ComputeVolumeValueScale(voxels);
        volumeValueScale = scale;
        autoOpacityMultiplier = autoOpacity;
    }

    public void ClearVolume()
    {
        hasVolumeTexture = false;
        volumeValueScale = 1f;
        autoOpacityMultiplier = 1f;
    }

    public void Render(
      CameraState camera,
      Vector2i clientSize,
      VolumeRenderMode mode,
      float densityGain,
      float opacityGain,
      bool clipEnabled,
      float clipOffset)
    {
        if (!hasVolumeTexture || shaderProgram == 0 || vao == 0 || volumeTextureId == 0 || transferFunctionTextureId == 0)
            return;
        if (clientSize.X <= 0 || clientSize.Y <= 0)
            return;

        var fov = MathHelper.DegreesToRadians(45f);
        var aspect = clientSize.X / (float)clientSize.Y;
        var tanHalfFov = MathF.Tan(fov * 0.5f);
        camera.GetPose(out var eye, out var forward, out var right, out var up);

        GL.Disable(EnableCap.DepthTest);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        GL.UseProgram(shaderProgram);
        GL.Uniform3(GL.GetUniformLocation(shaderProgram, "uCameraPosition"), eye);
        GL.Uniform3(GL.GetUniformLocation(shaderProgram, "uCameraForward"), forward);
        GL.Uniform3(GL.GetUniformLocation(shaderProgram, "uCameraRight"), right);
        GL.Uniform3(GL.GetUniformLocation(shaderProgram, "uCameraUp"), up);
        GL.Uniform1(GL.GetUniformLocation(shaderProgram, "uAspect"), aspect);
        GL.Uniform1(GL.GetUniformLocation(shaderProgram, "uTanHalfFov"), tanHalfFov);
        GL.Uniform1(GL.GetUniformLocation(shaderProgram, "uStepSize"), RayStepSize);
        GL.Uniform1(GL.GetUniformLocation(shaderProgram, "uDensityGain"), densityGain);
        GL.Uniform1(GL.GetUniformLocation(shaderProgram, "uVolumeValueScale"), volumeValueScale);
        GL.Uniform1(GL.GetUniformLocation(shaderProgram, "uOpacityGain"), opacityGain * autoOpacityMultiplier);
        GL.Uniform1(GL.GetUniformLocation(shaderProgram, "uEarlyTerminateAlpha"), EarlyTerminateAlpha);
        GL.Uniform1(GL.GetUniformLocation(shaderProgram, "uRenderMode"), (int)mode);
        GL.Uniform1(GL.GetUniformLocation(shaderProgram, "uClipEnabled"), clipEnabled ? 1 : 0);
        GL.Uniform3(GL.GetUniformLocation(shaderProgram, "uClipNormal"), Vector3.UnitY);
        GL.Uniform1(GL.GetUniformLocation(shaderProgram, "uClipOffset"), clipOffset);

        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture3D, volumeTextureId);
        GL.ActiveTexture(TextureUnit.Texture1);
        GL.BindTexture(TextureTarget.Texture1D, transferFunctionTextureId);

        GL.BindVertexArray(vao);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
        GL.BindVertexArray(0);
        GL.BindTexture(TextureTarget.Texture1D, 0);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture3D, 0);
        GL.UseProgram(0);

        GL.Disable(EnableCap.Blend);
        GL.Enable(EnableCap.DepthTest);
    }

    static (float Scale, float AutoOpacity) ComputeVolumeValueScale(uint[] voxels)
    {
        uint maxValue = 0;
        double sumValue = 0;
        int nonZeroCount = 0;

        foreach (var value in voxels)
        {
            if (value > maxValue)
                maxValue = value;
            if (value > 0)
            {
                sumValue += value;
                nonZeroCount++;
            }
        }

        float scale = maxValue == 0 ? 1f : 1f / maxValue;
        float meanDensity = nonZeroCount == 0 ? 0f : (float)(sumValue / nonZeroCount) * scale;
        float autoOpacity = Math.Clamp(0.25f / MathF.Max(meanDensity, 0.005f), 1f, 15f);
        return (scale, autoOpacity);
    }

    static int CreateTransferFunctionTexture()
    {
        var data = BuildTransferFunctionBytes();
        var textureId = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture1D, textureId);
        GL.TexParameter(TextureTarget.Texture1D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture1D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture1D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        GL.TexImage1D(TextureTarget.Texture1D, 0, PixelInternalFormat.Rgba8, 256, 0, PixelFormat.Rgba, PixelType.UnsignedByte, data);
        GL.BindTexture(TextureTarget.Texture1D, 0);
        return textureId;
    }

    static byte[] BuildTransferFunctionBytes()
    {
        var data = new byte[256 * 4];
        for (int i = 0; i < 256; i++)
        {
            var t = i / 255f;
            var color = EvaluateTransferColor(t);
            var alpha = t;
            data[i * 4] = (byte)Math.Clamp((int)MathF.Round(color.X * 255f), 0, 255);
            data[i * 4 + 1] = (byte)Math.Clamp((int)MathF.Round(color.Y * 255f), 0, 255);
            data[i * 4 + 2] = (byte)Math.Clamp((int)MathF.Round(color.Z * 255f), 0, 255);
            data[i * 4 + 3] = (byte)Math.Clamp((int)MathF.Round(alpha * 255f), 0, 255);
        }
        return data;
    }

    static Vector3 EvaluateTransferColor(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        var a = new Vector3(0.05f, 0.12f, 0.45f);
        var b = new Vector3(0.00f, 0.90f, 1.00f);
        var c = new Vector3(1.00f, 0.95f, 0.20f);
        var d = new Vector3(1.00f, 0.25f, 0.00f);
        if (t < 0.33f)
            return Vector3.Lerp(a, b, t / 0.33f);
        if (t < 0.66f)
            return Vector3.Lerp(b, c, (t - 0.33f) / 0.33f);
        return Vector3.Lerp(c, d, (t - 0.66f) / 0.34f);
    }

    public void Dispose()
    {
        if (vbo != 0) { GL.DeleteBuffer(vbo); vbo = 0; }
        if (vao != 0) { GL.DeleteVertexArray(vao); vao = 0; }
        if (shaderProgram != 0) { GL.DeleteProgram(shaderProgram); shaderProgram = 0; }
        if (volumeTextureId != 0) { GL.DeleteTexture(volumeTextureId); volumeTextureId = 0; }
        if (transferFunctionTextureId != 0) { GL.DeleteTexture(transferFunctionTextureId); transferFunctionTextureId = 0; }
    }
}
