using SkiaSharp;

namespace Fdia2.Core;

public enum ColorMode
{
  Grayscale,
  InfraredThermogram,
}

public static unsafe class HeatmapProcessor
{
  private delegate void WritePixel(byte* pBitmap, int offset, byte intensity);

  public static ColorMode ColorMode { get; set; } = ColorMode.Grayscale;

  public static int Scale { get; set; } = 1;

  static readonly (byte R, byte G, byte B)[] InfraredThermogramPalette;

  static HeatmapProcessor()
  {
    (byte R, byte G, byte B)[] stops =
    [
      (  0,   0,   0),
      (128,   0, 128),
      (255,   0,   0),
      (255, 165,   0),
      (255, 255,   0),
      (255, 255, 255),
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

  public static List<string> ProcessFiles(IEnumerable<string> filePaths, string outputDir)
  {
    Span<byte> map = stackalloc byte[256 * 256];
    Span<uint> counter = stackalloc uint[256 * 256];
    Span<byte> buffer = stackalloc byte[256 * 1024];

    var outputFiles = new List<string>();
    foreach (var filePath in filePaths)
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

    return outputFiles;
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

    var nMax = NLog(max);

    var bitmap = CreateBitmap(out var writer);
    var stride = bitmap.RowBytes;
    var bytesPerPixel = bitmap.Info.BytesPerPixel;
    var bitmapData = bitmap.GetPixelSpan();

    fixed (byte* pBitmap = bitmapData)
    fixed (uint* pMap = counter)
    {
      for (int y = 0; y < 256; y++)
      {
        for (int x = 0; x < 256; x++)
        {
          var ptr = pMap + y * 256 + x;
          var intensity = (byte)(NLog(*ptr) / nMax * 255);
          var offset = y * stride + x * bytesPerPixel;
          writer(pBitmap, offset, intensity);
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

  static SKBitmap CreateBitmap(out WritePixel writer)
  {
    switch (ColorMode)
    {
      case ColorMode.Grayscale:
        writer = WritePixelGray;
        return new SKBitmap(new SKImageInfo(256, 256, SKColorType.Gray8));
      case ColorMode.InfraredThermogram:
        writer = WritePixelInfrared;
        return new SKBitmap(new SKImageInfo(256, 256, SKColorType.Rgb888x));
      default:
        throw new InvalidOperationException("Unsupported color mode: " + ColorMode);
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

  public static Stream GetDataStream(string file)
  {
    var fi = new FileInfo(file);
    if (fi.Length > int.MaxValue)
      throw new InvalidOperationException("File is too large to process.");
    if (fi.Length < 2)
      throw new InvalidOperationException("File is too small to process.");

    if (IsImageFile(file) && TryDecodeImage(file, out var imageStream) && imageStream.Length > 0)
      return imageStream;

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
          throw new InvalidDataException(" - Image is too large to process.");

        Console.WriteLine($" - Expected pixel data size: {expectedSize:N0} bytes");
        var pixels = new byte[expectedSize];
        var result = codec.GetPixels(info, pixels);
        if (result == SKCodecResult.Success || result == SKCodecResult.IncompleteInput)
        {
          ostream = new MemoryStream(pixels);
          return true;
        }

        Console.WriteLine($" - Failed to decode image data: {result}");
      }
    }
    catch (Exception ex)
    {
      Console.WriteLine(" - Failed to decode image data: " + ex.Message);
    }

    return false;
  }

  public static bool IsImageFile(string path)
  {
    var ext = Path.GetExtension(path).ToLowerInvariant();
    return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".gif";
  }

  public static double NLog(uint value) => Math.Log(value + 1);

  public static void OpenOutputDirectory(string outputDir)
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

  public static IEnumerable<string> ExpandWildcardArgs(IEnumerable<string> args)
  {
    foreach (var arg in args)
    {
      if (arg.StartsWith('-'))
        continue;

      if (arg.Contains('*') || arg.Contains('?'))
      {
        var dir = Path.GetDirectoryName(arg) ?? ".";
        var pattern = Path.GetFileName(arg);
        if (Directory.Exists(dir))
        {
          foreach (var file in Directory.EnumerateFiles(dir, pattern))
            yield return file;
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
