using Fdia2.Core;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using SkiaSharp;

namespace Fdia2.UI;

public sealed class HeatmapGuiWindow : GameWindow
{
  readonly string outputDir;
  readonly List<string> pendingFiles;
  readonly bool viewOutputDir;
  readonly List<string> previewFiles = [];
  Task<List<string>>? processingTask;
  int previewIndex = -1;
  int previewTextureId;
  int previewTextureWidth;
  int previewTextureHeight;
  int previewShaderProgram;
  int previewVao;
  int previewVbo;
  int previewEbo;
  string status = "Ready";

  public HeatmapGuiWindow(string outputDir, List<string> initialFiles, bool viewOutputDir = false)
    : base(
        new GameWindowSettings
        {
          UpdateFrequency = 120,
        },
        new NativeWindowSettings
        {
          Title = "fdia2",
          ClientSize = new Vector2i(960, 540)
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
    InitializePreviewRenderer();
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

    var clearColor = HeatmapProcessor.ColorMode == ColorMode.Grayscale
      ? new Color4(0.15f, 0.15f, 0.15f, 1f)
      : new Color4(0.0f, 0.0f, 0.0f, 1f);

    GL.ClearColor(clearColor);
    GL.Clear(ClearBufferMask.ColorBufferBit);
    RenderPreviewTexture();

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
      case Keys.G:
        HeatmapProcessor.ColorMode = ColorMode.Grayscale;
        status = "Color mode switched to grayscale";
        break;
      case Keys.I:
        HeatmapProcessor.ColorMode = ColorMode.InfraredThermogram;
        status = "Color mode switched to infrared thermogram";
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
      case Keys.H:
        PrintHelp();
        break;
    }

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

    var filesToProcess = pendingFiles.ToArray();
    pendingFiles.Clear();
    status = $"Processing {filesToProcess.Length} file(s)...";
    Console.WriteLine(status);
    processingTask = Task.Run(() => HeatmapProcessor.ProcessFiles(filesToProcess, outputDir));
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
        {
          status = $"{outputFiles.Count} file(s) processed | Preview {previewIndex + 1}/{previewFiles.Count}";
        }
        else
        {
          status = $"{outputFiles.Count} file(s) processed, preview load failed: {error}";
        }
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
    Title = $"fdia2 | Mode: {HeatmapProcessor.ColorMode} | Scale: {HeatmapProcessor.Scale}x{HeatmapProcessor.Scale} | Pending: {pendingFiles.Count} | Preview: {previewStatus} | Busy: {isBusy} | {status}";
  }

  void NavigatePreview(int delta)
  {
    if (previewFiles.Count == 0)
    {
      status = "No preview images available";
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
      previewIndex = -1;
      DeletePreviewTexture();
      return false;
    }

    var normalizedIndex = ((requestedIndex % previewFiles.Count) + previewFiles.Count) % previewFiles.Count;
    var previewFilePath = previewFiles[normalizedIndex];
    if (!TryCreateTextureFromFile(previewFilePath, out var textureId, out var textureWidth, out var textureHeight, out error))
      return false;

    ReplacePreviewTexture(textureId, textureWidth, textureHeight);
    previewIndex = normalizedIndex;
    return true;
  }

  bool TryCreateTextureFromFile(string filePath,
                                out int textureId,
                                out int textureWidth,
                                out int textureHeight,
                                out string? error)
  {
    textureId = 0;
    textureWidth = 0;
    textureHeight = 0;
    error = null;

    if (!File.Exists(filePath))
    {
      error = "Preview file not found: " + filePath;
      return false;
    }

    try
    {
      using var bitmap = SKBitmap.Decode(filePath);
      if (bitmap == null)
      {
        error = "Failed to decode preview image: " + Path.GetFileName(filePath);
        return false;
      }

      using var rgbaBitmap = new SKBitmap(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
      if (!bitmap.CopyTo(rgbaBitmap, SKColorType.Rgba8888))
      {
        error = "Failed to convert preview image format: " + Path.GetFileName(filePath);
        return false;
      }

      var pixelData = rgbaBitmap.GetPixelSpan().ToArray();
      textureId = GL.GenTexture();
      GL.BindTexture(TextureTarget.Texture2D, textureId);
      GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
      GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
      GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
      GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
      GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
      GL.TexImage2D(
        TextureTarget.Texture2D,
        0,
        PixelInternalFormat.Rgba,
        rgbaBitmap.Width,
        rgbaBitmap.Height,
        0,
        PixelFormat.Rgba,
        PixelType.UnsignedByte,
        pixelData);
      GL.BindTexture(TextureTarget.Texture2D, 0);

      textureWidth = rgbaBitmap.Width;
      textureHeight = rgbaBitmap.Height;
      return true;
    }
    catch (Exception ex)
    {
      if (textureId != 0)
      {
        GL.DeleteTexture(textureId);
        textureId = 0;
      }

      error = "Failed to create preview texture: " + ex.Message;
      return false;
    }
  }

  void ReplacePreviewTexture(int newTextureId, int width, int height)
  {
    DeletePreviewTexture();
    previewTextureId = newTextureId;
    previewTextureWidth = width;
    previewTextureHeight = height;
  }

  void DeletePreviewTexture()
  {
    if (previewTextureId != 0)
    {
      GL.DeleteTexture(previewTextureId);
      previewTextureId = 0;
    }

    previewTextureWidth = 0;
    previewTextureHeight = 0;
  }

  void InitializePreviewRenderer()
  {
    previewShaderProgram = CreatePreviewShaderProgram();
    previewVao = GL.GenVertexArray();
    previewVbo = GL.GenBuffer();
    previewEbo = GL.GenBuffer();

    GL.BindVertexArray(previewVao);
    GL.BindBuffer(BufferTarget.ArrayBuffer, previewVbo);
    GL.BufferData(BufferTarget.ArrayBuffer, 16 * sizeof(float), IntPtr.Zero, BufferUsageHint.DynamicDraw);
    GL.BindBuffer(BufferTarget.ElementArrayBuffer, previewEbo);

    uint[] indices = [0, 1, 2, 2, 3, 0];
    GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Length * sizeof(uint), indices, BufferUsageHint.StaticDraw);

    var stride = 4 * sizeof(float);
    GL.EnableVertexAttribArray(0);
    GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, 0);
    GL.EnableVertexAttribArray(1);
    GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 2 * sizeof(float));
    GL.BindVertexArray(0);

    GL.UseProgram(previewShaderProgram);
    var textureUniformLocation = GL.GetUniformLocation(previewShaderProgram, "uTexture");
    GL.Uniform1(textureUniformLocation, 0);
    GL.UseProgram(0);
  }

  int CreatePreviewShaderProgram()
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
      uniform sampler2D uTexture;
      void main()
      {
          fragColor = texture(uTexture, vTexCoord);
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
      throw new InvalidOperationException("Failed to link preview shader program: " + linkLog);
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

  void RenderPreviewTexture()
  {
    if (previewTextureId == 0 || previewVao == 0 || previewVbo == 0 || previewShaderProgram == 0)
      return;

    if (ClientSize.X <= 0 || ClientSize.Y <= 0)
      return;

    var sideLength = Math.Min(ClientSize.X, ClientSize.Y);
    var halfWidth = sideLength / (float)ClientSize.X;
    var halfHeight = sideLength / (float)ClientSize.Y;

    float[] vertices =
    [
      -halfWidth, -halfHeight, 0f, 1f,
      halfWidth, -halfHeight, 1f, 1f,
      halfWidth, halfHeight, 1f, 0f,
      -halfWidth, halfHeight, 0f, 0f,
    ];

    GL.BindBuffer(BufferTarget.ArrayBuffer, previewVbo);
    GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, vertices.Length * sizeof(float), vertices);

    GL.UseProgram(previewShaderProgram);
    GL.ActiveTexture(TextureUnit.Texture0);
    GL.BindTexture(TextureTarget.Texture2D, previewTextureId);
    GL.BindVertexArray(previewVao);
    GL.DrawElements(PrimitiveType.Triangles, 6, DrawElementsType.UnsignedInt, 0);
    GL.BindVertexArray(0);
    GL.BindTexture(TextureTarget.Texture2D, 0);
    GL.UseProgram(0);
  }

  protected override void OnUnload()
  {
    DeletePreviewTexture();

    if (previewEbo != 0)
    {
      GL.DeleteBuffer(previewEbo);
      previewEbo = 0;
    }

    if (previewVbo != 0)
    {
      GL.DeleteBuffer(previewVbo);
      previewVbo = 0;
    }

    if (previewVao != 0)
    {
      GL.DeleteVertexArray(previewVao);
      previewVao = 0;
    }

    if (previewShaderProgram != 0)
    {
      GL.DeleteProgram(previewShaderProgram);
      previewShaderProgram = 0;
    }

    base.OnUnload();
  }

  static void PrintHelp()
  {
    Console.WriteLine("GUI mode started.");
    Console.WriteLine("Drag files to the window, then press F5 to process.");
    Console.WriteLine($"Scale: {HeatmapProcessor.Scale}x{HeatmapProcessor.Scale} (set via -sN, e.g. -s2)");
    Console.WriteLine("Keys: G=Grayscale, I=Infrared, Left/A=Previous preview, Right/D=Next preview, O=Open output folder, H=Help, Esc=Quit");
  }
}
