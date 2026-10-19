using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Scour.App.ViewModels;

namespace Scour.App.Views;

public sealed partial class CleanPage : Page
{
    public CleanPage()
    {
        InitializeComponent();
    }

    public CleanViewModel Vm => CleanViewModel.Instance;

    public bool HasAdminWaiting(int count) => count > 0;

    public bool CanCleanNow(bool canClean, bool busy) => canClean && !busy;

    private async void OnScanClick(object sender, RoutedEventArgs e) => await Vm.ScanAsync();

    private async void OnCleanClick(object sender, RoutedEventArgs e) => await Vm.CleanAsync(XamlRoot);

    private async void OnUnlockClick(object sender, RoutedEventArgs e) => await Vm.UnlockAdminRulesAsync();

    private void OnResultClosed(InfoBar sender, object args) => Vm.DismissResult();

    private void OnOpenLogClick(object sender, RoutedEventArgs e) => Vm.OpenLog();

    private void OnProfileClick(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuFlyoutItem)?.Tag is string selection)
            Vm.ApplySelection(selection);
    }
}
