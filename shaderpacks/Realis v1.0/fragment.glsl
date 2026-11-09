#version 330

// Realis v1.0 — Fragment Shader
// PBR Lighting, Atmospheric Scattering, Multi-bounce AO, SSS Foliage, ACES Tonemap
// Адаптировано под MinecraftCS

in float fragAO;
in vec3 fragPos;
in vec2 fragUV;
flat in vec3 fragNormal;
in float isFoliage;

uniform sampler2D texture0;   // атлас блоков
uniform sampler2D texture1;   // карта занятости мира (теней)
uniform vec3 sunDir;          // направление НА солнце
uniform vec3 sunColor;
uniform vec3 ambientColor;
uniform float shadowsOn;
uniform vec3 occOrigin;
uniform vec3 eyePosition;
uniform vec3 fogColor;
uniform float softShadows;
uniform float fogNear;
uniform float fogFar;
uniform float gameTime;

out vec4 finalColor;

const int WX = @WX@;
const int WY = @WY@;
const int WZ = @WZ@;
const int MAX_STEPS = 128;
const float PI = 3.14159265359;

float safeDir(float v) { return abs(v) < 1e-5 ? (v < 0.0 ? -1e-5 : 1e-5) : v; }

bool occupied(ivec3 c)
{
    if (any(lessThan(c, ivec3(0))) || any(greaterThanEqual(c, ivec3(WX,WY,WZ)))) return false;
    return texelFetch(texture1, ivec2(c.x + WX * c.y, c.z), 0).r > 0.5;
}

// Трассировка воксельных теней
float sunVisibility(vec3 origin, vec3 dir)
{
    vec3 d = vec3(safeDir(dir.x), safeDir(dir.y), safeDir(dir.z));
    ivec3 cell = ivec3(floor(origin));
    ivec3 stepDir = ivec3(d.x > 0.0 ? 1 : -1, d.y > 0.0 ? 1 : -1, d.z > 0.0 ? 1 : -1);
    vec3 tDelta = abs(1.0 / d);
    vec3 edge = vec3(cell) + vec3(d.x > 0.0 ? 1.0 : 0.0, d.y > 0.0 ? 1.0 : 0.0, d.z > 0.0 ? 1.0 : 0.0);
    vec3 tMax = (edge - origin) / d;
    for (int i = 0; i < MAX_STEPS; i++)
    {
        if (tMax.x < tMax.y && tMax.x < tMax.z) { cell.x += stepDir.x; tMax.x += tDelta.x; }
        else if (tMax.y < tMax.z)               { cell.y += stepDir.y; tMax.y += tDelta.y; }
        else                                    { cell.z += stepDir.z; tMax.z += tDelta.z; }
        if (cell.y >= WY || cell.x < 0 || cell.x >= WX || cell.z < 0 || cell.z >= WZ) return 1.0;
        if (cell.y >= 0 && occupied(cell)) return 0.0;
    }
    return 1.0;
}

// Мягкие тени с мультисемплингом
float filteredShadow(vec3 p, float distanceXZ)
{
    if (softShadows < 0.5 || distanceXZ > 35.0) return sunVisibility(p, sunDir);
    vec3 tangent = normalize(cross(sunDir, abs(sunDir.y) < 0.95 ? vec3(0,1,0) : vec3(1,0,0)));
    vec3 bitangent = cross(sunDir, tangent);
    float radius = 0.025;
    float v = sunVisibility(p, normalize(sunDir + tangent * radius));
    v += sunVisibility(p, normalize(sunDir + (-0.5 * tangent + 0.8660254 * bitangent) * radius));
    v += sunVisibility(p, normalize(sunDir + (-0.5 * tangent - 0.8660254 * bitangent) * radius));
    return v / 3.0;
}

// ACES Filmic Tone Mapping из Realis final.glsl
vec3 tonemapACES(vec3 x)
{
    const float a = 2.51;
    const float b = 0.03;
    const float c = 2.43;
    const float d = 0.59;
    const float e = 0.14;
    return clamp((x * (a * x + b)) / (x * (c * x + d) + e), 0.0, 1.0);
}

void main()
{
    float distanceXZ = length((fragPos - eyePosition).xz);
    if (distanceXZ >= fogFar) { finalColor = vec4(fogColor, 1.0); return; }

    vec3 n = round(fragNormal);
    vec3 toView = normalize(eyePosition - fragPos);
    vec3 p = fragPos - occOrigin;

    // Считывание альбедо из атласа и перевод в линейное пространство
    vec4 rawTex = texture(texture0, fragUV);
    vec3 albedo = pow(rawTex.rgb, vec3(2.2));

    // Определение свойств материала по атласу
    float tile = floor(fragUV.x * 11.0);
    float roughness = 0.75;
    float f0 = 0.04;
    float sssAmount = 0.0;

    if (tile == 7.0 || isFoliage > 0.5) // Leaves
    {
        roughness = 0.55;
        sssAmount = 0.85;
    }
    else if (tile == 0.0 || tile == 1.0) // Grass
    {
        roughness = 0.80;
        sssAmount = 0.30;
    }
    else if (tile == 3.0 || tile == 10.0) // Stone / Cobblestone
    {
        roughness = 0.65;
        f0 = 0.05;
    }
    else if (tile == 9.0) // Bricks
    {
        roughness = 0.50;
        f0 = 0.06;
    }
    else if (tile == 8.0) // Planks
    {
        roughness = 0.60;
        f0 = 0.04;
    }

    // Теневой коэффициент
    float ndl = max(dot(n, sunDir), 0.0);
    float vis = 1.0;
    if (shadowsOn > 0.5 && (ndl > 0.0 || sssAmount > 0.0) && dot(sunColor, vec3(1.0)) > 0.01)
        vis = filteredShadow(p + n * 0.025, distanceXZ);

    // 1. Direct Diffuse
    vec3 diffuse = sunColor * ndl * vis;

    // 2. Subsurface Scattering для листвы (просвечивание на солнце, как в Realis lighting.glsl)
    if (sssAmount > 0.0)
    {
        float wrap = clamp(ndl * 0.5 + 0.5, 0.0, 1.0);
        float through = pow(clamp(dot(-toView, sunDir) * 0.5 + 0.5, 0.0, 1.0), 4.0);
        vec3 sss = sunColor * (0.35 * wrap + 0.9 * through) * (1.0 - ndl) * vis;
        diffuse += sss * sssAmount * (0.6 * albedo + 0.4);
    }

    // 3. GGX Specular Highlight (Realis PBR specular)
    vec3 specular = vec3(0.0);
    if (ndl > 0.0 && vis > 0.01)
    {
        vec3 halfway = normalize(toView + sunDir);
        float NoH = max(dot(n, halfway), 0.0);
        float NoV = max(dot(n, toView), 0.001);
        float VoH = max(dot(toView, halfway), 0.0);
        float a = max(roughness * roughness, 0.004);
        float a2 = a * a;
        float d = NoH * NoH * (a2 - 1.0) + 1.0;
        float D_ggx = a2 / (PI * d * d);
        float k = a * 0.5;
        float V_smith = 0.25 / ((NoV * (1.0 - k) + k) * (ndl * (1.0 - k) + k));
        float F_schlick = f0 + (1.0 - f0) * pow(1.0 - VoH, 5.0);
        specular = sunColor * (D_ggx * V_smith * F_schlick * ndl * PI) * vis;
    }

    // 4. Multi-bounce Ambient Occlusion (Realis Jimenez et al.)
    vec3 aoA = 2.0404 * albedo - 0.3324;
    vec3 aoB = -4.7951 * albedo + 0.6417;
    vec3 aoC = 2.7552 * albedo + 0.6903;
    vec3 occlusion = max(vec3(fragAO), ((fragAO * aoA + aoB) * fragAO + aoC) * fragAO);

    // 5. Sky Ambient & Natural Ground Bounce (Realis sky model)
    float hemisphere = 0.45 + 0.55 * (n.y * 0.5 + 0.5);
    vec3 bounce = sunColor * vec3(1.0, 0.94, 0.82) * (0.085 * (1.0 - 0.6 * n.y)) * (shadowsOn > 0.5 ? 1.0 : 0.5);
    vec3 ambient = (ambientColor * hemisphere + bounce) * occlusion;

    // Результирующий поверхностный свет
    vec3 surfaceColor = albedo * (diffuse + ambient) + specular;

    // 6. Атмосферное рассеяние (Mie Corona + Rayleigh Fog из Realis atmosphere.glsl)
    vec3 viewDir = normalize(fragPos - eyePosition);
    float cosSun = dot(viewDir, sunDir);
    float mie = pow(max(cosSun, 0.0), 16.0) * 0.4 + pow(max(cosSun, 0.0), 64.0) * 0.6;
    vec3 inscatterFog = mix(fogColor, sunColor * 1.35, mie * clamp(sunDir.y * 2.0, 0.0, 1.0));
    float fog = smoothstep(fogNear, fogFar, distanceXZ);
    vec3 litWithFog = mix(surfaceColor, inscatterFog, fog);

    // 7. ACES Tone Mapping & Виньетка (Realis final.glsl)
    vec3 hdrMapped = tonemapACES(litWithFog * 1.15);

    // Легкая кинематографическая виньетка
    vec2 screenDist = gl_FragCoord.xy / vec2(1280.0, 720.0) - vec2(0.5);
    float vignette = 1.0 - dot(screenDist, screenDist) * 0.22;
    hdrMapped *= clamp(vignette, 0.0, 1.0);

    // Перевод в гамму дисплея 2.2
    vec3 finalRgb = pow(hdrMapped, vec3(1.0 / 2.2));
    finalColor = vec4(finalRgb, rawTex.a);
}
