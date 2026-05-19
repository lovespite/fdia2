#version 330 core
in vec3 vWorldPos;
out vec4 fragColor;
uniform vec3 uReferenceOrigin;
uniform vec3 uCameraPosition;
uniform float uMinorCell;
uniform float uMajorCell;
uniform float uFadeDistance;
uniform vec3 uMinorColor;
uniform vec3 uMajorColor;

float GridFactor(vec2 pos, float cellSize)
{
    vec2 coord = pos / cellSize;
    vec2 deriv = max(fwidth(coord), vec2(1e-4));
    vec2 lineDist = abs(fract(coord - 0.5) - 0.5) / deriv;
    float line = min(lineDist.x, lineDist.y);
    return 1.0 - clamp(line, 0.0, 1.0);
}

void main()
{
    vec2 local = vWorldPos.xz - uReferenceOrigin.xz;
    float minor = GridFactor(local, uMinorCell);
    float major = GridFactor(local, uMajorCell);
    float dist = length(vWorldPos - uCameraPosition);
    float fade = clamp(1.0 - dist / max(uFadeDistance, 1e-4), 0.0, 1.0);
    float alpha = max(minor * 0.28, major * 0.85) * fade;
    if (alpha < 0.01)
        discard;

    vec3 color = mix(uMinorColor, uMajorColor, clamp(major, 0.0, 1.0));
    fragColor = vec4(color, alpha);
}
