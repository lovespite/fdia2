#version 330 core
in vec2 vTexCoord;
out vec4 fragColor;

uniform sampler3D uVolumeTex;
uniform sampler1D uTransferTex;
uniform vec3 uCameraPosition;
uniform vec3 uCameraForward;
uniform vec3 uCameraRight;
uniform vec3 uCameraUp;
uniform float uAspect;
uniform float uTanHalfFov;
uniform float uStepSize;
uniform float uDensityGain;
uniform float uVolumeValueScale;
uniform float uOpacityGain;
uniform float uEarlyTerminateAlpha;
uniform int uRenderMode; // 0: composite, 1: mip
uniform int uClipEnabled;
uniform vec3 uClipNormal;
uniform float uClipOffset;

const vec3 kBoxMin = vec3(-0.5);
const vec3 kBoxMax = vec3(0.5);

bool RayBoxIntersect(vec3 rayOrigin, vec3 rayDir, out float tEnter, out float tExit)
{
    vec3 invDir = 1.0 / rayDir;
    vec3 t0 = (kBoxMin - rayOrigin) * invDir;
    vec3 t1 = (kBoxMax - rayOrigin) * invDir;
    vec3 tMin = min(t0, t1);
    vec3 tMax = max(t0, t1);
    tEnter = max(max(tMin.x, tMin.y), tMin.z);
    tExit = min(min(tMax.x, tMax.y), tMax.z);
    return tExit >= max(tEnter, 0.0);
}

bool IsClipped(vec3 worldPos)
{
    if (uClipEnabled == 0)
        return false;
    float d = dot(worldPos, normalize(uClipNormal));
    return d > uClipOffset;
}

void main()
{
    vec2 ndc = vTexCoord * 2.0 - 1.0;
    vec3 rayDir = normalize(
        uCameraForward
        + ndc.x * uCameraRight * uTanHalfFov * uAspect
        + ndc.y * uCameraUp * uTanHalfFov);

    float tEnter;
    float tExit;
    if (!RayBoxIntersect(uCameraPosition, rayDir, tEnter, tExit))
    {
        fragColor = vec4(0.0);
        return;
    }

    tEnter = max(tEnter, 0.0);
    vec4 accum = vec4(0.0);
    float maxDensity = 0.0;
    float t = tEnter;
    while (t <= tExit)
    {
        vec3 worldPos = uCameraPosition + rayDir * t;
        if (!IsClipped(worldPos))
        {
            vec3 texCoord = worldPos + vec3(0.5);
            float density = texture(uVolumeTex, texCoord).r;
            density = clamp(density * uVolumeValueScale, 0.0, 1.0);
            density = clamp(pow(density, 0.62) * uDensityGain, 0.0, 1.0);

            if (uRenderMode == 1)
            {
                maxDensity = max(maxDensity, density);
            }
            else
            {
                vec4 sampleColor = texture(uTransferTex, density);
                sampleColor.a = clamp(sampleColor.a * uOpacityGain, 0.0, 1.0);
                accum.rgb += (1.0 - accum.a) * sampleColor.rgb * sampleColor.a;
                accum.a += (1.0 - accum.a) * sampleColor.a;
                if (accum.a >= uEarlyTerminateAlpha)
                    break;
            }
        }

        t += uStepSize;
    }

    if (uRenderMode == 1)
    {
        if (maxDensity <= 0.0)
        {
            fragColor = vec4(0.0);
            return;
        }

        vec4 mipColor = texture(uTransferTex, maxDensity);
        fragColor = vec4(mipColor.rgb, 0.92);
        return;
    }

    fragColor = accum;
}
