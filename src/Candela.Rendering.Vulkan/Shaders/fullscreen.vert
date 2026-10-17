#version 460
#extension GL_GOOGLE_include_directive : require
// One triangle covering the viewport (vertex ids 0, 1, 2; no vertex buffer).

layout(location = 0) out vec2 outUv;

void main()
{
    vec2 uv = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
    outUv = uv;
    gl_Position = vec4(uv * 2.0 - 1.0, 0.0, 1.0);
}
