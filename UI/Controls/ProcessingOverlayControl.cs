using Fdia2.Core;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using SkiaSharp;

namespace Fdia2.UI.Controls;

public sealed class ProcessingOverlayControl : IDisposable
{
    const float FontSizePx = 13f;
    const float TitleFontSizePx = 14f;
    const float LineSpacingPx = 4f;
    const float PaddingPx = 12f;
    const float CornerRadiusPx = 8f;
    const int MarginPx = 12;
    const float BarHeightPx = 10f;
    const float BarTrackInsetPx = 0f;
    const float RowGapPx = 6f;
    const float SectionGapPx = 8f;
    const float OverlayWidthPx = 380f;
    const int MaxActiveRows = 8;

    public sealed class State
    {
        public bool IsVisible { get; init; }
        public bool IsActive { get; init; }
        public int TotalFiles { get; init; }
        public int PendingFiles { get; init; }
        public int RunningFiles { get; init; }
        public int SucceededFiles { get; init; }
        public int FailedFiles { get; init; }
        public int MaxParallelPipelines { get; init; }
        public TimeSpan Elapsed { get; init; }
        public string Summary { get; init; } = string.Empty;
        public IReadOnlyList<VolumeProcessor.ProcessingFileProgress> ActiveFiles { get; init; } =
          Array.Empty<VolumeProcessor.ProcessingFileProgress>();
    }

    int shaderProgram;
    int vao;
    int vbo;
    int textureId;
    int textureWidth;
    int textureHeight;

    State? lastState;
    string lastSignature = string.Empty;
    bool dirty = true;

    public void Initialize()
    {
        shaderProgram = ShaderUtils.CreateProgramFromResources("hud.vert", "hud.frag", "processing overlay");
        vao = GL.GenVertexArray();
        vbo = GL.GenBuffer();

        GL.BindVertexArray(vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, 6 * 4 * sizeof(float), IntPtr.Zero, BufferUsageHint.DynamicDraw);
        var stride = 4 * sizeof(float);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 2 * sizeof(float));
        GL.BindVertexArray(0);

        textureId = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, textureId);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.BindTexture(TextureTarget.Texture2D, 0);

        GL.UseProgram(shaderProgram);
        GL.Uniform1(GL.GetUniformLocation(shaderProgram, "uHudTexture"), 0);
        GL.UseProgram(0);
    }

    public void Update(State state)
    {
        var signature = BuildSignature(state);
        if (!string.Equals(signature, lastSignature, StringComparison.Ordinal))
        {
            lastSignature = signature;
            lastState = state;
            dirty = true;
        }
    }

    static string BuildSignature(State s)
    {
        if (!s.IsVisible) return "hidden";
        var sb = new System.Text.StringBuilder(256);
        sb.Append(s.IsActive).Append('|').Append(s.TotalFiles).Append('|').Append(s.PendingFiles).Append('|');
        sb.Append(s.RunningFiles).Append('|').Append(s.SucceededFiles).Append('|').Append(s.FailedFiles).Append('|');
        sb.Append(s.MaxParallelPipelines).Append('|').Append((int)s.Elapsed.TotalSeconds).Append('|').Append(s.Summary).Append('|');
        foreach (var f in s.ActiveFiles)
            sb.Append(f.FileName).Append(':').Append(f.ProgressPercent).Append(';');
        return sb.ToString();
    }

    public void Render(Vector2i clientSize)
    {
        UpdateTextureIfNeeded();
        if (shaderProgram == 0 || vao == 0 || vbo == 0 || textureId == 0)
            return;
        if (textureWidth <= 0 || textureHeight <= 0)
            return;
        if (clientSize.X <= 0 || clientSize.Y <= 0)
            return;
        if (lastState is not { IsVisible: true })
            return;

        // bottom-right corner
        var widthNdc = 2f * textureWidth / clientSize.X;
        var heightNdc = 2f * textureHeight / clientSize.Y;
        var marginX = 2f * MarginPx / clientSize.X;
        var marginY = 2f * MarginPx / clientSize.Y;

        var right = 1f - marginX;
        var left = right - widthNdc;
        var bottom = -1f + marginY;
        var top = bottom + heightNdc;

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

        GL.UseProgram(shaderProgram);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, textureId);
        GL.BindVertexArray(vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, vertices.Length * sizeof(float), vertices);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
        GL.BindVertexArray(0);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        GL.UseProgram(0);

        GL.Disable(EnableCap.Blend);
        GL.Enable(EnableCap.DepthTest);
    }

    void UpdateTextureIfNeeded()
    {
        if (!dirty) return;
        dirty = false;

        var state = lastState;
        if (state is null || !state.IsVisible)
        {
            textureWidth = 0;
            textureHeight = 0;
            return;
        }

        using var textPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var subPaint = new SKPaint { Color = new SKColor(210, 210, 220), IsAntialias = true };
        using var trackPaint = new SKPaint { Color = new SKColor(255, 255, 255, 60), IsAntialias = true };
        using var fillPaint = new SKPaint { Color = new SKColor(85, 190, 255, 230), IsAntialias = true };
        using var doneFillPaint = new SKPaint { Color = new SKColor(120, 215, 130, 230), IsAntialias = true };
        using var failFillPaint = new SKPaint { Color = new SKColor(230, 90, 90, 230), IsAntialias = true };

        using var titleFont = new SKFont(SKTypeface.Default, TitleFontSizePx);
        using var rowFont = new SKFont(SKTypeface.Default, FontSizePx);
        var titleMetrics = titleFont.Metrics;
        var rowMetrics = rowFont.Metrics;
        var titleLineH = MathF.Ceiling(titleMetrics.Descent - titleMetrics.Ascent + LineSpacingPx);
        var rowLineH = MathF.Ceiling(rowMetrics.Descent - rowMetrics.Ascent + LineSpacingPx);

        var width = (int)MathF.Ceiling(OverlayWidthPx);
        var innerWidth = width - PaddingPx * 2f;

        var totalDone = state.SucceededFiles + state.FailedFiles;
        var totalPercent = state.TotalFiles > 0 ? Math.Clamp(100 * totalDone / state.TotalFiles, 0, 100) : 0;

        var activeFiles = state.ActiveFiles
          .OrderBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
          .Take(MaxActiveRows)
          .ToArray();

        // Layout (heights):
        // title (titleLineH)
        // total bar row: label line (rowLineH) + bar (BarHeightPx) + small gap
        // section gap
        // per file: name line (rowLineH) + bar (BarHeightPx) + small gap
        // section gap
        // stats line (rowLineH)
        var totalRowH = rowLineH + BarHeightPx + RowGapPx;
        var perFileH = activeFiles.Length * (rowLineH + BarHeightPx + RowGapPx);
        var statsRows = state.IsActive ? 1 : (string.IsNullOrWhiteSpace(state.Summary) ? 0 : 1);

        var contentHeight = titleLineH
          + totalRowH
          + (perFileH > 0 ? SectionGapPx + perFileH : 0)
          + (statsRows > 0 ? SectionGapPx + statsRows * rowLineH : 0);
        if (!state.IsActive && activeFiles.Length == 0 && !string.IsNullOrWhiteSpace(state.Summary))
        {
            // ensure summary visible; contentHeight already covers
        }

        var height = (int)MathF.Ceiling(contentHeight + PaddingPx * 2f);

        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            using var bgPaint = new SKPaint
            {
                Color = new SKColor(0, 0, 0, 180),
                IsAntialias = true,
            };
            canvas.DrawRoundRect(new SKRect(0, 0, width, height), CornerRadiusPx, CornerRadiusPx, bgPaint);

            var x = PaddingPx;
            var y = PaddingPx;

            // Title
            var titleText = state.IsActive
              ? $"Processing {state.TotalFiles} file(s)"
              : (state.TotalFiles > 0 ? "Processing complete" : "Processing");
            canvas.DrawText(titleText, x, y - titleMetrics.Ascent, SKTextAlign.Left, titleFont, textPaint);
            y += titleLineH;

            // Total progress bar
            var totalLabel = $"Total {totalDone}/{state.TotalFiles}  ({totalPercent}%)";
            canvas.DrawText(totalLabel, x, y - rowMetrics.Ascent, SKTextAlign.Left, rowFont, textPaint);
            y += rowLineH;
            DrawBar(canvas, x, y, innerWidth, BarHeightPx, totalPercent / 100f,
              trackPaint, state.FailedFiles > 0 && totalDone == state.TotalFiles ? failFillPaint
                : (!state.IsActive ? doneFillPaint : fillPaint));
            y += BarHeightPx + RowGapPx;

            // Per-file rows
            if (activeFiles.Length > 0)
            {
                y += SectionGapPx;
                foreach (var f in activeFiles)
                {
                    var nameText = TruncateForWidth(rowFont, f.FileName, innerWidth - 48f);
                    canvas.DrawText(nameText, x, y - rowMetrics.Ascent, SKTextAlign.Left, rowFont, subPaint);
                    var pctText = $"{f.ProgressPercent}%";
                    var pctWidth = rowFont.MeasureText(pctText);
                    canvas.DrawText(pctText, x + innerWidth - pctWidth, y - rowMetrics.Ascent, SKTextAlign.Left, rowFont, subPaint);
                    y += rowLineH;
                    DrawBar(canvas, x, y, innerWidth, BarHeightPx, f.ProgressPercent / 100f, trackPaint, fillPaint);
                    y += BarHeightPx + RowGapPx;
                }
            }

            // Stats line
            if (statsRows > 0)
            {
                y += SectionGapPx;
                string statsText;
                if (state.IsActive)
                {
                    statsText = $"OK {state.SucceededFiles} | Fail {state.FailedFiles} | Pending {state.PendingFiles} | Run {state.RunningFiles} | Par {state.MaxParallelPipelines} | {FormatElapsed(state.Elapsed)}";
                }
                else
                {
                    statsText = state.Summary;
                }
                statsText = TruncateForWidth(rowFont, statsText, innerWidth);
                canvas.DrawText(statsText, x, y - rowMetrics.Ascent, SKTextAlign.Left, rowFont, subPaint);
            }
        }

        var pixelData = bitmap.GetPixelSpan().ToArray();
        GL.BindTexture(TextureTarget.Texture2D, textureId);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        if (width == textureWidth && height == textureHeight)
        {
            GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, width, height, PixelFormat.Rgba, PixelType.UnsignedByte, pixelData);
        }
        else
        {
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, width, height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, pixelData);
            textureWidth = width;
            textureHeight = height;
        }
        GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    static void DrawBar(SKCanvas canvas, float x, float y, float width, float height, float fraction, SKPaint track, SKPaint fill)
    {
        var rect = new SKRect(x + BarTrackInsetPx, y, x + width - BarTrackInsetPx, y + height);
        var radius = height * 0.5f;
        canvas.DrawRoundRect(rect, radius, radius, track);
        if (fraction <= 0f) return;
        var fillWidth = (rect.Right - rect.Left) * Math.Clamp(fraction, 0f, 1f);
        var fillRect = new SKRect(rect.Left, rect.Top, rect.Left + fillWidth, rect.Bottom);
        canvas.DrawRoundRect(fillRect, radius, radius, fill);
    }

    static string TruncateForWidth(SKFont font, string text, float maxWidth)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (font.MeasureText(text) <= maxWidth) return text;
        const string ellipsis = "…";
        var lo = 0;
        var hi = text.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            var candidate = text.Substring(0, mid) + ellipsis;
            if (font.MeasureText(candidate) <= maxWidth) lo = mid;
            else hi = mid - 1;
        }
        return text.Substring(0, lo) + ellipsis;
    }

    static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        return elapsed.TotalHours >= 1 ? elapsed.ToString(@"hh\:mm\:ss") : elapsed.ToString(@"mm\:ss");
    }

    public void Dispose()
    {
        if (vbo != 0) { GL.DeleteBuffer(vbo); vbo = 0; }
        if (vao != 0) { GL.DeleteVertexArray(vao); vao = 0; }
        if (shaderProgram != 0) { GL.DeleteProgram(shaderProgram); shaderProgram = 0; }
        if (textureId != 0) { GL.DeleteTexture(textureId); textureId = 0; }
    }
}
