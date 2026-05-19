using Fdia2.Core;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace Fdia2.UI;

public sealed class Fdia3GuiWindow : GameWindow
{
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
  string status = "Ready";

  bool isRotating;
  Vector2 lastMousePosition;
  float yaw = MathHelper.DegreesToRadians(45f);
  float pitch = MathHelper.DegreesToRadians(25f);
  float distance = 2.4f;

  public Fdia3GuiWindow(string outputDir, List<string> initialFiles, bool viewOutputDir = false)
    : base(
        new GameWindowSettings
        {
          UpdateFrequency = 120,
        },
        new NativeWindowSettings
        {
          Title = "fdia3 GUI (OpenTK Point Cloud)",
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
    RenderPointCloud();
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
    var isBusy = processingTask is { IsCompleted: false } ? "Yes" : "No";
    var previewStatus = previewFiles.Count > 0 && previewIndex >= 0
      ? $"{previewIndex + 1}/{previewFiles.Count}"
      : "None";
    Title = $"fdia3 GUI | Pending: {pendingFiles.Count} | Preview: {previewStatus} | Points: {pointCount:N0} | Busy: {isBusy} | {status}";
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
      pointCount = 0;
      UploadPointCloud(Array.Empty<float>(), 0);
      previewIndex = -1;
      return false;
    }

    var normalizedIndex = ((requestedIndex % previewFiles.Count) + previewFiles.Count) % previewFiles.Count;
    var previewFilePath = previewFiles[normalizedIndex];
    if (!TryCreatePointCloud(previewFilePath, out var vertices, out var loadedPointCount, out error))
      return false;

    UploadPointCloud(vertices, loadedPointCount);
    previewIndex = normalizedIndex;
    return true;
  }

  bool TryCreatePointCloud(string fd3FilePath, out float[] vertices, out int loadedPointCount, out string? error)
  {
    vertices = Array.Empty<float>();
    loadedPointCount = 0;
    error = null;

    if (!File.Exists(fd3FilePath))
    {
      error = "Preview file not found: " + fd3FilePath;
      return false;
    }

    try
    {
      var volumeData = VolumeProcessor.LoadVolumeZip(fd3FilePath);
      loadedPointCount = checked((int)volumeData.NonZeroVoxelCount);
      if (loadedPointCount == 0)
      {
        vertices = Array.Empty<float>();
        return true;
      }

      vertices = new float[loadedPointCount * 4];
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

      if (write != vertices.Length)
      {
        var resized = new float[write];
        Array.Copy(vertices, resized, write);
        vertices = resized;
        loadedPointCount = write / 4;
      }

      return true;
    }
    catch (Exception ex)
    {
      error = "Failed to load point cloud: " + ex.Message;
      return false;
    }
  }

  void UploadPointCloud(float[] vertices, int loadedPointCount)
  {
    pointCount = loadedPointCount;

    GL.BindVertexArray(pointCloudVao);
    GL.BindBuffer(BufferTarget.ArrayBuffer, pointCloudVbo);
    GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.StaticDraw);
    GL.BindVertexArray(0);
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
      throw new InvalidOperationException("Failed to link point cloud shader program: " + linkLog);
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

  void InitializeReferenceRenderer()
  {
    referenceShaderProgram = CreateReferenceShaderProgram();
    referenceVao = GL.GenVertexArray();
    referenceVbo = GL.GenBuffer();

    var vertices = BuildReferenceVertices();
    referenceVertexCount = vertices.Length / 6;

    GL.BindVertexArray(referenceVao);
    GL.BindBuffer(BufferTarget.ArrayBuffer, referenceVbo);
    GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.StaticDraw);

    var stride = 6 * sizeof(float);
    GL.EnableVertexAttribArray(0);
    GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
    GL.EnableVertexAttribArray(1);
    GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
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
      throw new InvalidOperationException("Failed to link reference shader program: " + linkLog);
    }

    GL.DetachShader(shaderProgram, vertexShader);
    GL.DetachShader(shaderProgram, fragmentShader);
    GL.DeleteShader(vertexShader);
    GL.DeleteShader(fragmentShader);
    return shaderProgram;
  }

  static float[] BuildReferenceVertices()
  {
    var vertices = new List<float>();
    const float axisHalf = 0.65f;
    const float gridHalf = 0.5f;
    const float gridY = -0.5f;
    const int gridDivisions = 20;

    AddLine(vertices, new Vector3(-axisHalf, 0f, 0f), new Vector3(axisHalf, 0f, 0f), new Vector3(0.95f, 0.25f, 0.25f));
    AddLine(vertices, new Vector3(0f, -axisHalf, 0f), new Vector3(0f, axisHalf, 0f), new Vector3(0.30f, 0.95f, 0.30f));
    AddLine(vertices, new Vector3(0f, 0f, -axisHalf), new Vector3(0f, 0f, axisHalf), new Vector3(0.30f, 0.55f, 0.98f));

    for (int i = 0; i <= gridDivisions; i++)
    {
      var t = i / (float)gridDivisions;
      var coord = -gridHalf + t * (gridHalf * 2f);
      var centerLine = Math.Abs(coord) < 1e-6f;
      var color = centerLine ? new Vector3(0.28f, 0.28f, 0.30f) : new Vector3(0.17f, 0.17f, 0.19f);

      AddLine(vertices, new Vector3(-gridHalf, gridY, coord), new Vector3(gridHalf, gridY, coord), color);
      AddLine(vertices, new Vector3(coord, gridY, -gridHalf), new Vector3(coord, gridY, gridHalf), color);
    }

    return vertices.ToArray();
  }

  static void AddLine(List<float> vertices, Vector3 start, Vector3 end, Vector3 color)
  {
    vertices.Add(start.X);
    vertices.Add(start.Y);
    vertices.Add(start.Z);
    vertices.Add(color.X);
    vertices.Add(color.Y);
    vertices.Add(color.Z);

    vertices.Add(end.X);
    vertices.Add(end.Y);
    vertices.Add(end.Z);
    vertices.Add(color.X);
    vertices.Add(color.Y);
    vertices.Add(color.Z);
  }

  void GetCameraMatrices(out Matrix4 view, out Matrix4 projection)
  {
    var aspect = ClientSize.X / (float)ClientSize.Y;
    projection = Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(45f), aspect, 0.01f, 100f);
    var eye = new Vector3(
      distance * MathF.Cos(pitch) * MathF.Cos(yaw),
      distance * MathF.Sin(pitch),
      distance * MathF.Cos(pitch) * MathF.Sin(yaw));
    view = Matrix4.LookAt(eye, Vector3.Zero, Vector3.UnitY);
  }

  void RenderReferenceGeometry()
  {
    if (referenceVertexCount <= 0 || referenceVao == 0 || referenceShaderProgram == 0)
      return;

    if (ClientSize.X <= 0 || ClientSize.Y <= 0)
      return;

    GetCameraMatrices(out var view, out var projection);
    GL.UseProgram(referenceShaderProgram);
    var viewLocation = GL.GetUniformLocation(referenceShaderProgram, "uView");
    var projectionLocation = GL.GetUniformLocation(referenceShaderProgram, "uProjection");
    GL.UniformMatrix4(viewLocation, false, ref view);
    GL.UniformMatrix4(projectionLocation, false, ref projection);
    GL.BindVertexArray(referenceVao);
    GL.DrawArrays(PrimitiveType.Lines, 0, referenceVertexCount);
    GL.BindVertexArray(0);
    GL.UseProgram(0);
  }

  void RenderPointCloud()
  {
    if (pointCount <= 0 || pointCloudVao == 0 || pointCloudShaderProgram == 0)
      return;

    if (ClientSize.X <= 0 || ClientSize.Y <= 0)
      return;

    GetCameraMatrices(out var view, out var projection);

    GL.UseProgram(pointCloudShaderProgram);
    var viewLocation = GL.GetUniformLocation(pointCloudShaderProgram, "uView");
    var projectionLocation = GL.GetUniformLocation(pointCloudShaderProgram, "uProjection");
    GL.UniformMatrix4(viewLocation, false, ref view);
    GL.UniformMatrix4(projectionLocation, false, ref projection);
    GL.BindVertexArray(pointCloudVao);
    GL.DrawArrays(PrimitiveType.Points, 0, pointCount);
    GL.BindVertexArray(0);
    GL.UseProgram(0);
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

    base.OnUnload();
  }

  static void PrintHelp()
  {
    Console.WriteLine("fdia3 GUI mode started.");
    Console.WriteLine("Drag files to the window, then press F5 to process/load.");
    Console.WriteLine("Valid .fd3 files are loaded directly for rendering.");
    Console.WriteLine("Reference geometry: XYZ axes + XZ grid plane.");
    Console.WriteLine("Mouse: Left Drag=Rotate, Wheel=Zoom");
    Console.WriteLine("Keys: Left/A=Previous preview, Right/D=Next preview, R=Reset camera, O=Open output folder, H=Help, Esc=Quit");
  }
}
