using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Scour.App.ViewModels;
using Windows.Foundation;
using Windows.Storage.Pickers;

namespace Scour.App.Views;

public sealed partial class AnalysePage : Page
{
    public AnalysePage()
    {
        InitializeComponent();
    }

    public AnalyseViewModel Vm => AnalyseViewModel.Instance;

    public string CustomFolderText(string? folder) => folder is null ? "" : $"Folder: {folder}";

    public double ProgressValue(double? fraction) => fraction ?? 0;

    public bool IsIndeterminate(double? fraction) => fraction is null;


    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Vm.TreeChanged += OnTreeChanged;
        Map.Show(Vm.Tree, Vm.CurrentNode);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        Vm.TreeChanged -= OnTreeChanged;
        base.OnNavigatedFrom(e);
    }

    private void OnTreeChanged(object? sender, EventArgs e) => Map.Show(Vm.Tree, Vm.CurrentNode);

    private void OnDriveChanged(object sender, SelectionChangedEventArgs e) => Vm.CustomFolder = null;

    private async void OnChooseFolderClick(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindow!.Hwnd);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
            return;
        Vm.CustomFolder = folder.Path;
        await Vm.AnalyseAsync();
    }

    private async void OnAnalyseClick(object sender, RoutedEventArgs e) => await Vm.AnalyseAsync();

    private void OnStopClick(object sender, RoutedEventArgs e) => Vm.Cancel();

    private void OnUpClick(object sender, RoutedEventArgs e) => Vm.Up();

    private void OnCrumbClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Item is Crumb crumb)
            Vm.Navigate(crumb.Node);
    }

    private void OnMapNodeInvoked(object? sender, Controls.TreemapNodeEventArgs e) => Vm.Navigate(e.Node);

    private void OnMapContext(object? sender, Controls.TreemapNodeEventArgs e) => ShowMenu(e.Node, Map, e.Position);

    private void OnViewChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var types = sender.SelectedItem?.Text == "File types";
        TreePanel.Visibility = types ? Visibility.Collapsed : Visibility.Visible;
        TypesPanel.Visibility = types ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSortClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string tag && Enum.TryParse<TreeSort>(tag, out var sort))
            Vm.Sort = sort;
    }

    private void OnChevronClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is TreeRow row)
            Vm.ToggleRow(row);
    }

    private void OnRowDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is TreeRow { IsDirectory: true } row)
            Vm.Navigate(row.Node);
    }

    private void OnRowRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TreeRow row } element)
            ShowMenu(row.Node, element, e.GetPosition(element));
    }

    private void ShowMenu(int node, FrameworkElement target, Point position)
    {
        var menu = new MenuFlyout();
        void Add(string text, string glyph, Action action)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        if (Vm.Tree!.IsDirectory(node))
            Add("Open", "", () => Vm.Navigate(node));
        Add("Show in Explorer", "", () => Vm.ShowInExplorer(node));
        Add("Copy path", "", () => Vm.CopyPath(node));
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Move to Recycle Bin", "", async () => await Vm.DeleteAsync(node, XamlRoot));
        Add("Never clean this", "", () => Vm.Exclude(node));
        menu.ShowAt(target, new FlyoutShowOptions { Position = position });
    }
}
