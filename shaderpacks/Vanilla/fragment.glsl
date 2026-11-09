#version 330
in float fragAO;
in vec3 fragPos;
in vec2 fragUV;
flat in vec3 fragNormal;

uniform sampler2D texture0;   // атлас блоков
uniform sampler2D texture1;   // карта занятости мира
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
out vec4 finalColor;

const int WX = @WX@;
const int WY = @WY@;
const int WZ = @WZ@;
const int MAX_STEPS = 128;

float safeDir(float v) { return abs(v) < 1e-5 ? (v < 0.0 ? -1e-5 : 1e-5) : v; }

bool occupied(ivec3 c)
{
    if (any(lessThan(c, ivec3(0))) || any(greaterThanEqual(c, ivec3(WX,WY,WZ)))) return false;
    return texelFetch(texture1, ivec2(c.x + WX * c.y, c.z), 0).r > 0.5;
}

// 1.0 — солнце видно, 0.0 — точка в тени
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

float filteredShadow(vec3 p, float distanceXZ)
{
    if (softShadows < 0.5 || distanceXZ > 35.0) return sunVisibility(p,sunDir);
    vec3 tangent = normalize(cross(sunDir,abs(sunDir.y) < 0.95 ? vec3(0,1,0) : vec3(1,0,0)));
    vec3 bitangent = cross(sunDir,tangent);
    float radius = 0.025;
    float v = sunVisibility(p,normalize(sunDir + tangent*radius));
    v += sunVisibility(p,normalize(sunDir + (-0.5*tangent + 0.8660254*bitangent)*radius));
    v += sunVisibility(p,normalize(sunDir + (-0.5*tangent - 0.8660254*bitangent)*radius));
    return v / 3.0;
}

void main()
{
    float distanceXZ = length((fragPos-eyePosition).xz);
    if (distanceXZ >= fogFar) { finalColor = vec4(fogColor,1.0); return; }
    vec3 n = round(fragNormal);
    vec3 p = fragPos - occOrigin;
    float ndl = max(dot(n, sunDir), 0.0);
    float vis = 1.0;
    if (shadowsOn > 0.5 && ndl > 0.0 && dot(sunColor, vec3(1.0)) > 0.01)
        vis = filteredShadow(p + n * 0.025,distanceXZ);
    float ao = fragAO;
    float hemisphere = 0.66 + 0.34 * max(n.y,0.0);
    vec3 bounce = vec3(0.055,0.043,0.028) * (1.0 - max(n.y,0.0));
    vec3 light = (ambientColor * hemisphere + bounce) * ao + sunColor * ndl * vis;
    vec3 albedo = pow(texture(texture0,fragUV).rgb,vec3(2.2));
    vec3 mapped = vec3(1.0) - exp(-albedo * light * 1.65);
    vec3 color = pow(mapped,vec3(1.0/2.2));
    float fog = smoothstep(fogNear,fogFar,distanceXZ);
    finalColor = vec4(mix(color,fogColor,fog),1.0);
}
