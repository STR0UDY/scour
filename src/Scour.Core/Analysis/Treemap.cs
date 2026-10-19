namespace Scour.Core.Analysis;

public readonly record struct TreemapRect(double X, double Y, double Width, double Height)
{
    public double Area => Width * Height;
    public bool Contains(double x, double y) => x >= X && y >= Y && x < X + Width && y < Y + Height;
}

/// <param name="Node">The <see cref="DiskTree"/> node this cell draws.</param>
/// <param name="Depth">0 for the children of the laid-out node, 1 for grandchildren…</param>
public readonly record struct TreemapCell(int Node, TreemapRect Rect, int Depth, bool IsDirectory, bool HasHeader);

/// <summary>Squarified treemap layout (Bruls, Huijsen and van Wijk), nested for folders.</summary>
public static class Treemap
{
    /// <summary>Rectangles for <paramref name="values"/> (largest first) filling <paramref name="bounds"/>, with aspect ratios near 1.</summary>
    public static List<TreemapRect> Squarify(IReadOnlyList<double> values, TreemapRect bounds)
    {
        var result = new List<TreemapRect>(values.Count);
        var total = values.Sum();
        if (total <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
            return values.Select(_ => new TreemapRect(bounds.X, bounds.Y, 0, 0)).ToList();

        var scale = bounds.Area / total;
        var rect = bounds;
        var row = new List<double>();
        var i = 0;
        while (i < values.Count)
        {
            var v = values[i] * scale;
            var side = Math.Min(rect.Width, rect.Height);
            if (row.Count == 0 || Worst(row, v, side) <= Worst(row, 0, side))
            {
                row.Add(v);
                i++;
            }
            else
            {
                rect = LayoutRow(row, rect, result);
                row.Clear();
            }
        }
        if (row.Count > 0)
            LayoutRow(row, rect, result);
        return result;
    }

    /// <summary>
    /// Lays out a node's children, and their children inside them, down to
    /// <paramref name="maxDepth"/>. Folders big enough get a header strip for their name.
    /// Cells smaller than <paramref name="minSide"/> pixels on either side are dropped.
    /// </summary>
    public static List<TreemapCell> Layout(DiskTree tree, int node, TreemapRect bounds, int maxDepth = 5,
        double minSide = 3, double headerHeight = 16, double padding = 2, int maxCells = 25000)
    {
        var cells = new List<TreemapCell>();
        var pending = new Queue<(int Node, TreemapRect Rect, int Depth)>();
        pending.Enqueue((node, bounds, 0));

        while (pending.Count > 0 && cells.Count < maxCells)
        {
            var (parent, area, depth) = pending.Dequeue();
            var children = tree.Children(parent).Where(c => tree.Size(c) > 0).ToList();
            if (children.Count == 0)
                continue;

            var rects = Squarify(children.Select(c => (double)tree.Size(c)).ToList(), area);
            for (var k = 0; k < children.Count && cells.Count < maxCells; k++)
            {
                var r = rects[k];
                if (r.Width < minSide || r.Height < minSide)
                    continue; // Sorted largest first, but squarify can still yield a thin one; keep going.

                var child = children[k];
                var isDir = tree.IsDirectory(child);
                var header = isDir && r.Height > headerHeight * 2.5 && r.Width > 40;
                cells.Add(new TreemapCell(child, r, depth, isDir, header));

                if (isDir && depth + 1 < maxDepth)
                {
                    var top = header ? headerHeight : padding;
                    var inner = new TreemapRect(r.X + padding, r.Y + top, r.Width - 2 * padding, r.Height - top - padding);
                    if (inner.Width >= minSide * 2 && inner.Height >= minSide * 2)
                        pending.Enqueue((child, inner, depth + 1));
                }
            }
        }
        return cells;
    }

    /// <summary>The deepest cell under a point, for hover and click.</summary>
    public static TreemapCell? HitTest(IReadOnlyList<TreemapCell> cells, double x, double y)
    {
        TreemapCell? best = null;
        foreach (var cell in cells)
        {
            if (cell.Rect.Contains(x, y) && (best is null || cell.Depth >= best.Value.Depth))
                best = cell;
        }
        return best;
    }

    private static double Worst(List<double> row, double extra, double side)
    {
        var sum = row.Sum() + extra;
        var max = Math.Max(row.Max(), extra);
        var min = extra > 0 ? Math.Min(row.Min(), extra) : row.Min();
        if (min <= 0 || sum <= 0)
            return double.MaxValue;
        var s2 = sum * sum;
        var w2 = side * side;
        return Math.Max(w2 * max / s2, s2 / (w2 * min));
    }

    private static TreemapRect LayoutRow(List<double> row, TreemapRect rect, List<TreemapRect> result)
    {
        var sum = row.Sum();
        if (rect.Width >= rect.Height)
        {
            // A column down the left edge.
            var width = sum / rect.Height;
            var y = rect.Y;
            foreach (var v in row)
            {
                var h = v / width;
                result.Add(new TreemapRect(rect.X, y, width, h));
                y += h;
            }
            return new TreemapRect(rect.X + width, rect.Y, Math.Max(0, rect.Width - width), rect.Height);
        }
        else
        {
            // A row along the top edge.
            var height = sum / rect.Width;
            var x = rect.X;
            foreach (var v in row)
            {
                var w = v / height;
                result.Add(new TreemapRect(x, rect.Y, w, height));
                x += w;
            }
            return new TreemapRect(rect.X, rect.Y + height, rect.Width, Math.Max(0, rect.Height - height));
        }
    }
}
