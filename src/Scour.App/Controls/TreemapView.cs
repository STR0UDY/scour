using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Scour.App.Helpers;
using Scour.Core.Analysis;
using Windows.Foundation;
using Windows.UI;

namespace Scour.App.Controls;

public sealed class TreemapNodeEventArgs(int node, Point position) : EventArgs
{
    public int Node { get; } = node;
    public Point Position { get; } = position;
}

/// <summary>
/// A squarified treemap of one folder, drawn with Win2D. Folders get a header strip and nest
/// their contents; files are coloured by kind. Click a folder to drill in; right-click for actions.
/// </summary>
public sealed partial class TreemapView : Grid
{
    private readonly CanvasControl _canvas = new();
    private readonly Border _tip;
    private readonly TextBlock _tipText = new() { FontSize = 12, TextWrapping = TextWrapping.NoWrap };
    private List<TreemapCell> _cells = [];
    private DiskTree? _tree;
    private int _node;
    private int _hover = -1;
    private Size _laidOutFor;

    public TreemapView()
    {
        _canvas.Draw += OnDraw;
        Children.Add(_canvas);
        _tip = new Border
        {
            Child = _tipText,
            Padding = new Thickness(10, 6, 10, 6),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(8),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        Children.Add(_tip);

        PointerMoved += OnPointerMoved;
        PointerExited += (_, _) => SetHover(-1);
        Tapped += OnTapped;
        RightTapped += OnRightTapped;
        SizeChanged += (_, _) => Relayout();
        Unloaded += (_, _) => _canvas.RemoveFromVisualTree();
        ActualThemeChanged += (_, _) => _canvas.Invalidate();
    }

    /// <summary>A folder was clicked: drill into it.</summary>
    public event EventHandler<TreemapNodeEventArgs>? NodeInvoked;

    /// <summary>Right-click on a cell, with the point for a context menu.</summary>
    public event EventHandler<TreemapNodeEventArgs>? NodeContextRequested;

    public void Show(DiskTree? tree, int node)
    {
        _tree = tree;
        _node = node;
        _hover = -1;
        _laidOutFor = default;
        Relayout();
    }

    private void Relayout()
    {
        var size = new Size(ActualWidth, ActualHeight);
        if (_tree is null || size.Width < 10 || size.Height < 10)
        {
            _cells = [];
        }
        else if (size != _laidOutFor || _cells.Count == 0)
        {
            _cells = Treemap.Layout(_tree, _node, new TreemapRect(0, 0, size.Width, size.Height), maxDepth: 6);
            _laidOutFor = size;
        }
        _canvas.Invalidate();
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        var dark = ActualTheme == ElementTheme.Dark;
        ds.Clear(dark ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 243, 243, 243));
        if (_tree is null)
            return;

        using var headerFormat = new CanvasTextFormat { FontSize = 11, WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character };
        using var labelFormat = new CanvasTextFormat { FontSize = 11, WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character };
        var border = dark ? Color.FromArgb(255, 20, 20, 20) : Color.FromArgb(255, 255, 255, 255);

        foreach (var cell in _cells)
        {
            var r = new Rect(cell.Rect.X, cell.Rect.Y, cell.Rect.Width, cell.Rect.Height);
            var fill = cell.IsDirectory ? FolderColor(cell.Depth, dark) : FileColor(_tree.Name(cell.Node), cell.Depth, dark);
            if (cell.Node == _hover)
                fill = Lighten(fill, dark ? 0.25 : -0.12);
            ds.FillRectangle(r, fill);
            ds.DrawRectangle(r, border, 1);

            var text = dark ? Colors.White : Color.FromArgb(255, 20, 20, 20);
            if (cell.IsDirectory && cell.HasHeader)
            {
                ds.DrawText($"{_tree.Name(cell.Node)}  {Format.Bytes(_tree.Size(cell.Node))}",
                    new Rect(r.X + 5, r.Y + 1, Math.Max(0, r.Width - 10), 15), text, headerFormat);
            }
            else if (!cell.IsDirectory && r.Width > 60 && r.Height > 30)
            {
                ds.DrawText(_tree.Name(cell.Node), new Rect(r.X + 4, r.Y + 3, r.Width - 8, 15), text, labelFormat);
                ds.DrawText(Format.Bytes(_tree.Size(cell.Node)), new Rect(r.X + 4, r.Y + 17, r.Width - 8, 15),
                    Color.FromArgb(200, text.R, text.G, text.B), labelFormat);
            }
        }
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(this).Position;
        SetHover(Treemap.HitTest(_cells, p.X, p.Y)?.Node ?? -1);
    }

    private void SetHover(int node)
    {
        if (node == _hover)
            return;
        _hover = node;
        if (node < 0 || _tree is null)
        {
            _tip.Visibility = Visibility.Collapsed;
        }
        else
        {
            _tipText.Text = $"{_tree.FullPath(node)}   {Format.Bytes(_tree.Size(node))}" + (_tree.IsDirectory(node) ? $" · {Format.Files(_tree.FileCount(node))}" : "");
            _tip.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AcrylicInAppFillColorDefaultBrush"];
            _tip.Visibility = Visibility.Visible;
        }
        _canvas.Invalidate();
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        var p = e.GetPosition(this);
        if (Treemap.HitTest(_cells, p.X, p.Y) is not { } cell || _tree is null)
            return;
        // Clicking a file drills into the folder that holds it.
        var target = cell.IsDirectory ? cell.Node : _tree.Parent(cell.Node);
        // Drill one level at a time: the outermost folder under the current view on the way to target.
        var chain = _tree.Ancestry(target);
        var index = chain.ToList().IndexOf(_node);
        var next = index >= 0 && index + 1 < chain.Count ? chain[index + 1] : target;
        if (next != _node)
            NodeInvoked?.Invoke(this, new TreemapNodeEventArgs(next, default));
    }

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var p = e.GetPosition(this);
        if (Treemap.HitTest(_cells, p.X, p.Y) is { } cell)
            NodeContextRequested?.Invoke(this, new TreemapNodeEventArgs(cell.Node, p));
    }

    // ---------------------------------------------------------------- Colours

    private static Color FolderColor(int depth, bool dark)
    {
        var v = dark ? (byte)Math.Min(90, 44 + depth * 8) : (byte)Math.Max(170, 226 - depth * 10);
        return Color.FromArgb(255, v, (byte)Math.Min(255, v + 4), (byte)Math.Min(255, v + 6));
    }

    private static Color FileColor(string name, int depth, bool dark)
    {
        var (r, g, b) = Kind(Path.GetExtension(name).ToLowerInvariant()) switch
        {
            FileKind.Video => (0xE1, 0x1D, 0x48),
            FileKind.Image => (0xF5, 0x9E, 0x0B),
            FileKind.Audio => (0xA8, 0x55, 0xF7),
            FileKind.Archive => (0x63, 0x66, 0xF1),
            FileKind.Program => (0x10, 0xB9, 0x81),
            FileKind.Document => (0x0E, 0xA5, 0xE9),
            FileKind.Code => (0x84, 0xCC, 0x16),
            FileKind.Data => (0x14, 0xB8, 0xA6),
            _ => (0x94, 0xA3, 0xB8),
        };
        var c = Color.FromArgb(255, (byte)r, (byte)g, (byte)b);
        return Lighten(c, dark ? -0.15 - depth * 0.03 : 0.2 + depth * 0.04);
    }

    private static Color Lighten(Color c, double amount)
    {
        byte Mix(byte v) => (byte)Math.Clamp(amount >= 0 ? v + (255 - v) * amount : v * (1 + amount), 0, 255);
        return Color.FromArgb(c.A, Mix(c.R), Mix(c.G), Mix(c.B));
    }

    private enum FileKind { Other, Video, Image, Audio, Archive, Program, Document, Code, Data }

    private static readonly Dictionary<string, FileKind> Kinds = Build();

    private static Dictionary<string, FileKind> Build()
    {
        var map = new Dictionary<string, FileKind>(StringComparer.OrdinalIgnoreCase);
        void Add(FileKind kind, params string[] exts)
        {
            foreach (var e in exts)
                map["." + e] = kind;
        }
        Add(FileKind.Video, "mp4", "mkv", "mov", "avi", "wmv", "webm", "m4v", "ts");
        Add(FileKind.Image, "jpg", "jpeg", "png", "gif", "bmp", "webp", "heic", "tif", "tiff", "psd", "raw", "cr2", "nef", "dds", "tga");
        Add(FileKind.Audio, "mp3", "wav", "flac", "aac", "ogg", "m4a", "wma", "opus");
        Add(FileKind.Archive, "zip", "7z", "rar", "tar", "gz", "xz", "iso", "cab", "msi", "pak", "pack", "bz2", "zst", "vhdx", "vhd", "img");
        Add(FileKind.Program, "exe", "dll", "sys", "lib", "pdb", "so", "node", "rlib", "rmeta", "o", "obj", "pyd", "bin");
        Add(FileKind.Document, "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "md", "odt", "rtf", "epub");
        Add(FileKind.Code, "cs", "js", "ts", "tsx", "jsx", "py", "rs", "go", "java", "cpp", "c", "h", "hpp", "json", "xml", "yaml", "yml", "html", "css", "map", "lua");
        Add(FileKind.Data, "db", "sqlite", "dat", "log", "mdf", "ldf", "vdi", "vmdk", "dmp", "etl", "cache", "blob");
        return map;
    }

    private static FileKind Kind(string ext) => Kinds.GetValueOrDefault(ext, FileKind.Other);
}
