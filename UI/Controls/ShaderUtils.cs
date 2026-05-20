using OpenTK.Graphics.OpenGL4;
using System.Reflection;

namespace Fdia2.UI.Controls;

internal static class ShaderUtils
{
    public static int CreateProgramFromResources(string vertexShaderFileName, string fragmentShaderFileName, string label)
    {
        var vs = LoadResource(vertexShaderFileName);
        var fs = LoadResource(fragmentShaderFileName);
        return CreateProgram(vs, fs, label);
    }

    public static string LoadResource(string shaderFileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var normalizedName = shaderFileName.Replace('\\', '.').Replace('/', '.');
        var suffix = ".Shaders." + normalizedName;
        var resourceName = assembly
          .GetManifestResourceNames()
          .FirstOrDefault(name => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
          ?? throw new InvalidOperationException($"Shader resource not found for '{shaderFileName}'. Expected suffix '{suffix}'.");

        using var stream = assembly.GetManifestResourceStream(resourceName)
          ?? throw new InvalidOperationException($"Failed to open shader resource stream '{resourceName}'.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static int CreateProgram(string vertexShaderSource, string fragmentShaderSource, string label)
    {
        var vertexShader = CompileShader(ShaderType.VertexShader, vertexShaderSource);
        var fragmentShader = CompileShader(ShaderType.FragmentShader, fragmentShaderSource);
        var program = GL.CreateProgram();

        GL.AttachShader(program, vertexShader);
        GL.AttachShader(program, fragmentShader);
        GL.LinkProgram(program);
        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out var linkStatus);
        if (linkStatus == 0)
        {
            var linkLog = GL.GetProgramInfoLog(program);
            GL.DeleteProgram(program);
            throw new InvalidOperationException($"Failed to link {label} shader program: {linkLog}");
        }

        GL.DetachShader(program, vertexShader);
        GL.DetachShader(program, fragmentShader);
        GL.DeleteShader(vertexShader);
        GL.DeleteShader(fragmentShader);
        return program;
    }

    public static int CompileShader(ShaderType shaderType, string source)
    {
        var shader = GL.CreateShader(shaderType);
        GL.ShaderSource(shader, source);
        GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out var compileStatus);
        if (compileStatus == 0)
        {
            var compileLog = GL.GetShaderInfoLog(shader);
            GL.DeleteShader(shader);
            throw new InvalidOperationException($"Failed to compile {shaderType} shader: {compileLog}");
        }

        return shader;
    }
}
