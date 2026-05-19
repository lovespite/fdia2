using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Fdia2.Core;

public static class VolumeProcessor
{
  public const int AxisLength = 256;
  public const int VoxelCount = AxisLength * AxisLength * AxisLength;
  const int BytesPerVoxel = sizeof(ushort);
  const int GroupSize = 3;
  const string RawEntryName = "volume.raw";
  const string MetaEntryName = "meta.json";

  sealed class VolumeZipMetadata
  {
    public string Format { get; set; } = "fdia3-volume-v1";
    public int[] Dimensions { get; set; } = [AxisLength, AxisLength, AxisLength];
    public string ElementType { get; set; } = "ushort";
    public string Endianness { get; set; } = "little";
    public string AxisOrder { get; set; } = "x-fastest,y,z";
    public string SourceFileName { get; set; } = string.Empty;
    public long NonZeroVoxelCount { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
  }

  public sealed class VolumeData
  {
    public required ushort[] Voxels { get; init; }
    public required string SourceFileName { get; init; }
    public long NonZeroVoxelCount { get; init; }
  }

  public static List<string> ProcessFiles(IEnumerable<string> filePaths, string outputDir)
  {
    var outputFiles = new List<string>();
    var buffer = new byte[256 * 1024];
    foreach (var filePath in filePaths)
    {
      try
      {
        Console.WriteLine($"Processing file: '{filePath}'...");
        var outputFile = ProcessFile(filePath, outputDir, buffer);
        outputFiles.Add(outputFile);
        Console.WriteLine($"DONE! '{filePath}' -> '{outputFile}'");
      }
      catch (Exception ex)
      {
        Console.WriteLine($"FAIL! Skipped file '{filePath}': {ex.Message}");
      }
    }

    return outputFiles;
  }

  static string ProcessFile(string filePath, string outputDir, byte[] buffer)
  {
    if (!File.Exists(filePath))
      throw new FileNotFoundException("File not found: " + filePath);

    using var stream = HeatmapProcessor.GetDataStream(filePath);
    var volume = new ushort[VoxelCount];
    FillVolume(stream, volume, buffer);

    var outputFilePath = Path.Combine(outputDir, Path.GetFileName(filePath) + ".volume.zip");
    SaveVolumeZip(volume, filePath, outputFilePath);
    return outputFilePath;
  }

  static void FillVolume(Stream stream, ushort[] volume, byte[] buffer)
  {
    Span<byte> carry = stackalloc byte[GroupSize];
    var carryCount = 0;

    int read;
    while ((read = stream.Read(buffer)) > 0)
    {
      var span = buffer.AsSpan(0, read);
      var offset = 0;

      if (carryCount > 0)
      {
        while (carryCount < GroupSize && offset < span.Length)
          carry[carryCount++] = span[offset++];

        if (carryCount == GroupSize)
        {
          IncrementVoxel(volume, carry[0], carry[1], carry[2]);
          carryCount = 0;
        }
      }

      var remaining = span.Length - offset;
      var alignedLength = remaining - (remaining % GroupSize);
      var aligned = span.Slice(offset, alignedLength);
      for (int i = 0; i < aligned.Length; i += GroupSize)
        IncrementVoxel(volume, aligned[i], aligned[i + 1], aligned[i + 2]);

      var tailStart = offset + alignedLength;
      carryCount = span.Length - tailStart;
      for (int i = 0; i < carryCount; i++)
        carry[i] = span[tailStart + i];
    }
  }

  static void IncrementVoxel(ushort[] volume, byte x, byte y, byte z)
  {
    var index = GetVoxelIndex(x, y, z);
    ref var value = ref volume[index];
    if (value < ushort.MaxValue)
      value++;
  }

  static int GetVoxelIndex(byte x, byte y, byte z) => x | (y << 8) | (z << 16);

  public static void SaveVolumeZip(ushort[] volume, string sourceFilePath, string outputZipPath)
  {
    if (volume.Length != VoxelCount)
      throw new ArgumentException($"Volume length must be {VoxelCount}.", nameof(volume));

    Directory.CreateDirectory(Path.GetDirectoryName(outputZipPath) ?? ".");
    if (File.Exists(outputZipPath))
      File.Delete(outputZipPath);

    var rawBytes = SerializeVolume(volume);
    var metadata = new VolumeZipMetadata
    {
      SourceFileName = Path.GetFileName(sourceFilePath),
      NonZeroVoxelCount = CountNonZero(volume),
      CreatedUtc = DateTimeOffset.UtcNow,
    };
    var metaJson = BuildMetadataJson(metadata);

    using var zip = ZipFile.Open(outputZipPath, ZipArchiveMode.Create);

    var rawEntry = zip.CreateEntry(RawEntryName, CompressionLevel.SmallestSize);
    using (var rawStream = rawEntry.Open())
      rawStream.Write(rawBytes);

    var metaEntry = zip.CreateEntry(MetaEntryName, CompressionLevel.SmallestSize);
    using (var metaWriter = new StreamWriter(metaEntry.Open()))
      metaWriter.Write(metaJson);
  }

  public static VolumeData LoadVolumeZip(string zipFilePath)
  {
    if (!File.Exists(zipFilePath))
      throw new FileNotFoundException("Volume zip file not found: " + zipFilePath);

    using var zip = ZipFile.OpenRead(zipFilePath);
    var rawEntry = zip.GetEntry(RawEntryName)
      ?? throw new InvalidDataException($"Volume zip is missing '{RawEntryName}'.");
    var metaEntry = zip.GetEntry(MetaEntryName);

    byte[] rawBytes;
    using (var rawStream = rawEntry.Open())
    {
      using var ms = new MemoryStream();
      rawStream.CopyTo(ms);
      rawBytes = ms.ToArray();
    }

    var expectedLength = VoxelCount * BytesPerVoxel;
    if (rawBytes.Length != expectedLength)
      throw new InvalidDataException($"Invalid '{RawEntryName}' length. Expected {expectedLength} bytes, got {rawBytes.Length}.");

    var volume = DeserializeVolume(rawBytes);
    var metadata = ReadMetadata(metaEntry, Path.GetFileName(zipFilePath), CountNonZero(volume));

    return new VolumeData
    {
      Voxels = volume,
      SourceFileName = metadata.SourceFileName,
      NonZeroVoxelCount = metadata.NonZeroVoxelCount,
    };
  }

  static byte[] SerializeVolume(ushort[] volume)
  {
    var rawBytes = new byte[volume.Length * BytesPerVoxel];
    if (BitConverter.IsLittleEndian)
    {
      Buffer.BlockCopy(volume, 0, rawBytes, 0, rawBytes.Length);
      return rawBytes;
    }

    for (int i = 0; i < volume.Length; i++)
      BinaryPrimitives.WriteUInt16LittleEndian(rawBytes.AsSpan(i * BytesPerVoxel, BytesPerVoxel), volume[i]);

    return rawBytes;
  }

  static ushort[] DeserializeVolume(byte[] rawBytes)
  {
    var volume = new ushort[VoxelCount];
    if (BitConverter.IsLittleEndian)
    {
      Buffer.BlockCopy(rawBytes, 0, volume, 0, rawBytes.Length);
      return volume;
    }

    for (int i = 0; i < volume.Length; i++)
      volume[i] = BinaryPrimitives.ReadUInt16LittleEndian(rawBytes.AsSpan(i * BytesPerVoxel, BytesPerVoxel));

    return volume;
  }

  static VolumeZipMetadata ReadMetadata(ZipArchiveEntry? metaEntry, string fallbackSourceFileName, long nonZeroVoxelCount)
  {
    if (metaEntry == null)
    {
      return new VolumeZipMetadata
      {
        SourceFileName = fallbackSourceFileName,
        NonZeroVoxelCount = nonZeroVoxelCount,
        CreatedUtc = DateTimeOffset.UtcNow,
      };
    }

    using var reader = new StreamReader(metaEntry.Open());
    var json = reader.ReadToEnd();
    using var document = JsonDocument.Parse(json);
    var root = document.RootElement;
    if (root.ValueKind != JsonValueKind.Object)
      throw new InvalidDataException("Invalid metadata JSON format.");

    var metadata = new VolumeZipMetadata
    {
      SourceFileName = fallbackSourceFileName,
      NonZeroVoxelCount = nonZeroVoxelCount,
      CreatedUtc = DateTimeOffset.UtcNow,
    };

    if (root.TryGetProperty("dimensions", out var dimensions))
      ValidateDimensions(dimensions);

    if (root.TryGetProperty("sourceFileName", out var sourceFileName)
        && sourceFileName.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(sourceFileName.GetString()))
    {
      metadata.SourceFileName = sourceFileName.GetString()!;
    }

    if (root.TryGetProperty("nonZeroVoxelCount", out var nonZero)
        && nonZero.TryGetInt64(out var nonZeroValue)
        && nonZeroValue >= 0)
    {
      metadata.NonZeroVoxelCount = nonZeroValue;
    }

    if (root.TryGetProperty("createdUtc", out var createdUtc)
        && createdUtc.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(createdUtc.GetString(), out var created))
    {
      metadata.CreatedUtc = created;
    }

    return metadata;
  }

  static string BuildMetadataJson(VolumeZipMetadata metadata)
  {
    using var stream = new MemoryStream();
    using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
    {
      writer.WriteStartObject();
      writer.WriteString("format", metadata.Format);
      writer.WriteStartArray("dimensions");
      writer.WriteNumberValue(AxisLength);
      writer.WriteNumberValue(AxisLength);
      writer.WriteNumberValue(AxisLength);
      writer.WriteEndArray();
      writer.WriteString("elementType", metadata.ElementType);
      writer.WriteString("endianness", metadata.Endianness);
      writer.WriteString("axisOrder", metadata.AxisOrder);
      writer.WriteString("sourceFileName", metadata.SourceFileName);
      writer.WriteNumber("nonZeroVoxelCount", metadata.NonZeroVoxelCount);
      writer.WriteString("createdUtc", metadata.CreatedUtc);
      writer.WriteEndObject();
    }

    return Encoding.UTF8.GetString(stream.ToArray());
  }

  static void ValidateDimensions(JsonElement dimensions)
  {
    if (dimensions.ValueKind != JsonValueKind.Array || dimensions.GetArrayLength() != 3)
      throw new InvalidDataException("Invalid dimensions in metadata.");

    var x = dimensions[0].GetInt32();
    var y = dimensions[1].GetInt32();
    var z = dimensions[2].GetInt32();
    if (x != AxisLength || y != AxisLength || z != AxisLength)
      throw new InvalidDataException("Unsupported volume dimensions in metadata.");
  }

  static long CountNonZero(ushort[] volume)
  {
    long count = 0;
    foreach (var value in volume)
    {
      if (value != 0)
        count++;
    }

    return count;
  }
}
