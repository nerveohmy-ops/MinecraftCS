#version 330
in vec3 vertexPosition;
in vec2 vertexTexCoord;
in vec3 vertexNormal;
in vec4 vertexColor;

uniform mat4 mvp;

out float fragAO;
out vec3 fragPos;
out vec2 fragUV;
flat out vec3 fragNormal;

void main()
{
    fragPos = vertexPosition;
    fragUV = vertexTexCoord;
    fragNormal = vertexNormal;
    fragAO = vertexColor.r;
    gl_Position = mvp * vec4(vertexPosition, 1.0);
}
