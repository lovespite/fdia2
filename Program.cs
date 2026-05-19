using SkiaSharp;

enum ColorMode
{
  Grayscale,
  InfraredThermogram,
}


internal unsafe class Program
{
  delegate void WritePixel(byte* pBitmap, int offset, byte intensity);
  static List<string> argList = [];

  static readonly (byte R, byte G, byte B)[] InfraredThermogramPalette;
  static Program()
  {
    // Iron colormap: Black → Purple → Red → Orange → Yellow → White
    // 参考 FLIR Iron 色谱的经典锚点
    (byte R, byte G, byte B)[] stops =
    [
        (  0,   0,   0),   // 0.00 — 黑
        (128,   0, 128),   // 0.25 — 紫
        (255,   0,   0),   // 0.50 — 红
        (255, 165,   0),   // 0.65 — 橙
        (255, 255,   0),   // 0.80 — 黄
        (255, 255, 255),   // 1.00 — 白
    ];

    var palette = new (byte R, byte G, byte B)[256];
    int segments = stops.Length - 1;
    for (int i = 0; i < 256; i++)
    {
      double t = i / 255.0 * segments;
      int seg = Math.Min((int)t, segments - 1);
      double f = t - seg;
      var a = stops[seg];
      var b = stops[seg + 1];
      palette[i] = (
          R: (byte)(a.R + (b.R - a.R) * f),
          G: (byte)(a.G + (b.G - a.G) * f),
          B: (byte)(a.B + (b.B - a.B) * f)
      );
    }
    InfraredThermogramPalette = palette;
  }

  static ColorMode colorMode { get; set; } = ColorMode.Grayscale;
  static bool ViewOutputDir { get; set; } = false;
  static int Scale { get; set; } = 1;

  static void ParseOptions(ReadOnlySpan<char> expression)
  {
    for (int i = 0; i < expression.Length; i++)
    {
      switch (expression[i])
      {
        case '-':
          continue; // Skip the '-' character
        case 'v':
          ViewOutputDir = true;
          break;
        case 'g':
          colorMode = ColorMode.Grayscale;
          break;
        case 'i':
          colorMode = ColorMode.InfraredThermogram;
          break;
        case 's':
          {
            var j = i + 1;
            while (j < expression.Length && char.IsDigit(expression[j]))
            {
              j++;
            }

            if (j == i + 1)
            {
              Console.WriteLine("WARN! Missing scale value after 's'.");
              break;
            }

            if (!int.TryParse(expression[(i + 1)..j], out var parsedScale) || parsedScale <= 0)
            {
              Console.WriteLine($"WARN! Invalid scale value: '{expression[(i + 1)..j].ToString()}', fallback to scale=1.");
              Scale = 1;
            }
            else
            {
              Scale = parsedScale;
            }

            i = j - 1;
          }
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
    Console.WriteLine($"Usage: {exeName} [-v|g|i|sN] <file1> <file2> ...");
    Console.WriteLine("Options:");
    Console.WriteLine("  v, Open the output directory after processing");
    Console.WriteLine("  g, Use grayscale color mode (default)");
    Console.WriteLine("  i, Use infrared thermogram color mode");
    Console.WriteLine("  sN, Scale each logical pixel to N x N block (e.g. -s2, -vis2)");
  }

  private static void Main(string[] args)
  {
    argList = [.. args];
    if (argList.Count == 0 || (argList.Count == 1 && (argList[0] == "help" || argList[0] == "--help" || argList[0] == "-h")))
    {
      PrintUsage();
      return;
    }

    var options = argList[0].StartsWith('-') ? argList[0] : "";
    ParseOptions(options);
    var outputDir = Path.Combine(Directory.GetCurrentDirectory(), "output");
    Directory.CreateDirectory(outputDir);

    Span<byte> map = stackalloc byte[256 * 256];
    Span<uint> counter = stackalloc uint[256 * 256];
    Span<byte> buffer = stackalloc byte[256 * 1024]; // 256 KB buffer

    var outputFiles = new List<string>();
    foreach (var filePath in ExpandWildcardArgs(argList))
    {
      try
      {
        Console.WriteLine($"Processing file: '{filePath}'...");
        var outputFile = ProcessFile(filePath, counter, map, buffer, outputDir);
        if (outputFile != null)
        {
          outputFiles.Add(outputFile);
          Console.WriteLine($"DONE! '{filePath}' -> '{outputFile}'");
        }
      }
      catch (Exception ex)
      {
        Console.WriteLine($"FAIL! Skipped file '{filePath}': {ex.Message}");
      }
    }

    Console.WriteLine($"{outputFiles.Count} file(s) processed.");

    if (ViewOutputDir)
    {
      try
      {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
          FileName = outputDir,
          UseShellExecute = true
        });
      }
      catch (Exception ex)
      {
        Console.WriteLine("FAIL! Failed to open the output directory: " + ex.Message);
      }
    }
  }

  static string? ProcessFile(string filePath,
                             Span<uint> counter,
                             Span<byte> map,
                             Span<byte> buffer,
                             string outputDir)
  {
    if (!File.Exists(filePath))
      throw new FileNotFoundException("File not found: " + filePath);

    using var stream = GetDataStream(filePath);

    counter.Clear();
    map.Clear();

    var max = 0u;
    var read = 0;
    unsafe
    {
      fixed (uint* pCounter = counter)
      fixed (byte* pBuffer = buffer)
      {
        while ((read = stream.Read(buffer)) > 0)
        {
          for (int i = 0; i < read - 1; i += 2)
          {
            var x = pBuffer[i];
            var y = pBuffer[i + 1];
            var ptr = pCounter + y * 256 + x;
            (*ptr)++;

            max = Math.Max(max, *ptr);
          }
        }
      }
    }

    var nMax = NLog(max);

    var bitmap = CreateBitmap(out var writter);
    var stride = bitmap.RowBytes;
    var bytesPerPixel = bitmap.Info.BytesPerPixel;
    var bitmapData = bitmap.GetPixelSpan();

    unsafe
    {
      fixed (byte* pBitmap = bitmapData)
      fixed (uint* pMap = counter)
      {
        for (int y = 0; y < 256; y++)
        {
          for (int x = 0; x < 256; x++)
          {
            var ptr = pMap + y * 256 + x;
            var intensity = (byte)(NLog(*ptr) / nMax * 255);
            var baseY = y * Scale;
            var baseX = x * Scale;
            for (int sy = 0; sy < Scale; sy++)
            {
              var rowOffset = (baseY + sy) * stride + baseX * bytesPerPixel;
              for (int sx = 0; sx < Scale; sx++)
              {
                var offset = rowOffset + sx * bytesPerPixel;
                writter(pBitmap, offset, intensity);
              }
            }
          }
        }
      }
    }

    var outputFilePath = Path.Combine(
      outputDir,
      Path.GetFileName(filePath) + ".heatmap.png");

    using (var ofs = File.Create(outputFilePath))
    {
      bitmap
        .Encode(SKEncodedImageFormat.Png, 100)
        .SaveTo(ofs);
    }

    return outputFilePath;
  }

  static SKBitmap CreateBitmap(out WritePixel writter)
  {
    var size = checked(256 * Scale);
    switch (colorMode)
    {
      case ColorMode.Grayscale:
        writter = WritePixelGray;
        return new SKBitmap(new SKImageInfo(size, size, SKColorType.Gray8));
      case ColorMode.InfraredThermogram:
        writter = WritePixelInfrared;
        return new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgb888x));
      default:
        throw new InvalidOperationException("Unsupported color mode: " + colorMode);
    }
  }

  static void WritePixelGray(byte* pBitmap, int offset, byte intensity)
  {
    pBitmap[offset] = intensity;
  }

  static void WritePixelInfrared(byte* pBitmap, int offset, byte intensity)
  {
    var (R, G, B) = InfraredThermogramPalette[intensity];
    pBitmap[offset] = R;
    pBitmap[offset + 1] = G;
    pBitmap[offset + 2] = B;
  }

  static string GetArg(string name, string? alias = null, string defaultValue = "")
  {
    var argIndex = argList.FindIndex(x => x == $"--{name}" || alias != null && x == $"-{alias}");
    if (argIndex >= 0 && argIndex < argList.Count - 1)
    {
      return argList[argIndex + 1];
    }
    return defaultValue;
  }

  static bool HasFlag(string name, string? alias = null)
  {
    return argList.Any(x => x == $"--{name}" || alias != null && x == $"-{alias}");
  }

  static Stream GetDataStream(string file)
  {
    var fi = new FileInfo(file);
    if (fi.Length > int.MaxValue)
    {
      throw new InvalidOperationException("File is too large to process.");
    }
    if (fi.Length < 2)
    {
      throw new InvalidOperationException("File is too small to process.");
    }

    if (IsImageFile(file) && TryDecodeImage(file, out var imageStream) && imageStream.Length > 0)
    {
      return imageStream;
    }

    // Fallback to raw byte stream if not an image or if decoding fails 
    return File.OpenRead(file);
  }

  static bool TryDecodeImage(string path, out Stream ostream)
  {
    ostream = Stream.Null;
    try
    {
      Console.WriteLine(" - Decoding image:" + Path.GetFileName(path));
      using var stream = File.OpenRead(path);
      using var codec = SKCodec.Create(stream);
      if (codec != null)
      {
        var info = codec.Info;
        Console.WriteLine($" - Image dimensions: {info.Width}x{info.Height}, Color Type: {info.ColorType}, Alpha Type: {info.AlphaType}");
        var expectedSize = info.RowBytes * info.Height;
        if (expectedSize > int.MaxValue)
        {
          throw new InvalidDataException(" - Image is too large to process.");
        }
        Console.WriteLine($" - Expected pixel data size: {expectedSize:N0} bytes");
        var pixels = new byte[expectedSize];
        var result = codec.GetPixels(info, pixels);
        if (result == SKCodecResult.Success || result == SKCodecResult.IncompleteInput)
        {
          ostream = new MemoryStream(pixels);
          return true;
        }
        else
        {
          Console.WriteLine($" - Failed to decode image data: {result}");
          return false;
        }
      }
    }
    catch (Exception ex)
    {
      Console.WriteLine(" - Failed to decode image data: " + ex.Message);
    }
    return false;
  }

  static ReadOnlySpan<byte> GetFileHeader(string path, int length)
  {
    Span<byte> header = new byte[length];
    using var fs = File.OpenRead(path);
    fs.ReadExactly(header);
    return header;
  }

  static bool IsImageFile(string path)
  {
    var ext = Path.GetExtension(path).ToLowerInvariant();

    if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".gif")
    {
      return true;
    }

    return false;
  }

  static double NLog(uint value)
  {
    return Math.Log(value + 1);
  }

  static IEnumerable<string> ExpandWildcardArgs(IEnumerable<string> args)
  {
    foreach (var arg in args)
    {
      if (arg.StartsWith('-')) continue;
      if (arg.Contains('*') || arg.Contains('?'))
      {
        var dir = Path.GetDirectoryName(arg) ?? ".";
        var pattern = Path.GetFileName(arg);
        if (Directory.Exists(dir))
        {
          var files = Directory.EnumerateFiles(dir, pattern);
          foreach (var file in files) yield return file;
        }
        else
        {
          Console.WriteLine($"WARN! Directory '{dir}' does not exist for wildcard argument '{arg}'.");
        }
      }
      else
      {
        yield return arg;
      }
    }
  }
}
