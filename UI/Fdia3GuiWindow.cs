using Fdia2.Core;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using SkiaSharp;

namespace Fdia2.UI;

public sealed class Fdia3GuiWindow : GameWindow
{
  enum RenderMode
  {
    PointCloud,
    VolumeComposite,
    VolumeMip,
  }

  readonly string outputDir;
  readonly List<string> pendingFiles;
  readonly bool viewOutputDir;
  readonly List<string> previewFiles = [];
  Task<List<string>>? processingTask;
  int previewIndex = -1;

  int pointCloudShaderProgram;
  int pointCloudVao;
  int pointCloudVbo;
  int pointCount;

  int referenceShaderProgram;
  int referenceVao;
  int referenceVbo;
  int referenceVertexCount;
  int referenceGridShaderProgram;
  int referenceGridVao;
  int referenceGridVbo;
  int referenceGridVertexCount;

  int volumeShaderProgram;
  int volumeVao;
  int volumeVbo;
  int volumeTextureId;
  int transferFunctionTextureId;
  bool hasVolumeTexture;
  float volumeValueScale = 1f;

  int hudShaderProgram;
  int hudVao;
  int hudVbo;
  int hudTextureId;
  int hudTextureWidth;
  int hudTextureHeight;
  string hudText = string.Empty;
  bool hudTextDirty = true;

  RenderMode renderMode = RenderMode.PointCloud;
  bool clippingEnabled = false;
  float clipOffset = 0f;
  float densityGain = DensityGainMax;
  float opacityGain = OpacityGainMax;
  string status = "Ready";

  bool isRotating;
  Vector2 lastMousePosition;
  float yaw = MathHelper.DegreesToRadians(45f);
  float pitch = MathHelper.DegreesToRadians(25f);
  float distance = 2.4f;

  const float ClipOffsetMin = -0.5f;
  const float ClipOffsetMax = 0.5f;
  const float ClipOffsetStep = 0.02f;
  const float DensityGainMin = 0.1f;
  const float DensityGainMax = 6.0f;
  const float OpacityGainMin = 0.05f;
  const float OpacityGainMax = 8.0f;
  const float RayStepSize = 0.0065f;
  const float EarlyTerminateAlpha = 0.98f;
  const string BaseWindowTitle = "fdia3";
  const float HudFontSizePx = 14f;
  const float HudLineSpacingPx = 3f;
  const float HudPaddingPx = 10f;
  const float HudCornerRadiusPx = 8f;
  const int HudMarginPx = 12;
  static readonly Vector3 ReferenceOrigin = new(-0.5f, -0.5f, -0.5f);
  const float ReferenceAxisLengthMin = 4f;
  const float ReferenceAxisLengthFactor = 6f;
  const float ReferenceGridExtentMin = 10f;
  const float ReferenceGridExtentFactor = 10f;
  const float ReferenceGridMinorCell = 0.05f;
  const float ReferenceGridMajorCell = 0.25f;

  public Fdia3GuiWindow(string outputDir, List<string> initialFiles, bool viewOutputDir = false)
    : base(
        new GameWindowSettings
        {
          UpdateFrequency = 120,
        },
        new NativeWindowSettings
        {
          Title = BaseWindowTitle,
          ClientSize = new Vector2i(1100, 700)
        })
  {
    this.outputDir = outputDir;
    pendingFiles = initialFiles;
    this.viewOutputDir = viewOutputDir;
  }

  protected override void OnLoad()
  {
    base.OnLoad();
    VSync = VSyncMode.On;
    GL.Enable(EnableCap.DepthTest);
    GL.Enable(EnableCap.ProgramPointSize);

    InitializeReferenceRenderer();
    InitializePointCloudRenderer();
    InitializeVolumeRenderer();
    InitializeHudRenderer();
    PrintHelp();

    if (pendingFiles.Count > 0)
      StartProcessingPendingFiles();
    else
      status = "Drag files into this window, then press F5";

    UpdateWindowTitle();
  }

  protected override void OnResize(ResizeEventArgs e)
  {
    base.OnResize(e);
    GL.Viewport(0, 0, e.Width, e.Height);
  }

  protected override void OnFileDrop(FileDropEventArgs e)
  {
    base.OnFileDrop(e);
    pendingFiles.AddRange(e.FileNames);
    status = $"{pendingFiles.Count} file(s) queued";
    Console.WriteLine($"Queued {e.FileNames.Length} file(s) from drag & drop.");
    UpdateWindowTitle();
  }

  protected override void OnUpdateFrame(FrameEventArgs args)
  {
    base.OnUpdateFrame(args);
    UpdateProcessingStatus();
    UpdateWindowTitle();
  }

  protected override void OnRenderFrame(FrameEventArgs args)
  {
    base.OnRenderFrame(args);
    GL.ClearColor(new Color4(0.05f, 0.05f, 0.08f, 1f));
    GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

    RenderReferenceGeometry();
    if (renderMode == RenderMode.PointCloud)
      RenderPointCloud();
    else
      RenderVolume();
    RenderHudOverlay();

    SwapBuffers();
  }

  protected override void OnKeyDown(KeyboardKeyEventArgs e)
  {
    base.OnKeyDown(e);

    switch (e.Key)
    {
      case Keys.Escape:
        Close();
        break;
      case Keys.F5:
        StartProcessingPendingFiles();
        break;
      case Keys.O:
        HeatmapProcessor.OpenOutputDirectory(outputDir);
        break;
      case Keys.Left:
      case Keys.A:
        NavigatePreview(-1);
        break;
      case Keys.Right:
      case Keys.D:
        NavigatePreview(1);
        break;
      case Keys.R:
        ResetCamera();
        status = "Camera reset";
        break;
      case Keys.F1:
        SetRenderMode(RenderMode.PointCloud);
        break;
      case Keys.F2:
        SetRenderMode(RenderMode.VolumeComposite);
        break;
      case Keys.F3:
        SetRenderMode(RenderMode.VolumeMip);
        break;
      case Keys.C:
        clippingEnabled = !clippingEnabled;
        status = clippingEnabled ? "Clipping enabled" : "Clipping disabled";
        break;
      case Keys.LeftBracket:
        clipOffset = Math.Clamp(clipOffset - ClipOffsetStep, ClipOffsetMin, ClipOffsetMax);
        status = $"Clip offset: {clipOffset:F2}";
        break;
      case Keys.RightBracket:
        clipOffset = Math.Clamp(clipOffset + ClipOffsetStep, ClipOffsetMin, ClipOffsetMax);
        status = $"Clip offset: {clipOffset:F2}";
        break;
      case Keys.Minus:
        opacityGain = Math.Clamp(opacityGain * 0.9f, OpacityGainMin, OpacityGainMax);
        status = $"Opacity gain: {opacityGain:F2}";
        break;
      case Keys.Equal:
        opacityGain = Math.Clamp(opacityGain * 1.1f, OpacityGainMin, OpacityGainMax);
        status = $"Opacity gain: {opacityGain:F2}";
        break;
      case Keys.Comma:
        densityGain = Math.Clamp(densityGain * 0.9f, DensityGainMin, DensityGainMax);
        status = $"Density gain: {densityGain:F2}";
        break;
      case Keys.Period:
        densityGain = Math.Clamp(densityGain * 1.1f, DensityGainMin, DensityGainMax);
        status = $"Density gain: {densityGain:F2}";
        break;
      case Keys.H:
        PrintHelp();
        break;
    }

    UpdateWindowTitle();
  }

  protected override void OnMouseDown(MouseButtonEventArgs e)
  {
    base.OnMouseDown(e);
    if (e.Button != MouseButton.Left)
      return;

    isRotating = true;
    lastMousePosition = MousePosition;
  }

  protected override void OnMouseUp(MouseButtonEventArgs e)
  {
    base.OnMouseUp(e);
    if (e.Button == MouseButton.Left)
      isRotating = false;
  }

  protected override void OnMouseMove(MouseMoveEventArgs e)
  {
    base.OnMouseMove(e);
    if (!isRotating)
      return;

    var delta = e.Position - lastMousePosition;
    lastMousePosition = e.Position;
    yaw += delta.X * 0.01f;
    pitch = MathHelper.Clamp(pitch - delta.Y * 0.01f, -1.55f, 1.55f);
  }

  protected override void OnMouseWheel(MouseWheelEventArgs e)
  {
    base.OnMouseWheel(e);
    if (Math.Abs(e.OffsetY) <= float.Epsilon)
      return;

    distance = MathHelper.Clamp(distance * MathF.Exp(-e.OffsetY * 0.1f), 1.2f, 20f);
    status = $"Zoom: {distance:F2}";
    UpdateWindowTitle();
  }

  void SetRenderMode(RenderMode mode)
  {
    renderMode = mode;
    status = mode switch
    {
      RenderMode.PointCloud => "Switched to point cloud",
      RenderMode.VolumeComposite => "Switched to volume composite",
      RenderMode.VolumeMip => "Switched to volume MIP",
      _ => status,
    };
  }

  void StartProcessingPendingFiles()
  {
    if (processingTask is { IsCompleted: false })
    {
      status = "Processing is already in progress";
      UpdateWindowTitle();
      return;
    }

    if (pendingFiles.Count == 0)
    {
      status = "No files queued";
      Console.WriteLine("No files queued.");
      UpdateWindowTitle();
      return;
    }

    var filesToHandle = pendingFiles.ToArray();
    pendingFiles.Clear();

    var directPreviewFiles = new List<string>();
    var filesToProcess = new List<string>();
    var invalidFd3Count = 0;
    foreach (var filePath in filesToHandle)
    {
      if (!VolumeProcessor.IsFd3FilePath(filePath))
      {
        filesToProcess.Add(filePath);
        continue;
      }

      if (VolumeProcessor.TryValidateFd3(filePath, out var validationError))
      {
        directPreviewFiles.Add(filePath);
      }
      else
      {
        invalidFd3Count++;
        Console.WriteLine($"FAIL! Invalid .fd3 file '{filePath}': {validationError}");
      }
    }

    if (directPreviewFiles.Count > 0)
    {
      MergePreviewFiles(directPreviewFiles);
      var latestPreview = directPreviewFiles[^1];
      var latestPreviewIndex = previewFiles.IndexOf(latestPreview);
      if (SelectPreview(latestPreviewIndex, out var directLoadError))
        status = $"Loaded {directPreviewFiles.Count} .fd3 file(s) | Preview {previewIndex + 1}/{previewFiles.Count}";
      else
        status = $"Loaded .fd3 file(s), preview load failed: {directLoadError}";
    }

    if (filesToProcess.Count == 0)
    {
      if (directPreviewFiles.Count == 0)
      {
        status = invalidFd3Count > 0
          ? $"No files processed. {invalidFd3Count} invalid .fd3 file(s) skipped"
          : "No supported files queued";
      }
      else if (invalidFd3Count > 0)
      {
        status += $" | {invalidFd3Count} invalid .fd3 skipped";
      }

      Console.WriteLine(status);
      UpdateWindowTitle();
      return;
    }

    status = directPreviewFiles.Count > 0
      ? $"Loaded {directPreviewFiles.Count} .fd3 file(s), processing {filesToProcess.Count} file(s)..."
      : $"Processing {filesToProcess.Count} file(s)...";
    if (invalidFd3Count > 0)
      status += $" | {invalidFd3Count} invalid .fd3 skipped";

    Console.WriteLine(status);
    processingTask = Task.Run(() => VolumeProcessor.ProcessFiles(filesToProcess, outputDir));
    UpdateWindowTitle();
  }

  void UpdateProcessingStatus()
  {
    if (processingTask is not { IsCompleted: true })
      return;

    try
    {
      var outputFiles = processingTask.GetAwaiter().GetResult();
      if (outputFiles.Count == 0)
      {
        status = "No files were processed";
      }
      else
      {
        MergePreviewFiles(outputFiles);
        var latestPreview = outputFiles[^1];
        var latestPreviewIndex = previewFiles.IndexOf(latestPreview);
        if (SelectPreview(latestPreviewIndex, out var error))
          status = $"{outputFiles.Count} file(s) processed | Preview {previewIndex + 1}/{previewFiles.Count}";
        else
          status = $"{outputFiles.Count} file(s) processed, preview load failed: {error}";
      }

      Console.WriteLine(status);
      if (viewOutputDir && outputFiles.Count > 0)
        HeatmapProcessor.OpenOutputDirectory(outputDir);
    }
    catch (Exception ex)
    {
      status = "Processing failed";
      Console.WriteLine("FAIL! Processing task failed: " + ex.GetBaseException().Message);
    }
    finally
    {
      processingTask = null;
    }
  }

  void UpdateWindowTitle()
  {
    var nextHudText = BuildHudText();
    if (!string.Equals(hudText, nextHudText, StringComparison.Ordinal))
    {
      hudText = nextHudText;
      hudTextDirty = true;
    }

    Title = BaseWindowTitle;
  }

  string BuildHudText()
  {
    var isBusy = processingTask is { IsCompleted: false } ? "Yes" : "No";
    var previewStatus = previewFiles.Count > 0 && previewIndex >= 0
      ? $"{previewIndex + 1}/{previewFiles.Count}"
      : "None";
    var clipState = clippingEnabled ? $"On@{clipOffset:F2}" : "Off";
    return string.Join(
      Environment.NewLine,
      [
        $"Mode: {renderMode}",
        $"Dens: {densityGain:F2} | Opac: {opacityGain:F2}",
        $"Clip: {clipState}",
        $"Pending: {pendingFiles.Count} | Preview: {previewStatus}",
        $"Points: {pointCount:N0} | Busy: {isBusy}",
        $"Origin(0,0,0): ({ReferenceOrigin.X:F1},{ReferenceOrigin.Y:F1},{ReferenceOrigin.Z:F1})",
        $"Status: {status}",
        "Keys: F1/F2/F3 Mode | C Clip | [ ] ClipOffset",
        "      , . Density | - = Opacity | <- -> Preview | R Reset",
      ]);
  }

  void ResetCamera()
  {
    yaw = MathHelper.DegreesToRadians(45f);
    pitch = MathHelper.DegreesToRadians(25f);
    distance = 2.4f;
  }

  void NavigatePreview(int delta)
  {
    if (previewFiles.Count == 0)
    {
      status = "No preview volumes available";
      return;
    }

    var currentIndex = previewIndex >= 0 ? previewIndex : 0;
    if (SelectPreview(currentIndex + delta, out var error))
      status = $"Preview {previewIndex + 1}/{previewFiles.Count}: {Path.GetFileName(previewFiles[previewIndex])}";
    else
      status = "Failed to load preview: " + error;
  }

  void MergePreviewFiles(IReadOnlyList<string> outputFiles)
  {
    foreach (var outputFile in outputFiles)
    {
      if (!previewFiles.Contains(outputFile))
        previewFiles.Add(outputFile);
    }
  }

  bool SelectPreview(int requestedIndex, out string? error)
  {
    error = null;
    if (previewFiles.Count == 0)
    {
      ClearPreviewData();
      previewIndex = -1;
      return false;
    }

    var normalizedIndex = ((requestedIndex % previewFiles.Count) + previewFiles.Count) % previewFiles.Count;
    var previewFilePath = previewFiles[normalizedIndex];
    if (!TryLoadPreviewVolume(previewFilePath, out var volumeData, out error))
      return false;

    UploadVolumeTexture(volumeData.Voxels);
    volumeValueScale = ComputeVolumeValueScale(volumeData.Voxels);
    var vertices = BuildPointCloudVertices(volumeData, out var loadedPointCount);
    UploadPointCloud(vertices, loadedPointCount);
    previewIndex = normalizedIndex;
    return true;
  }

  static bool TryLoadPreviewVolume(string fd3FilePath, out VolumeProcessor.VolumeData volumeData, out string? error)
  {
    error = null;
    volumeData = null!;
    if (!File.Exists(fd3FilePath))
    {
      error = "Preview file not found: " + fd3FilePath;
      return false;
    }

    try
    {
      volumeData = VolumeProcessor.LoadVolumeZip(fd3FilePath);
      return true;
    }
    catch (Exception ex)
    {
      error = "Failed to load volume: " + ex.Message;
      return false;
    }
  }

  static float[] BuildPointCloudVertices(VolumeProcessor.VolumeData volumeData, out int loadedPointCount)
  {
    loadedPointCount = checked((int)volumeData.NonZeroVoxelCount);
    if (loadedPointCount == 0)
      return Array.Empty<float>();

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
          vertices[write++] = value / (float)ushort.MaxValue;
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

  void ClearPreviewData()
  {
    UploadPointCloud(Array.Empty<float>(), 0);
    hasVolumeTexture = false;
    volumeValueScale = 1f;
  }

  static float ComputeVolumeValueScale(ushort[] voxels)
  {
    ushort maxValue = 0;
    foreach (var value in voxels)
    {
      if (value > maxValue)
        maxValue = value;
    }

    return maxValue == 0 ? 1f : ushort.MaxValue / (float)maxValue;
  }

  void UploadPointCloud(float[] vertices, int loadedPointCount)
  {
    pointCount = loadedPointCount;

    GL.BindVertexArray(pointCloudVao);
    GL.BindBuffer(BufferTarget.ArrayBuffer, pointCloudVbo);
    GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.StaticDraw);
    GL.BindVertexArray(0);
  }

  void UploadVolumeTexture(ushort[] voxels)
  {
    if (voxels.Length != VolumeProcessor.VoxelCount)
      throw new ArgumentException($"Volume voxel length must be {VolumeProcessor.VoxelCount}.", nameof(voxels));

    if (volumeTextureId == 0)
      volumeTextureId = GL.GenTexture();

    GL.BindTexture(TextureTarget.Texture3D, volumeTextureId);
    GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
    GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
    GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
    GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
    GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
    GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
    GL.TexImage3D(
      TextureTarget.Texture3D,
      0,
      PixelInternalFormat.R16,
      VolumeProcessor.AxisLength,
      VolumeProcessor.AxisLength,
      VolumeProcessor.AxisLength,
      0,
      PixelFormat.Red,
      PixelType.UnsignedShort,
      voxels);
    GL.BindTexture(TextureTarget.Texture3D, 0);
    hasVolumeTexture = true;
  }

  void InitializePointCloudRenderer()
  {
    pointCloudShaderProgram = CreatePointCloudShaderProgram();
    pointCloudVao = GL.GenVertexArray();
    pointCloudVbo = GL.GenBuffer();

    GL.BindVertexArray(pointCloudVao);
    GL.BindBuffer(BufferTarget.ArrayBuffer, pointCloudVbo);
    GL.BufferData(BufferTarget.ArrayBuffer, 0, IntPtr.Zero, BufferUsageHint.DynamicDraw);

    var stride = 4 * sizeof(float);
    GL.EnableVertexAttribArray(0);
    GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
    GL.EnableVertexAttribArray(1);
    GL.VertexAttribPointer(1, 1, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
    GL.BindVertexArray(0);
  }

  int CreatePointCloudShaderProgram()
  {
    const string vertexShaderSource = """
      #version 330 core
      layout (location = 0) in vec3 aPosition;
      layout (location = 1) in float aIntensity;
      out float vIntensity;
      uniform mat4 uView;
      uniform mat4 uProjection;
      void main()
      {
          gl_Position = uProjection * uView * vec4(aPosition, 1.0);
          gl_PointSize = 2.0 + aIntensity * 4.0;
          vIntensity = aIntensity;
      }
      """;

    const string fragmentShaderSource = """
      #version 330 core
      in float vIntensity;
      out vec4 fragColor;

      vec3 ColorRamp(float t)
      {
          t = clamp(t, 0.0, 1.0);
          vec3 a = vec3(0.05, 0.12, 0.45);
          vec3 b = vec3(0.00, 0.90, 1.00);
          vec3 c = vec3(1.00, 0.95, 0.20);
          vec3 d = vec3(1.00, 0.25, 0.00);
          if (t < 0.33)
              return mix(a, b, t / 0.33);
          if (t < 0.66)
              return mix(b, c, (t - 0.33) / 0.33);
          return mix(c, d, (t - 0.66) / 0.34);
      }

      void main()
      {
          vec3 color = ColorRamp(vIntensity);
          fragColor = vec4(color, 1.0);
      }
      """;

    return CreateShaderProgram(vertexShaderSource, fragmentShaderSource, "point cloud");
  }

  void InitializeReferenceRenderer()
  {
    referenceShaderProgram = CreateReferenceShaderProgram();
    referenceVao = GL.GenVertexArray();
    referenceVbo = GL.GenBuffer();
    referenceVertexCount = 6;

    GL.BindVertexArray(referenceVao);
    GL.BindBuffer(BufferTarget.ArrayBuffer, referenceVbo);
    GL.BufferData(BufferTarget.ArrayBuffer, referenceVertexCount * 6 * sizeof(float), IntPtr.Zero, BufferUsageHint.DynamicDraw);

    var stride = 6 * sizeof(float);
    GL.EnableVertexAttribArray(0);
    GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
    GL.EnableVertexAttribArray(1);
    GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
    GL.BindVertexArray(0);

    referenceGridShaderProgram = CreateReferenceGridShaderProgram();
    referenceGridVao = GL.GenVertexArray();
    referenceGridVbo = GL.GenBuffer();
    referenceGridVertexCount = 6;

    GL.BindVertexArray(referenceGridVao);
    GL.BindBuffer(BufferTarget.ArrayBuffer, referenceGridVbo);
    GL.BufferData(BufferTarget.ArrayBuffer, referenceGridVertexCount * 3 * sizeof(float), IntPtr.Zero, BufferUsageHint.DynamicDraw);
    GL.EnableVertexAttribArray(0);
    GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
    GL.BindVertexArray(0);
  }

  int CreateReferenceShaderProgram()
  {
    const string vertexShaderSource = """
      #version 330 core
      layout (location = 0) in vec3 aPosition;
      layout (location = 1) in vec3 aColor;
      out vec3 vColor;
      uniform mat4 uView;
      uniform mat4 uProjection;
      void main()
      {
          gl_Position = uProjection * uView * vec4(aPosition, 1.0);
          vColor = aColor;
      }
      """;

    const string fragmentShaderSource = """
      #version 330 core
      in vec3 vColor;
      out vec4 fragColor;
      void main()
      {
          fragColor = vec4(vColor, 1.0);
      }
      """;

    return CreateShaderProgram(vertexShaderSource, fragmentShaderSource, "reference");
  }

  int CreateReferenceGridShaderProgram()
  {
    const string vertexShaderSource = """
      #version 330 core
      layout (location = 0) in vec3 aWorldPos;
      out vec3 vWorldPos;
      uniform mat4 uView;
      uniform mat4 uProjection;
      void main()
      {
          vWorldPos = aWorldPos;
          gl_Position = uProjection * uView * vec4(aWorldPos, 1.0);
      }
      """;

    const string fragmentShaderSource = """
      #version 330 core
      in vec3 vWorldPos;
      out vec4 fragColor;
      uniform vec3 uReferenceOrigin;
      uniform vec3 uCameraPosition;
      uniform float uMinorCell;
      uniform float uMajorCell;
      uniform float uFadeDistance;
      uniform vec3 uMinorColor;
      uniform vec3 uMajorColor;

      float GridFactor(vec2 pos, float cellSize)
      {
          vec2 coord = pos / cellSize;
          vec2 deriv = max(fwidth(coord), vec2(1e-4));
          vec2 lineDist = abs(fract(coord - 0.5) - 0.5) / deriv;
          float line = min(lineDist.x, lineDist.y);
          return 1.0 - clamp(line, 0.0, 1.0);
      }

      void main()
      {
          vec2 local = vWorldPos.xz - uReferenceOrigin.xz;
          float minor = GridFactor(local, uMinorCell);
          float major = GridFactor(local, uMajorCell);
          float dist = length(vWorldPos - uCameraPosition);
          float fade = clamp(1.0 - dist / max(uFadeDistance, 1e-4), 0.0, 1.0);
          float alpha = max(minor * 0.28, major * 0.85) * fade;
          if (alpha < 0.01)
              discard;

          vec3 color = mix(uMinorColor, uMajorColor, clamp(major, 0.0, 1.0));
          fragColor = vec4(color, alpha);
      }
      """;

    return CreateShaderProgram(vertexShaderSource, fragmentShaderSource, "reference grid");
  }

  void UpdateReferenceGeometry(Vector3 eye)
  {
    var distanceToOrigin = (eye - ReferenceOrigin).Length;
    var axisLength = MathF.Max(ReferenceAxisLengthMin, distanceToOrigin * ReferenceAxisLengthFactor);
    var axisEndX = ReferenceOrigin + new Vector3(axisLength, 0f, 0f);
    var axisEndY = ReferenceOrigin + new Vector3(0f, axisLength, 0f);
    var axisEndZ = ReferenceOrigin + new Vector3(0f, 0f, axisLength);

    float[] axisVertices =
    [
      ReferenceOrigin.X, ReferenceOrigin.Y, ReferenceOrigin.Z, 0.95f, 0.25f, 0.25f,
      axisEndX.X, axisEndX.Y, axisEndX.Z, 0.95f, 0.25f, 0.25f,
      ReferenceOrigin.X, ReferenceOrigin.Y, ReferenceOrigin.Z, 0.30f, 0.95f, 0.30f,
      axisEndY.X, axisEndY.Y, axisEndY.Z, 0.30f, 0.95f, 0.30f,
      ReferenceOrigin.X, ReferenceOrigin.Y, ReferenceOrigin.Z, 0.30f, 0.55f, 0.98f,
      axisEndZ.X, axisEndZ.Y, axisEndZ.Z, 0.30f, 0.55f, 0.98f,
    ];

    GL.BindBuffer(BufferTarget.ArrayBuffer, referenceVbo);
    GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, axisVertices.Length * sizeof(float), axisVertices);

    var gridExtent = MathF.Max(ReferenceGridExtentMin, distanceToOrigin * ReferenceGridExtentFactor);
    var centerX = MathF.Floor((eye.X - ReferenceOrigin.X) / ReferenceGridMajorCell) * ReferenceGridMajorCell + ReferenceOrigin.X;
    var centerZ = MathF.Floor((eye.Z - ReferenceOrigin.Z) / ReferenceGridMajorCell) * ReferenceGridMajorCell + ReferenceOrigin.Z;
    var y = ReferenceOrigin.Y;
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

    GL.BindBuffer(BufferTarget.ArrayBuffer, referenceGridVbo);
    GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, gridVertices.Length * sizeof(float), gridVertices);
    GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
  }

  void InitializeVolumeRenderer()
  {
    volumeShaderProgram = CreateVolumeShaderProgram();
    volumeVao = GL.GenVertexArray();
    volumeVbo = GL.GenBuffer();

    float[] quadVertices =
    [
      -1f, -1f, 0f, 0f,
      1f, -1f, 1f, 0f,
      1f, 1f, 1f, 1f,
      -1f, -1f, 0f, 0f,
      1f, 1f, 1f, 1f,
      -1f, 1f, 0f, 1f,
    ];

    GL.BindVertexArray(volumeVao);
    GL.BindBuffer(BufferTarget.ArrayBuffer, volumeVbo);
    GL.BufferData(BufferTarget.ArrayBuffer, quadVertices.Length * sizeof(float), quadVertices, BufferUsageHint.StaticDraw);
    var stride = 4 * sizeof(float);
    GL.EnableVertexAttribArray(0);
    GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, 0);
    GL.EnableVertexAttribArray(1);
    GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 2 * sizeof(float));
    GL.BindVertexArray(0);

    transferFunctionTextureId = CreateTransferFunctionTexture();

    GL.UseProgram(volumeShaderProgram);
    GL.Uniform1(GL.GetUniformLocation(volumeShaderProgram, "uVolumeTex"), 0);
    GL.Uniform1(GL.GetUniformLocation(volumeShaderProgram, "uTransferTex"), 1);
    GL.UseProgram(0);
  }

  int CreateVolumeShaderProgram()
  {
    const string vertexShaderSource = """
      #version 330 core
      layout (location = 0) in vec2 aPosition;
      layout (location = 1) in vec2 aTexCoord;
      out vec2 vTexCoord;
      void main()
      {
          gl_Position = vec4(aPosition, 0.0, 1.0);
          vTexCoord = aTexCoord;
      }
      """;

    const string fragmentShaderSource = """
      #version 330 core
      in vec2 vTexCoord;
      out vec4 fragColor;

      uniform sampler3D uVolumeTex;
      uniform sampler1D uTransferTex;
      uniform vec3 uCameraPosition;
      uniform vec3 uCameraForward;
      uniform vec3 uCameraRight;
      uniform vec3 uCameraUp;
      uniform float uAspect;
      uniform float uTanHalfFov;
      uniform float uStepSize;
      uniform float uDensityGain;
      uniform float uVolumeValueScale;
      uniform float uOpacityGain;
      uniform float uEarlyTerminateAlpha;
      uniform int uRenderMode; // 0: composite, 1: mip
      uniform int uClipEnabled;
      uniform vec3 uClipNormal;
      uniform float uClipOffset;

      const vec3 kBoxMin = vec3(-0.5);
      const vec3 kBoxMax = vec3(0.5);

      bool RayBoxIntersect(vec3 rayOrigin, vec3 rayDir, out float tEnter, out float tExit)
      {
          vec3 invDir = 1.0 / rayDir;
          vec3 t0 = (kBoxMin - rayOrigin) * invDir;
          vec3 t1 = (kBoxMax - rayOrigin) * invDir;
          vec3 tMin = min(t0, t1);
          vec3 tMax = max(t0, t1);
          tEnter = max(max(tMin.x, tMin.y), tMin.z);
          tExit = min(min(tMax.x, tMax.y), tMax.z);
          return tExit >= max(tEnter, 0.0);
      }

      bool IsClipped(vec3 worldPos)
      {
          if (uClipEnabled == 0)
              return false;
          float d = dot(worldPos, normalize(uClipNormal));
          return d > uClipOffset;
      }

      void main()
      {
          vec2 ndc = vTexCoord * 2.0 - 1.0;
          vec3 rayDir = normalize(
              uCameraForward
              + ndc.x * uCameraRight * uTanHalfFov * uAspect
              + ndc.y * uCameraUp * uTanHalfFov);

          float tEnter;
          float tExit;
          if (!RayBoxIntersect(uCameraPosition, rayDir, tEnter, tExit))
          {
              fragColor = vec4(0.0);
              return;
          }

          tEnter = max(tEnter, 0.0);
          vec4 accum = vec4(0.0);
          float maxDensity = 0.0;
          float t = tEnter;
          while (t <= tExit)
          {
              vec3 worldPos = uCameraPosition + rayDir * t;
              if (!IsClipped(worldPos))
              {
                  vec3 texCoord = worldPos + vec3(0.5);
                  float density = texture(uVolumeTex, texCoord).r;
                  density = clamp(density * uVolumeValueScale, 0.0, 1.0);
                  density = clamp(pow(density, 0.62) * uDensityGain, 0.0, 1.0);

                  if (uRenderMode == 1)
                  {
                      maxDensity = max(maxDensity, density);
                  }
                  else
                  {
                      vec4 sampleColor = texture(uTransferTex, density);
                      sampleColor.a = clamp(sampleColor.a * uOpacityGain, 0.0, 1.0);
                      accum.rgb += (1.0 - accum.a) * sampleColor.rgb * sampleColor.a;
                      accum.a += (1.0 - accum.a) * sampleColor.a;
                      if (accum.a >= uEarlyTerminateAlpha)
                          break;
                  }
              }

              t += uStepSize;
          }

          if (uRenderMode == 1)
          {
              if (maxDensity <= 0.0)
              {
                  fragColor = vec4(0.0);
                  return;
              }

              vec4 mipColor = texture(uTransferTex, maxDensity);
              fragColor = vec4(mipColor.rgb, 0.92);
              return;
          }

          fragColor = accum;
      }
      """;

    return CreateShaderProgram(vertexShaderSource, fragmentShaderSource, "volume");
  }

  static int CreateShaderProgram(string vertexShaderSource, string fragmentShaderSource, string label)
  {
    var vertexShader = CompileShader(ShaderType.VertexShader, vertexShaderSource);
    var fragmentShader = CompileShader(ShaderType.FragmentShader, fragmentShaderSource);
    var shaderProgram = GL.CreateProgram();

    GL.AttachShader(shaderProgram, vertexShader);
    GL.AttachShader(shaderProgram, fragmentShader);
    GL.LinkProgram(shaderProgram);
    GL.GetProgram(shaderProgram, GetProgramParameterName.LinkStatus, out var linkStatus);
    if (linkStatus == 0)
    {
      var linkLog = GL.GetProgramInfoLog(shaderProgram);
      GL.DeleteProgram(shaderProgram);
      throw new InvalidOperationException($"Failed to link {label} shader program: {linkLog}");
    }

    GL.DetachShader(shaderProgram, vertexShader);
    GL.DetachShader(shaderProgram, fragmentShader);
    GL.DeleteShader(vertexShader);
    GL.DeleteShader(fragmentShader);
    return shaderProgram;
  }

  static int CompileShader(ShaderType shaderType, string source)
  {
    var shader = GL.CreateShader(shaderType);
    GL.ShaderSource(shader, source);
    GL.CompileShader(shader);
    GL.GetShader(shader, ShaderParameter.CompileStatus, out var compileStatus);
    if (compileStatus == 0)
    {
      var compileLog = GL.GetShaderInfoLog(shader);
      GL.DeleteShader(shader);
      throw new InvalidOperationException($"Failed to compile {shaderType} shader: {compileLog}");
    }

    return shader;
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
      var alpha = Math.Clamp(MathF.Pow(t, 1.65f) * 0.18f, 0f, 1f);
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

  void InitializeHudRenderer()
  {
    hudShaderProgram = CreateHudShaderProgram();
    hudVao = GL.GenVertexArray();
    hudVbo = GL.GenBuffer();

    GL.BindVertexArray(hudVao);
    GL.BindBuffer(BufferTarget.ArrayBuffer, hudVbo);
    GL.BufferData(BufferTarget.ArrayBuffer, 6 * 4 * sizeof(float), IntPtr.Zero, BufferUsageHint.DynamicDraw);
    var stride = 4 * sizeof(float);
    GL.EnableVertexAttribArray(0);
    GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, 0);
    GL.EnableVertexAttribArray(1);
    GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 2 * sizeof(float));
    GL.BindVertexArray(0);

    hudTextureId = GL.GenTexture();
    GL.BindTexture(TextureTarget.Texture2D, hudTextureId);
    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
    GL.BindTexture(TextureTarget.Texture2D, 0);

    GL.UseProgram(hudShaderProgram);
    GL.Uniform1(GL.GetUniformLocation(hudShaderProgram, "uHudTexture"), 0);
    GL.UseProgram(0);
  }

  int CreateHudShaderProgram()
  {
    const string vertexShaderSource = """
      #version 330 core
      layout (location = 0) in vec2 aPosition;
      layout (location = 1) in vec2 aTexCoord;
      out vec2 vTexCoord;
      void main()
      {
          gl_Position = vec4(aPosition, 0.0, 1.0);
          vTexCoord = aTexCoord;
      }
      """;

    const string fragmentShaderSource = """
      #version 330 core
      in vec2 vTexCoord;
      out vec4 fragColor;
      uniform sampler2D uHudTexture;
      void main()
      {
          fragColor = texture(uHudTexture, vTexCoord);
      }
      """;

    return CreateShaderProgram(vertexShaderSource, fragmentShaderSource, "hud");
  }

  void UpdateHudTextureIfNeeded()
  {
    if (!hudTextDirty)
      return;

    hudTextDirty = false;
    if (string.IsNullOrWhiteSpace(hudText))
    {
      hudTextureWidth = 0;
      hudTextureHeight = 0;
      return;
    }

    var lines = hudText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
    if (lines.Length == 0)
    {
      hudTextureWidth = 0;
      hudTextureHeight = 0;
      return;
    }

    using var textPaint = new SKPaint
    {
      Color = SKColors.White,
      IsAntialias = true,
    };
    using var font = new SKFont(SKTypeface.Default, HudFontSizePx);
    var metrics = font.Metrics;
    var lineHeight = MathF.Ceiling(metrics.Descent - metrics.Ascent + HudLineSpacingPx);
    float maxLineWidth = 0f;
    foreach (var line in lines)
      maxLineWidth = Math.Max(maxLineWidth, font.MeasureText(line));

    var width = Math.Max(1, (int)MathF.Ceiling(maxLineWidth + HudPaddingPx * 2f));
    var height = Math.Max(1, (int)MathF.Ceiling(lines.Length * lineHeight + HudPaddingPx * 2f));
    using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
    using (var canvas = new SKCanvas(bitmap))
    {
      canvas.Clear(SKColors.Transparent);
      using var bgPaint = new SKPaint
      {
        Color = new SKColor(0, 0, 0, 160),
        IsAntialias = true,
      };
      canvas.DrawRoundRect(new SKRect(0, 0, width, height), HudCornerRadiusPx, HudCornerRadiusPx, bgPaint);

      var baseline = HudPaddingPx - metrics.Ascent;
      for (int i = 0; i < lines.Length; i++)
      {
        canvas.DrawText(lines[i], HudPaddingPx, baseline + i * lineHeight, SKTextAlign.Left, font, textPaint);
      }
    }

    var pixelData = bitmap.GetPixelSpan().ToArray();
    GL.BindTexture(TextureTarget.Texture2D, hudTextureId);
    GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
    GL.TexImage2D(
      TextureTarget.Texture2D,
      0,
      PixelInternalFormat.Rgba,
      width,
      height,
      0,
      PixelFormat.Rgba,
      PixelType.UnsignedByte,
      pixelData);
    GL.BindTexture(TextureTarget.Texture2D, 0);

    hudTextureWidth = width;
    hudTextureHeight = height;
  }

  void GetCameraPose(out Vector3 eye, out Vector3 forward, out Vector3 right, out Vector3 up)
  {
    eye = new Vector3(
      distance * MathF.Cos(pitch) * MathF.Cos(yaw),
      distance * MathF.Sin(pitch),
      distance * MathF.Cos(pitch) * MathF.Sin(yaw));

    forward = Vector3.Normalize(-eye);
    right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
    up = Vector3.Normalize(Vector3.Cross(right, forward));
  }

  void GetCameraMatrices(out Matrix4 view, out Matrix4 projection)
  {
    var aspect = ClientSize.X / (float)ClientSize.Y;
    projection = Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(45f), aspect, 0.01f, 100f);
    GetCameraPose(out var eye, out _, out _, out _);
    view = Matrix4.LookAt(eye, Vector3.Zero, Vector3.UnitY);
  }

  void RenderReferenceGeometry()
  {
    if (referenceVertexCount <= 0 || referenceVao == 0 || referenceShaderProgram == 0)
      return;

    if (referenceGridVertexCount <= 0 || referenceGridVao == 0 || referenceGridShaderProgram == 0)
      return;

    if (ClientSize.X <= 0 || ClientSize.Y <= 0)
      return;

    GetCameraPose(out var eye, out _, out _, out _);
    UpdateReferenceGeometry(eye);
    GetCameraMatrices(out var view, out var projection);

    GL.Enable(EnableCap.Blend);
    GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
    GL.UseProgram(referenceGridShaderProgram);
    GL.UniformMatrix4(GL.GetUniformLocation(referenceGridShaderProgram, "uView"), false, ref view);
    GL.UniformMatrix4(GL.GetUniformLocation(referenceGridShaderProgram, "uProjection"), false, ref projection);
    GL.Uniform3(GL.GetUniformLocation(referenceGridShaderProgram, "uReferenceOrigin"), ReferenceOrigin);
    GL.Uniform3(GL.GetUniformLocation(referenceGridShaderProgram, "uCameraPosition"), eye);
    GL.Uniform1(GL.GetUniformLocation(referenceGridShaderProgram, "uMinorCell"), ReferenceGridMinorCell);
    GL.Uniform1(GL.GetUniformLocation(referenceGridShaderProgram, "uMajorCell"), ReferenceGridMajorCell);
    GL.Uniform1(GL.GetUniformLocation(referenceGridShaderProgram, "uFadeDistance"), MathF.Max(ReferenceGridExtentMin, (eye - ReferenceOrigin).Length * 1.2f));
    GL.Uniform3(GL.GetUniformLocation(referenceGridShaderProgram, "uMinorColor"), new Vector3(0.15f, 0.15f, 0.17f));
    GL.Uniform3(GL.GetUniformLocation(referenceGridShaderProgram, "uMajorColor"), new Vector3(0.30f, 0.30f, 0.33f));
    GL.BindVertexArray(referenceGridVao);
    GL.DrawArrays(PrimitiveType.Triangles, 0, referenceGridVertexCount);
    GL.BindVertexArray(0);
    GL.UseProgram(0);
    GL.Disable(EnableCap.Blend);

    GL.LineWidth(2f);
    GL.UseProgram(referenceShaderProgram);
    GL.UniformMatrix4(GL.GetUniformLocation(referenceShaderProgram, "uView"), false, ref view);
    GL.UniformMatrix4(GL.GetUniformLocation(referenceShaderProgram, "uProjection"), false, ref projection);
    GL.BindVertexArray(referenceVao);
    GL.DrawArrays(PrimitiveType.Lines, 0, referenceVertexCount);
    GL.BindVertexArray(0);
    GL.UseProgram(0);
    GL.LineWidth(1f);
  }

  void RenderHudOverlay()
  {
    UpdateHudTextureIfNeeded();
    if (hudShaderProgram == 0 || hudVao == 0 || hudVbo == 0 || hudTextureId == 0)
      return;

    if (hudTextureWidth <= 0 || hudTextureHeight <= 0)
      return;

    if (ClientSize.X <= 0 || ClientSize.Y <= 0)
      return;

    var marginX = 2f * HudMarginPx / ClientSize.X;
    var marginY = 2f * HudMarginPx / ClientSize.Y;
    var widthNdc = 2f * hudTextureWidth / ClientSize.X;
    var heightNdc = 2f * hudTextureHeight / ClientSize.Y;

    var left = -1f + marginX;
    var right = left + widthNdc;
    var top = 1f - marginY;
    var bottom = top - heightNdc;
    float[] vertices =
    [
      left, top, 0f, 0f,
      right, top, 1f, 0f,
      right, bottom, 1f, 1f,
      left, top, 0f, 0f,
      right, bottom, 1f, 1f,
      left, bottom, 0f, 1f,
    ];

    GL.Disable(EnableCap.DepthTest);
    GL.Enable(EnableCap.Blend);
    GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

    GL.UseProgram(hudShaderProgram);
    GL.ActiveTexture(TextureUnit.Texture0);
    GL.BindTexture(TextureTarget.Texture2D, hudTextureId);
    GL.BindVertexArray(hudVao);
    GL.BindBuffer(BufferTarget.ArrayBuffer, hudVbo);
    GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, vertices.Length * sizeof(float), vertices);
    GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
    GL.BindVertexArray(0);
    GL.BindTexture(TextureTarget.Texture2D, 0);
    GL.UseProgram(0);

    GL.Disable(EnableCap.Blend);
    GL.Enable(EnableCap.DepthTest);
  }

  void RenderPointCloud()
  {
    if (pointCount <= 0 || pointCloudVao == 0 || pointCloudShaderProgram == 0)
      return;

    if (ClientSize.X <= 0 || ClientSize.Y <= 0)
      return;

    GetCameraMatrices(out var view, out var projection);
    GL.UseProgram(pointCloudShaderProgram);
    GL.UniformMatrix4(GL.GetUniformLocation(pointCloudShaderProgram, "uView"), false, ref view);
    GL.UniformMatrix4(GL.GetUniformLocation(pointCloudShaderProgram, "uProjection"), false, ref projection);
    GL.BindVertexArray(pointCloudVao);
    GL.DrawArrays(PrimitiveType.Points, 0, pointCount);
    GL.BindVertexArray(0);
    GL.UseProgram(0);
  }

  void RenderVolume()
  {
    if (!hasVolumeTexture || volumeShaderProgram == 0 || volumeVao == 0 || volumeTextureId == 0 || transferFunctionTextureId == 0)
      return;

    if (ClientSize.X <= 0 || ClientSize.Y <= 0)
      return;

    var fov = MathHelper.DegreesToRadians(45f);
    var aspect = ClientSize.X / (float)ClientSize.Y;
    var tanHalfFov = MathF.Tan(fov * 0.5f);
    GetCameraPose(out var eye, out var forward, out var right, out var up);

    GL.Disable(EnableCap.DepthTest);
    GL.Enable(EnableCap.Blend);
    GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

    GL.UseProgram(volumeShaderProgram);
    GL.Uniform3(GL.GetUniformLocation(volumeShaderProgram, "uCameraPosition"), eye);
    GL.Uniform3(GL.GetUniformLocation(volumeShaderProgram, "uCameraForward"), forward);
    GL.Uniform3(GL.GetUniformLocation(volumeShaderProgram, "uCameraRight"), right);
    GL.Uniform3(GL.GetUniformLocation(volumeShaderProgram, "uCameraUp"), up);
    GL.Uniform1(GL.GetUniformLocation(volumeShaderProgram, "uAspect"), aspect);
    GL.Uniform1(GL.GetUniformLocation(volumeShaderProgram, "uTanHalfFov"), tanHalfFov);
    GL.Uniform1(GL.GetUniformLocation(volumeShaderProgram, "uStepSize"), RayStepSize);
    GL.Uniform1(GL.GetUniformLocation(volumeShaderProgram, "uDensityGain"), densityGain);
    GL.Uniform1(GL.GetUniformLocation(volumeShaderProgram, "uVolumeValueScale"), volumeValueScale);
    GL.Uniform1(GL.GetUniformLocation(volumeShaderProgram, "uOpacityGain"), opacityGain);
    GL.Uniform1(GL.GetUniformLocation(volumeShaderProgram, "uEarlyTerminateAlpha"), EarlyTerminateAlpha);
    GL.Uniform1(GL.GetUniformLocation(volumeShaderProgram, "uRenderMode"), renderMode == RenderMode.VolumeMip ? 1 : 0);
    GL.Uniform1(GL.GetUniformLocation(volumeShaderProgram, "uClipEnabled"), clippingEnabled ? 1 : 0);
    GL.Uniform3(GL.GetUniformLocation(volumeShaderProgram, "uClipNormal"), Vector3.UnitY);
    GL.Uniform1(GL.GetUniformLocation(volumeShaderProgram, "uClipOffset"), clipOffset);

    GL.ActiveTexture(TextureUnit.Texture0);
    GL.BindTexture(TextureTarget.Texture3D, volumeTextureId);
    GL.ActiveTexture(TextureUnit.Texture1);
    GL.BindTexture(TextureTarget.Texture1D, transferFunctionTextureId);

    GL.BindVertexArray(volumeVao);
    GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
    GL.BindVertexArray(0);
    GL.BindTexture(TextureTarget.Texture1D, 0);
    GL.ActiveTexture(TextureUnit.Texture0);
    GL.BindTexture(TextureTarget.Texture3D, 0);
    GL.UseProgram(0);

    GL.Disable(EnableCap.Blend);
    GL.Enable(EnableCap.DepthTest);
  }

  protected override void OnUnload()
  {
    if (referenceVbo != 0)
    {
      GL.DeleteBuffer(referenceVbo);
      referenceVbo = 0;
    }

    if (referenceVao != 0)
    {
      GL.DeleteVertexArray(referenceVao);
      referenceVao = 0;
    }

    if (referenceShaderProgram != 0)
    {
      GL.DeleteProgram(referenceShaderProgram);
      referenceShaderProgram = 0;
    }

    if (referenceGridVbo != 0)
    {
      GL.DeleteBuffer(referenceGridVbo);
      referenceGridVbo = 0;
    }

    if (referenceGridVao != 0)
    {
      GL.DeleteVertexArray(referenceGridVao);
      referenceGridVao = 0;
    }

    if (referenceGridShaderProgram != 0)
    {
      GL.DeleteProgram(referenceGridShaderProgram);
      referenceGridShaderProgram = 0;
    }

    if (pointCloudVbo != 0)
    {
      GL.DeleteBuffer(pointCloudVbo);
      pointCloudVbo = 0;
    }

    if (pointCloudVao != 0)
    {
      GL.DeleteVertexArray(pointCloudVao);
      pointCloudVao = 0;
    }

    if (pointCloudShaderProgram != 0)
    {
      GL.DeleteProgram(pointCloudShaderProgram);
      pointCloudShaderProgram = 0;
    }

    if (volumeVbo != 0)
    {
      GL.DeleteBuffer(volumeVbo);
      volumeVbo = 0;
    }

    if (volumeVao != 0)
    {
      GL.DeleteVertexArray(volumeVao);
      volumeVao = 0;
    }

    if (volumeShaderProgram != 0)
    {
      GL.DeleteProgram(volumeShaderProgram);
      volumeShaderProgram = 0;
    }

    if (volumeTextureId != 0)
    {
      GL.DeleteTexture(volumeTextureId);
      volumeTextureId = 0;
    }

    if (transferFunctionTextureId != 0)
    {
      GL.DeleteTexture(transferFunctionTextureId);
      transferFunctionTextureId = 0;
    }

    if (hudVbo != 0)
    {
      GL.DeleteBuffer(hudVbo);
      hudVbo = 0;
    }

    if (hudVao != 0)
    {
      GL.DeleteVertexArray(hudVao);
      hudVao = 0;
    }

    if (hudShaderProgram != 0)
    {
      GL.DeleteProgram(hudShaderProgram);
      hudShaderProgram = 0;
    }

    if (hudTextureId != 0)
    {
      GL.DeleteTexture(hudTextureId);
      hudTextureId = 0;
    }

    base.OnUnload();
  }

  static void PrintHelp()
  {
    Console.WriteLine("fdia3 GUI mode started.");
    Console.WriteLine("Drag files to the window, then press F5 to process/load.");
    Console.WriteLine("Valid .fd3 files are loaded directly for rendering.");
    Console.WriteLine("Live status HUD is rendered at top-left (window title is simplified).");
    Console.WriteLine("Reference geometry: infinite-looking +X/+Y/+Z axes and infinite XZ grid.");
    Console.WriteLine("Logic origin (0,0,0) is anchored at world coordinate (-0.5,-0.5,-0.5).");
    Console.WriteLine("Render modes: F1=PointCloud, F2=Volume Composite, F3=Volume MIP");
    Console.WriteLine("Volume controls: C=Clip On/Off, [ / ]=Clip Offset, , / .=Density Gain, - / ==Opacity Gain");
    Console.WriteLine("Mouse: Left Drag=Rotate, Wheel=Zoom");
    Console.WriteLine("Keys: Left/A=Previous preview, Right/D=Next preview, R=Reset camera, O=Open output folder, H=Help, Esc=Quit");
  }
}
