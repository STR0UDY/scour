namespace Scour.Core.Analysis;

/// <summary>One file or folder as a scanner reports it, before sizes are summed.</summary>
/// <param name="Id">Unique within one scan (MFT record number, or a walker counter).</param>
/// <param name="ParentId">The containing folder's <paramref name="Id"/>.</param>
/// <param name="Size">Logical size; 0 for folders (summed later).</param>
/// <param name="Allocated">Size on disk: clusters actually used, so compressed and sparse files read honestly.</param>
public readonly record struct RawEntry(ulong Id, ulong ParentId, string Name, bool IsDirectory, long Size, long Allocated);

/// <summary>
/// A whole drive (or folder) as compact parallel arrays, so millions of entries stay cheap.
/// Folder sizes are the sums of everything below them. Nodes are ints; 0 is the root.
/// </summary>
public sealed class DiskTree
{
    private readonly string[] _names;
    private readonly int[] _parents;
    private readonly bool[] _isDirectory;
    private readonly long[] _size;
    private readonly long[] _allocated;
    private readonly int[] _files;
    private readonly int[][] _children;

    internal DiskTree(string rootPath, string[] names, int[] parents, bool[] isDirectory, long[] size, long[] allocated, int[] files, int[][] children, bool fromMft)
    {
        RootPath = rootPath;
        _names = names;
        _parents = parents;
        _isDirectory = isDirectory;
        _size = size;
        _allocated = allocated;
        _files = files;
        _children = children;
        FromMft = fromMft;
    }

    public const int Root = 0;

    public string RootPath { get; }

    /// <summary>Read straight from the NTFS master file table (fast, needs admin) rather than walked.</summary>
    public bool FromMft { get; }

    public int Count => _names.Length;

    public string Name(int node) => node == Root ? RootPath : _names[node];
    public int Parent(int node) => _parents[node];
    public bool IsDirectory(int node) => _isDirectory[node];
    public long Size(int node) => _size[node];
    public long SizeOnDisk(int node) => _allocated[node];

    /// <summary>Files at or below this node.</summary>
    public int FileCount(int node) => _files[node];

    /// <summary>Children, largest first. Removed nodes are gone from here.</summary>
    public IReadOnlyList<int> Children(int node) => _children[node];

    public string FullPath(int node)
    {
        if (node == Root)
            return RootPath;
        var parts = new Stack<string>();
        for (var n = node; n != Root && n >= 0; n = _parents[n])
            parts.Push(_names[n]);
        return Path.Combine([RootPath, .. parts]);
    }

    /// <summary>The chain from the root down to this node, for a breadcrumb.</summary>
    public IReadOnlyList<int> Ancestry(int node)
    {
        var chain = new List<int>();
        for (var n = node; n >= 0; n = n == Root ? -1 : _parents[n])
            chain.Add(n);
        chain.Reverse();
        return chain;
    }

    /// <summary>Finds a node by full path, or -1.</summary>
    public int Find(string fullPath)
    {
        var relative = Path.GetRelativePath(RootPath, fullPath);
        if (relative == ".")
            return Root;
        var node = Root;
        foreach (var part in relative.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            node = _children[node].FirstOrDefault(c => _names[c].Equals(part, StringComparison.OrdinalIgnoreCase), -1);
            if (node < 0)
                return -1;
        }
        return node;
    }

    /// <summary>After a delete: detach the node and take its size off every ancestor.</summary>
    public void Remove(int node)
    {
        if (node == Root)
            return;
        var parent = _parents[node];
        _children[parent] = _children[parent].Where(c => c != node).ToArray();
        for (var n = parent; n >= 0; n = n == Root ? -1 : _parents[n])
        {
            _size[n] -= _size[node];
            _allocated[n] -= _allocated[node];
            _files[n] -= _files[node];
        }
    }

    /// <summary>Bytes and file counts by extension under a node, largest first.</summary>
    public IReadOnlyList<FileTypeStat> TypeBreakdown(int node, int top = 50)
    {
        var stats = new Dictionary<string, (long Bytes, long Allocated, int Count)>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<int>();
        pending.Push(node);
        while (pending.Count > 0)
        {
            var n = pending.Pop();
            if (_isDirectory[n])
            {
                foreach (var c in _children[n])
                    pending.Push(c);
                continue;
            }
            var ext = Path.GetExtension(_names[n]);
            if (ext.Length is 0 or > 12)
                ext = "(none)";
            var s = stats.GetValueOrDefault(ext.ToLowerInvariant());
            stats[ext.ToLowerInvariant()] = (s.Bytes + _size[n], s.Allocated + _allocated[n], s.Count + 1);
        }
        return stats.Select(kv => new FileTypeStat(kv.Key, kv.Value.Bytes, kv.Value.Allocated, kv.Value.Count))
            .OrderByDescending(s => s.Bytes)
            .Take(top)
            .ToList();
    }
}

public sealed record FileTypeStat(string Extension, long Bytes, long Allocated, int Count);
