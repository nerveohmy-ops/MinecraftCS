$input v_uv, v_color
#include <bgfx_shader.sh>
SAMPLER2D(s_atlas, 0);
void main()
{
    gl_FragColor = vec4(texture2D(s_atlas, v_uv).rgb * v_color.rgb, 1.0);
}
