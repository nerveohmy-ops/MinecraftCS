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

@SHADOW_TRACE@

void main()
{
    float distanceXZ = length((fragPos-eyePosition).xz);
    if (distanceXZ >= fogFar) { finalColor = vec4(fogColor,1.0); return; }
    vec3 n = round(fragNormal);
    float ndl = max(dot(n, sunDir), 0.0);
    float vis = 1.0;
    if (shadowsOn > 0.5 && ndl > 0.0 && dot(sunColor, vec3(1.0)) > 0.01)
        vis = filteredShadow(fragPos + n * 0.025);
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
