#version 330

in float fragAO;
in vec3 fragPos;
in vec2 fragUV;
flat in vec3 fragNormal;

uniform sampler2D texture0;
uniform sampler2D texture1;
uniform vec3 sunDir;
uniform vec3 sunColor;
uniform vec3 ambientColor;
uniform vec3 eyePosition;
uniform vec3 fogColor;
uniform vec3 occOrigin;
uniform float shadowsOn;
uniform float softShadows;
uniform float fogNear;
uniform float fogFar;
uniform vec2 invViewport;

out vec4 finalColor;

const float exposure = @EXPOSURE@;
const float contrast = @CONTRAST@;
const float saturation = @SATURATION@;
const float vibrance = @VIBRANCE@;
const bool vignetteEnabled = @VIGNETTE@;

@SHADOW_TRACE@

vec3 gradeColor(vec3 color)
{
    float luma = dot(color, vec3(0.2126, 0.7152, 0.0722));
    float chroma = max(color.r, max(color.g, color.b)) - min(color.r, min(color.g, color.b));
    float saturationScale = saturation * mix(vibrance, 1.0, clamp(chroma, 0.0, 1.0));
    color = mix(vec3(luma), color, saturationScale);
    return clamp((color - 0.5) * contrast + 0.5, 0.0, 1.0);
}

void main()
{
    float distanceXZ = length((fragPos - eyePosition).xz);
    if (distanceXZ >= fogFar) {
        finalColor = vec4(fogColor, 1.0);
        return;
    }

    vec3 normal = round(fragNormal);
    float diffuse = max(dot(normal, sunDir), 0.0);
    float visibility = 1.0;
    if (shadowsOn > 0.5 && diffuse > 0.0 && dot(sunColor, vec3(1.0)) > 0.01)
        visibility = filteredShadow(fragPos + normal * 0.025);

    vec4 texel = texture(texture0, fragUV);
    vec3 albedo = pow(texel.rgb, vec3(2.2));
    float skyLight = 0.65 + 0.35 * max(normal.y, 0.0);
    vec3 light = ambientColor * skyLight * fragAO + sunColor * diffuse * visibility;
    vec3 mapped = vec3(1.0) - exp(-albedo * light * (1.65 * exposure));
    vec3 color = gradeColor(pow(mapped, vec3(1.0 / 2.2)));
    if (vignetteEnabled) {
        vec2 center = gl_FragCoord.xy * invViewport - 0.5;
        color *= 1.0 - 0.28 * dot(center, center);
    }

    float fog = smoothstep(fogNear, fogFar, distanceXZ);
    finalColor = vec4(mix(color, fogColor, fog), texel.a);
}
