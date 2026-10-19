using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Scour.App.ViewModels;

namespace Scour.App.Views;

public sealed partial class AppsPage : Page
{
    public AppsPage()
    {
        InitializeComponent();
    }

    public AppsViewModel Vm => AppsViewModel.Instance;

    public string WaitingTitle(string? name) => name is null ? "" : $"Waiting for {name}'s uninstaller…";

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (!Vm.IsUninstalling)
            await Vm.LoadAsync();
    }

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if ((SortPicker.SelectedItem as ComboBoxItem)?.Tag is string tag && Enum.TryParse<AppSort>(tag, out var sort))
            Vm.Sort = sort;
    }

    private async void OnUninstallClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AppRow row && !Vm.IsUninstalling)
            await Vm.UninstallAsync(row, XamlRoot);
    }

    private void OnFinishedClick(object sender, RoutedEventArgs e) => Vm.StopWaiting();
}
