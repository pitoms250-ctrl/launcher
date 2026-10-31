using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Serilog;
using Launcher.UI.ViewModels;

namespace Launcher.UI.Views;

public partial class SkinLobbyView : UserControl
{
    public static readonly DependencyProperty SkinProperty = DependencyProperty.Register(
        nameof(Skin), typeof(BitmapSource), typeof(SkinLobbyView),
        new PropertyMetadata(null, OnSkinChanged));

    private readonly ModelVisual3D _modelVisual = new();
    private BitmapSource? _renderSkin;

    public BitmapSource? Skin
    {
        get => (BitmapSource?)GetValue(SkinProperty);
        set => SetValue(SkinProperty, value);
    }

    public SkinLobbyView()
    {
        InitializeComponent();
        _renderSkin = LoadFallbackSkin();
        DataContextChanged += (_, _) => SyncSkinFromViewModel();
        Loaded += (_, _) =>
        {
            SyncSkinFromViewModel();
            RebuildModelSafely();
        };
        SkinViewport.Children.Add(new ModelVisual3D
        {
            Content = new Model3DGroup
            {
                Children =
                {
                    new AmbientLight(Color.FromRgb(165, 190, 180)),
                    new DirectionalLight(Colors.White, new Vector3D(-0.7, -1, -2)),
                    new DirectionalLight(Color.FromRgb(120, 255, 195), new Vector3D(1, 0.3, 1)),
                }
            }
        });
        SkinViewport.Children.Add(_modelVisual);
    }

    private static void OnSkinChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is SkinLobbyView view)
        {
            Log.Information("LobbySkin: viewport received texture. HasSkin={HasSkin}, IsLoaded={IsLoaded}",
                args.NewValue is BitmapSource, view.IsLoaded);
            if (args.NewValue is BitmapSource skin) view._renderSkin = skin;
            if (view.IsLoaded && view._renderSkin is not null) view.RebuildModelSafely();
        }
    }

    private void SyncSkinFromViewModel()
    {
        if (DataContext is not MainViewModel viewModel) return;
        Log.Information("LobbySkin: view model attached. PlayerSkinPresent={PlayerSkinPresent}", viewModel.PlayerSkin is not null);
        if (viewModel.PlayerSkin is { } skin) Skin = skin;
    }

    private void RebuildModelSafely()
    {
        try { RebuildModel(); }
        catch (Exception ex) { Log.Error(ex, "LobbySkin: 3D model render failed"); }
    }

    private void RebuildModel()
    {
        var skin = _renderSkin;
        if (skin is null)
        {
            Log.Warning("LobbySkin: fallback and player textures are unavailable.");
            return;
        }

        var root = new Model3DGroup();
        AddPlayer(root, skin);
        Log.Information("LobbySkin: built 3D player. TextureWidth={TextureWidth}, TextureHeight={TextureHeight}, FaceCount={FaceCount}",
            skin.PixelWidth, skin.PixelHeight, root.Children.Count);

        var rotation = new AxisAngleRotation3D(new Vector3D(0, 1, 0), -11);
        root.Transform = new RotateTransform3D(rotation);
        rotation.BeginAnimation(AxisAngleRotation3D.AngleProperty, new DoubleAnimation(-11, 11,
            new Duration(TimeSpan.FromSeconds(7)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });

        _modelVisual.Content = root;
    }

    private static BitmapSource LoadFallbackSkin()
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri("pack://application:,,,/Assets/default_skin.png", UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static void AddPlayer(Model3DGroup root, BitmapSource skin)
    {
        var head = new Point3D(0, 12, 0);
        var torso = new Point3D(0, 2, 0);
        var rightArm = new Point3D(-6, 2, 0);
        var leftArm = new Point3D(6, 2, 0);
        var rightLeg = new Point3D(-2, -10, 0);
        var leftLeg = new Point3D(2, -10, 0);

        AddCuboid(root, skin, head, new Size3D(8, 8, 8), new UvFaces(
            new Int32Rect(8, 0, 8, 8), new Int32Rect(16, 0, 8, 8),
            new Int32Rect(0, 8, 8, 8), new Int32Rect(8, 8, 8, 8),
            new Int32Rect(16, 8, 8, 8), new Int32Rect(24, 8, 8, 8)));
        AddCuboid(root, skin, head, new Size3D(8.35, 8.35, 8.35), new UvFaces(
            new Int32Rect(40, 0, 8, 8), new Int32Rect(48, 0, 8, 8),
            new Int32Rect(32, 8, 8, 8), new Int32Rect(40, 8, 8, 8),
            new Int32Rect(48, 8, 8, 8), new Int32Rect(56, 8, 8, 8)), skipTransparent: true);

        AddCuboid(root, skin, torso, new Size3D(8, 12, 4), new UvFaces(
            new Int32Rect(20, 16, 8, 4), new Int32Rect(28, 16, 8, 4),
            new Int32Rect(16, 20, 4, 12), new Int32Rect(20, 20, 8, 12),
            new Int32Rect(28, 20, 4, 12), new Int32Rect(32, 20, 8, 12)));
        AddCuboid(root, skin, torso, new Size3D(8.3, 12.3, 4.3), new UvFaces(
            new Int32Rect(20, 32, 8, 4), new Int32Rect(28, 32, 8, 4),
            new Int32Rect(16, 36, 4, 12), new Int32Rect(20, 36, 8, 12),
            new Int32Rect(28, 36, 4, 12), new Int32Rect(32, 36, 8, 12)), skipTransparent: true);

        AddLimb(root, skin, rightArm, new Int32Rect(44, 16, 4, 4), new Int32Rect(48, 16, 4, 4),
            new Int32Rect(40, 20, 4, 12), new Int32Rect(44, 20, 4, 12), new Int32Rect(48, 20, 4, 12), new Int32Rect(52, 20, 4, 12));
        AddLimb(root, skin, leftArm, new Int32Rect(36, 48, 4, 4), new Int32Rect(40, 48, 4, 4),
            new Int32Rect(32, 52, 4, 12), new Int32Rect(36, 52, 4, 12), new Int32Rect(40, 52, 4, 12), new Int32Rect(44, 52, 4, 12));
        AddLimb(root, skin, rightLeg, new Int32Rect(4, 16, 4, 4), new Int32Rect(8, 16, 4, 4),
            new Int32Rect(0, 20, 4, 12), new Int32Rect(4, 20, 4, 12), new Int32Rect(8, 20, 4, 12), new Int32Rect(12, 20, 4, 12));
        AddLimb(root, skin, leftLeg, new Int32Rect(20, 48, 4, 4), new Int32Rect(24, 48, 4, 4),
            new Int32Rect(16, 52, 4, 12), new Int32Rect(20, 52, 4, 12), new Int32Rect(24, 52, 4, 12), new Int32Rect(28, 52, 4, 12));

        AddCuboid(root, skin, new Point3D(-6, 2, 0), new Size3D(4.3, 12.3, 4.3), new UvFaces(
            new Int32Rect(44, 32, 4, 4), new Int32Rect(48, 32, 4, 4),
            new Int32Rect(40, 36, 4, 12), new Int32Rect(44, 36, 4, 12),
            new Int32Rect(48, 36, 4, 12), new Int32Rect(52, 36, 4, 12)), skipTransparent: true);
        AddCuboid(root, skin, new Point3D(6, 2, 0), new Size3D(4.3, 12.3, 4.3), new UvFaces(
            new Int32Rect(52, 48, 4, 4), new Int32Rect(56, 48, 4, 4),
            new Int32Rect(48, 52, 4, 12), new Int32Rect(52, 52, 4, 12),
            new Int32Rect(56, 52, 4, 12), new Int32Rect(60, 52, 4, 12)), skipTransparent: true);
        AddCuboid(root, skin, new Point3D(-2, -10, 0), new Size3D(4.3, 12.3, 4.3), new UvFaces(
            new Int32Rect(4, 32, 4, 4), new Int32Rect(8, 32, 4, 4),
            new Int32Rect(0, 36, 4, 12), new Int32Rect(4, 36, 4, 12),
            new Int32Rect(8, 36, 4, 12), new Int32Rect(12, 36, 4, 12)), skipTransparent: true);
        AddCuboid(root, skin, new Point3D(2, -10, 0), new Size3D(4.3, 12.3, 4.3), new UvFaces(
            new Int32Rect(4, 48, 4, 4), new Int32Rect(8, 48, 4, 4),
            new Int32Rect(0, 52, 4, 12), new Int32Rect(4, 52, 4, 12),
            new Int32Rect(8, 52, 4, 12), new Int32Rect(12, 52, 4, 12)), skipTransparent: true);
    }

    private static void AddLimb(Model3DGroup root, BitmapSource skin, Point3D center,
        Int32Rect top, Int32Rect bottom, Int32Rect right, Int32Rect front, Int32Rect left, Int32Rect back)
    {
        var uv = new UvFaces(top, bottom, right, front, left, back);
        AddCuboid(root, skin, center, new Size3D(4, 12, 4), uv);
    }

    private static void AddCuboid(Model3DGroup root, BitmapSource skin, Point3D center, Size3D size,
        UvFaces uv, bool skipTransparent = false)
    {
        var x0 = center.X - size.X / 2;
        var x1 = center.X + size.X / 2;
        var y0 = center.Y - size.Y / 2;
        var y1 = center.Y + size.Y / 2;
        var z0 = center.Z - size.Z / 2;
        var z1 = center.Z + size.Z / 2;

        AddFace(root, skin, uv.Front, skipTransparent,
            new Point3D(x0, y0, z1), new Point3D(x1, y0, z1), new Point3D(x1, y1, z1), new Point3D(x0, y1, z1));
        AddFace(root, skin, uv.Back, skipTransparent,
            new Point3D(x1, y0, z0), new Point3D(x0, y0, z0), new Point3D(x0, y1, z0), new Point3D(x1, y1, z0));
        AddFace(root, skin, uv.Right, skipTransparent,
            new Point3D(x1, y0, z1), new Point3D(x1, y0, z0), new Point3D(x1, y1, z0), new Point3D(x1, y1, z1));
        AddFace(root, skin, uv.Left, skipTransparent,
            new Point3D(x0, y0, z0), new Point3D(x0, y0, z1), new Point3D(x0, y1, z1), new Point3D(x0, y1, z0));
        AddFace(root, skin, uv.Top, skipTransparent,
            new Point3D(x0, y1, z1), new Point3D(x1, y1, z1), new Point3D(x1, y1, z0), new Point3D(x0, y1, z0));
        AddFace(root, skin, uv.Bottom, skipTransparent,
            new Point3D(x0, y0, z0), new Point3D(x1, y0, z0), new Point3D(x1, y0, z1), new Point3D(x0, y0, z1));
    }

    private static void AddFace(Model3DGroup root, BitmapSource skin, Int32Rect crop, bool skipTransparent,
        Point3D p0, Point3D p1, Point3D p2, Point3D p3)
    {
        if (crop.Width <= 0 || crop.Height <= 0 || crop.X + crop.Width > skin.PixelWidth || crop.Y + crop.Height > skin.PixelHeight)
            return;

        var texture = new CroppedBitmap(skin, crop);
        if (skipTransparent && IsFullyTransparent(texture)) return;

        var brush = new ImageBrush(UpscaleNearest(texture, 16))
        {
            Stretch = Stretch.Fill,
            TileMode = TileMode.None,
        };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);

        var mesh = new MeshGeometry3D
        {
            Positions = new Point3DCollection { p0, p1, p2, p3 },
            TextureCoordinates = new PointCollection
            {
                new Point(0, 1), new Point(1, 1), new Point(1, 0), new Point(0, 0),
            },
            TriangleIndices = new Int32Collection { 0, 1, 2, 0, 2, 3 },
        };
        var material = new DiffuseMaterial(brush);
        root.Children.Add(new GeometryModel3D(mesh, material) { BackMaterial = material });
    }

    /// <summary>
    /// Грани скина — всего 4×4…8×8 пикселей; в 3D при растяжении на сотни пикселей они размываются сглаживанием.
    /// Увеличиваем грань целыми блоками заранее — тогда пиксели остаются резкими квадратами.
    /// </summary>
    private static BitmapSource UpscaleNearest(BitmapSource source, int factor)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int w = converted.PixelWidth, h = converted.PixelHeight;
        var src = new byte[w * h * 4];
        converted.CopyPixels(src, w * 4, 0);

        int bigW = w * factor, bigH = h * factor;
        var dst = new byte[bigW * bigH * 4];
        for (var y = 0; y < bigH; y++)
            for (var x = 0; x < bigW; x++)
                Buffer.BlockCopy(src, ((y / factor) * w + x / factor) * 4, dst, (y * bigW + x) * 4, 4);

        var big = BitmapSource.Create(bigW, bigH, 96, 96, PixelFormats.Bgra32, null, dst, bigW * 4);
        big.Freeze();
        return big;
    }

    private static bool IsFullyTransparent(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        for (var index = 3; index < pixels.Length; index += 4)
            if (pixels[index] != 0) return false;
        return true;
    }

    private sealed record UvFaces(Int32Rect Top, Int32Rect Bottom, Int32Rect Right, Int32Rect Front, Int32Rect Left, Int32Rect Back);
}