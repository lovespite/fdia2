#version 330 core
layout (location = 0) in vec3 aPosition;
layout (location = 1) in float aIntensity;
out float vIntensity;
uniform mat4 uView;
uniform mat4 uProjection;
void main()
{
    gl_Position = uProjection * uView * vec4(aPosition, 1.0);
    gl_PointSize = 2.0 + aIntensity * 4.0;
    vIntensity = aIntensity;
}
