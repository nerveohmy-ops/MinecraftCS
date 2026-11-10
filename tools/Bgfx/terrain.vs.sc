$input a_position, a_normal, a_texcoord0, a_color0
$output v_uv, v_color
#include <bgfx_shader.sh>
void main()
{
    gl_Position = mul(u_modelViewProj, vec4(a_position, 1.0));
    v_uv = a_texcoord0;
    float diffuse = max(dot(a_normal, normalize(vec3(0.5, 1.0, 0.4))), 0.0);
    float light = (0.45 + 0.45 * diffuse) * a_color0.r;
    v_color = vec4(light, light, light, 1.0);
}
