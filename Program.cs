using Fdia2.Core;
using Fdia2.UI;

namespace Fdia2.Cli;

internal class Program
{
  static bool ViewOutputDir { get; set; } = false;

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
        case 'u':
          break;
        default:
          Console.WriteLine($"WARN! Unknown option: '{expression[i]}'");
          break;
      }
    }
  }

  static void PrintUsage()
  {
    var exeName = Path.GetFileNameWithoutExtension(Environment.GetCommandLineArgs()[0]);
    Console.WriteLine($"Usage: {exeName} [-u] [-v|g|i] <file1> <file2> ...");
    Console.WriteLine("Options:");
    Console.WriteLine("  u, Run in GUI mode (default when no arguments)");
    Console.WriteLine("  v, Open the output directory after processing");
    Console.WriteLine("  g, Use grayscale color mode (default)");
    Console.WriteLine("  i, Use infrared thermogram color mode");
    Console.WriteLine();
    Console.WriteLine("GUI quick keys:");
    Console.WriteLine("  G/I switch color mode, F5 process queued files, O open output, Esc quit");
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

    var outputFiles = HeatmapProcessor.ProcessFiles(fileArgs, outputDir);
    Console.WriteLine($"{outputFiles.Count} file(s) processed.");

    if (ViewOutputDir)
      HeatmapProcessor.OpenOutputDirectory(outputDir);
  }

  static void RunGuiMode(IEnumerable<string> args)
  {
    var outputDir = Path.Combine(Directory.GetCurrentDirectory(), "output");
    Directory.CreateDirectory(outputDir);
    var initialFiles = HeatmapProcessor.ExpandWildcardArgs(args).ToList();

    using var guiWindow = new HeatmapGuiWindow(outputDir, initialFiles, ViewOutputDir);
    guiWindow.Run();
  }
}
