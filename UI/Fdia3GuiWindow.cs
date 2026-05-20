using Fdia2.Core;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using SkiaSharp;
using System.Reflection;

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

    enum HudSliderTarget
    {
        None,
        Density,
        Opacity,
        Slice,
    }

    sealed class ProcessingOverlayState
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
    readonly ProcessingOverlayState processingOverlay = new();
    DateTimeOffset processingOverlayStartedUtc;
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
    int referenceGridShaderProgram;
    int referenceGridVao;
    int referenceGridVbo;
    int referenceGridVertexCount;

    int volumeShaderProgram;
    int volumeVao;
    int volumeVbo;
    int volumeTextureId;
    int transferFunctionTextureId;
    bool hasVolumeTexture;
    float volumeValueScale = 1f;
    float autoOpacityMultiplier = 1f;
    bool useWhiteBackground = false;

    int hudShaderProgram;
    int hudVao;
    int hudVbo;
    int hudTextureId;
    int hudTextureWidth;
    int hudTextureHeight;
    string hudText = string.Empty;
    bool hudTextDirty = true;

    RenderMode renderMode = RenderMode.VolumeMip;
    bool clippingEnabled = false;
    float clipOffset = 0f;
    float densityGain = DensityGainMax;
    float opacityGain = OpacityGainMax;
    string status = "Ready";
    string hudFileName = "None";
    HudSliderTarget activeHudSlider = HudSliderTarget.None;
    Vector4 densitySliderRectPx;
    Vector4 opacitySliderRectPx;
    Vector4 sliceSliderRectPx;

    bool isRotating;
    Vector2 lastMousePosition;
    float yaw = MathHelper.DegreesToRadians(45f);
    float pitch = MathHelper.DegreesToRadians(25f);
    float distance = 2.4f;

    const float ClipOffsetMin = -0.5f;
    const float ClipOffsetMax = 0.5f;
    const float ClipOffsetStep = 0.02f;
    const float DensityGainMin = 0.1f;
    const float DensityGainMax = 6.0f;
    const float OpacityGainMin = 0.05f;
    const float OpacityGainMax = 8.0f;
    const float RayStepSize = 0.0065f;
    const float EarlyTerminateAlpha = 0.98f;
    const string BaseWindowTitle = "fdia3";
    const float HudFontSizePx = 14f;
    const float HudLineSpacingPx = 3f;
    const float HudPaddingPx = 10f;
    const float HudCornerRadiusPx = 8f;
    const int HudMarginPx = 12;
    const float HudSliderTrackHeightPx = 6f;
    const float HudSliderHitHeightPx = 20f;
    const float HudSliderSectionGapPx = 8f;
    const float HudSliderSectionLabelGapPx = 4f;
    const float HudSliderThumbRadiusPx = 7f;
    static readonly Vector3 ReferenceOrigin = new(-0.5f, -0.5f, -0.5f);
    const float ReferenceAxisLengthMin = 4f;
    const float ReferenceAxisLengthFactor = 6f;
    const float ReferenceGridExtentMin = 10f;
    const float ReferenceGridExtentFactor = 10f;
    const float ReferenceGridMinorCell = 0.05f;
    const float ReferenceGridMajorCell = 0.25f;

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

        InitializeReferenceRenderer();
        InitializePointCloudRenderer();
        InitializeVolumeRenderer();
        InitializeHudRenderer();
        PrintHelp();

        if (pendingFiles.Count > 0)
            StartProcessingPendingFiles();
        else
            status = "Drag files into this window, then press F5";

        UpdateWindowTitle(true);
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
        UpdateWindowTitle(true);
    }

    protected override void OnUpdateFrame(FrameEventArgs args)
    {
        base.OnUpdateFrame(args);
        UpdateProcessingStatus();
        UpdateWindowTitle(false);
    }

    protected override void OnRenderFrame(FrameEventArgs args)
    {
        base.OnRenderFrame(args);
        if (useWhiteBackground)
            GL.ClearColor(new Color4(0.95f, 0.95f, 0.95f, 1f));
        else
            GL.ClearColor(new Color4(0.05f, 0.05f, 0.08f, 1f));
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        RenderReferenceGeometry();
        if (renderMode == RenderMode.PointCloud)
            RenderPointCloud();
        else
            RenderVolume();
        RenderHudOverlay();

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

        UpdateWindowTitle();
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButton.Left)
            return;

        if (TryBeginHudSliderDrag(MousePosition))
            return;

        isRotating = true;
        lastMousePosition = MousePosition;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButton.Left)
            return;

        if (activeHudSlider != HudSliderTarget.None)
        {
            activeHudSlider = HudSliderTarget.None;
            return;
        }

        if (e.Button == MouseButton.Left)
            isRotating = false;
    }

    protected override void OnMouseMove(MouseMoveEventArgs e)
    {
        base.OnMouseMove(e);
        if (activeHudSlider != HudSliderTarget.None)
        {
            UpdateHudSliderValueFromMouse(e.Position.X);
            return;
        }

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

    void SetRenderMode(RenderMode mode)
    {
        renderMode = mode;
        activeHudSlider = HudSliderTarget.None;

        status = mode switch
        {
            RenderMode.PointCloud => "Switched to point cloud",
            RenderMode.VolumeComposite => "Switched to volume composite",
            RenderMode.VolumeMip => "Switched to volume MIP",
            RenderMode.VolumeHybrid => "Switched to volume hybrid",
            _ => status,
        };
    }

    bool TryBeginHudSliderDrag(Vector2 mousePosition)
    {
        if (renderMode != RenderMode.VolumeComposite && renderMode != RenderMode.VolumeMip && renderMode != RenderMode.VolumeHybrid)
            return false;

        if ((renderMode == RenderMode.VolumeComposite || renderMode == RenderMode.VolumeHybrid) && ContainsRect(densitySliderRectPx, mousePosition))
        {
            activeHudSlider = HudSliderTarget.Density;
            isRotating = false;
            UpdateHudSliderValueFromMouse(mousePosition.X);
            return true;
        }

        if ((renderMode == RenderMode.VolumeComposite || renderMode == RenderMode.VolumeHybrid) && ContainsRect(opacitySliderRectPx, mousePosition))
        {
            activeHudSlider = HudSliderTarget.Opacity;
            isRotating = false;
            UpdateHudSliderValueFromMouse(mousePosition.X);
            return true;
        }

        if (renderMode == RenderMode.VolumeMip && ContainsRect(sliceSliderRectPx, mousePosition))
        {
            activeHudSlider = HudSliderTarget.Slice;
            isRotating = false;
            UpdateHudSliderValueFromMouse(mousePosition.X);
            return true;
        }

        return false;
    }

    void UpdateHudSliderValueFromMouse(float mouseX)
    {
        var (rect, min, max, label) = activeHudSlider switch
        {
            HudSliderTarget.Density => (densitySliderRectPx, DensityGainMin, DensityGainMax, "Density"),
            HudSliderTarget.Opacity => (opacitySliderRectPx, OpacityGainMin, OpacityGainMax, "Opacity"),
            HudSliderTarget.Slice => (sliceSliderRectPx, ClipOffsetMin, ClipOffsetMax, "Slice"),
            _ => (Vector4.Zero, 0f, 0f, string.Empty),
        };

        if (activeHudSlider == HudSliderTarget.None || rect.Z <= rect.X)
            return;

        var t = Math.Clamp((mouseX - rect.X) / (rect.Z - rect.X), 0f, 1f);
        var value = min + t * (max - min);
        switch (activeHudSlider)
        {
            case HudSliderTarget.Density:
                densityGain = value;
                break;
            case HudSliderTarget.Opacity:
                opacityGain = value;
                break;
            case HudSliderTarget.Slice:
                clipOffset = value;
                break;
        }

        status = $"{label} slider: {value:F2}";
        UpdateWindowTitle();
    }

    static bool ContainsRect(Vector4 rect, Vector2 point) =>
      point.X >= rect.X && point.X <= rect.Z && point.Y >= rect.Y && point.Y <= rect.W;

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
            UpdateWindowTitle();
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
        UpdateWindowTitle();
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
            processingOverlay.IsVisible = true;
            processingOverlay.IsActive = true;
            processingOverlay.TotalFiles = totalFiles;
            processingOverlay.PendingFiles = totalFiles;
            processingOverlay.RunningFiles = 0;
            processingOverlay.SucceededFiles = 0;
            processingOverlay.FailedFiles = 0;
            processingOverlay.MaxParallelPipelines = Math.Min(VolumeProcessor.MaxParallelPipelines, totalFiles);
            processingOverlay.Elapsed = TimeSpan.Zero;
            processingOverlay.Summary = string.Empty;
            processingOverlay.ActiveFiles.Clear();
        }
    }

    void OnProcessingProgress(VolumeProcessor.ProcessingProgressSnapshot snapshot)
    {
        lock (processingOverlaySync)
        {
            processingOverlay.IsVisible = true;
            processingOverlay.IsActive = snapshot.PendingFiles > 0 || snapshot.RunningFiles > 0;
            processingOverlay.TotalFiles = snapshot.TotalFiles;
            processingOverlay.PendingFiles = snapshot.PendingFiles;
            processingOverlay.RunningFiles = snapshot.RunningFiles;
            processingOverlay.SucceededFiles = snapshot.SucceededFiles;
            processingOverlay.FailedFiles = snapshot.FailedFiles;
            processingOverlay.MaxParallelPipelines = snapshot.MaxParallelPipelines;
            processingOverlay.Elapsed = snapshot.Elapsed;
            processingOverlay.ActiveFiles.Clear();
            processingOverlay.ActiveFiles.AddRange(snapshot.ActiveFiles);
            if (!processingOverlay.IsActive && processingOverlay.TotalFiles > 0)
            {
                processingOverlay.Summary = BuildProcessingSummary(
                  processingOverlay.SucceededFiles,
                  processingOverlay.FailedFiles,
                  processingOverlay.Elapsed);
            }
        }
    }

    void HideProcessingOverlay()
    {
        lock (processingOverlaySync)
        {
            processingOverlay.IsVisible = false;
            processingOverlay.IsActive = false;
            processingOverlay.TotalFiles = 0;
            processingOverlay.PendingFiles = 0;
            processingOverlay.RunningFiles = 0;
            processingOverlay.SucceededFiles = 0;
            processingOverlay.FailedFiles = 0;
            processingOverlay.MaxParallelPipelines = 0;
            processingOverlay.Elapsed = TimeSpan.Zero;
            processingOverlay.Summary = string.Empty;
            processingOverlay.ActiveFiles.Clear();
        }
    }

    void FinalizeProcessingOverlay(int succeededFiles, bool processingFailed)
    {
        lock (processingOverlaySync)
        {
            if (!processingOverlay.IsVisible)
                return;

            processingOverlay.IsActive = false;
            processingOverlay.PendingFiles = 0;
            processingOverlay.RunningFiles = 0;
            processingOverlay.SucceededFiles = succeededFiles;
            if (processingFailed && processingOverlay.TotalFiles > succeededFiles && processingOverlay.FailedFiles == 0)
                processingOverlay.FailedFiles = processingOverlay.TotalFiles - succeededFiles;
            if (processingOverlay.Elapsed <= TimeSpan.Zero)
                processingOverlay.Elapsed = DateTimeOffset.UtcNow - processingOverlayStartedUtc;
            processingOverlay.ActiveFiles.Clear();
            processingOverlay.Summary = BuildProcessingSummary(
              processingOverlay.SucceededFiles,
              processingOverlay.FailedFiles,
              processingOverlay.Elapsed);
            if (processingFailed)
                processingOverlay.Summary += " (task failed)";
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

    void UpdateWindowTitle() => UpdateWindowTitle(false);

    void UpdateWindowTitle(bool force)
    {
        var nextHudText = BuildHudText();
        if (force || !string.Equals(hudText, nextHudText, StringComparison.Ordinal))
        {
            hudText = nextHudText;
            hudTextDirty = true;
        }

        if (force || !string.Equals(Title, BaseWindowTitle, StringComparison.Ordinal))
        {
            Title = BaseWindowTitle;
        }
    }

    string BuildHudText()
    {
        var isBusy = processingTask is { IsCompleted: false } ? "Yes" : "No";
        var previewStatus = previewFiles.Count > 0 && previewIndex >= 0
          ? $"{previewIndex + 1}/{previewFiles.Count}"
          : "None";
        var clipState = clippingEnabled ? $"On@{clipOffset:F2}" : "Off";

        var sb = new System.Text.StringBuilder(2048);
        sb.Append("Mode: ").Append(renderMode).AppendLine();
        sb.Append("Dens: ").Append(densityGain.ToString("F2")).Append(" | Opac: ").Append(opacityGain.ToString("F2")).Append(" (Auto x").Append(autoOpacityMultiplier.ToString("F1")).AppendLine(")");
        sb.Append("Clip: ").Append(clipState).AppendLine();
        sb.Append("Pending: ").Append(pendingFiles.Count).Append(" | Preview: ").Append(previewStatus).AppendLine();
        sb.Append("File: ").Append(hudFileName).AppendLine();
        sb.Append("Points: ").Append(pointCount.ToString("N0")).Append(" | Busy: ").Append(isBusy).AppendLine();
        sb.Append("Origin(0,0,0): (").Append(ReferenceOrigin.X.ToString("F1")).Append(',').Append(ReferenceOrigin.Y.ToString("F1")).Append(',').Append(ReferenceOrigin.Z.ToString("F1")).AppendLine(")");
        sb.Append("Status: ").Append(status).AppendLine();
        sb.AppendLine("Keys: F1-F4 Mode | C Clip | [ ] ClipOffset | B BG");
        sb.AppendLine("      , . Density | - = Opacity | <- -> Preview | R Reset");
        AppendProcessingOverlaySection(sb);

        if (renderMode == RenderMode.VolumeComposite || renderMode == RenderMode.VolumeHybrid)
            sb.Append("Composite sliders: drag Density/Opacity bars below");
        else if (renderMode == RenderMode.VolumeMip)
            sb.Append("MIP slider: drag Slice bar below");

        return sb.ToString();
    }

    void AppendProcessingOverlaySection(System.Text.StringBuilder sb)
    {
        bool isVisible;
        bool isActive;
        int totalFiles;
        int pendingFilesCount;
        int runningFilesCount;
        int succeededFiles;
        int failedFiles;
        int maxParallel;
        TimeSpan elapsed;
        string summary;
        VolumeProcessor.ProcessingFileProgress[] activeFiles;
        lock (processingOverlaySync)
        {
            isVisible = processingOverlay.IsVisible;
            isActive = processingOverlay.IsActive;
            totalFiles = processingOverlay.TotalFiles;
            pendingFilesCount = processingOverlay.PendingFiles;
            runningFilesCount = processingOverlay.RunningFiles;
            succeededFiles = processingOverlay.SucceededFiles;
            failedFiles = processingOverlay.FailedFiles;
            maxParallel = processingOverlay.MaxParallelPipelines;
            elapsed = processingOverlay.Elapsed;
            summary = processingOverlay.Summary;
            activeFiles = [.. processingOverlay.ActiveFiles];
        }

        if (!isVisible)
            return;

        sb.AppendLine("Processing Overlay:");
        if (isActive)
        {
            sb.Append("Queue: ").Append(pendingFilesCount).Append('/').Append(totalFiles).Append(" pending");
            sb.Append(" | Running: ").Append(runningFilesCount);
            sb.Append(" | MaxParallel: ").Append(maxParallel).AppendLine();
            sb.Append("Result: OK ").Append(succeededFiles);
            sb.Append(" | Fail ").Append(failedFiles);
            sb.Append(" | Elapsed ").Append(FormatElapsed(elapsed)).AppendLine();
            foreach (var file in activeFiles.OrderBy(f => f.FileName, StringComparer.OrdinalIgnoreCase))
                sb.Append("  ").Append(file.FileName).Append(": ").Append(file.ProgressPercent).AppendLine("%");
            if (activeFiles.Length == 0)
                sb.AppendLine("  Active: waiting for workers...");
        }
        else if (!string.IsNullOrWhiteSpace(summary))
        {
            sb.AppendLine(summary);
        }
    }

    void ResetCamera()
    {
        yaw = MathHelper.DegreesToRadians(45f);
        pitch = MathHelper.DegreesToRadians(25f);
        distance = 2.4f;
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

        UploadVolumeTexture(volumeData.Voxels);
        var (scale, autoOpacity) = ComputeVolumeValueScale(volumeData.Voxels);
        volumeValueScale = scale;
        autoOpacityMultiplier = autoOpacity;
        var vertices = BuildPointCloudVertices(volumeData, out var loadedPointCount);
        UploadPointCloud(vertices, loadedPointCount);
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

    static float[] BuildPointCloudVertices(VolumeProcessor.VolumeData volumeData, out int loadedPointCount)
    {
        loadedPointCount = checked((int)volumeData.NonZeroVoxelCount);
        if (loadedPointCount == 0)
            return Array.Empty<float>();

        // We need the scale to normalize intensity here. 
        // Since this is static, we calculate it locally or pass it. 
        // Calculating locally for simplicity as it's just a maxValue check.
        uint maxValue = 0;
        foreach (var v in volumeData.Voxels) if (v > maxValue) maxValue = v;
        float scale = maxValue == 0 ? 1f : 1f / maxValue;

        var vertices = new float[loadedPointCount * 4];
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
                    vertices[write++] = value * scale;
                }
            }
        }

        if (write == vertices.Length)
            return vertices;

        var resized = new float[write];
        Array.Copy(vertices, resized, write);
        loadedPointCount = write / 4;
        return resized;
    }

    void ClearPreviewData()
    {
        UploadPointCloud(Array.Empty<float>(), 0);
        hasVolumeTexture = false;
        volumeValueScale = 1f;
        hudFileName = "None";
    }

    static (float Scale, float AutoOpacity) ComputeVolumeValueScale(uint[] voxels)
    {
        uint maxValue = 0;
        double sumValue = 0;
        int nonZeroCount = 0;

        foreach (var value in voxels)
        {
            if (value > maxValue)
                maxValue = value;
            if (value > 0)
            {
                sumValue += value;
                nonZeroCount++;
            }
        }

        float scale = maxValue == 0 ? 1f : 1f / maxValue;
        float meanDensity = nonZeroCount == 0 ? 0f : (float)(sumValue / nonZeroCount) * scale;

        // Calculate auto-multiplier: target a mean density visibility of ~0.25
        float autoOpacity = Math.Clamp(0.25f / MathF.Max(meanDensity, 0.005f), 1f, 15f);

        return (scale, autoOpacity);
    }

    void UploadPointCloud(float[] vertices, int loadedPointCount)
    {
        pointCount = loadedPointCount;

        GL.BindVertexArray(pointCloudVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, pointCloudVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.StaticDraw);
        GL.BindVertexArray(0);
    }

    void UploadVolumeTexture(uint[] voxels)
    {
        if (voxels.Length != VolumeProcessor.VoxelCount)
            throw new ArgumentException($"Volume voxel length must be {VolumeProcessor.VoxelCount}.", nameof(voxels));

        if (volumeTextureId == 0)
            volumeTextureId = GL.GenTexture();

        // Convert uint to float for R32F texture to support hardware filtering
        var floatVoxels = new float[voxels.Length];
        for (int i = 0; i < voxels.Length; i++)
            floatVoxels[i] = voxels[i];

        GL.BindTexture(TextureTarget.Texture3D, volumeTextureId);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
        GL.TexImage3D(
          TextureTarget.Texture3D,
          0,
          PixelInternalFormat.R32f,
          VolumeProcessor.AxisLength,
          VolumeProcessor.AxisLength,
          VolumeProcessor.AxisLength,
          0,
          PixelFormat.Red,
          PixelType.Float,
          floatVoxels);
        GL.BindTexture(TextureTarget.Texture3D, 0);
        hasVolumeTexture = true;
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
        return CreateShaderProgramFromResources("point_cloud.vert", "point_cloud.frag", "point cloud");
    }

    void InitializeReferenceRenderer()
    {
        referenceShaderProgram = CreateReferenceShaderProgram();
        referenceVao = GL.GenVertexArray();
        referenceVbo = GL.GenBuffer();
        referenceVertexCount = 6;

        GL.BindVertexArray(referenceVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, referenceVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, referenceVertexCount * 6 * sizeof(float), IntPtr.Zero, BufferUsageHint.DynamicDraw);

        var stride = 6 * sizeof(float);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        GL.BindVertexArray(0);

        referenceGridShaderProgram = CreateReferenceGridShaderProgram();
        referenceGridVao = GL.GenVertexArray();
        referenceGridVbo = GL.GenBuffer();
        referenceGridVertexCount = 6;

        GL.BindVertexArray(referenceGridVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, referenceGridVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, referenceGridVertexCount * 3 * sizeof(float), IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
        GL.BindVertexArray(0);
    }

    int CreateReferenceShaderProgram()
    {
        return CreateShaderProgramFromResources("reference.vert", "reference.frag", "reference");
    }

    int CreateReferenceGridShaderProgram()
    {
        return CreateShaderProgramFromResources("reference_grid.vert", "reference_grid.frag", "reference grid");
    }

    void UpdateReferenceGeometry(Vector3 eye)
    {
        var distanceToOrigin = (eye - ReferenceOrigin).Length;
        var axisLength = MathF.Max(ReferenceAxisLengthMin, distanceToOrigin * ReferenceAxisLengthFactor);
        var axisEndX = ReferenceOrigin + new Vector3(axisLength, 0f, 0f);
        var axisEndY = ReferenceOrigin + new Vector3(0f, axisLength, 0f);
        var axisEndZ = ReferenceOrigin + new Vector3(0f, 0f, axisLength);

        float[] axisVertices =
        [
          ReferenceOrigin.X, ReferenceOrigin.Y, ReferenceOrigin.Z, 0.95f, 0.25f, 0.25f,
      axisEndX.X, axisEndX.Y, axisEndX.Z, 0.95f, 0.25f, 0.25f,
      ReferenceOrigin.X, ReferenceOrigin.Y, ReferenceOrigin.Z, 0.30f, 0.95f, 0.30f,
      axisEndY.X, axisEndY.Y, axisEndY.Z, 0.30f, 0.95f, 0.30f,
      ReferenceOrigin.X, ReferenceOrigin.Y, ReferenceOrigin.Z, 0.30f, 0.55f, 0.98f,
      axisEndZ.X, axisEndZ.Y, axisEndZ.Z, 0.30f, 0.55f, 0.98f,
    ];

        GL.BindBuffer(BufferTarget.ArrayBuffer, referenceVbo);
        GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, axisVertices.Length * sizeof(float), axisVertices);

        var gridExtent = MathF.Max(ReferenceGridExtentMin, distanceToOrigin * ReferenceGridExtentFactor);
        var centerX = MathF.Floor((eye.X - ReferenceOrigin.X) / ReferenceGridMajorCell) * ReferenceGridMajorCell + ReferenceOrigin.X;
        var centerZ = MathF.Floor((eye.Z - ReferenceOrigin.Z) / ReferenceGridMajorCell) * ReferenceGridMajorCell + ReferenceOrigin.Z;
        var y = ReferenceOrigin.Y;
        var x0 = centerX - gridExtent;
        var x1 = centerX + gridExtent;
        var z0 = centerZ - gridExtent;
        var z1 = centerZ + gridExtent;

        float[] gridVertices =
        [
          x0, y, z0,
      x1, y, z0,
      x1, y, z1,
      x0, y, z0,
      x1, y, z1,
      x0, y, z1,
    ];

        GL.BindBuffer(BufferTarget.ArrayBuffer, referenceGridVbo);
        GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, gridVertices.Length * sizeof(float), gridVertices);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
    }

    void InitializeVolumeRenderer()
    {
        volumeShaderProgram = CreateVolumeShaderProgram();
        volumeVao = GL.GenVertexArray();
        volumeVbo = GL.GenBuffer();

        float[] quadVertices =
        [
          -1f, -1f, 0f, 0f,
      1f, -1f, 1f, 0f,
      1f, 1f, 1f, 1f,
      -1f, -1f, 0f, 0f,
      1f, 1f, 1f, 1f,
      -1f, 1f, 0f, 1f,
    ];

        GL.BindVertexArray(volumeVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, volumeVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, quadVertices.Length * sizeof(float), quadVertices, BufferUsageHint.StaticDraw);
        var stride = 4 * sizeof(float);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 2 * sizeof(float));
        GL.BindVertexArray(0);

        transferFunctionTextureId = CreateTransferFunctionTexture();

        GL.UseProgram(volumeShaderProgram);
        GL.Uniform1(GL.GetUniformLocation(volumeShaderProgram, "uVolumeTex"), 0);
        GL.Uniform1(GL.GetUniformLocation(volumeShaderProgram, "uTransferTex"), 1);
        GL.UseProgram(0);
    }

    int CreateVolumeShaderProgram()
    {
        return CreateShaderProgramFromResources("volume.vert", "volume.frag", "volume");
    }

    static int CreateShaderProgramFromResources(string vertexShaderFileName, string fragmentShaderFileName, string label)
    {
        var vertexShaderSource = LoadShaderSourceFromResource(vertexShaderFileName);
        var fragmentShaderSource = LoadShaderSourceFromResource(fragmentShaderFileName);
        return CreateShaderProgram(vertexShaderSource, fragmentShaderSource, label);
    }

    static string LoadShaderSourceFromResource(string shaderFileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var normalizedName = shaderFileName.Replace('\\', '.').Replace('/', '.');
        var suffix = ".Shaders." + normalizedName;
        var resourceName = assembly
          .GetManifestResourceNames()
          .FirstOrDefault(name => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

        if (resourceName == null)
            throw new InvalidOperationException($"Shader resource not found for '{shaderFileName}'. Expected suffix '{suffix}'.");

        using var stream = assembly.GetManifestResourceStream(resourceName)
          ?? throw new InvalidOperationException($"Failed to open shader resource stream '{resourceName}'.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    static int CreateShaderProgram(string vertexShaderSource, string fragmentShaderSource, string label)
    {
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
            throw new InvalidOperationException($"Failed to link {label} shader program: {linkLog}");
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

    static int CreateTransferFunctionTexture()
    {
        var data = BuildTransferFunctionBytes();
        var textureId = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture1D, textureId);
        GL.TexParameter(TextureTarget.Texture1D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture1D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture1D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        GL.TexImage1D(TextureTarget.Texture1D, 0, PixelInternalFormat.Rgba8, 256, 0, PixelFormat.Rgba, PixelType.UnsignedByte, data);
        GL.BindTexture(TextureTarget.Texture1D, 0);
        return textureId;
    }

    static byte[] BuildTransferFunctionBytes()
    {
        var data = new byte[256 * 4];
        for (int i = 0; i < 256; i++)
        {
            var t = i / 255f;
            var color = EvaluateTransferColor(t);
            var alpha = t; // Linear density for shader physical accumulation
            data[i * 4] = (byte)Math.Clamp((int)MathF.Round(color.X * 255f), 0, 255);
            data[i * 4 + 1] = (byte)Math.Clamp((int)MathF.Round(color.Y * 255f), 0, 255);
            data[i * 4 + 2] = (byte)Math.Clamp((int)MathF.Round(color.Z * 255f), 0, 255);
            data[i * 4 + 3] = (byte)Math.Clamp((int)MathF.Round(alpha * 255f), 0, 255);
        }

        return data;
    }

    static Vector3 EvaluateTransferColor(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        var a = new Vector3(0.05f, 0.12f, 0.45f);
        var b = new Vector3(0.00f, 0.90f, 1.00f);
        var c = new Vector3(1.00f, 0.95f, 0.20f);
        var d = new Vector3(1.00f, 0.25f, 0.00f);
        if (t < 0.33f)
            return Vector3.Lerp(a, b, t / 0.33f);
        if (t < 0.66f)
            return Vector3.Lerp(b, c, (t - 0.33f) / 0.33f);
        return Vector3.Lerp(c, d, (t - 0.66f) / 0.34f);
    }

    void InitializeHudRenderer()
    {
        hudShaderProgram = CreateHudShaderProgram();
        hudVao = GL.GenVertexArray();
        hudVbo = GL.GenBuffer();

        GL.BindVertexArray(hudVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, hudVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, 6 * 4 * sizeof(float), IntPtr.Zero, BufferUsageHint.DynamicDraw);
        var stride = 4 * sizeof(float);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 2 * sizeof(float));
        GL.BindVertexArray(0);

        hudTextureId = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, hudTextureId);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.BindTexture(TextureTarget.Texture2D, 0);

        GL.UseProgram(hudShaderProgram);
        GL.Uniform1(GL.GetUniformLocation(hudShaderProgram, "uHudTexture"), 0);
        GL.UseProgram(0);
    }

    int CreateHudShaderProgram()
    {
        return CreateShaderProgramFromResources("hud.vert", "hud.frag", "hud");
    }

    void UpdateHudTextureIfNeeded()
    {
        if (!hudTextDirty)
            return;

        hudTextDirty = false;
        densitySliderRectPx = Vector4.Zero;
        opacitySliderRectPx = Vector4.Zero;
        sliceSliderRectPx = Vector4.Zero;
        if (string.IsNullOrWhiteSpace(hudText))
        {
            hudTextureWidth = 0;
            hudTextureHeight = 0;
            return;
        }

        var lines = hudText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            hudTextureWidth = 0;
            hudTextureHeight = 0;
            return;
        }

        using var textPaint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true,
        };
        using var font = new SKFont(SKTypeface.Default, HudFontSizePx);
        var metrics = font.Metrics;
        var lineHeight = MathF.Ceiling(metrics.Descent - metrics.Ascent + HudLineSpacingPx);
        float maxLineWidth = 0f;
        foreach (var line in lines)
            maxLineWidth = Math.Max(maxLineWidth, font.MeasureText(line));

        // Cap HUD width to prevent excessive stretching with long paths/strings
        maxLineWidth = 320f;// Math.Min(maxLineWidth, 800f);

        var showCompositeSliders = renderMode == RenderMode.VolumeComposite || renderMode == RenderMode.VolumeHybrid;
        var showMipSliceSlider = renderMode == RenderMode.VolumeMip;
        var sliderCount = showCompositeSliders ? 2 : showMipSliceSlider ? 1 : 0;
        var sliderTrackWidth = Math.Max(190f, maxLineWidth);
        var sliderLabelHeight = MathF.Ceiling(metrics.Descent - metrics.Ascent);
        var sliderRowHeight = sliderLabelHeight + HudSliderSectionLabelGapPx + HudSliderHitHeightPx;
        var sliderSectionHeight = sliderCount > 0
          ? HudSliderSectionGapPx + sliderCount * sliderRowHeight + Math.Max(0, sliderCount - 1) * HudSliderSectionGapPx
          : 0f;

        var width = Math.Max(1, (int)MathF.Ceiling(Math.Max(maxLineWidth, sliderTrackWidth) + HudPaddingPx * 2f));
        var height = Math.Max(1, (int)MathF.Ceiling(lines.Length * lineHeight + HudPaddingPx * 2f + sliderSectionHeight));
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            using var bgPaint = new SKPaint
            {
                Color = new SKColor(0, 0, 0, 160),
                IsAntialias = true,
            };
            canvas.DrawRoundRect(new SKRect(0, 0, width, height), HudCornerRadiusPx, HudCornerRadiusPx, bgPaint);

            var baseline = HudPaddingPx - metrics.Ascent;
            for (int i = 0; i < lines.Length; i++)
            {
                canvas.DrawText(lines[i], HudPaddingPx, baseline + i * lineHeight, SKTextAlign.Left, font, textPaint);
            }

            if (sliderCount > 0)
            {
                using var trackPaint = new SKPaint
                {
                    IsAntialias = true,
                    Color = new SKColor(255, 255, 255, 70),
                };
                using var fillPaint = new SKPaint
                {
                    IsAntialias = true,
                    Color = new SKColor(85, 190, 255, 220),
                };
                using var thumbPaint = new SKPaint
                {
                    IsAntialias = true,
                    Color = new SKColor(240, 248, 255, 240),
                };

                var sliderLeft = HudPaddingPx;
                var sliderTop = HudPaddingPx + lines.Length * lineHeight + HudSliderSectionGapPx;
                var sliderWidth = width - HudPaddingPx * 2f;
                var nextSliderTop = sliderTop;
                if (showCompositeSliders)
                {
                    var densityRect = DrawHudSlider(canvas, font, textPaint, trackPaint, fillPaint, thumbPaint,
                                                    "Density", densityGain, DensityGainMin, DensityGainMax,
                                                    sliderLeft, nextSliderTop, sliderWidth, sliderLabelHeight);
                    nextSliderTop += sliderRowHeight + HudSliderSectionGapPx;
                    var opacityRect = DrawHudSlider(canvas, font, textPaint, trackPaint, fillPaint, thumbPaint,
                                                    "Opacity", opacityGain, OpacityGainMin, OpacityGainMax,
                                                    sliderLeft, nextSliderTop, sliderWidth, sliderLabelHeight);

                    densitySliderRectPx = new Vector4(
                      HudMarginPx + densityRect.Left,
                      HudMarginPx + densityRect.Top,
                      HudMarginPx + densityRect.Right,
                      HudMarginPx + densityRect.Bottom);
                    opacitySliderRectPx = new Vector4(
                      HudMarginPx + opacityRect.Left,
                      HudMarginPx + opacityRect.Top,
                      HudMarginPx + opacityRect.Right,
                      HudMarginPx + opacityRect.Bottom);
                }
                else if (showMipSliceSlider)
                {
                    var sliceRect = DrawHudSlider(canvas, font, textPaint, trackPaint, fillPaint, thumbPaint,
                                                  "Slice", clipOffset, ClipOffsetMin, ClipOffsetMax,
                                                  sliderLeft, nextSliderTop, sliderWidth, sliderLabelHeight);
                    sliceSliderRectPx = new Vector4(
                      HudMarginPx + sliceRect.Left,
                      HudMarginPx + sliceRect.Top,
                      HudMarginPx + sliceRect.Right,
                      HudMarginPx + sliceRect.Bottom);
                }
            }
        }

        var pixelData = bitmap.GetPixelSpan().ToArray();
        GL.BindTexture(TextureTarget.Texture2D, hudTextureId);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);

        if (width == hudTextureWidth && height == hudTextureHeight)
        {
            GL.TexSubImage2D(
              TextureTarget.Texture2D,
              0,
              0,
              0,
              width,
              height,
              PixelFormat.Rgba,
              PixelType.UnsignedByte,
              pixelData);
        }
        else
        {
            GL.TexImage2D(
              TextureTarget.Texture2D,
              0,
              PixelInternalFormat.Rgba,
              width,
              height,
              0,
              PixelFormat.Rgba,
              PixelType.UnsignedByte,
              pixelData);
            hudTextureWidth = width;
            hudTextureHeight = height;
        }
        GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    static SKRect DrawHudSlider(SKCanvas canvas,
                                SKFont font,
                                SKPaint textPaint,
                                SKPaint trackPaint,
                                SKPaint fillPaint,
                                SKPaint thumbPaint,
                                 string label,
                                 float value,
                                 float min,
                                 float max,
                                 float left,
                                 float top,
                                 float width,
                                 float labelHeight)
    {
        var normalized = max <= min ? 0f : Math.Clamp((value - min) / (max - min), 0f, 1f);
        var labelBaseline = top - font.Metrics.Ascent;
        canvas.DrawText($"{label}: {value:F2}", left, labelBaseline, SKTextAlign.Left, font, textPaint);

        var hitTop = top + labelHeight + HudSliderSectionLabelGapPx;
        var trackTop = hitTop + (HudSliderHitHeightPx - HudSliderTrackHeightPx) * 0.5f;
        var trackRect = new SKRect(left, trackTop, left + width, trackTop + HudSliderTrackHeightPx);
        canvas.DrawRoundRect(trackRect, HudSliderTrackHeightPx * 0.5f, HudSliderTrackHeightPx * 0.5f, trackPaint);

        var fillRect = new SKRect(trackRect.Left, trackRect.Top, trackRect.Left + trackRect.Width * normalized, trackRect.Bottom);
        canvas.DrawRoundRect(fillRect, HudSliderTrackHeightPx * 0.5f, HudSliderTrackHeightPx * 0.5f, fillPaint);

        var thumbX = trackRect.Left + trackRect.Width * normalized;
        var thumbY = (trackRect.Top + trackRect.Bottom) * 0.5f;
        canvas.DrawCircle(thumbX, thumbY, HudSliderThumbRadiusPx, thumbPaint);

        return new SKRect(left, hitTop, left + width, hitTop + HudSliderHitHeightPx);
    }

    void GetCameraPose(out Vector3 eye, out Vector3 forward, out Vector3 right, out Vector3 up)
    {
        eye = new Vector3(
          distance * MathF.Cos(pitch) * MathF.Cos(yaw),
          distance * MathF.Sin(pitch),
          distance * MathF.Cos(pitch) * MathF.Sin(yaw));

        forward = Vector3.Normalize(-eye);
        right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        up = Vector3.Normalize(Vector3.Cross(right, forward));
    }

    void GetCameraMatrices(out Matrix4 view, out Matrix4 projection)
    {
        var aspect = ClientSize.X / (float)ClientSize.Y;
        projection = Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(45f), aspect, 0.01f, 100f);
        GetCameraPose(out var eye, out _, out _, out _);
        view = Matrix4.LookAt(eye, Vector3.Zero, Vector3.UnitY);
    }

    void RenderReferenceGeometry()
    {
        if (referenceVertexCount <= 0 || referenceVao == 0 || referenceShaderProgram == 0)
            return;

        if (referenceGridVertexCount <= 0 || referenceGridVao == 0 || referenceGridShaderProgram == 0)
            return;

        if (ClientSize.X <= 0 || ClientSize.Y <= 0)
            return;

        GetCameraPose(out var eye, out _, out _, out _);
        UpdateReferenceGeometry(eye);
        GetCameraMatrices(out var view, out var projection);

        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.UseProgram(referenceGridShaderProgram);
        GL.UniformMatrix4(GL.GetUniformLocation(referenceGridShaderProgram, "uView"), false, ref view);
        GL.UniformMatrix4(GL.GetUniformLocation(referenceGridShaderProgram, "uProjection"), false, ref projection);
        GL.Uniform3(GL.GetUniformLocation(referenceGridShaderProgram, "uReferenceOrigin"), ReferenceOrigin);
        GL.Uniform3(GL.GetUniformLocation(referenceGridShaderProgram, "uCameraPosition"), eye);
        GL.Uniform1(GL.GetUniformLocation(referenceGridShaderProgram, "uMinorCell"), ReferenceGridMinorCell);
        GL.Uniform1(GL.GetUniformLocation(referenceGridShaderProgram, "uMajorCell"), ReferenceGridMajorCell);
        GL.Uniform1(GL.GetUniformLocation(referenceGridShaderProgram, "uFadeDistance"), MathF.Max(ReferenceGridExtentMin, (eye - ReferenceOrigin).Length * 1.2f));
        GL.Uniform3(GL.GetUniformLocation(referenceGridShaderProgram, "uMinorColor"), new Vector3(0.15f, 0.15f, 0.17f));
        GL.Uniform3(GL.GetUniformLocation(referenceGridShaderProgram, "uMajorColor"), new Vector3(0.30f, 0.30f, 0.33f));
        GL.BindVertexArray(referenceGridVao);
        GL.DrawArrays(PrimitiveType.Triangles, 0, referenceGridVertexCount);
        GL.BindVertexArray(0);
        GL.UseProgram(0);
        GL.Disable(EnableCap.Blend);

        GL.LineWidth(2f);
        GL.UseProgram(referenceShaderProgram);
        GL.UniformMatrix4(GL.GetUniformLocation(referenceShaderProgram, "uView"), false, ref view);
        GL.UniformMatrix4(GL.GetUniformLocation(referenceShaderProgram, "uProjection"), false, ref projection);
        GL.BindVertexArray(referenceVao);
        GL.DrawArrays(PrimitiveType.Lines, 0, referenceVertexCount);
        GL.BindVertexArray(0);
        GL.UseProgram(0);
        GL.LineWidth(1f);
    }

    void RenderHudOverlay()
    {
        UpdateHudTextureIfNeeded();
        if (hudShaderProgram == 0 || hudVao == 0 || hudVbo == 0 || hudTextureId == 0)
            return;

        if (hudTextureWidth <= 0 || hudTextureHeight <= 0)
            return;

        if (ClientSize.X <= 0 || ClientSize.Y <= 0)
            return;

        var marginX = 2f * HudMarginPx / ClientSize.X;
        var marginY = 2f * HudMarginPx / ClientSize.Y;
        var widthNdc = 2f * hudTextureWidth / ClientSize.X;
        var heightNdc = 2f * hudTextureHeight / ClientSize.Y;

        var left = -1f + marginX;
        var right = left + widthNdc;
        var top = 1f - marginY;
        var bottom = top - heightNdc;
        float[] vertices =
        [
          left, top, 0f, 0f,
      right, top, 1f, 0f,
      right, bottom, 1f, 1f,
      left, top, 0f, 0f,
      right, bottom, 1f, 1f,
      left, bottom, 0f, 1f,
    ];

        GL.Disable(EnableCap.DepthTest);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        GL.UseProgram(hudShaderProgram);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, hudTextureId);
        GL.BindVertexArray(hudVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, hudVbo);
        GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, vertices.Length * sizeof(float), vertices);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
        GL.BindVertexArray(0);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        GL.UseProgram(0);

        GL.Disable(EnableCap.Blend);
        GL.Enable(EnableCap.DepthTest);
    }

    void RenderPointCloud()
    {
        if (pointCount <= 0 || pointCloudVao == 0 || pointCloudShaderProgram == 0)
            return;

        if (ClientSize.X <= 0 || ClientSize.Y <= 0)
            return;

        GetCameraMatrices(out var view, out var projection);
        GL.UseProgram(pointCloudShaderProgram);
        GL.UniformMatrix4(GL.GetUniformLocation(pointCloudShaderProgram, "uView"), false, ref view);
        GL.UniformMatrix4(GL.GetUniformLocation(pointCloudShaderProgram, "uProjection"), false, ref projection);
        GL.BindVertexArray(pointCloudVao);
        GL.DrawArrays(PrimitiveType.Points, 0, pointCount);
        GL.BindVertexArray(0);
        GL.UseProgram(0);
    }

    void RenderVolume()
    {
        if (!hasVolumeTexture || volumeShaderProgram == 0 || volumeVao == 0 || volumeTextureId == 0 || transferFunctionTextureId == 0)
            return;

        if (ClientSize.X <= 0 || ClientSize.Y <= 0)
            return;

        var fov = MathHelper.DegreesToRadians(45f);
        var aspect = ClientSize.X / (float)ClientSize.Y;
        var tanHalfFov = MathF.Tan(fov * 0.5f);
        GetCameraPose(out var eye, out var forward, out var right, out var up);

        GL.Disable(EnableCap.DepthTest);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        GL.UseProgram(volumeShaderProgram);
        GL.Uniform3(GL.GetUniformLocation(volumeShaderProgram, "uCameraPosition"), eye);
        GL.Uniform3(GL.GetUniformLocation(volumeShaderProgram, "uCameraForward"), forward);
        GL.Uniform3(GL.GetUniformLocation(volumeShaderProgram, "uCameraRight"), right);
        GL.Uniform3(GL.GetUniformLocation(volumeShaderProgram, "uCameraUp"), up);
        GL.Uniform1(GL.GetUniformLocation(volumeShaderProgram, "uAspect"), aspect);
        GL.Uniform1(GL.GetUniformLocation(volumeShaderProgram, "uTanHalfFov"), tanHalfFov);
        GL.Uniform1(GL.GetUniformLocation(volumeShaderProgram, "uStepSize"), RayStepSize);
        GL.Uniform1(GL.GetUniformLocation(volumeShaderProgram, "uDensityGain"), densityGain);
        GL.Uniform1(GL.GetUniformLocation(volumeShaderProgram, "uVolumeValueScale"), volumeValueScale);
        GL.Uniform1(GL.GetUniformLocation(volumeShaderProgram, "uOpacityGain"), opacityGain * autoOpacityMultiplier);
        GL.Uniform1(GL.GetUniformLocation(volumeShaderProgram, "uEarlyTerminateAlpha"), EarlyTerminateAlpha);

        int shaderMode = 0;
        if (renderMode == RenderMode.VolumeMip) shaderMode = 1;
        else if (renderMode == RenderMode.VolumeHybrid) shaderMode = 2;
        GL.Uniform1(GL.GetUniformLocation(volumeShaderProgram, "uRenderMode"), shaderMode);

        GL.Uniform1(GL.GetUniformLocation(volumeShaderProgram, "uClipEnabled"), clippingEnabled ? 1 : 0);
        GL.Uniform3(GL.GetUniformLocation(volumeShaderProgram, "uClipNormal"), Vector3.UnitY);
        GL.Uniform1(GL.GetUniformLocation(volumeShaderProgram, "uClipOffset"), clipOffset);

        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture3D, volumeTextureId);
        GL.ActiveTexture(TextureUnit.Texture1);
        GL.BindTexture(TextureTarget.Texture1D, transferFunctionTextureId);

        GL.BindVertexArray(volumeVao);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
        GL.BindVertexArray(0);
        GL.BindTexture(TextureTarget.Texture1D, 0);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture3D, 0);
        GL.UseProgram(0);

        GL.Disable(EnableCap.Blend);
        GL.Enable(EnableCap.DepthTest);
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

        if (referenceGridVbo != 0)
        {
            GL.DeleteBuffer(referenceGridVbo);
            referenceGridVbo = 0;
        }

        if (referenceGridVao != 0)
        {
            GL.DeleteVertexArray(referenceGridVao);
            referenceGridVao = 0;
        }

        if (referenceGridShaderProgram != 0)
        {
            GL.DeleteProgram(referenceGridShaderProgram);
            referenceGridShaderProgram = 0;
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

        if (volumeVbo != 0)
        {
            GL.DeleteBuffer(volumeVbo);
            volumeVbo = 0;
        }

        if (volumeVao != 0)
        {
            GL.DeleteVertexArray(volumeVao);
            volumeVao = 0;
        }

        if (volumeShaderProgram != 0)
        {
            GL.DeleteProgram(volumeShaderProgram);
            volumeShaderProgram = 0;
        }

        if (volumeTextureId != 0)
        {
            GL.DeleteTexture(volumeTextureId);
            volumeTextureId = 0;
        }

        if (transferFunctionTextureId != 0)
        {
            GL.DeleteTexture(transferFunctionTextureId);
            transferFunctionTextureId = 0;
        }

        if (hudVbo != 0)
        {
            GL.DeleteBuffer(hudVbo);
            hudVbo = 0;
        }

        if (hudVao != 0)
        {
            GL.DeleteVertexArray(hudVao);
            hudVao = 0;
        }

        if (hudShaderProgram != 0)
        {
            GL.DeleteProgram(hudShaderProgram);
            hudShaderProgram = 0;
        }

        if (hudTextureId != 0)
        {
            GL.DeleteTexture(hudTextureId);
            hudTextureId = 0;
        }

        base.OnUnload();
    }

    static void PrintHelp()
    {
        Console.WriteLine("fdia3 GUI mode started.");
        Console.WriteLine("Drag files to the window, then press F5 to process/load.");
        Console.WriteLine("Valid .fd3 files are loaded directly for rendering.");
        Console.WriteLine("Live status HUD is rendered at top-left (window title is simplified).");
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
