namespace Scour.Core.Analysis;

/// <summary>Collects <see cref="RawEntry"/> records in any order, then links and sums them into a <see cref="DiskTree"/>.</summary>
public sealed class DiskTreeBuilder
{
    private readonly List<RawEntry> _entries = [];

    public int Count => _entries.Count;

    public void Add(RawEntry entry) => _entries.Add(entry);

    public void AddRange(IEnumerable<RawEntry> entries) => _entries.AddRange(entries);

    /// <param name="rootId">The id of the folder being analysed (5 on NTFS: the volume root).</param>
    /// <param name="rootPath">Its path, e.g. <c>C:\</c>.</param>
    public DiskTree Build(ulong rootId, string rootPath, bool fromMft)
    {
        // Index 0 is the root; everything else gets an index in arrival order.
        var index = new Dictionary<ulong, int>(_entries.Count + 1) { [rootId] = 0 };
        var source = new List<RawEntry>(_entries.Count + 1) { new(rootId, rootId, rootPath, true, 0, 0) };
        foreach (var e in _entries)
        {
            if (e.Id == rootId || !index.TryAdd(e.Id, source.Count))
                continue;
            source.Add(e);
        }

        var n = source.Count;
        var names = new string[n];
        var parents = new int[n];
        var isDir = new bool[n];
        var size = new long[n];
        var allocated = new long[n];
        var files = new int[n];
        var childLists = new List<int>?[n];

        for (var i = 0; i < n; i++)
        {
            var e = source[i];
            names[i] = e.Name;
            isDir[i] = e.IsDirectory;
            size[i] = e.Size;
            allocated[i] = e.Allocated;
            files[i] = e.IsDirectory ? 0 : 1;
            if (i == 0)
            {
                parents[i] = -1;
                continue;
            }
            // Orphans (parent outside the analysed folder, or a deleted record) hang off nothing.
            parents[i] = index.TryGetValue(e.ParentId, out var p) && p != i ? p : -1;
            if (parents[i] >= 0)
                (childLists[parents[i]] ??= []).Add(i);
        }

        // Post-order walk from the root: sum sizes up, and drop anything unreachable (cycles, orphans).
        var reachable = new bool[n];
        var order = new List<int>(n);
        var stack = new Stack<int>();
        stack.Push(0);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (reachable[node])
                continue;
            reachable[node] = true;
            order.Add(node);
            if (childLists[node] is { } kids)
            {
                foreach (var c in kids)
                    stack.Push(c);
            }
        }
        for (var i = order.Count - 1; i > 0; i--)
        {
            var node = order[i];
            var p = parents[node];
            size[p] += size[node];
            allocated[p] += allocated[node];
            files[p] += files[node];
        }

        _entries.Clear();
        _entries.TrimExcess();
        source.Clear();
        index.Clear();

        var children = new int[n][];
        for (var i = 0; i < n; i++)
        {
            children[i] = childLists[i] is { } kids && reachable[i]
                ? kids.Where(c => reachable[c]).OrderByDescending(c => size[c]).ToArray()
                : [];
        }
        return new DiskTree(rootPath, names, parents, isDir, size, allocated, files, children, fromMft);
    }
}
