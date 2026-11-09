#version 330

// Realis v1.0 — Vertex Shader
// Адаптировано для MinecraftCS

in vec3 vertexPosition;
in vec2 vertexTexCoord;
in vec3 vertexNormal;
in vec4 vertexColor;

uniform mat4 mvp;
uniform float gameTime;

out float fragAO;
out vec3 fragPos;
out vec2 fragUV;
flat out vec3 fragNormal;
out float isFoliage;

void main()
{
    vec3 p = vertexPosition;

    // Определение листвы по UV атласа (тайл 7 = leaves)
    float tile = floor(vertexTexCoord.x * 11.0);
    float foliage = (tile == 7.0) ? 1.0 : 0.0;
    isFoliage = foliage;

    // Мягкое покачивание листвы на ветру из Realis waving.glsl
    if (foliage > 0.5)
    {
        float t = gameTime * 2.2;
        float waveX = sin(t + p.x * 0.75 + p.z * 0.5) * 0.035;
        float waveZ = cos(t * 0.85 + p.y * 1.1 + p.x * 0.4) * 0.035;
        p.x += waveX;
        p.z += waveZ;
    }

    fragPos = p;
    fragUV = vertexTexCoord;
    fragNormal = vertexNormal;
    fragAO = vertexColor.r;
    gl_Position = mvp * vec4(p, 1.0);
}
