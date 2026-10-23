using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using BambuBar.Models;

namespace BambuBar.UI;

/// <summary>
/// The printer as a live 3D model (converted from the macOS app's Blender models by tools/convert-models.py): the
/// print grows slice by slice in the active filament, the bed (CoreXY) or gantry (bed slingers) follows the layers, the
/// toolhead moves while printing, spools take their filament colours and the chamber light follows the printer's.
/// Drag to turn it; double-click to reset. Falls back to the rendered drawing when the model can't load.
/// </summary>
public sealed class PrinterModelView : Grid
{
    private const double RestYaw = 0.42, RestPitch = 0.16;
    private const double FieldOfView = 17;  // vertical, as SceneKit measures it

    private readonly Viewport3D _viewport = new() { ClipToBounds = true, IsHitTestVisible = false };
    private readonly PerspectiveCamera _camera = new() { NearPlaneDistance = 0.05, FarPlaneDistance = 20 };
    private readonly ModelVisual3D _sceneVisual = new();
    private readonly Model3DGroup _pivot = new();
    private readonly RotateTransform3D _yawRotation = new(new AxisAngleRotation3D(new Vector3D(0, 1, 0), 0));
    private readonly RotateTransform3D _pitchRotation = new(new AxisAngleRotation3D(new Vector3D(1, 0, 0), 0));
    private readonly Border _fallback = new();
    private ModelScene? _scene;
    private PrinterArt? _art;
    private double _yaw = RestYaw, _pitch = RestPitch;
    private Point? _lastDrag;
    private bool _moving;
    private TimeSpan _lastFrame;
    private DateTime _motionStart;
    private double _radius = 0.4;

    public PrinterModelView()
    {
        Background = Brushes.Transparent;  // takes the mouse for turning
        Cursor = Cursors.SizeAll;
        _viewport.Camera = _camera;
        var root = new Model3DGroup();
        root.Children.Add(_pivot);
        AddLights(root);
        _pivot.Transform = new Transform3DGroup { Children = { _yawRotation, _pitchRotation } };
        _sceneVisual.Content = root;
        _viewport.Children.Add(_sceneVisual);
        Children.Add(_viewport);
        Children.Add(_fallback);
        Orient();
        SizeChanged += (_, _) => FrameCamera();
        IsVisibleChanged += (_, _) => UpdateMotion();
        Unloaded += (_, _) => StopRendering();
        Loaded += (_, _) => UpdateMotion();
        ToolTip = "Drag to turn the printer; double-click to reset";
        ToolTipService.SetInitialShowDelay(this, 1200);
    }

    private static void AddLights(Model3DGroup root)
    {
        // Matches the macOS studio: a warm key light, a cool fill, a rim from behind, and a soft ambient for the dome.
        root.Children.Add(new AmbientLight(Color.FromRgb(70, 72, 78)));
        root.Children.Add(new DirectionalLight(Color.FromRgb(240, 234, 226), new Vector3D(0.6, -1.0, -0.9)));
        root.Children.Add(new DirectionalLight(Color.FromRgb(70, 76, 86), new Vector3D(-0.8, -0.25, -0.5)));
        root.Children.Add(new DirectionalLight(Color.FromRgb(150, 160, 178), new Vector3D(-0.4, -0.6, 0.9)));
    }

    /// <summary>Shows a printer state, loading the shape's model on first use.</summary>
    public void Apply(PrinterArt art)
    {
        var shapeChanged = _art?.Shape != art.Shape || _scene is null;
        _art = art;
        if (shapeChanged) Load(art.Shape);
        Opacity = art.Dimmed ? 0.6 : 1;
        if (_scene is null)
        {
            _fallback.Child = new PrinterArtView(art);
            _fallback.Visibility = Visibility.Visible;
            _viewport.Visibility = Visibility.Collapsed;
            return;
        }
        _fallback.Child = null;
        _fallback.Visibility = Visibility.Collapsed;
        _viewport.Visibility = Visibility.Visible;
        _scene.Apply(art, _moving ? MotionTime : null);
        UpdateMotion();
    }

    private void Load(PrinterShape shape)
    {
        _pivot.Children.Clear();
        _scene = ModelLibrary.Load(shape) is { } model ? new ModelScene(model) : null;
        if (_scene is null) return;
        _pivot.Children.Add(_scene.Root);
        _radius = _scene.Radius;
        FrameCamera();
    }

    private void FrameCamera()
    {
        var width = Math.Max(1, ActualWidth);
        var height = Math.Max(1, ActualHeight);
        // WPF's field of view is horizontal; SceneKit's is vertical.
        var vertical = FieldOfView * Math.PI / 180;
        var horizontal = 2 * Math.Atan(Math.Tan(vertical / 2) * width / height);
        _camera.FieldOfView = horizontal * 180 / Math.PI;
        var distance = _radius / Math.Sin(vertical / 2) * 0.92;
        _camera.Position = new Point3D(0, 0, distance);
        _camera.LookDirection = new Vector3D(0, 0, -1);
        _camera.UpDirection = new Vector3D(0, 1, 0);
        _camera.FarPlaneDistance = distance * 4;
    }

    private void Orient()
    {
        ((AxisAngleRotation3D)_yawRotation.Rotation).Angle = _yaw * 180 / Math.PI;
        ((AxisAngleRotation3D)_pitchRotation.Rotation).Angle = _pitch * 180 / Math.PI;
    }

    // MARK: - Motion

    private double MotionTime => (DateTime.UtcNow - _motionStart).TotalSeconds;

    private void UpdateMotion()
    {
        var shouldMove = _art is { Animated: true, State: JobState.Printing, Dimmed: false } && _scene is not null && IsVisible;
        if (shouldMove == _moving) return;
        _moving = shouldMove;
        if (_moving)
        {
            _motionStart = DateTime.UtcNow;
            CompositionTarget.Rendering += OnFrame;
        }
        else
        {
            StopRendering();
            if (_art is not null) _scene?.Apply(_art, null);
        }
    }

    private void StopRendering()
    {
        CompositionTarget.Rendering -= OnFrame;
        _moving = false;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        // 30 frames a second is plenty for a toolhead the size of a fingernail.
        if (e is RenderingEventArgs args)
        {
            if (args.RenderingTime - _lastFrame < TimeSpan.FromMilliseconds(32)) return;
            _lastFrame = args.RenderingTime;
        }
        if (_art is not null) _scene?.Apply(_art, MotionTime);
    }

    // MARK: - Turning

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ClickCount == 2)
        {
            AnimateTo(RestYaw, RestPitch);
            return;
        }
        _lastDrag = e.GetPosition(this);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_lastDrag is not { } last || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(this);
        _yaw += (point.X - last.X) * 0.012;
        _pitch = Math.Clamp(_pitch + (point.Y - last.Y) * 0.008, -0.15, 0.7);
        _lastDrag = point;
        Orient();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _lastDrag = null;
        ReleaseMouseCapture();
    }

    private void AnimateTo(double yaw, double pitch)
    {
        double fromYaw = _yaw, fromPitch = _pitch;
        var start = DateTime.UtcNow;
        void Step(object? s, EventArgs e)
        {
            var t = Math.Min(1, (DateTime.UtcNow - start).TotalSeconds / 0.5);
            var eased = t * t * (3 - 2 * t);
            _yaw = fromYaw + (yaw - fromYaw) * eased;
            _pitch = fromPitch + (pitch - fromPitch) * eased;
            Orient();
            if (t >= 1) CompositionTarget.Rendering -= Step;
        }
        CompositionTarget.Rendering += Step;
    }
}

/// <summary>One shape's model, parsed once (in the background) and shared, frozen, by every view showing it.</summary>
public sealed class ModelData
{
    public sealed class Node
    {
        public string Name = "";
        public Matrix3D Local = Matrix3D.Identity;
        public MeshGeometry3D? Mesh;
        public string Material = "";
        public bool DoubleSided;
        public List<Node> Children = [];
    }

    public sealed record Surface(Color Diffuse, double Metallic, double Roughness, Color? Emissive, double Opacity);

    public Node Root { get; init; } = new();
    public Dictionary<string, Surface> Surfaces { get; init; } = [];
}

public static class ModelLibrary
{
    private static readonly Dictionary<PrinterShape, Task<ModelData?>> Loads = [];

    /// <summary>Starts parsing a model on a worker thread, so the panel doesn't wait for it later.</summary>
    public static void Preload(PrinterShape shape)
    {
        lock (Loads)
        {
            if (!Loads.ContainsKey(shape)) Loads[shape] = Task.Run(() => Parse(shape));
        }
    }

    public static ModelData? Load(PrinterShape shape)
    {
        Preload(shape);
        Task<ModelData?> task;
        lock (Loads) task = Loads[shape];
        return task.GetAwaiter().GetResult();
    }

    private static ModelData? Parse(PrinterShape shape)
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Printers.{shape.AssetName()}.mesh.json.gz");
            if (stream is null) return null;
            using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            using var json = JsonDocument.Parse(gzip);
            var surfaces = new Dictionary<string, ModelData.Surface>();
            foreach (var material in json.RootElement.GetProperty("materials").EnumerateObject())
            {
                var m = material.Value;
                Color Linear(string key, Color fallback)
                {
                    if (!m.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Array) return fallback;
                    var c = v.EnumerateArray().Select(x => x.GetDouble()).ToArray();
                    // USD colours are linear; the screen wants sRGB.
                    static byte S(double x) => (byte)Math.Round(Math.Clamp(Math.Pow(Math.Clamp(x, 0, 1), 1 / 2.2), 0, 1) * 255);
                    return Color.FromRgb(S(c[0]), S(c[1]), S(c[2]));
                }
                double Number(string key, double fallback) => m.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;
                Color? emissive = m.TryGetProperty("emissiveColor", out var e) && e.EnumerateArray().Any(x => x.GetDouble() > 0) ? Linear("emissiveColor", Colors.Black) : null;
                surfaces[material.Name] = new ModelData.Surface(Linear("diffuseColor", Colors.Gray), Number("metallic", 0), Number("roughness", 0.5), emissive, Number("opacity", 1));
            }
            return new ModelData { Root = ParseNode(json.RootElement.GetProperty("root")), Surfaces = surfaces };
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private static ModelData.Node ParseNode(JsonElement element)
    {
        var node = new ModelData.Node { Name = element.GetProperty("name").GetString() ?? "" };
        if (element.TryGetProperty("m", out var m))
        {
            var v = m.EnumerateArray().Select(x => x.GetDouble()).ToArray();
            node.Local = new Matrix3D(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11], v[12], v[13], v[14], v[15]);
        }
        if (element.TryGetProperty("mesh", out var meshElement))
        {
            var p = meshElement.GetProperty("p").EnumerateArray().Select(x => x.GetDouble()).ToArray();
            var mesh = new MeshGeometry3D();
            var positions = new Point3DCollection(p.Length / 3);
            for (var i = 0; i + 2 < p.Length; i += 3) positions.Add(new Point3D(p[i], p[i + 1], p[i + 2]));
            mesh.Positions = positions;
            if (meshElement.TryGetProperty("n", out var n))
            {
                var nv = n.EnumerateArray().Select(x => x.GetDouble()).ToArray();
                var normals = new Vector3DCollection(nv.Length / 3);
                for (var i = 0; i + 2 < nv.Length; i += 3) normals.Add(new Vector3D(nv[i], nv[i + 1], nv[i + 2]));
                mesh.Normals = normals;
            }
            var indices = new Int32Collection(meshElement.GetProperty("i").EnumerateArray().Select(x => x.GetInt32()));
            mesh.TriangleIndices = indices;
            mesh.Freeze();
            node.Mesh = mesh;
            node.Material = meshElement.TryGetProperty("mat", out var mat) ? mat.GetString() ?? "" : "";
            node.DoubleSided = meshElement.TryGetProperty("double", out var d) && d.GetBoolean();
        }
        if (element.TryGetProperty("children", out var children))
        {
            foreach (var child in children.EnumerateArray()) node.Children.Add(ParseNode(child));
        }
        return node;
    }
}

/// <summary>One view's live copy of a model: its own materials and transforms over the shared meshes.</summary>
internal sealed class ModelScene
{
    private sealed class Built
    {
        public required ModelData.Node Data;
        public required Model3DGroup Group;
        public required TranslateTransform3D Offset;
        public Matrix3D World;
        /// <summary>The parent's transform to model space, for offsets given in model space.</summary>
        public Matrix3D ParentWorld;
        public List<(GeometryModel3D Model, Material? Front, Material? Back)> Geometry = [];
        public List<Built> Children = [];
    }

    public Model3DGroup Root { get; } = new();
    public double Radius { get; }

    private readonly Model3DGroup _content = new();
    private readonly Model3DGroup _glass = new();
    private readonly Dictionary<string, Built> _byName = [];
    private readonly List<Built> _parts = [];
    private readonly SolidColorBrush _partBrush = new(Palette.Accent);
    private readonly List<(Built Root, SolidColorBrush Filament)> _spools = [];
    private readonly List<EmissiveMaterial> _lampEmission = [];
    private readonly SpotLight? _lamp;
    private readonly double _partHeight;
    private readonly Built? _ams, _carriage, _gantry, _bed, _head;
    private readonly ModelData _data;

    public ModelScene(ModelData data)
    {
        _data = data;
        var top = Build(data.Root, Matrix3D.Identity);
        _content.Children.Add(top.Group);
        // Glass last: WPF blends transparent surfaces only over what's already drawn.
        _content.Children.Add(_glass);

        var bounds = _content.Bounds;
        var centre = new Vector3D(bounds.X + bounds.SizeX / 2, bounds.Y + bounds.SizeY / 2, bounds.Z + bounds.SizeZ / 2);
        Root.Children.Add(_content);
        Root.Transform = new TranslateTransform3D(-centre);
        Radius = Math.Max(bounds.SizeX, Math.Max(bounds.SizeY, bounds.SizeZ)) * 0.62;
        AddFloorShadow(bounds);

        for (var i = 0; i < 64; i++)
        {
            if (_byName.TryGetValue($"Part_{i:00}", out var part)) _parts.Add(part);
        }
        var partMaterial = MakeMaterial(_partBrush, 0, 0.42);
        foreach (var part in _parts) Retint(part, partMaterial);
        if (_parts.Count > 0)
        {
            var first = Bounds(_parts[0]);
            var last = Bounds(_parts[^1]);
            _partHeight = last.Y + last.SizeY - first.Y;
        }

        _ams = Find("AMS");
        for (var i = 0; i < 4; i++)
        {
            if (Find($"Spool_{i}") is not { } spool || Find($"Filament_{i}") is not { } filament) continue;
            var brush = new SolidColorBrush(Colors.White);
            Retint(filament, MakeMaterial(brush, 0, 0.35));
            _spools.Add((spool, brush));
        }
        _carriage = Find("BedCarriage");
        _gantry = Find("Gantry");
        _bed = Find("Bed");
        _head = Find("Toolhead");

        if (Find("Lamp") is { } lamp)
        {
            var position = lamp.World.Transform(new Point3D(0, 0, 0));
            // A warm, wide spot pointing down from the ceiling.
            _lamp = new SpotLight(Colors.Black, position, new Vector3D(0, -1, 0), 120, 50)
            {
                Range = 0.6, ConstantAttenuation = 0.6, LinearAttenuation = 1.5, QuadraticAttenuation = 0,
            };
            _content.Children.Add(_lamp);
        }
    }

    private Built? Find(string name) => _byName.GetValueOrDefault(name);

    private Built Build(ModelData.Node node, Matrix3D parentWorld)
    {
        var world = node.Local * parentWorld;
        var offset = new TranslateTransform3D();
        var group = new Model3DGroup { Transform = new Transform3DGroup { Children = { new MatrixTransform3D(node.Local), offset } } };
        var built = new Built { Data = node, Group = group, Offset = offset, World = world, ParentWorld = parentWorld };
        if (node.Mesh is not null)
        {
            var surface = _data.Surfaces.GetValueOrDefault(node.Material);
            var isGlass = node.Material.StartsWith("BB_Glass", StringComparison.Ordinal) || node.Material.StartsWith("BB_Smoke", StringComparison.Ordinal);
            Material material;
            if (isGlass)
            {
                // Node opacity, as the macOS app does: the export carries no transmission.
                var smoke = node.Material.StartsWith("BB_Smoke", StringComparison.Ordinal);
                var tint = smoke ? Color.FromArgb(140, 5, 5, 6) : Color.FromArgb(41, 20, 20, 22);
                material = new MaterialGroup { Children = { new DiffuseMaterial(new SolidColorBrush(tint)), new SpecularMaterial(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 60) } };
            }
            else
            {
                material = MakeMaterial(new SolidColorBrush(surface?.Diffuse ?? Colors.Gray), surface?.Metallic ?? 0, surface?.Roughness ?? 0.5);
                if (surface?.Emissive is { } emissive && node.Material.StartsWith("BB_Lamp", StringComparison.Ordinal))
                {
                    var lampGlow = new EmissiveMaterial(new SolidColorBrush(Colors.Black));
                    _lampEmission.Add(lampGlow);
                    ((MaterialGroup)material).Children.Add(lampGlow);
                }
                else if (surface?.Emissive is { } glow)
                {
                    ((MaterialGroup)material).Children.Add(new EmissiveMaterial(new SolidColorBrush(glow)));
                }
            }
            var back = isGlass || node.DoubleSided ? material : null;
            var model = new GeometryModel3D(node.Mesh, material) { BackMaterial = back };
            if (isGlass)
            {
                // Transparent surfaces are drawn after everything else, in their place in the model.
                var placed = new GeometryModel3D(node.Mesh, material) { BackMaterial = back, Transform = new MatrixTransform3D(world) };
                _glass.Children.Add(placed);
                built.Geometry.Add((placed, material, back));
            }
            else
            {
                group.Children.Add(model);
                built.Geometry.Add((model, material, back));
            }
        }
        foreach (var child in node.Children)
        {
            var c = Build(child, world);
            built.Children.Add(c);
            group.Children.Add(c.Group);
        }
        _byName.TryAdd(node.Name, built);
        return built;
    }

    private static MaterialGroup MakeMaterial(Brush diffuse, double metallic, double roughness)
    {
        var group = new MaterialGroup();
        group.Children.Add(new DiffuseMaterial(diffuse));
        var shine = (1 - roughness) * (0.25 + 0.6 * metallic);
        if (shine > 0.05)
        {
            var level = (byte)Math.Round(Math.Clamp(shine, 0, 1) * 255);
            group.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromRgb(level, level, level)), 6 + (1 - roughness) * 70));
        }
        return group;
    }

    private static void Retint(Built node, Material material)
    {
        for (var i = 0; i < node.Geometry.Count; i++)
        {
            var (model, _, back) = node.Geometry[i];
            model.Material = material;
            if (back is not null) model.BackMaterial = material;
            node.Geometry[i] = (model, material, back is null ? null : material);
        }
        foreach (var child in node.Children) Retint(child, material);
    }

    private static void SetHidden(Built node, bool hidden)
    {
        foreach (var (model, front, back) in node.Geometry)
        {
            model.Material = hidden ? null : front;
            model.BackMaterial = hidden ? null : back;
        }
        foreach (var child in node.Children) SetHidden(child, hidden);
    }

    private static Rect3D Bounds(Built node)
    {
        var rect = Rect3D.Empty;
        void Walk(Built b)
        {
            if (b.Data.Mesh is { } mesh)
            {
                foreach (var p in mesh.Positions) rect.Union(b.World.Transform(p));
            }
            foreach (var c in b.Children) Walk(c);
        }
        Walk(node);
        return rect;
    }

    private void AddFloorShadow(Rect3D bounds)
    {
        var y = bounds.Y + 0.001;
        var cx = bounds.X + bounds.SizeX / 2;
        var cz = bounds.Z + bounds.SizeZ / 2;
        var r = Math.Max(bounds.SizeX, bounds.SizeZ) * 0.85;
        var mesh = new MeshGeometry3D
        {
            Positions = [new(cx - r, y, cz - r), new(cx + r, y, cz - r), new(cx + r, y, cz + r), new(cx - r, y, cz + r)],
            TextureCoordinates = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)],
            TriangleIndices = [0, 2, 1, 0, 3, 2],
        };
        var brush = new RadialGradientBrush(
            [new GradientStop(Color.FromArgb(130, 0, 0, 0), 0), new GradientStop(Color.FromArgb(60, 0, 0, 0), 0.35), new GradientStop(Colors.Transparent, 0.62)]);
        // A soft contact shadow, standing in for SceneKit's shadow-catching floor.
        _glass.Children.Insert(0, new GeometryModel3D(mesh, new DiffuseMaterial(brush)));
    }

    /// <summary>Shows a state; <paramref name="time"/> (seconds since printing started) moves the toolhead, null rests it.</summary>
    public void Apply(PrinterArt art, double? time)
    {
        var fraction = art.ShownFraction;
        var visible = (int)Math.Ceiling(fraction * _parts.Count);
        for (var i = 0; i < _parts.Count; i++) SetHidden(_parts[i], i >= visible);
        _partBrush.Color = art.Filament ?? Palette.Accent;

        if (_ams is not null) SetHidden(_ams, art.Spools.Count == 0);
        for (var i = 0; i < _spools.Count; i++)
        {
            var color = i < art.Spools.Count ? art.Spools[i] : null;
            if (art.Spools.Count > 0) SetHidden(_spools[i].Root, color is null);
            if (color is { } c) _spools[i].Filament.Color = c;
        }

        var lit = art.LightOn && !art.Dimmed;
        if (_lamp is not null) _lamp.Color = lit ? Color.FromRgb(255, 237, 209) : Colors.Black;
        foreach (var glow in _lampEmission) ((SolidColorBrush)glow.Brush).Color = lit ? Color.FromRgb(255, 242, 219) : Colors.Black;

        // The bed (CoreXY) drops, or the gantry (bed slinger) rises, by what has been printed.
        var rest = _partHeight * (1 - fraction);
        Move(_carriage, new Vector3D(0, rest, 0));
        Move(_gantry, new Vector3D(0, -rest, 0));

        if (time is double t)
        {
            // The toolhead traces the print's outline; on bed slingers the bed slides under it.
            Move(_head, HeadPath(t));
            Move(_bed, BedPath(t));
        }
        else
        {
            Move(_head, new Vector3D());
            Move(_bed, new Vector3D());
        }
    }

    /// <summary>Offsets a node by a vector given in the model's own space.</summary>
    private static void Move(Built? node, Vector3D offset)
    {
        if (node is null) return;
        // The offset applies after the node's own transform, in its parent's space.
        var toParent = node.ParentWorld;
        if (toParent.HasInverse) toParent.Invert();
        var local = toParent.Transform(offset);
        node.Offset.OffsetX = local.X;
        node.Offset.OffsetY = local.Y;
        node.Offset.OffsetZ = local.Z;
    }

    private static readonly (Vector3D To, double Duration)[] HeadSteps =
    [
        (new(0.028, 0, 0), 0.7), (new(0.028, 0, 0.018), 0.45), (new(-0.028, 0, 0.018), 0.9), (new(-0.028, 0, 0), 0.45), (new(0, 0, 0), 0.5),
    ];

    private static readonly (Vector3D To, double Duration)[] BedSteps = [(new(0, 0, 0.022), 1.1), (new(0, 0, -0.022), 1.4), (new(0, 0, 0), 0.6)];

    private static Vector3D HeadPath(double t) => Along(HeadSteps, t);
    private static Vector3D BedPath(double t) => Along(BedSteps, t);

    private static Vector3D Along((Vector3D To, double Duration)[] steps, double t)
    {
        var total = steps.Sum(s => s.Duration);
        t %= total;
        var from = steps[^1].To;
        foreach (var (to, duration) in steps)
        {
            if (t <= duration)
            {
                var f = t / duration;
                f = f * f * (3 - 2 * f);  // ease in and out
                return from + (to - from) * f;
            }
            t -= duration;
            from = to;
        }
        return from;
    }
}
