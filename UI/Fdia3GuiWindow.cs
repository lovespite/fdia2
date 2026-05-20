using Fdia2.Core;
using Fdia2.UI.Controls;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace Fdia2.UI;

public sealed class Fdia3GuiWindow : GameWindow
{
    enum RenderMode
    {
        PointCloud,
        VolumeComposite,
        VolumeMip,
        VolumeHybrid,
    }

    sealed class ProcessingOverlayStateData
    {
        public bool IsVisible { get; set; }
        public bool IsActive { get; set; }
        public int TotalFiles { get; set; }
        public int PendingFiles { get; set; }
        public int RunningFiles { get; set; }
        public int SucceededFiles { get; set; }
        public int FailedFiles { get; set; }
        public int MaxParallelPipelines { get; set; }
        public TimeSpan Elapsed { get; set; }
        public string Summary { get; set; } = string.Empty;
        public List<VolumeProcessor.ProcessingFileProgress> ActiveFiles { get; } = [];
    }

    readonly string outputDir;
    readonly List<string> pendingFiles;
    readonly bool viewOutputDir;
    readonly List<string> previewFiles = [];
    readonly object processingOverlaySync = new();
    readonly ProcessingOverlayStateData processingOverlayState = new();
    DateTimeOffset processingOverlayStartedUtc;
    Task<List<string>>? processingTask;
    int previewIndex = -1;

    readonly CameraState camera = new();
    readonly ReferenceGridControl referenceGrid = new();
    readonly PointCloudControl pointCloud = new();
    readonly VolumeControl volume = new();
    readonly HudControl hud = new();
    readonly ProcessingOverlayControl processingOverlay = new();

    RenderMode renderMode = RenderMode.VolumeMip;
    bool clippingEnabled = false;
    float clipOffset = 0f;
    float densityGain = DensityGainMax;
    float opacityGain = OpacityGainMax;
    string status = "Ready";
    string hudFileName = "None";
    HudSliderKey activeHudSlider = HudSliderKey.None;
    bool useWhiteBackground = false;

    bool isRotating;
    Vector2 lastMousePosition;

    const float ClipOffsetMin = -0.5f;
    const float ClipOffsetMax = 0.5f;
    const float ClipOffsetStep = 0.02f;
    const float DensityGainMin = 0.1f;
    const float DensityGainMax = 6.0f;
    const float OpacityGainMin = 0.05f;
    const float OpacityGainMax = 8.0f;
    const string BaseWindowTitle = "fdia3";

    public Fdia3GuiWindow(string outputDir, List<string> initialFiles, bool viewOutputDir = false)
      : base(
          new GameWindowSettings
          {
              UpdateFrequency = 60,
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

        referenceGrid.Initialize();
        pointCloud.Initialize();
        volume.Initialize();
        hud.Initialize();
        processingOverlay.Initialize();
        PrintHelp();

        if (pendingFiles.Count > 0)
            StartProcessingPendingFiles();
        else
            status = "Drag files into this window, then press F5";

        UpdateHudContent(true);
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
        UpdateHudContent(true);
    }

    protected override void OnUpdateFrame(FrameEventArgs args)
    {
        base.OnUpdateFrame(args);
        UpdateProcessingStatus();
        UpdateHudContent(false);
        UpdateProcessingOverlay();
    }

    protected override void OnRenderFrame(FrameEventArgs args)
    {
        base.OnRenderFrame(args);
        if (useWhiteBackground)
            GL.ClearColor(new Color4(0.95f, 0.95f, 0.95f, 1f));
        else
            GL.ClearColor(new Color4(0.05f, 0.05f, 0.08f, 1f));
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        referenceGrid.Render(camera, ClientSize);
        if (renderMode == RenderMode.PointCloud)
            pointCloud.Render(camera, ClientSize);
        else
            volume.Render(camera, ClientSize, ToVolumeMode(renderMode), densityGain, opacityGain, clippingEnabled, clipOffset);
        hud.Render(ClientSize);
        processingOverlay.Render(ClientSize);

        SwapBuffers();
    }

    static VolumeRenderMode ToVolumeMode(RenderMode m) => m switch
    {
        RenderMode.VolumeMip => VolumeRenderMode.Mip,
        RenderMode.VolumeHybrid => VolumeRenderMode.Hybrid,
        _ => VolumeRenderMode.Composite,
    };

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
                camera.Reset();
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
            case Keys.F4:
                SetRenderMode(RenderMode.VolumeHybrid);
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
            case Keys.B:
                useWhiteBackground = !useWhiteBackground;
                status = useWhiteBackground ? "Switched to white background" : "Switched to dark background";
                break;
        }

        UpdateHudContent(false);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButton.Left)
            return;

        var hit = hud.HitTest(MousePosition);
        if (hit != HudSliderKey.None && IsSliderEnabled(hit))
        {
            activeHudSlider = hit;
            isRotating = false;
            ApplyHudSliderValue(MousePosition.X);
            return;
        }

        isRotating = true;
        lastMousePosition = MousePosition;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButton.Left)
            return;

        if (activeHudSlider != HudSliderKey.None)
        {
            activeHudSlider = HudSliderKey.None;
            return;
        }

        isRotating = false;
    }

    protected override void OnMouseMove(MouseMoveEventArgs e)
    {
        base.OnMouseMove(e);
        if (activeHudSlider != HudSliderKey.None)
        {
            ApplyHudSliderValue(e.Position.X);
            return;
        }

        if (!isRotating)
            return;

        var delta = e.Position - lastMousePosition;
        lastMousePosition = e.Position;
        camera.Yaw += delta.X * 0.01f;
        camera.Pitch = MathHelper.Clamp(camera.Pitch - delta.Y * 0.01f, -1.55f, 1.55f);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Math.Abs(e.OffsetY) <= float.Epsilon)
            return;

        camera.Distance = MathHelper.Clamp(camera.Distance * MathF.Exp(-e.OffsetY * 0.1f), 1.2f, 20f);
        status = $"Zoom: {camera.Distance:F2}";
        UpdateHudContent(false);
    }

    bool IsSliderEnabled(HudSliderKey key) => key switch
    {
        HudSliderKey.Density => renderMode == RenderMode.VolumeComposite || renderMode == RenderMode.VolumeHybrid,
        HudSliderKey.Opacity => renderMode == RenderMode.VolumeComposite || renderMode == RenderMode.VolumeHybrid,
        HudSliderKey.Slice => renderMode == RenderMode.VolumeMip,
        _ => false,
    };

    void ApplyHudSliderValue(float mouseX)
    {
        var value = hud.MouseToValue(activeHudSlider, mouseX);
        if (value is null) return;

        switch (activeHudSlider)
        {
            case HudSliderKey.Density:
                densityGain = value.Value;
                status = $"Density slider: {densityGain:F2}";
                break;
            case HudSliderKey.Opacity:
                opacityGain = value.Value;
                status = $"Opacity slider: {opacityGain:F2}";
                break;
            case HudSliderKey.Slice:
                clipOffset = value.Value;
                status = $"Slice slider: {clipOffset:F2}";
                break;
        }

        UpdateHudContent(false);
    }

    void SetRenderMode(RenderMode mode)
    {
        renderMode = mode;
        activeHudSlider = HudSliderKey.None;

        status = mode switch
        {
            RenderMode.PointCloud => "Switched to point cloud",
            RenderMode.VolumeComposite => "Switched to volume composite",
            RenderMode.VolumeMip => "Switched to volume MIP",
            RenderMode.VolumeHybrid => "Switched to volume hybrid",
            _ => status,
        };
    }

    void StartProcessingPendingFiles()
    {
        if (processingTask is { IsCompleted: false })
        {
            status = "Processing is already in progress";
            return;
        }

        if (pendingFiles.Count == 0)
        {
            status = "No files queued";
            Console.WriteLine("No files queued.");
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
            HideProcessingOverlay();
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
            return;
        }

        status = directPreviewFiles.Count > 0
          ? $"Loaded {directPreviewFiles.Count} .fd3 file(s), processing {filesToProcess.Count} file(s)..."
          : $"Processing {filesToProcess.Count} file(s)...";
        if (invalidFd3Count > 0)
            status += $" | {invalidFd3Count} invalid .fd3 skipped";

        Console.WriteLine(status);
        BeginProcessingOverlay(filesToProcess.Count);
        processingTask = Task.Run(() => VolumeProcessor.ProcessFiles(filesToProcess, outputDir, OnProcessingProgress));
    }

    void UpdateProcessingStatus()
    {
        if (processingTask is not { IsCompleted: true })
            return;

        var processedCount = 0;
        var processingFailed = false;
        try
        {
            var outputFiles = processingTask.GetAwaiter().GetResult();
            processedCount = outputFiles.Count;
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
            processingFailed = true;
            status = "Processing failed";
            Console.WriteLine("FAIL! Processing task failed: " + ex.GetBaseException().Message);
        }
        finally
        {
            FinalizeProcessingOverlay(processedCount, processingFailed);
            processingTask = null;
        }
    }

    void BeginProcessingOverlay(int totalFiles)
    {
        lock (processingOverlaySync)
        {
            processingOverlayStartedUtc = DateTimeOffset.UtcNow;
            processingOverlayState.IsVisible = true;
            processingOverlayState.IsActive = true;
            processingOverlayState.TotalFiles = totalFiles;
            processingOverlayState.PendingFiles = totalFiles;
            processingOverlayState.RunningFiles = 0;
            processingOverlayState.SucceededFiles = 0;
            processingOverlayState.FailedFiles = 0;
            processingOverlayState.MaxParallelPipelines = Math.Min(VolumeProcessor.MaxParallelPipelines, totalFiles);
            processingOverlayState.Elapsed = TimeSpan.Zero;
            processingOverlayState.Summary = string.Empty;
            processingOverlayState.ActiveFiles.Clear();
        }
    }

    void OnProcessingProgress(VolumeProcessor.ProcessingProgressSnapshot snapshot)
    {
        lock (processingOverlaySync)
        {
            processingOverlayState.IsVisible = true;
            processingOverlayState.IsActive = snapshot.PendingFiles > 0 || snapshot.RunningFiles > 0;
            processingOverlayState.TotalFiles = snapshot.TotalFiles;
            processingOverlayState.PendingFiles = snapshot.PendingFiles;
            processingOverlayState.RunningFiles = snapshot.RunningFiles;
            processingOverlayState.SucceededFiles = snapshot.SucceededFiles;
            processingOverlayState.FailedFiles = snapshot.FailedFiles;
            processingOverlayState.MaxParallelPipelines = snapshot.MaxParallelPipelines;
            processingOverlayState.Elapsed = snapshot.Elapsed;
            processingOverlayState.ActiveFiles.Clear();
            processingOverlayState.ActiveFiles.AddRange(snapshot.ActiveFiles);
            if (!processingOverlayState.IsActive && processingOverlayState.TotalFiles > 0)
            {
                processingOverlayState.Summary = BuildProcessingSummary(
                  processingOverlayState.SucceededFiles,
                  processingOverlayState.FailedFiles,
                  processingOverlayState.Elapsed);
            }
        }
    }

    void HideProcessingOverlay()
    {
        lock (processingOverlaySync)
        {
            processingOverlayState.IsVisible = false;
            processingOverlayState.IsActive = false;
            processingOverlayState.TotalFiles = 0;
            processingOverlayState.PendingFiles = 0;
            processingOverlayState.RunningFiles = 0;
            processingOverlayState.SucceededFiles = 0;
            processingOverlayState.FailedFiles = 0;
            processingOverlayState.MaxParallelPipelines = 0;
            processingOverlayState.Elapsed = TimeSpan.Zero;
            processingOverlayState.Summary = string.Empty;
            processingOverlayState.ActiveFiles.Clear();
        }
    }

    void FinalizeProcessingOverlay(int succeededFiles, bool processingFailed)
    {
        lock (processingOverlaySync)
        {
            if (!processingOverlayState.IsVisible)
                return;

            processingOverlayState.IsActive = false;
            processingOverlayState.PendingFiles = 0;
            processingOverlayState.RunningFiles = 0;
            processingOverlayState.SucceededFiles = succeededFiles;
            if (processingFailed && processingOverlayState.TotalFiles > succeededFiles && processingOverlayState.FailedFiles == 0)
                processingOverlayState.FailedFiles = processingOverlayState.TotalFiles - succeededFiles;
            if (processingOverlayState.Elapsed <= TimeSpan.Zero)
                processingOverlayState.Elapsed = DateTimeOffset.UtcNow - processingOverlayStartedUtc;
            processingOverlayState.ActiveFiles.Clear();
            processingOverlayState.Summary = BuildProcessingSummary(
              processingOverlayState.SucceededFiles,
              processingOverlayState.FailedFiles,
              processingOverlayState.Elapsed);
            if (processingFailed)
                processingOverlayState.Summary += " (task failed)";
        }
    }

    static string BuildProcessingSummary(int succeededFiles, int failedFiles, TimeSpan elapsed) =>
      $"Summary: succeeded {succeededFiles}, failed/skipped {failedFiles}, elapsed {FormatElapsed(elapsed)}";

    static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;
        return elapsed.TotalHours >= 1
          ? elapsed.ToString(@"hh\:mm\:ss")
          : elapsed.ToString(@"mm\:ss");
    }

    void UpdateProcessingOverlay()
    {
        ProcessingOverlayControl.State snapshot;
        lock (processingOverlaySync)
        {
            snapshot = new ProcessingOverlayControl.State
            {
                IsVisible = processingOverlayState.IsVisible,
                IsActive = processingOverlayState.IsActive,
                TotalFiles = processingOverlayState.TotalFiles,
                PendingFiles = processingOverlayState.PendingFiles,
                RunningFiles = processingOverlayState.RunningFiles,
                SucceededFiles = processingOverlayState.SucceededFiles,
                FailedFiles = processingOverlayState.FailedFiles,
                MaxParallelPipelines = processingOverlayState.MaxParallelPipelines,
                Elapsed = processingOverlayState.Elapsed,
                Summary = processingOverlayState.Summary,
                ActiveFiles = processingOverlayState.ActiveFiles.ToArray(),
            };
        }
        processingOverlay.Update(snapshot);
    }

    void UpdateHudContent(bool force)
    {
        var text = BuildHudText();
        var sliders = BuildSliders();
        hud.Update(text, sliders);
        if (force || !string.Equals(Title, BaseWindowTitle, StringComparison.Ordinal))
            Title = BaseWindowTitle;
    }

    string BuildHudText()
    {
        var isBusy = processingTask is { IsCompleted: false } ? "Yes" : "No";
        var previewStatus = previewFiles.Count > 0 && previewIndex >= 0
          ? $"{previewIndex + 1}/{previewFiles.Count}"
          : "None";
        var clipState = clippingEnabled ? $"On@{clipOffset:F2}" : "Off";

        var sb = new System.Text.StringBuilder(1024);
        sb.Append("Mode: ").Append(renderMode).AppendLine();
        sb.Append("Dens: ").Append(densityGain.ToString("F2")).Append(" | Opac: ").Append(opacityGain.ToString("F2")).Append(" (Auto x").Append(volume.AutoOpacityMultiplier.ToString("F1")).AppendLine(")");
        sb.Append("Clip: ").Append(clipState).AppendLine();
        sb.Append("Pending: ").Append(pendingFiles.Count).Append(" | Preview: ").Append(previewStatus).AppendLine();
        sb.Append("File: ").Append(hudFileName).AppendLine();
        sb.Append("Points: ").Append(pointCloud.PointCount.ToString("N0")).Append(" | Busy: ").Append(isBusy).AppendLine();
        sb.Append("Origin(0,0,0): (").Append(ReferenceGridControl.Origin.X.ToString("F1")).Append(',').Append(ReferenceGridControl.Origin.Y.ToString("F1")).Append(',').Append(ReferenceGridControl.Origin.Z.ToString("F1")).AppendLine(")");
        sb.Append("Status: ").Append(status).AppendLine();
        sb.AppendLine("Keys: F1-F4 Mode | C Clip | [ ] ClipOffset | B BG");
        sb.AppendLine("      , . Density | - = Opacity | <- -> Preview | R Reset");

        if (renderMode == RenderMode.VolumeComposite || renderMode == RenderMode.VolumeHybrid)
            sb.Append("Composite sliders: drag Density/Opacity bars below");
        else if (renderMode == RenderMode.VolumeMip)
            sb.Append("MIP slider: drag Slice bar below");

        return sb.ToString();
    }

    IReadOnlyList<HudSliderConfig> BuildSliders()
    {
        if (renderMode == RenderMode.VolumeComposite || renderMode == RenderMode.VolumeHybrid)
        {
            return new[]
            {
                new HudSliderConfig(HudSliderKey.Density, "Density", densityGain, DensityGainMin, DensityGainMax),
                new HudSliderConfig(HudSliderKey.Opacity, "Opacity", opacityGain, OpacityGainMin, OpacityGainMax),
            };
        }
        if (renderMode == RenderMode.VolumeMip)
        {
            return new[]
            {
                new HudSliderConfig(HudSliderKey.Slice, "Slice", clipOffset, ClipOffsetMin, ClipOffsetMax),
            };
        }
        return Array.Empty<HudSliderConfig>();
    }

    void NavigatePreview(int delta)
    {
        if (processingTask is not { IsCompleted: false })
            HideProcessingOverlay();

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

        volume.UploadVolume(volumeData.Voxels);
        var vertices = PointCloudControl.BuildVertices(volumeData, out var loadedPointCount);
        pointCloud.Upload(vertices, loadedPointCount);
        previewIndex = normalizedIndex;
        hudFileName = ResolveHudFileName(previewFilePath, volumeData);
        return true;
    }

    static string ResolveHudFileName(string previewFilePath, VolumeProcessor.VolumeData volumeData)
    {
        if (!string.IsNullOrWhiteSpace(volumeData.SourceFileName))
            return Path.GetFileName(volumeData.SourceFileName);

        return Path.GetFileName(previewFilePath);
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

    void ClearPreviewData()
    {
        pointCloud.Upload(Array.Empty<float>(), 0);
        volume.ClearVolume();
        hudFileName = "None";
    }

    protected override void OnUnload()
    {
        referenceGrid.Dispose();
        pointCloud.Dispose();
        volume.Dispose();
        hud.Dispose();
        processingOverlay.Dispose();
        base.OnUnload();
    }

    static void PrintHelp()
    {
        Console.WriteLine("fdia3 GUI mode started.");
        Console.WriteLine("Drag files to the window, then press F5 to process/load.");
        Console.WriteLine("Valid .fd3 files are loaded directly for rendering.");
        Console.WriteLine("Live status HUD is rendered at top-left; processing progress overlay at bottom-right.");
        Console.WriteLine("Reference geometry: infinite-looking +X/+Y/+Z axes and infinite XZ grid.");
        Console.WriteLine("Logic origin (0,0,0) is anchored at world coordinate (-0.5,-0.5,-0.5).");
        Console.WriteLine("Render modes: F1=PointCloud, F2=Volume Composite, F3=Volume MIP, F4=Volume Hybrid");
        Console.WriteLine("Volume controls: C=Clip On/Off, [ / ]=Clip Offset, , / .=Density Gain, - / ==Opacity Gain");
        Console.WriteLine("Composite/Hybrid mode: drag Density/Opacity sliders in the HUD (top-left).");
        Console.WriteLine("MIP mode: drag Slice slider in the HUD (top-left).");
        Console.WriteLine("Mouse: Left Drag=Rotate, Wheel=Zoom");
        Console.WriteLine("Keys: Left/A=Previous preview, Right/D=Next preview, R=Reset camera, O=Open output folder, H=Help, Esc=Quit");
    }
}
