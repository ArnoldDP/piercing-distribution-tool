using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Wpf;
using PiercingDistributionTool.Core.Preview;

namespace PiercingDistributionTool.Preview;

/// <summary>
/// A small OpenGL view: textured, single-light, orbit camera. Accurate enough to judge where piercings
/// sit on a body, not a match for the game's shaders.
/// </summary>
public sealed class PreviewView : ContentControl
{
    private readonly GLWpfControl _gl = new();
    private readonly List<GpuMesh> _meshes = new();
    private readonly Dictionary<string, int> _textures = new(StringComparer.OrdinalIgnoreCase);
    private NpcModel? _pending;
    private TextureLoader? _pendingLoader;
    private bool _pendingDirty;
    private int _program;
    private int _whiteTexture;

    private float _yaw = MathF.PI, _pitch = 0.1f, _distance = 160f;
    private Vector3 _target = new(0, 0, 70);
    private Point? _lastMouse;

    public bool ShowPiercings { get; set; } = true;

    public PreviewView()
    {
        Content = _gl;
        _gl.Start(new GLWpfControlSettings { MajorVersion = 3, MinorVersion = 3, RenderContinuously = false });
        _gl.Render += _ => Draw();
        _gl.MouseDown += (_, e) => { _lastMouse = e.GetPosition(this); _gl.CaptureMouse(); };
        _gl.MouseUp += (_, _) => { _lastMouse = null; _gl.ReleaseMouseCapture(); };
        _gl.MouseMove += OnMouseMove;
        _gl.MouseWheel += (_, e) => { _distance = Math.Clamp(_distance * (e.Delta > 0 ? 0.9f : 1.1f), 15f, 600f); _gl.InvalidateVisual(); };
    }

    /// <summary>Queues a model for upload; the GL work happens on the next render.</summary>
    public void SetModel(NpcModel? model, TextureLoader loader)
    {
        _pending = model;
        _pendingLoader = loader;
        _pendingDirty = true;
        if (model != null) FrameModel(model);
        _gl.InvalidateVisual();
    }

    public void Refresh() => _gl.InvalidateVisual();

    private void FrameModel(NpcModel model)
    {
        float minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (var p in model.Parts.Where(p => !p.IsPiercing))
            for (var i = 2; i < p.Positions.Length; i += 3)
            {
                minZ = Math.Min(minZ, p.Positions[i]);
                maxZ = Math.Max(maxZ, p.Positions[i]);
            }
        if (minZ > maxZ) return;
        _target = new Vector3(0, 0, (minZ + maxZ) / 2);
        _distance = (maxZ - minZ) * 1.3f;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_lastMouse is not { } last) return;
        var now = e.GetPosition(this);
        var dx = (float)(now.X - last.X);
        var dy = (float)(now.Y - last.Y);
        _lastMouse = now;

        if (e.RightButton == MouseButtonState.Pressed)
            _target.Z += dy * _distance / 400f; // pan up/down to look at the face or body
        else
        {
            _yaw -= dx * 0.01f;
            _pitch = Math.Clamp(_pitch + dy * 0.01f, -1.4f, 1.4f);
        }
        _gl.InvalidateVisual();
    }

    private void Draw()
    {
        if (_program == 0) Init();
        if (_pendingDirty) Upload();

        GL.ClearColor(0.16f, 0.17f, 0.19f, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        GL.Enable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        var aspect = (float)Math.Max(1, _gl.ActualWidth) / (float)Math.Max(1, _gl.ActualHeight);
        var eye = _target + new Vector3(
            _distance * MathF.Cos(_pitch) * MathF.Sin(_yaw),
            _distance * MathF.Cos(_pitch) * MathF.Cos(_yaw),
            _distance * MathF.Sin(_pitch));
        var view = Matrix4.LookAt(eye, _target, Vector3.UnitZ);
        var proj = Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(35), aspect, 1f, 5000f);
        var mvp = view * proj;

        GL.UseProgram(_program);
        GL.UniformMatrix4(GL.GetUniformLocation(_program, "uMvp"), false, ref mvp);
        var light = Vector3.Normalize(eye - _target + new Vector3(0, 0, _distance * 0.5f));
        GL.Uniform3(GL.GetUniformLocation(_program, "uLight"), light);

        foreach (var m in _meshes)
        {
            if (m.IsPiercing && !ShowPiercings) continue;
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, m.Texture);
            GL.Uniform1(GL.GetUniformLocation(_program, "uAlphaTest"), m.AlphaTest ? 1 : 0);
            GL.BindVertexArray(m.Vao);
            GL.DrawElements(PrimitiveType.Triangles, m.IndexCount, DrawElementsType.UnsignedInt, 0);
        }
        GL.BindVertexArray(0);
    }

    private void Init()
    {
        const string vs = """
            #version 330 core
            layout(location=0) in vec3 aPos;
            layout(location=1) in vec3 aNrm;
            layout(location=2) in vec2 aUv;
            uniform mat4 uMvp;
            out vec3 vNrm; out vec2 vUv;
            void main(){ vNrm = aNrm; vUv = aUv; gl_Position = uMvp * vec4(aPos,1.0); }
            """;
        const string fs = """
            #version 330 core
            in vec3 vNrm; in vec2 vUv;
            uniform sampler2D uTex; uniform vec3 uLight; uniform int uAlphaTest;
            out vec4 frag;
            void main(){
              vec4 c = texture(uTex, vUv);
              if (uAlphaTest == 1 && c.a < 0.35) discard;   // hair and lash cut-outs; skin alpha is not opacity
              float d = abs(dot(normalize(vNrm), uLight));
              frag = vec4(c.rgb * (0.35 + 0.75 * d), 1.0);
            }
            """;
        _program = GL.CreateProgram();
        foreach (var (type, src) in new[] { (ShaderType.VertexShader, vs), (ShaderType.FragmentShader, fs) })
        {
            var s = GL.CreateShader(type);
            GL.ShaderSource(s, src);
            GL.CompileShader(s);
            GL.AttachShader(_program, s);
        }
        GL.LinkProgram(_program);
        _whiteTexture = UploadTexture(new Rgba(1, 1, new byte[] { 200, 200, 200, 255 }));
    }

    private void Upload()
    {
        _pendingDirty = false;
        foreach (var m in _meshes)
        {
            GL.DeleteVertexArray(m.Vao);
            GL.DeleteBuffers(4, m.Buffers);
        }
        _meshes.Clear();
        if (_pending == null || _pendingLoader == null) return;

        foreach (var part in _pending.Parts)
        {
            var vao = GL.GenVertexArray();
            GL.BindVertexArray(vao);
            var buffers = new int[4];
            GL.GenBuffers(4, buffers);
            Attrib(buffers[0], 0, 3, part.Positions);
            Attrib(buffers[1], 1, 3, part.Normals);
            Attrib(buffers[2], 2, 2, part.Uvs);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, buffers[3]);
            GL.BufferData(BufferTarget.ElementArrayBuffer, part.Indices.Length * sizeof(uint), part.Indices, BufferUsageHint.StaticDraw);
            GL.BindVertexArray(0);

            _meshes.Add(new GpuMesh(vao, buffers, part.Indices.Length, TextureFor(part.DiffusePath), part.IsPiercing, part.AlphaTest));
        }
    }

    private static void Attrib(int buffer, int location, int size, float[] data)
    {
        GL.BindBuffer(BufferTarget.ArrayBuffer, buffer);
        GL.BufferData(BufferTarget.ArrayBuffer, data.Length * sizeof(float), data, BufferUsageHint.StaticDraw);
        GL.EnableVertexAttribArray(location);
        GL.VertexAttribPointer(location, size, VertexAttribPointerType.Float, false, 0, 0);
    }

    private int TextureFor(string? path)
    {
        if (path == null) return _whiteTexture;
        if (_textures.TryGetValue(path, out var id)) return id;
        var rgba = _pendingLoader!.Load(path);
        id = rgba == null ? _whiteTexture : UploadTexture(rgba);
        _textures[path] = id;
        return id;
    }

    private static int UploadTexture(Rgba rgba)
    {
        var id = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, id);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, rgba.Width, rgba.Height, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, rgba.Pixels);
        GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        return id;
    }

    private sealed record GpuMesh(int Vao, int[] Buffers, int IndexCount, int Texture, bool IsPiercing, bool AlphaTest);
}
