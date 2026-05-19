#version 330 core
layout (location = 0) in vec3 aWorldPos;
out vec3 vWorldPos;
uniform mat4 uView;
uniform mat4 uProjection;
void main()
{
    vWorldPos = aWorldPos;
    gl_Position = uProjection * uView * vec4(aWorldPos, 1.0);
}
