using Fdia2.Core;
using Fdia2.UI;

namespace Fdia2.Cli;

internal class Program
{
  static bool ViewOutputDir { get; set; } = false;
  static bool Run3DMode { get; set; } = false;

  private static void Main(string[] args)
  {
    if (args.Length == 1 && (args[0] == "help" || args[0] == "--help" || args[0] == "-h"))
    {
      PrintUsage();
      return;
    }

    ParseOptions(args);

    if (ShouldRunGui(args))
    {
      RunGuiMode(args);
      return;
    }

    RunCliMode(args);
  }

  static void ParseOptions(IEnumerable<string> args)
  {
    foreach (var arg in args)
    {
      if (!arg.StartsWith('-') || arg.StartsWith("--"))
        continue;

      ParseOptionCluster(arg.AsSpan());
    }
  }

  static void ParseOptionCluster(ReadOnlySpan<char> expression)
  {
    for (int i = 0; i < expression.Length; i++)
    {
      switch (expression[i])
      {
        case '-':
          continue;
        case 'v':
          ViewOutputDir = true;
          break;
        case 'g':
          HeatmapProcessor.ColorMode = ColorMode.Grayscale;
          break;
        case 'i':
          HeatmapProcessor.ColorMode = ColorMode.InfraredThermogram;
          break;
        case 's':
          i = ParseScaleOption(expression, i);
          break;
        case '3':
          Run3DMode = true;
          break;
        case 'u':
          break;
        default:
          Console.WriteLine($"WARN! Unknown option: '{expression[i]}'");
          break;
      }
    }
  }

  static int ParseScaleOption(ReadOnlySpan<char> expression, int optionIndex)
  {
    var valueStart = optionIndex + 1;
    if (valueStart >= expression.Length || !char.IsDigit(expression[valueStart]))
    {
      Console.WriteLine($"WARN! Invalid scale option: '{expression.ToString()}'. Expected '-sN', e.g. '-s2'.");
      return optionIndex;
    }

    var valueEnd = valueStart;
    while (valueEnd < expression.Length && char.IsDigit(expression[valueEnd]))
      valueEnd++;

    if (!int.TryParse(expression[valueStart..valueEnd], out var scale) || scale <= 0)
    {
      Console.WriteLine($"WARN! Invalid scale value in option '{expression.ToString()}'. Scale must be a positive integer.");
      return valueEnd - 1;
    }

    HeatmapProcessor.Scale = scale;
    return valueEnd - 1;
  }

  static void PrintUsage()
  {
    var exeName = Path.GetFileNameWithoutExtension(Environment.GetCommandLineArgs()[0]);
    Console.WriteLine($"Usage: {exeName} [-u] [-3] [-v|g|i] [-sN] <file1> <file2> ...");
    Console.WriteLine("Options:");
    Console.WriteLine("  u, Run in GUI mode (default when no arguments)");
    Console.WriteLine("  3, Run fdia3 mode (3-byte voxel analysis + point cloud, output *.fd3)");
    Console.WriteLine("  v, Open the output directory after processing");
    Console.WriteLine("  g, Use grayscale color mode (default)");
    Console.WriteLine("  i, Use infrared thermogram color mode");
    Console.WriteLine("  sN, Scale output pixels by N x N (example: -s2)");
    Console.WriteLine();
    Console.WriteLine("GUI quick keys:");
    Console.WriteLine("  G/I switch color mode, F5 process queued files, O open output, Esc quit");
    Console.WriteLine("fdia3 GUI keys:");
    Console.WriteLine("  Mouse LeftDrag/Wheel rotate/zoom, Left/Right switch preview, R reset camera");
    Console.WriteLine("  Tip: dropping valid .fd3 files loads them directly for rendering");
    Console.WriteLine("  F1/F2/F3 switch PointCloud/Volume Composite/Volume MIP; (C),([),(]),(,.),(-=) adjust volume view");
  }

  static bool ShouldRunGui(string[] args)
  {
    if (args.Length == 0)
      return true;

    return args.Any(arg => IsShortOptionEnabled(arg, 'u'));
  }

  static bool IsShortOptionEnabled(string arg, char option)
  {
    if (!arg.StartsWith('-') || arg.StartsWith("--"))
      return false;

    return arg.AsSpan(1).Contains(option);
  }

  static void RunCliMode(IEnumerable<string> args)
  {
    var outputDir = Path.Combine(Directory.GetCurrentDirectory(), "output");
    Directory.CreateDirectory(outputDir);

    var fileArgs = HeatmapProcessor.ExpandWildcardArgs(args).ToList();
    if (fileArgs.Count == 0)
    {
      PrintUsage();
      return;
    }

    var outputFiles = Run3DMode
      ? VolumeProcessor.ProcessFiles(fileArgs, outputDir)
      : HeatmapProcessor.ProcessFiles(fileArgs, outputDir);
    Console.WriteLine($"{outputFiles.Count} file(s) processed.");

    if (ViewOutputDir)
      HeatmapProcessor.OpenOutputDirectory(outputDir);
  }

  static void RunGuiMode(IEnumerable<string> args)
  {
    var outputDir = Path.Combine(Directory.GetCurrentDirectory(), "output");
    Directory.CreateDirectory(outputDir);
    var initialFiles = HeatmapProcessor.ExpandWildcardArgs(args).ToList();

    if (Run3DMode)
    {
      using var fdia3Window = new Fdia3GuiWindow(outputDir, initialFiles, ViewOutputDir);
      fdia3Window.Run();
      return;
    }

    using var fdia2Window = new HeatmapGuiWindow(outputDir, initialFiles, ViewOutputDir);
    fdia2Window.Run();
  }
}
