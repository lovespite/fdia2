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
uniform int uRenderMode; // 0: composite, 1: mip, 2: hybrid
uniform int uClipEnabled;
uniform vec3 uClipNormal;
uniform float uClipOffset;

const vec3 kBoxMin = vec3(-0.5);
const vec3 kBoxMax = vec3(0.5);

// Fast pseudo-random generator for jittering
float rand(vec2 co) {
    return fract(sin(dot(co.xy ,vec2(12.9898,78.233))) * 43758.5453);
}

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

// Compute gradient for volumetric lighting
vec3 GetGradient(vec3 texCoord) {
    float h = 1.0 / 128.0; // Gradient delta
    float v = texture(uVolumeTex, texCoord).r;
    float dx = texture(uVolumeTex, texCoord + vec3(h, 0, 0)).r - texture(uVolumeTex, texCoord - vec3(h, 0, 0)).r;
    float dy = texture(uVolumeTex, texCoord + vec3(0, h, 0)).r - texture(uVolumeTex, texCoord - vec3(0, h, 0)).r;
    float dz = texture(uVolumeTex, texCoord + vec3(0, 0, h)).r - texture(uVolumeTex, texCoord - vec3(0, 0, h)).r;
    return -normalize(vec3(dx, dy, dz) + 0.00001);
}

void main()
{
    vec2 ndc = vTexCoord * 2.0 - 1.0;
    vec3 rayDir = normalize(
        uCameraForward
        + ndc.x * uCameraRight * uTanHalfFov * uAspect
        + ndc.y * uCameraUp * uTanHalfFov);

    float tEnter, tExit;
    if (!RayBoxIntersect(uCameraPosition, rayDir, tEnter, tExit))
    {
        fragColor = vec4(0.0);
        return;
    }

    tEnter = max(tEnter, 0.0);
    
    // Stochastic Jittering to reduce slicing artifacts
    float jitter = rand(gl_FragCoord.xy) * uStepSize;
    float t = tEnter + jitter;
    
    vec4 accum = vec4(0.0);
    float maxDensity = 0.0;
    
    // Light setup
    vec3 lightDir = normalize(vec3(0.5, 1.0, 0.5));
    vec3 viewDir = -rayDir;

    while (t <= tExit)
    {
        vec3 worldPos = uCameraPosition + rayDir * t;
        if (!IsClipped(worldPos))
        {
            vec3 texCoord = worldPos + vec3(0.5);
            float rawDensity = texture(uVolumeTex, texCoord).r;
            float density = clamp(rawDensity * uVolumeValueScale, 0.0, 1.0);
            
            // Apply base gamma correction to density for better contrast
            density = clamp(pow(density, 0.65) * uDensityGain, 0.0, 1.0);

            if (uRenderMode == 1) // MIP Mode
            {
                maxDensity = max(maxDensity, density);
            }
            else if (density > 0.005)
            {
                vec4 baseColor = texture(uTransferTex, density);
                
                // --- Volumetric Lighting (Phong) ---
                vec3 normal = GetGradient(texCoord);
                float diff = max(dot(normal, lightDir), 0.0);
                float spec = pow(max(dot(reflect(-lightDir, normal), viewDir), 0.0), 16.0);
                
                // Combine lighting: Ambient + Diffuse + Specular
                vec3 litColor = baseColor.rgb * (0.3 + 0.7 * diff) + vec3(0.4) * spec;
                
                // --- Physically-based Opacity (Beer-Lambert Law) ---
                // We use uOpacityGain to scale the absorption coefficient
                float alpha_src = 1.0 - exp(-density * uOpacityGain * 25.0 * uStepSize);
                
                if (uRenderMode == 0) // Composite Mode
                {
                    accum.rgb += (1.0 - accum.a) * litColor * alpha_src;
                    accum.a += (1.0 - accum.a) * alpha_src;
                }
                else if (uRenderMode == 2) // Hybrid Emission Mode
                {
                    // Additive emission for high density + standard absorption
                    accum.rgb += (1.0 - accum.a) * litColor * alpha_src;
                    accum.rgb += baseColor.rgb * density * density * 0.5; // Emission glow
                    accum.a += (1.0 - accum.a) * alpha_src;
                }

                if (accum.a >= uEarlyTerminateAlpha)
                    break;
            }
        }
        t += uStepSize;
    }

    if (uRenderMode == 1) // MIP Result
    {
        if (maxDensity <= 0.0) { fragColor = vec4(0.0); return; }
        vec4 mipColor = texture(uTransferTex, maxDensity);
        fragColor = vec4(mipColor.rgb, 0.92);
        return;
    }

    fragColor = accum;
}