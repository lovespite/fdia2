#version 330 core
in float vIntensity;
out vec4 fragColor;

vec3 ColorRamp(float t)
{
    t = clamp(t, 0.0, 1.0);
    vec3 a = vec3(0.05, 0.12, 0.45);
    vec3 b = vec3(0.00, 0.90, 1.00);
    vec3 c = vec3(1.00, 0.95, 0.20);
    vec3 d = vec3(1.00, 0.25, 0.00);
    if (t < 0.33)
        return mix(a, b, t / 0.33);
    if (t < 0.66)
        return mix(b, c, (t - 0.33) / 0.33);
    return mix(c, d, (t - 0.66) / 0.34);
}

void main()
{
    vec3 color = ColorRamp(vIntensity);
    fragColor = vec4(color, 1.0);
}
