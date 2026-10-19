using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Scour.App.Helpers;
using Scour.App.Services;
using Scour.App.ViewModels;

namespace Scour.App.Views;

public sealed record DriveRow(string Label, string FreeText, double UsedPercent);

public sealed partial class HomePage : Page
{
    public HomePage()
    {
        InitializeComponent();
    }

    public CleanViewModel Vm => CleanViewModel.Instance;

    public bool HasAdminWaiting(int count) => count > 0;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        RefreshLastClean();
        RefreshDrives();
    }

    private void RefreshLastClean()
    {
        var last = AppServices.Settings.LastClean;
        if (last is null)
        {
            LastCleanText.Text = "You haven't cleaned with Scour yet.";
            LastLogLink.Visibility = Visibility.Collapsed;
            return;
        }
        var locked = last.Locked > 0 ? $" {Format.Count(last.Locked)} files were in use and skipped." : "";
        LastCleanText.Text = $"{Format.Ago(last.When)}: freed {Format.Bytes(last.BytesFreed)} ({Format.Files(last.FilesFreed)}).{locked}";
        LastLogLink.Visibility = last.LogPath is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshDrives()
    {
        var rows = new List<DriveRow>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady || drive.TotalSize == 0)
                    continue;
                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.Name.TrimEnd('\\') : $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})";
                var used = 100.0 * (drive.TotalSize - drive.TotalFreeSpace) / drive.TotalSize;
                rows.Add(new DriveRow(label, $"{Format.Bytes(drive.TotalFreeSpace)} free of {Format.Bytes(drive.TotalSize)}", used));
            }
            catch (IOException)
            {
            }
        }
        DriveList.ItemsSource = rows;
    }

    private async void OnScanClick(object sender, RoutedEventArgs e) => await Vm.ScanAsync();

    private void OnStopClick(object sender, RoutedEventArgs e) => Vm.CancelScan();

    private async void OnCleanClick(object sender, RoutedEventArgs e)
    {
        await Vm.CleanAsync(XamlRoot);
        RefreshLastClean();
        RefreshDrives();
    }

    private void OnReviewClick(object sender, RoutedEventArgs e) => App.MainWindow?.NavigateTo("clean");

    private async void OnUnlockClick(object sender, RoutedEventArgs e) => await Vm.UnlockAdminRulesAsync();

    private void OnResultClosed(InfoBar sender, object args) => Vm.DismissResult();

    private void OnOpenLogClick(object sender, RoutedEventArgs e) => Vm.OpenLog();

    private void OnLastLogClick(object sender, RoutedEventArgs e)
    {
        if (AppServices.Settings.LastClean?.LogPath is { } path)
            Shell.ShowInExplorer(path);
    }
}
