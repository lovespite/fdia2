using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.Json;

namespace Fdia2.Core;

public static class VolumeProcessor
{
    public const int AxisLength = 256;
    public const int VoxelCount = AxisLength * AxisLength * AxisLength;
    public const string Fd3Extension = ".fd3";
    public static int MaxParallelPipelines { get; set; } = Math.Max(1, Environment.ProcessorCount / 2 + 1);
    const int BytesPerVoxel = sizeof(uint);
    const int GroupSize = 3;
    const int StreamBufferSize = 256 * 1024;
    const int MappedBufferSize = StreamBufferSize - (StreamBufferSize % GroupSize);
    const long LargeFileThresholdBytes = 50L * 1024 * 1024;
    const int MaxChunkWorkers = 4;
    const string RawEntryName = "volume.raw";
    const string MetaEntryName = "meta.json";

    sealed class VolumeZipMetadata
    {
        public string Format { get; set; } = "fdia3-volume-v2";
        public int[] Dimensions { get; set; } = [AxisLength, AxisLength, AxisLength];
        public string ElementType { get; set; } = "uint";
        public string Endianness { get; set; } = "little";
        public string AxisOrder { get; set; } = "x-fastest,y,z";
        public string SourceFileName { get; set; } = string.Empty;
        public long NonZeroVoxelCount { get; set; }
        public DateTimeOffset CreatedUtc { get; set; }
    }

    public sealed class VolumeData
    {
        public required uint[] Voxels { get; init; }
        public required string SourceFileName { get; init; }
        public long NonZeroVoxelCount { get; init; }
    }

    public sealed class ProcessingFileProgress
    {
        public required string FilePath { get; init; }
        public required string FileName { get; init; }
        public int ProgressPercent { get; init; }
    }

    public sealed class ProcessingProgressSnapshot
    {
        public required int TotalFiles { get; init; }
        public required int PendingFiles { get; init; }
        public required int RunningFiles { get; init; }
        public required int SucceededFiles { get; init; }
        public required int FailedFiles { get; init; }
        public required int MaxParallelPipelines { get; init; }
        public required TimeSpan Elapsed { get; init; }
        public required IReadOnlyList<ProcessingFileProgress> ActiveFiles { get; init; }
    }

    public static List<string> ProcessFiles(IEnumerable<string> filePaths, string outputDir) =>
      ProcessFiles(filePaths, outputDir, null);

    public static List<string> ProcessFiles(
      IEnumerable<string> filePaths,
      string outputDir,
      Action<ProcessingProgressSnapshot>? progressCallback)
    {
        var inputFiles = filePaths.Select((path, index) => (Path: path, Index: index)).ToArray();
        if (inputFiles.Length == 0)
            return [];

        var pipelineLimit = ResolvePipelineLimit(inputFiles.Length);
        var outputFiles = new string?[inputFiles.Length];
        var tracker = progressCallback is null
          ? null
          : new ProcessingProgressTracker(inputFiles, pipelineLimit, progressCallback);
        tracker?.PublishSnapshot();

        Parallel.ForEach(inputFiles, new ParallelOptions { MaxDegreeOfParallelism = pipelineLimit }, item =>
        {
            tracker?.MarkRunning(item.Index);
            try
            {
                Console.WriteLine($"Processing file: '{item.Path}'...");
                var outputFile = ProcessFile(item.Path, outputDir, pipelineLimit, tracker?.CreateFileProgressReporter(item.Index));
                outputFiles[item.Index] = outputFile;
                tracker?.MarkSucceeded(item.Index);
                Console.WriteLine($"DONE! '{item.Path}' -> '{outputFile}'");
            }
            catch (Exception ex)
            {
                tracker?.MarkFailed(item.Index);
                Console.WriteLine($"FAIL! Skipped file '{item.Path}': {ex.Message}");
            }
        });

        tracker?.StopAndPublishFinalSnapshot();

        var completed = new List<string>(inputFiles.Length);
        foreach (var outputFile in outputFiles)
        {
            if (!string.IsNullOrWhiteSpace(outputFile))
                completed.Add(outputFile);
        }

        return completed;
    }

    static string ProcessFile(
      string filePath,
      string outputDir,
      int pipelineLimit,
      Action<int>? reportProgress)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("File not found: " + filePath);

        var volume = new uint[VoxelCount];
        var fileInfo = new FileInfo(filePath);
        var mappedBytesLength = (fileInfo.Length / GroupSize) * GroupSize;
        if (ShouldUseChunkedMappedPath(filePath, fileInfo))
        {
            var progress = CreateFileProgressCounter(mappedBytesLength, reportProgress);
            FillVolumeMapped(filePath, fileInfo.Length, volume, pipelineLimit, progress);
            progress?.Complete();
        }
        else
        {
            using var stream = HeatmapProcessor.GetDataStream(filePath);
            var buffer = new byte[StreamBufferSize];
            var totalBytes = ResolveReadableLength(stream, fileInfo.Length);
            var progress = CreateFileProgressCounter(totalBytes, reportProgress);
            FillVolume(stream, volume, buffer, progress);
            progress?.Complete();
        }

        var outputFilePath = Path.Combine(outputDir, Path.GetFileName(filePath) + Fd3Extension);
        SaveVolumeZip(volume, filePath, outputFilePath);
        return outputFilePath;
    }

    static int ResolvePipelineLimit(int fileCount)
    {
        var configuredLimit = MaxParallelPipelines > 0 ? MaxParallelPipelines : 1;
        return Math.Clamp(configuredLimit, 1, Math.Max(1, fileCount));
    }

    static bool ShouldUseChunkedMappedPath(string filePath, FileInfo fileInfo)
    {
        if (!OperatingSystem.IsWindows())
            return false;
        if (fileInfo.Length <= LargeFileThresholdBytes || fileInfo.Length < GroupSize)
            return false;
        if (HeatmapProcessor.IsImageFile(filePath))
            return false;

        return true;
    }

    static FileProgressCounter? CreateFileProgressCounter(long totalBytes, Action<int>? reportProgress)
    {
        if (reportProgress is null)
            return null;

        return new FileProgressCounter(totalBytes, reportProgress);
    }

    static long ResolveReadableLength(Stream stream, long fallbackLength)
    {
        if (stream.CanSeek)
        {
            try
            {
                var streamLength = stream.Length;
                if (streamLength > 0)
                    return streamLength;
            }
            catch (NotSupportedException)
            {
            }
        }

        return Math.Max(1, fallbackLength);
    }

    static void FillVolumeMapped(
      string filePath,
      long fileLength,
      uint[] volume,
      int pipelineLimit,
      FileProgressCounter? progress)
    {
        var totalGroups = fileLength / GroupSize;
        if (totalGroups <= 0)
            return;

        using var mappedFile = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        var workerCount = ResolveChunkWorkerCount(totalGroups, pipelineLimit);
        if (workerCount <= 1)
        {
            AccumulateMappedRange(mappedFile, 0, totalGroups * GroupSize, volume, progress);
            return;
        }

        var localVolumes = new uint[workerCount][];
        Parallel.For(0, workerCount, workerIndex =>
        {
            var startGroup = workerIndex * totalGroups / workerCount;
            var endGroup = (workerIndex + 1) * totalGroups / workerCount;
            var byteLength = (endGroup - startGroup) * GroupSize;
            if (byteLength <= 0)
                return;

            var localVolume = new uint[VoxelCount];
            var byteOffset = startGroup * GroupSize;
            AccumulateMappedRange(mappedFile, byteOffset, byteLength, localVolume, progress);
            localVolumes[workerIndex] = localVolume;
        });

        foreach (var localVolume in localVolumes)
        {
            if (localVolume is null)
                continue;

            MergeVolumeSaturating(volume, localVolume);
        }
    }

    static int ResolveChunkWorkerCount(long totalGroups, int pipelineLimit)
    {
        if (totalGroups <= 1 || Environment.ProcessorCount <= 1)
            return 1;

        var safePipelineLimit = Math.Max(1, pipelineLimit);
        var cpuBudget = Math.Max(1, Environment.ProcessorCount / safePipelineLimit);
        var workerCount = Math.Clamp(cpuBudget, 1, MaxChunkWorkers);

        if (workerCount == 1 && totalGroups > 1 && Environment.ProcessorCount > 1)
            workerCount = Math.Min(MaxChunkWorkers, Environment.ProcessorCount);

        if (totalGroups < workerCount)
            workerCount = (int)totalGroups;

        return Math.Max(1, workerCount);
    }

    static void AccumulateMappedRange(
      MemoryMappedFile mappedFile,
      long byteOffset,
      long byteLength,
      uint[] targetVolume,
      FileProgressCounter? progress)
    {
        if (byteLength <= 0)
            return;

        using var accessor = mappedFile.CreateViewAccessor(byteOffset, byteLength, MemoryMappedFileAccess.Read);
        var buffer = new byte[MappedBufferSize];
        long position = 0;
        while (position < byteLength)
        {
            var requestLength = (int)Math.Min(buffer.Length, byteLength - position);
            var read = accessor.ReadArray(position, buffer, 0, requestLength);
            if (read <= 0)
                break;

            var alignedRead = read - (read % GroupSize);
            for (int i = 0; i < alignedRead; i += GroupSize)
                IncrementVoxel(targetVolume, buffer[i], buffer[i + 1], buffer[i + 2]);
            progress?.AddProcessedBytes(alignedRead);

            if (alignedRead <= 0)
                break;

            position += alignedRead;
        }
    }

    static void MergeVolumeSaturating(uint[] destination, uint[] source)
    {
        for (int i = 0; i < destination.Length; i++)
        {
            var add = source[i];
            if (add == 0)
                continue;

            var current = destination[i];
            var sum = (ulong)current + add;
            destination[i] = sum > uint.MaxValue ? uint.MaxValue : (uint)sum;
        }
    }

    static void FillVolume(Stream stream, uint[] volume, byte[] buffer, FileProgressCounter? progress)
    {
        Span<byte> carry = stackalloc byte[GroupSize];
        var carryCount = 0;

        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            progress?.AddProcessedBytes(read);
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

    sealed class FileProgressCounter
    {
        readonly long totalBytes;
        readonly Action<int> reportProgress;
        long processedBytes;
        int lastPercent = -1;

        public FileProgressCounter(long totalBytes, Action<int> reportProgress)
        {
            this.totalBytes = Math.Max(1, totalBytes);
            this.reportProgress = reportProgress;
        }

        public void AddProcessedBytes(long bytes)
        {
            if (bytes <= 0)
                return;

            var processed = Interlocked.Add(ref processedBytes, bytes);
            var percent = (int)Math.Clamp(processed * 100 / totalBytes, 0, 99);
            PublishPercent(percent);
        }

        public void Complete()
        {
            PublishPercent(100);
        }

        void PublishPercent(int percent)
        {
            while (true)
            {
                var current = Volatile.Read(ref lastPercent);
                if (percent <= current)
                    return;
                if (Interlocked.CompareExchange(ref lastPercent, percent, current) == current)
                {
                    reportProgress(percent);
                    return;
                }
            }
        }
    }

    sealed class ProcessingProgressTracker
    {
        enum FileRuntimeState
        {
            Pending,
            Running,
            Succeeded,
            Failed,
        }

        sealed class FileRuntimeEntry
        {
            public required string FilePath { get; init; }
            public required string FileName { get; init; }
            public FileRuntimeState State { get; set; }
            public int ProgressPercent { get; set; }
        }

        readonly object sync = new();
        readonly Action<ProcessingProgressSnapshot> publish;
        readonly FileRuntimeEntry[] files;
        readonly Stopwatch elapsed = Stopwatch.StartNew();
        readonly int maxParallelPipelines;
        int pendingFiles;
        int runningFiles;
        int succeededFiles;
        int failedFiles;

        public ProcessingProgressTracker((string Path, int Index)[] inputFiles, int maxParallelPipelines, Action<ProcessingProgressSnapshot> publish)
        {
            this.publish = publish;
            this.maxParallelPipelines = maxParallelPipelines;
            files = new FileRuntimeEntry[inputFiles.Length];
            pendingFiles = inputFiles.Length;
            foreach (var item in inputFiles)
            {
                files[item.Index] = new FileRuntimeEntry
                {
                    FilePath = item.Path,
                    FileName = Path.GetFileName(item.Path),
                    State = FileRuntimeState.Pending,
                    ProgressPercent = 0,
                };
            }
        }

        public Action<int> CreateFileProgressReporter(int index) => percent => UpdateProgressPercent(index, percent);

        public void MarkRunning(int index)
        {
            if (!Mutate(index, entry =>
            {
                if (entry.State != FileRuntimeState.Pending)
                    return false;

                entry.State = FileRuntimeState.Running;
                entry.ProgressPercent = 0;
                pendingFiles--;
                runningFiles++;
                return true;
            }))
            {
                return;
            }

            PublishSnapshot();
        }

        public void MarkSucceeded(int index)
        {
            if (!Mutate(index, entry =>
            {
                if (entry.State != FileRuntimeState.Running)
                    return false;

                entry.State = FileRuntimeState.Succeeded;
                entry.ProgressPercent = 100;
                runningFiles--;
                succeededFiles++;
                return true;
            }))
            {
                return;
            }

            PublishSnapshot();
        }

        public void MarkFailed(int index)
        {
            if (!Mutate(index, entry =>
            {
                if (entry.State != FileRuntimeState.Running)
                    return false;

                entry.State = FileRuntimeState.Failed;
                runningFiles--;
                failedFiles++;
                return true;
            }))
            {
                return;
            }

            PublishSnapshot();
        }

        public void UpdateProgressPercent(int index, int progressPercent)
        {
            var normalized = Math.Clamp(progressPercent, 0, 100);
            if (!Mutate(index, entry =>
            {
                if (entry.State != FileRuntimeState.Running)
                    return false;
                if (normalized <= entry.ProgressPercent)
                    return false;

                entry.ProgressPercent = normalized;
                return true;
            }))
            {
                return;
            }

            PublishSnapshot();
        }

        public void StopAndPublishFinalSnapshot()
        {
            elapsed.Stop();
            PublishSnapshot();
        }

        public void PublishSnapshot()
        {
            ProcessingProgressSnapshot snapshot;
            lock (sync)
            {
                var activeFiles = files
                  .Where(file => file.State == FileRuntimeState.Running)
                  .Select(file => new ProcessingFileProgress
                  {
                      FilePath = file.FilePath,
                      FileName = file.FileName,
                      ProgressPercent = file.ProgressPercent,
                  })
                  .ToArray();

                snapshot = new ProcessingProgressSnapshot
                {
                    TotalFiles = files.Length,
                    PendingFiles = pendingFiles,
                    RunningFiles = runningFiles,
                    SucceededFiles = succeededFiles,
                    FailedFiles = failedFiles,
                    MaxParallelPipelines = maxParallelPipelines,
                    Elapsed = elapsed.Elapsed,
                    ActiveFiles = activeFiles,
                };
            }

            publish(snapshot);
        }

        bool Mutate(int index, Func<FileRuntimeEntry, bool> mutation)
        {
            lock (sync)
            {
                return mutation(files[index]);
            }
        }
    }

    static void IncrementVoxel(uint[] volume, byte x, byte y, byte z)
    {
        var index = GetVoxelIndex(x, y, z);
        ref var value = ref volume[index];
        if (value < uint.MaxValue)
            value++;
    }

    static int GetVoxelIndex(byte x, byte y, byte z) => x | (y << 8) | (z << 16);

    public static void SaveVolumeZip(uint[] volume, string sourceFilePath, string outputZipPath)
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

        uint[] volume;
        if (rawBytes.Length == VoxelCount * sizeof(ushort))
        {
            volume = DeserializeVolumeLegacy(rawBytes);
        }
        else if (rawBytes.Length == VoxelCount * sizeof(uint))
        {
            volume = DeserializeVolume(rawBytes);
        }
        else
        {
            throw new InvalidDataException($"Invalid '{RawEntryName}' length. Expected {VoxelCount * 4} or {VoxelCount * 2} bytes, got {rawBytes.Length}.");
        }

        var metadata = ReadMetadata(metaEntry, Path.GetFileName(zipFilePath), () => CountNonZero(volume));

        return new VolumeData
        {
            Voxels = volume,
            SourceFileName = metadata.SourceFileName,
            NonZeroVoxelCount = metadata.NonZeroVoxelCount,
        };
    }

    static uint[] DeserializeVolumeLegacy(byte[] rawBytes)
    {
        var volume = new uint[VoxelCount];
        for (int i = 0; i < VoxelCount; i++)
        {
            volume[i] = BinaryPrimitives.ReadUInt16LittleEndian(rawBytes.AsSpan(i * 2, 2));
        }
        return volume;
    }

    public static bool IsFd3FilePath(string filePath) =>
      string.Equals(Path.GetExtension(filePath), Fd3Extension, StringComparison.OrdinalIgnoreCase);

    public static bool TryValidateFd3(string filePath, out string? error)
    {
        error = null;
        if (!File.Exists(filePath))
        {
            error = "File not found: " + filePath;
            return false;
        }

        if (!IsFd3FilePath(filePath))
        {
            error = "File extension is not .fd3: " + filePath;
            return false;
        }

        try
        {
            using var zip = ZipFile.OpenRead(filePath);
            var rawEntry = zip.GetEntry(RawEntryName);
            if (rawEntry == null)
            {
                error = $"Volume file is missing '{RawEntryName}'.";
                return false;
            }

            if (rawEntry.Length != VoxelCount * sizeof(uint) && rawEntry.Length != VoxelCount * sizeof(ushort))
            {
                error = $"Invalid '{RawEntryName}' length. Expected {VoxelCount * 4} or {VoxelCount * 2} bytes, got {rawEntry.Length}.";
                return false;
            }

            var metaEntry = zip.GetEntry(MetaEntryName);
            if (metaEntry != null)
            {
                using var reader = new StreamReader(metaEntry.Open());
                var json = reader.ReadToEnd();
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    error = "Invalid metadata JSON format.";
                    return false;
                }

                if (root.TryGetProperty("dimensions", out var dimensions))
                    ValidateDimensions(dimensions);
            }

            return true;
        }
        catch (Exception ex)
        {
            error = "Invalid .fd3 file: " + ex.Message;
            return false;
        }
    }

    static byte[] SerializeVolume(uint[] volume)
    {
        var rawBytes = new byte[volume.Length * BytesPerVoxel];
        if (BitConverter.IsLittleEndian)
        {
            Buffer.BlockCopy(volume, 0, rawBytes, 0, rawBytes.Length);
            return rawBytes;
        }

        for (int i = 0; i < volume.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(rawBytes.AsSpan(i * BytesPerVoxel, BytesPerVoxel), volume[i]);

        return rawBytes;
    }

    static uint[] DeserializeVolume(byte[] rawBytes)
    {
        var volume = new uint[VoxelCount];
        if (BitConverter.IsLittleEndian)
        {
            Buffer.BlockCopy(rawBytes, 0, volume, 0, rawBytes.Length);
            return volume;
        }

        for (int i = 0; i < volume.Length; i++)
            volume[i] = BinaryPrimitives.ReadUInt32LittleEndian(rawBytes.AsSpan(i * BytesPerVoxel, BytesPerVoxel));

        return volume;
    }

    static VolumeZipMetadata ReadMetadata(ZipArchiveEntry? metaEntry, string fallbackSourceFileName, Func<long> CountNonZeroVoxel)
    {
        if (metaEntry == null)
        {
            return new VolumeZipMetadata
            {
                SourceFileName = fallbackSourceFileName,
                NonZeroVoxelCount = CountNonZeroVoxel(),
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
            NonZeroVoxelCount = 0,
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
        else
        {
            metadata.NonZeroVoxelCount = CountNonZeroVoxel();
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

    static long CountNonZero(uint[] volume)
    {
        long count = 0;
        foreach (var value in volume)
        {
            if (value > 0)
                count++;
        }
        return count;
    }
}
