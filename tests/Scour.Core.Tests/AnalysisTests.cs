using Scour.Core.Analysis;

namespace Scour.Core.Tests;

public sealed class DiskTreeTests
{
    // root(1) ─ a(2) ─ a1.txt(4: 100), a2.bin(5: 300)
    //         └ b(3) ─ b1.txt(6: 50)
    //         └ top.log(7: 10)
    //  orphan(9) points at a parent that doesn't exist.
    private static DiskTree Build()
    {
        var builder = new DiskTreeBuilder();
        builder.AddRange(
        [
            new RawEntry(4, 2, "a1.txt", false, 100, 4096),
            new RawEntry(2, 1, "a", true, 0, 0),
            new RawEntry(5, 2, "a2.bin", false, 300, 4096),
            new RawEntry(3, 1, "b", true, 0, 0),
            new RawEntry(6, 3, "b1.txt", false, 50, 4096),
            new RawEntry(7, 1, "top.log", false, 10, 0),
            new RawEntry(9, 42, "orphan", false, 999, 999),
        ]);
        return builder.Build(1, @"X:\", fromMft: false);
    }

    private static int Find(DiskTree tree, string path) => tree.Find(Path.Combine(@"X:\", path));

    [Fact]
    public void Folder_sizes_are_the_sum_of_their_contents()
    {
        var tree = Build();

        Assert.Equal(460, tree.Size(DiskTree.Root));
        Assert.Equal(400, tree.Size(Find(tree, "a")));
        Assert.Equal(4, tree.FileCount(DiskTree.Root));
        Assert.Equal(3 * 4096, tree.SizeOnDisk(DiskTree.Root));
    }

    [Fact]
    public void Orphans_are_not_counted()
    {
        Assert.Equal(-1, Find(Build(), "orphan"));
    }

    [Fact]
    public void Children_are_largest_first()
    {
        var tree = Build();

        Assert.Equal(["a", "b", "top.log"], tree.Children(DiskTree.Root).Select(tree.Name));
    }

    [Fact]
    public void Full_path_and_ancestry()
    {
        var tree = Build();
        var file = Find(tree, @"a\a2.bin");

        Assert.Equal(@"X:\a\a2.bin", tree.FullPath(file));
        Assert.Equal([DiskTree.Root, Find(tree, "a"), file], tree.Ancestry(file));
    }

    [Fact]
    public void Remove_takes_size_off_every_ancestor()
    {
        var tree = Build();
        tree.Remove(Find(tree, @"a\a2.bin"));

        Assert.Equal(160, tree.Size(DiskTree.Root));
        Assert.Equal(100, tree.Size(Find(tree, "a")));
        Assert.Equal(3, tree.FileCount(DiskTree.Root));
        Assert.Equal(-1, Find(tree, @"a\a2.bin"));
    }

    [Fact]
    public void Type_breakdown_groups_by_extension()
    {
        var types = Build().TypeBreakdown(DiskTree.Root);

        Assert.Equal(".bin", types[0].Extension);
        Assert.Equal(150, types.Single(t => t.Extension == ".txt").Bytes);
        Assert.Equal(2, types.Single(t => t.Extension == ".txt").Count);
    }
}

public sealed class TreemapTests
{
    [Fact]
    public void Squarify_fills_the_bounds_in_proportion()
    {
        var values = new double[] { 600, 300, 60, 30, 10 };
        var bounds = new TreemapRect(0, 0, 400, 250);

        var rects = Treemap.Squarify(values, bounds);

        Assert.Equal(values.Length, rects.Count);
        Assert.Equal(bounds.Area, rects.Sum(r => r.Area), 3);
        for (var i = 0; i < values.Length; i++)
            Assert.Equal(values[i] / values.Sum() * bounds.Area, rects[i].Area, 3);
        Assert.All(rects, r =>
        {
            Assert.InRange(r.X, -0.001, 400.001);
            Assert.InRange(r.X + r.Width, -0.001, 400.001);
            Assert.InRange(r.Y + r.Height, -0.001, 250.001);
        });
    }

    [Fact]
    public void Squarify_keeps_aspect_ratios_reasonable()
    {
        var values = Enumerable.Range(1, 20).Select(i => (double)(21 - i)).ToList();

        var rects = Treemap.Squarify(values, new TreemapRect(0, 0, 800, 600));

        Assert.All(rects.Take(10), r => Assert.InRange(Math.Max(r.Width / r.Height, r.Height / r.Width), 1, 6));
    }

    [Fact]
    public void Rectangles_do_not_overlap()
    {
        var rects = Treemap.Squarify([5, 4, 3, 2, 1, 1, 1], new TreemapRect(0, 0, 100, 100));
        for (var i = 0; i < rects.Count; i++)
        for (var j = i + 1; j < rects.Count; j++)
        {
            var a = rects[i];
            var b = rects[j];
            var overlapX = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
            var overlapY = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
            Assert.False(overlapX > 0.001 && overlapY > 0.001, $"{a} overlaps {b}");
        }
    }

    [Fact]
    public void Hit_test_returns_the_deepest_cell()
    {
        TreemapCell[] cells =
        [
            new(1, new TreemapRect(0, 0, 100, 100), 0, true, true),
            new(2, new TreemapRect(10, 20, 30, 30), 1, false, false),
        ];

        Assert.Equal(2, Treemap.HitTest(cells, 15, 25)?.Node);
        Assert.Equal(1, Treemap.HitTest(cells, 90, 90)?.Node);
        Assert.Null(Treemap.HitTest(cells, 150, 150));
    }
}

public sealed class MftReaderTests
{
    [Fact]
    public void Decodes_data_runs_with_relative_and_negative_offsets_and_sparse_runs()
    {
        // 0x21: 1 length byte, 2 offset bytes → 0x18 clusters at LCN 0x5634.
        // 0x11: 1 length byte, 1 offset byte  → 0x10 clusters at LCN +(-0x10) relative.
        // 0x01: sparse run of 0x05 clusters (no offset) → skipped.
        byte[] runs = [0x21, 0x18, 0x34, 0x56, 0x11, 0x10, 0xF0, 0x01, 0x05, 0x00];

        var decoded = MftReader.DecodeRuns(runs);

        Assert.Equal([(0x5634L, 0x18L), (0x5634L - 0x10, 0x10L)], decoded);
    }

    [Fact]
    public void Fixups_restore_the_last_two_bytes_of_each_sector()
    {
        var record = new byte[1024];
        "FILE"u8.CopyTo(record);
        BitConverter.GetBytes((ushort)0x30).CopyTo(record, 4); // update sequence array offset
        BitConverter.GetBytes((ushort)3).CopyTo(record, 6);    // check value + 2 sectors
        BitConverter.GetBytes((ushort)0xABCD).CopyTo(record, 0x30);
        record[0x32] = 0x11; record[0x33] = 0x22;               // real bytes for sector 1
        record[0x34] = 0x33; record[0x35] = 0x44;               // real bytes for sector 2
        BitConverter.GetBytes((ushort)0xABCD).CopyTo(record, 510);
        BitConverter.GetBytes((ushort)0xABCD).CopyTo(record, 1022);

        Assert.True(MftReader.ApplyFixups(record, 512));

        Assert.Equal([0x11, 0x22], record[510..512]);
        Assert.Equal([0x33, 0x44], record[1022..1024]);
    }

    [Fact]
    public void Torn_record_fails_the_fixup_check()
    {
        var record = new byte[1024];
        BitConverter.GetBytes((ushort)0x30).CopyTo(record, 4);
        BitConverter.GetBytes((ushort)3).CopyTo(record, 6);
        BitConverter.GetBytes((ushort)0xABCD).CopyTo(record, 0x30);
        BitConverter.GetBytes((ushort)0x1234).CopyTo(record, 510); // doesn't match the check value

        Assert.False(MftReader.ApplyFixups(record, 512));
    }
}

public sealed class FileSystemWalkerTests : IDisposable
{
    private readonly Sandbox _box = new();

    public void Dispose() => _box.Dispose();

    [Fact]
    public async Task Walk_sums_a_folder_and_does_not_follow_junctions()
    {
        _box.File(@"Data\a.bin", 1000);
        _box.File(@"Data\sub\b.bin", 500);
        var outside = _box.File(@"Outside\huge.bin", 100_000);
        _box.Junction(@"Data\link", Path.GetDirectoryName(outside)!);

        var tree = await DiskAnalyser.AnalyseAsync(_box.PathOf("Data"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(tree.FromMft);
        Assert.Equal(1500, tree.Size(DiskTree.Root));
        Assert.Equal(2, tree.FileCount(DiskTree.Root));
        Assert.True(tree.SizeOnDisk(DiskTree.Root) >= 1500);
    }
}
