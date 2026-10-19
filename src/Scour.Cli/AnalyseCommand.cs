using System.CommandLine;
using System.Diagnostics;
using Scour.Core.Analysis;

namespace Scour.Cli;

internal static class AnalyseCommand
{
    public static Command Create()
    {
        var path = new Argument<string>("path") { Description = "Drive or folder, e.g. C: or D:\\Games." };
        var top = new Option<int>("--top") { Description = "How many of the largest items to list.", DefaultValueFactory = _ => 20 };
        var depth = new Option<int>("--depth") { Description = "List the largest folders down to this depth.", DefaultValueFactory = _ => 2 };
        var types = new Option<bool>("--types") { Description = "Also show the breakdown by file type." };
        var json = new Option<bool>("--json") { Description = "Machine-readable output." };

        var command = new Command("analyse", "Show where the space on a drive or folder went. Read-only.") { path, top, depth, types, json };
        command.Aliases.Add("analyze");
        command.SetAction(async (parse, ct) =>
        {
            var target = parse.GetValue(path)!;
            if (target.Length == 2 && target[1] == ':')
                target += "\\";
            if (!Directory.Exists(target))
            {
                Format.Error($"{target} doesn't exist.");
                return 2;
            }

            var asJson = parse.GetValue(json);
            var watch = Stopwatch.StartNew();
            var progress = new SyncProgress<AnalyseProgress>(p =>
            {
                if (!asJson && !Console.IsErrorRedirected)
                    Console.Error.Write($"\r{p.Stage}… {(p.Fraction is { } f ? $"{f:P0}" : Format.Count(p.Items))}        ");
            });
            var tree = await DiskAnalyser.AnalyseAsync(target, null, progress, ct);
            watch.Stop();
            if (!asJson && !Console.IsErrorRedirected)
                Console.Error.Write("\r" + new string(' ', 60) + "\r");

            var largest = Largest(tree, parse.GetValue(depth)).Take(parse.GetValue(top)).ToList();
            if (asJson)
            {
                Report.Write(new
                {
                    path = tree.RootPath,
                    method = tree.FromMft ? "mft" : "walk",
                    seconds = watch.Elapsed.TotalSeconds,
                    size = tree.Size(DiskTree.Root),
                    sizeOnDisk = tree.SizeOnDisk(DiskTree.Root),
                    files = tree.FileCount(DiskTree.Root),
                    largest = largest.Select(n => new { path = tree.FullPath(n), size = tree.Size(n), sizeOnDisk = tree.SizeOnDisk(n), files = tree.FileCount(n) }),
                    types = parse.GetValue(types) ? tree.TypeBreakdown(DiskTree.Root, 25) : null,
                });
                return 0;
            }

            Console.WriteLine($"{tree.RootPath}: {Format.Bytes(tree.Size(DiskTree.Root))} ({Format.Bytes(tree.SizeOnDisk(DiskTree.Root))} on disk) in {Format.Count(tree.FileCount(DiskTree.Root))} files");
            Console.WriteLine(tree.FromMft
                ? $"Read from the master file table in {watch.Elapsed.TotalSeconds:0.0} s."
                : $"Walked in {watch.Elapsed.TotalSeconds:0.0} s. Run elevated on an NTFS drive to read the MFT instead (much faster).");
            Console.WriteLine();
            foreach (var node in largest)
                Console.WriteLine($"  {Format.Bytes(tree.Size(node)),10}  {Format.Count(tree.FileCount(node)),10} files  {tree.FullPath(node)}");

            if (parse.GetValue(types))
            {
                Console.WriteLine();
                foreach (var stat in tree.TypeBreakdown(DiskTree.Root, 15))
                    Console.WriteLine($"  {Format.Fit(stat.Extension, 12)} {Format.Bytes(stat.Bytes),10}  {Format.Count(stat.Count),10} files");
            }
            return 0;
        });
        return command;
    }

    /// <summary>Folders down to <paramref name="depth"/> plus every file, largest first.</summary>
    private static IEnumerable<int> Largest(DiskTree tree, int depth)
    {
        var all = new List<int>();
        var pending = new Stack<(int Node, int Depth)>();
        pending.Push((DiskTree.Root, 0));
        while (pending.Count > 0)
        {
            var (node, d) = pending.Pop();
            foreach (var child in tree.Children(node))
            {
                if (!tree.IsDirectory(child) || d + 1 <= depth)
                    all.Add(child);
                if (tree.IsDirectory(child) && d + 1 < depth)
                    pending.Push((child, d + 1));
            }
        }
        return all.OrderByDescending(tree.Size);
    }
}
