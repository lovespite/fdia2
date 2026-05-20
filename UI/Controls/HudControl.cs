using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using SkiaSharp;

namespace Fdia2.UI.Controls;

public enum HudSliderKey
{
    None,
    Density,
    Opacity,
    Slice,
}

public readonly struct HudSliderConfig
{
    public HudSliderConfig(HudSliderKey key, string label, float value, float min, float max)
    {
        Key = key;
        Label = label;
        Value = value;
        Min = min;
        Max = max;
    }

    public HudSliderKey Key { get; }
    public string Label { get; }
    public float Value { get; }
    public float Min { get; }
    public float Max { get; }
}

public sealed class HudControl : IDisposable
{
    const float FontSizePx = 14f;
    const float LineSpacingPx = 3f;
    const float PaddingPx = 10f;
    const float CornerRadiusPx = 8f;
    const int MarginPx = 12;
    const float SliderTrackHeightPx = 6f;
    const float SliderHitHeightPx = 20f;
    const float SliderSectionGapPx = 8f;
    const float SliderSectionLabelGapPx = 4f;
    const float SliderThumbRadiusPx = 7f;

    int shaderProgram;
    int vao;
    int vbo;
    int textureId;
    int textureWidth;
    int textureHeight;

    string text = string.Empty;
    HudSliderConfig[] sliders = Array.Empty<HudSliderConfig>();
    bool dirty = true;

    Vector4 densityRectPx;
    Vector4 opacityRectPx;
    Vector4 sliceRectPx;

    public void Initialize()
    {
        shaderProgram = ShaderUtils.CreateProgramFromResources("hud.vert", "hud.frag", "hud");
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

    public void Update(string newText, IReadOnlyList<HudSliderConfig> newSliders)
    {
        if (!string.Equals(text, newText, StringComparison.Ordinal))
        {
            text = newText;
            dirty = true;
        }

        if (!SlidersEqual(sliders, newSliders))
        {
            sliders = newSliders.ToArray();
            dirty = true;
        }
    }

    static bool SlidersEqual(HudSliderConfig[] a, IReadOnlyList<HudSliderConfig> b)
    {
        if (a.Length != b.Count) return false;
        for (int i = 0; i < a.Length; i++)
        {
            var x = a[i]; var y = b[i];
            if (x.Key != y.Key || x.Min != y.Min || x.Max != y.Max || x.Value != y.Value || x.Label != y.Label)
                return false;
        }
        return true;
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

        var marginX = 2f * MarginPx / clientSize.X;
        var marginY = 2f * MarginPx / clientSize.Y;
        var widthNdc = 2f * textureWidth / clientSize.X;
        var heightNdc = 2f * textureHeight / clientSize.Y;

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

    public HudSliderKey HitTest(Vector2 mouse)
    {
        if (Contains(densityRectPx, mouse)) return HudSliderKey.Density;
        if (Contains(opacityRectPx, mouse)) return HudSliderKey.Opacity;
        if (Contains(sliceRectPx, mouse)) return HudSliderKey.Slice;
        return HudSliderKey.None;
    }

    public bool TryGetSliderRange(HudSliderKey key, out Vector4 rect, out float min, out float max)
    {
        rect = key switch
        {
            HudSliderKey.Density => densityRectPx,
            HudSliderKey.Opacity => opacityRectPx,
            HudSliderKey.Slice => sliceRectPx,
            _ => Vector4.Zero,
        };

        foreach (var s in sliders)
        {
            if (s.Key == key)
            {
                min = s.Min;
                max = s.Max;
                return rect.Z > rect.X;
            }
        }

        min = 0; max = 0;
        return false;
    }

    public float? MouseToValue(HudSliderKey key, float mouseX)
    {
        if (!TryGetSliderRange(key, out var rect, out var min, out var max))
            return null;
        var t = Math.Clamp((mouseX - rect.X) / (rect.Z - rect.X), 0f, 1f);
        return min + t * (max - min);
    }

    static bool Contains(Vector4 rect, Vector2 point) =>
      rect.Z > rect.X && rect.W > rect.Y
      && point.X >= rect.X && point.X <= rect.Z
      && point.Y >= rect.Y && point.Y <= rect.W;

    void UpdateTextureIfNeeded()
    {
        if (!dirty)
            return;

        dirty = false;
        densityRectPx = Vector4.Zero;
        opacityRectPx = Vector4.Zero;
        sliceRectPx = Vector4.Zero;

        if (string.IsNullOrWhiteSpace(text))
        {
            textureWidth = 0;
            textureHeight = 0;
            return;
        }

        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            textureWidth = 0;
            textureHeight = 0;
            return;
        }

        using var textPaint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true,
        };
        using var font = new SKFont(SKTypeface.Default, FontSizePx);
        var metrics = font.Metrics;
        var lineHeight = MathF.Ceiling(metrics.Descent - metrics.Ascent + LineSpacingPx);
        float maxLineWidth = 0f;
        foreach (var line in lines)
            maxLineWidth = Math.Max(maxLineWidth, font.MeasureText(line));

        // Cap HUD width to prevent excessive stretching with long paths/strings
        maxLineWidth = 320f;

        var sliderCount = sliders.Length;
        var sliderTrackWidth = Math.Max(190f, maxLineWidth);
        var sliderLabelHeight = MathF.Ceiling(metrics.Descent - metrics.Ascent);
        var sliderRowHeight = sliderLabelHeight + SliderSectionLabelGapPx + SliderHitHeightPx;
        var sliderSectionHeight = sliderCount > 0
          ? SliderSectionGapPx + sliderCount * sliderRowHeight + Math.Max(0, sliderCount - 1) * SliderSectionGapPx
          : 0f;

        var width = Math.Max(1, (int)MathF.Ceiling(Math.Max(maxLineWidth, sliderTrackWidth) + PaddingPx * 2f));
        var height = Math.Max(1, (int)MathF.Ceiling(lines.Length * lineHeight + PaddingPx * 2f + sliderSectionHeight));

        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            using var bgPaint = new SKPaint
            {
                Color = new SKColor(0, 0, 0, 160),
                IsAntialias = true,
            };
            canvas.DrawRoundRect(new SKRect(0, 0, width, height), CornerRadiusPx, CornerRadiusPx, bgPaint);

            var baseline = PaddingPx - metrics.Ascent;
            for (int i = 0; i < lines.Length; i++)
            {
                canvas.DrawText(lines[i], PaddingPx, baseline + i * lineHeight, SKTextAlign.Left, font, textPaint);
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

                var sliderLeft = PaddingPx;
                var sliderTop = PaddingPx + lines.Length * lineHeight + SliderSectionGapPx;
                var sliderWidth = width - PaddingPx * 2f;
                var nextSliderTop = sliderTop;
                for (int i = 0; i < sliders.Length; i++)
                {
                    var s = sliders[i];
                    var hitRect = DrawSlider(canvas, font, textPaint, trackPaint, fillPaint, thumbPaint,
                        s.Label, s.Value, s.Min, s.Max,
                        sliderLeft, nextSliderTop, sliderWidth, sliderLabelHeight);
                    var rectPx = new Vector4(
                      MarginPx + hitRect.Left,
                      MarginPx + hitRect.Top,
                      MarginPx + hitRect.Right,
                      MarginPx + hitRect.Bottom);
                    switch (s.Key)
                    {
                        case HudSliderKey.Density: densityRectPx = rectPx; break;
                        case HudSliderKey.Opacity: opacityRectPx = rectPx; break;
                        case HudSliderKey.Slice: sliceRectPx = rectPx; break;
                    }
                    if (i < sliders.Length - 1)
                        nextSliderTop += sliderRowHeight + SliderSectionGapPx;
                }
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

    static SKRect DrawSlider(SKCanvas canvas,
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

        var hitTop = top + labelHeight + SliderSectionLabelGapPx;
        var trackTop = hitTop + (SliderHitHeightPx - SliderTrackHeightPx) * 0.5f;
        var trackRect = new SKRect(left, trackTop, left + width, trackTop + SliderTrackHeightPx);
        canvas.DrawRoundRect(trackRect, SliderTrackHeightPx * 0.5f, SliderTrackHeightPx * 0.5f, trackPaint);

        var fillRect = new SKRect(trackRect.Left, trackRect.Top, trackRect.Left + trackRect.Width * normalized, trackRect.Bottom);
        canvas.DrawRoundRect(fillRect, SliderTrackHeightPx * 0.5f, SliderTrackHeightPx * 0.5f, fillPaint);

        var thumbX = trackRect.Left + trackRect.Width * normalized;
        var thumbY = (trackRect.Top + trackRect.Bottom) * 0.5f;
        canvas.DrawCircle(thumbX, thumbY, SliderThumbRadiusPx, thumbPaint);

        return new SKRect(left, hitTop, left + width, hitTop + SliderHitHeightPx);
    }

    public void Dispose()
    {
        if (vbo != 0) { GL.DeleteBuffer(vbo); vbo = 0; }
        if (vao != 0) { GL.DeleteVertexArray(vao); vao = 0; }
        if (shaderProgram != 0) { GL.DeleteProgram(shaderProgram); shaderProgram = 0; }
        if (textureId != 0) { GL.DeleteTexture(textureId); textureId = 0; }
    }
}
