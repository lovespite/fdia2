using Fdia2.Core;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace Fdia2.UI;

public sealed class HeatmapGuiWindow : GameWindow
{
  readonly string outputDir;
  readonly List<string> pendingFiles;
  readonly bool viewOutputDir;
  KeyboardState? previousKeyboardState;
  string status = "Ready";

  public HeatmapGuiWindow(string outputDir, List<string> initialFiles, bool viewOutputDir = false)
    : base(
        GameWindowSettings.Default,
        new NativeWindowSettings
        {
          Title = "fdia2 GUI (OpenTK)",
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
    PrintHelp();

    if (pendingFiles.Count > 0)
      ProcessPendingFiles();
    else
      status = "Drag files into this window, then press F5";
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
  }

  protected override void OnUpdateFrame(FrameEventArgs args)
  {
    base.OnUpdateFrame(args);
    if (!IsFocused)
    {
      previousKeyboardState = KeyboardState;
      return;
    }

    if (JustPressed(Keys.Escape))
    {
      Close();
    }
    else if (JustPressed(Keys.G))
    {
      HeatmapProcessor.ColorMode = ColorMode.Grayscale;
      status = "Color mode switched to grayscale";
    }
    else if (JustPressed(Keys.I))
    {
      HeatmapProcessor.ColorMode = ColorMode.InfraredThermogram;
      status = "Color mode switched to infrared thermogram";
    }
    else if (JustPressed(Keys.F5))
    {
      ProcessPendingFiles();
    }
    else if (JustPressed(Keys.O))
    {
      HeatmapProcessor.OpenOutputDirectory(outputDir);
    }
    else if (JustPressed(Keys.H))
    {
      PrintHelp();
    }

    Title = $"fdia2 GUI (OpenTK) | Mode: {HeatmapProcessor.ColorMode} | Pending: {pendingFiles.Count} | {status}";
    previousKeyboardState = KeyboardState;
  }

  protected override void OnRenderFrame(FrameEventArgs args)
  {
    base.OnRenderFrame(args);

    var clearColor = HeatmapProcessor.ColorMode == ColorMode.Grayscale
      ? new Color4(0.15f, 0.15f, 0.15f, 1f)
      : new Color4(0.22f, 0.08f, 0.08f, 1f);

    GL.ClearColor(clearColor);
    GL.Clear(ClearBufferMask.ColorBufferBit);

    SwapBuffers();
  }

  void ProcessPendingFiles()
  {
    if (pendingFiles.Count == 0)
    {
      status = "No files queued";
      Console.WriteLine("No files queued.");
      return;
    }

    var filesToProcess = pendingFiles.ToArray();
    pendingFiles.Clear();

    var outputFiles = HeatmapProcessor.ProcessFiles(filesToProcess, outputDir);
    status = $"{outputFiles.Count} file(s) processed";
    Console.WriteLine(status);

    if (viewOutputDir && outputFiles.Count > 0)
      HeatmapProcessor.OpenOutputDirectory(outputDir);
  }

  bool JustPressed(Keys key)
  {
    return KeyboardState.IsKeyDown(key) && !(previousKeyboardState?.IsKeyDown(key) ?? false);
  }

  static void PrintHelp()
  {
    Console.WriteLine("GUI mode started.");
    Console.WriteLine("Drag files to the window, then press F5 to process.");
    Console.WriteLine("Keys: G=Grayscale, I=Infrared, O=Open output folder, H=Help, Esc=Quit");
  }
}
