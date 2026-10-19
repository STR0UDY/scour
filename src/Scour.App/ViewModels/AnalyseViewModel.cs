using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Scour.App.Helpers;
using Scour.App.Services;
using Scour.Core.Analysis;
using Scour.Core.Cleaning;

namespace Scour.App.ViewModels;

public sealed record DriveOption(string Label, string Path, bool IsNtfs)
{
    public override string ToString() => Label;
}

public sealed record Crumb(int Node, string Name)
{
    public override string ToString() => Name;
}

public sealed record TypeRow(string Extension, string SizeText, string CountText, double Share);

public enum TreeSort
{
    Size,
    Name,
    Files,
}

/// <summary>A row in the tree list: one node, indented by depth, expandable if it's a folder.</summary>
public sealed partial class TreeRow(AnalyseViewModel owner, int node, int depth) : ObservableObject
{
    public int Node { get; } = node;
    public int Depth { get; } = depth;
    public DiskTree Tree => owner.Tree!;

    public string Name => Tree.Name(Node);
    public bool IsDirectory => Tree.IsDirectory(Node);
    public bool HasChildren => IsDirectory && Tree.Children(Node).Count > 0;
    public string SizeText => Format.Bytes(Tree.Size(Node));
    public string OnDiskText => Format.Bytes(Tree.SizeOnDisk(Node));
    public string FilesText => IsDirectory ? Format.Count(Tree.FileCount(Node)) : "";
    public double Percent => owner.CurrentSize == 0 ? 0 : 100.0 * Tree.Size(Node) / owner.CurrentSize;
    public string PercentText => $"{Percent:0.0}%";
    public Microsoft.UI.Xaml.Thickness IndentMargin => new(Depth * 20, 0, 0, 0);
    public string Glyph => IsDirectory ? (IsExpanded ? "" : "") : "";
    public string Chevron => IsExpanded ? "" : "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Glyph), nameof(Chevron))]
    public partial bool IsExpanded { get; set; }
}

/// <summary>Analyse page: read a drive (MFT through the helper when allowed, else walk), then drill down.</summary>
public sealed partial class AnalyseViewModel : ObservableObject
{
    private const int MaxChildrenPerLevel = 300;
    private CancellationTokenSource? _cancel;

    public static AnalyseViewModel Instance { get; } = new();

    private AnalyseViewModel()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || !drive.IsReady)
                    continue;
                var name = drive.Name.TrimEnd('\\');
                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? name : $"{drive.VolumeLabel} ({name})";
                Drives.Add(new DriveOption($"{label} · {Format.Bytes(drive.TotalSize - drive.TotalFreeSpace)} used", drive.Name,
                    drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase)));
            }
            catch (IOException)
            {
            }
        }
        SelectedDrive = Drives.FirstOrDefault();
    }

    public ObservableCollection<DriveOption> Drives { get; } = [];
    public ObservableCollection<Crumb> Breadcrumb { get; } = [];
    public ObservableCollection<TreeRow> Rows { get; } = [];
    public ObservableCollection<TypeRow> Types { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUseFastScan))]
    public partial DriveOption? SelectedDrive { get; set; }

    /// <summary>A folder picked with "Choose folder…", analysed instead of a drive.</summary>
    [ObservableProperty]
    public partial string? CustomFolder { get; set; }

    [ObservableProperty]
    public partial bool UseFastScan { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(ShowEmpty))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string Progress { get; set; } = "";

    [ObservableProperty]
    public partial double? ProgressFraction { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTree), nameof(ShowEmpty))]
    public partial DiskTree? Tree { get; set; }

    [ObservableProperty]
    public partial int CurrentNode { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    public partial TreeSort Sort { get; set; } = TreeSort.Size;

    public bool IsIdle => !IsBusy;
    public bool HasTree => Tree is not null;
    public bool ShowEmpty => Tree is null && !IsBusy;
    public long CurrentSize => Tree?.Size(CurrentNode) ?? 0;

    /// <summary>Fast MFT reading applies to whole NTFS drives; it needs admin, through the helper.</summary>
    public bool CanUseFastScan => CustomFolder is null && SelectedDrive?.IsNtfs == true;

    public event EventHandler? TreeChanged;

    partial void OnCustomFolderChanged(string? value) => OnPropertyChanged(nameof(CanUseFastScan));

    public async Task AnalyseAsync()
    {
        var target = CustomFolder ?? SelectedDrive?.Path;
        if (target is null || IsBusy)
            return;

        _cancel = new CancellationTokenSource();
        IsBusy = true;
        Progress = "Starting…";
        ProgressFraction = null;
        var watch = Stopwatch.StartNew();
        try
        {
            ElevatedMftSource? source = null;
            if (CanUseFastScan && UseFastScan && !AppServices.Engine.IsElevated)
            {
                // One UAC prompt, shared with cleaning; if declined, fall back to walking.
                var helper = AppServices.CleanService.RunningHelper;
                if (helper is null && await CleanViewModel.Instance.TryElevateAsync())
                    helper = AppServices.CleanService.RunningHelper;
                if (helper is not null)
                    source = helper.ReadMftAsync;
            }

            var progress = new Progress<AnalyseProgress>(p =>
            {
                Progress = p.Fraction is { } f ? $"{p.Stage}… {f:P0}" : p.Items > 0 ? $"{p.Stage}… {Format.Count(p.Items)} items" : $"{p.Stage}…";
                ProgressFraction = p.Fraction * 100;
            });
            Tree = null; // Let the previous tree go before building the next one.
            var tree = await DiskAnalyser.AnalyseAsync(target, source, progress, _cancel.Token);
            watch.Stop();

            // Building a multi-million-entry tree leaves gigabytes of short-lived garbage;
            // hand it back to Windows now rather than whenever the GC next gets round to it.
            await Task.Run(() => GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true));

            Tree = tree;
            Summary = $"{Format.Bytes(tree.Size(DiskTree.Root))} ({Format.Bytes(tree.SizeOnDisk(DiskTree.Root))} on disk) in {Format.Files(tree.FileCount(DiskTree.Root))}"
                      + $" · {(tree.FromMft ? "read from the master file table" : "walked folder by folder")} in {watch.Elapsed.TotalSeconds:0.0} s";
            Navigate(DiskTree.Root);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (App.MainWindow is { } w)
                await Dialogs.InfoAsync(w.XamlRoot, "Couldn't analyse that", ex.Message);
        }
        finally
        {
            IsBusy = false;
            Progress = "";
        }
    }

    public void Cancel() => _cancel?.Cancel();

    /// <summary>Drill into a folder: treemap, breadcrumb, list and type breakdown all follow.</summary>
    public void Navigate(int node)
    {
        if (Tree is null)
            return;
        if (!Tree.IsDirectory(node))
            node = Tree.Parent(node);
        CurrentNode = node;

        Breadcrumb.Clear();
        foreach (var n in Tree.Ancestry(node))
            Breadcrumb.Add(new Crumb(n, n == DiskTree.Root ? Tree.RootPath.TrimEnd('\\') : Tree.Name(n)));

        RebuildRows();
        Types.Clear();
        var total = Math.Max(1, Tree.Size(node));
        foreach (var t in Tree.TypeBreakdown(node, 40))
            Types.Add(new TypeRow(t.Extension, Format.Bytes(t.Bytes), Format.Files(t.Count), 100.0 * t.Bytes / total));
        TreeChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Up()
    {
        if (Tree is not null && CurrentNode != DiskTree.Root)
            Navigate(Tree.Parent(CurrentNode));
    }

    partial void OnSortChanged(TreeSort value) => RebuildRows();

    private void RebuildRows()
    {
        Rows.Clear();
        if (Tree is null)
            return;
        foreach (var child in Ordered(CurrentNode))
            Rows.Add(new TreeRow(this, child, 0));
    }

    public void ToggleRow(TreeRow row)
    {
        if (!row.HasChildren)
            return;
        var index = Rows.IndexOf(row);
        if (index < 0)
            return;

        if (row.IsExpanded)
        {
            // Remove every descendant row below it.
            while (index + 1 < Rows.Count && Rows[index + 1].Depth > row.Depth)
                Rows.RemoveAt(index + 1);
            row.IsExpanded = false;
        }
        else
        {
            var insert = index + 1;
            foreach (var child in Ordered(row.Node))
                Rows.Insert(insert++, new TreeRow(this, child, row.Depth + 1));
            row.IsExpanded = true;
        }
    }

    private IEnumerable<int> Ordered(int node)
    {
        var children = Tree!.Children(node);
        IEnumerable<int> sorted = Sort switch
        {
            TreeSort.Name => children.OrderByDescending(Tree.IsDirectory).ThenBy(c => Tree.Name(c), StringComparer.CurrentCultureIgnoreCase),
            TreeSort.Files => children.OrderByDescending(Tree.FileCount),
            _ => children,
        };
        return sorted.Take(MaxChildrenPerLevel);
    }

    // ---------------------------------------------------------------- Actions

    public void ShowInExplorer(int node)
    {
        if (Tree is not null)
            Shell.ShowInExplorer(Tree.FullPath(node));
    }

    public void CopyPath(int node)
    {
        if (Tree is not null)
            Shell.CopyToClipboard(Tree.FullPath(node));
    }

    /// <summary>Recycle Bin only, after confirmation; the deny-list still applies.</summary>
    public async Task DeleteAsync(int node, Microsoft.UI.Xaml.XamlRoot root)
    {
        if (Tree is null || node == DiskTree.Root)
            return;
        var path = Tree.FullPath(node);
        var what = Tree.IsDirectory(node) ? $"the folder {Tree.Name(node)} and everything in it ({Format.Bytes(Tree.Size(node))}, {Format.Files(Tree.FileCount(node))})" : $"{Tree.Name(node)} ({Format.Bytes(Tree.Size(node))})";
        var confirm = await Dialogs.ShowAsync(root, "Move to the Recycle Bin?", $"Move {what} to the Recycle Bin?\n\n{path}", primary: "Move to Recycle Bin");
        if (confirm != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
            return;

        var error = await Task.Run(() => FileActions.SendToRecycleBin(path, AppServices.Engine.DenyList));
        if (error is not null)
        {
            await Dialogs.InfoAsync(root, "Not moved", error);
            return;
        }
        var parent = Tree.Parent(node);
        Tree.Remove(node);
        Navigate(CurrentNode == node ? parent : CurrentNode);
    }

    /// <summary>"Never clean this": adds the path to the exclusions every rule respects.</summary>
    public void Exclude(int node)
    {
        if (Tree is null)
            return;
        var path = Tree.FullPath(node);
        if (!AppServices.Settings.Exclusions.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            AppServices.Settings.Exclusions.Add(path);
            AppServices.ReloadEngine();
        }
    }
}
