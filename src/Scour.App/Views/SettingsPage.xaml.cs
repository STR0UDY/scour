using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Scour.App.Helpers;
using Scour.App.ViewModels;
using Windows.Storage.Pickers;

namespace Scour.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
    }

    public SettingsViewModel Vm { get; } = new();

    public Uri RepoUri => new(Vm.Repository);

    public Uri? UpdateUri(string? url) => url is null ? null : new Uri(url);

    public Visibility NoExclusions(int count) => count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private static T Init<T>(T picker)
    {
        WinRT.Interop.InitializeWithWindow.Initialize(picker!, App.MainWindow!.Hwnd);
        return picker;
    }

    private async void OnAddFolderClick(object sender, RoutedEventArgs e)
    {
        var picker = Init(new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder });
        picker.FileTypeFilter.Add("*");
        if (await picker.PickSingleFolderAsync() is { } folder)
            Vm.AddExclusion(folder.Path);
    }

    private async void OnAddFileClick(object sender, RoutedEventArgs e)
    {
        var picker = Init(new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder });
        picker.FileTypeFilter.Add("*");
        if (await picker.PickSingleFileAsync() is { } file)
            Vm.AddExclusion(file.Path);
    }

    private void OnRemoveExclusionClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string path)
            Vm.RemoveExclusion(path);
    }

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        var picker = Init(new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads });
        picker.FileTypeFilter.Add(".ini");
        if (await picker.PickSingleFileAsync() is not { } file)
            return;
        try
        {
            var message = await Vm.ImportWinapp2Async(file.Path);
            await Dialogs.InfoAsync(XamlRoot, "winapp2.ini imported", message + "\n\nFind the new items on the Clean page, unticked.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Dialogs.InfoAsync(XamlRoot, "Couldn't import that file", ex.Message);
        }
    }

    private void OnRemoveWinapp2Click(object sender, RoutedEventArgs e) => Vm.RemoveWinapp2();

    private async void OnCheckNowClick(object sender, RoutedEventArgs e) => await Vm.CheckNowAsync();

    private void OnLogsClick(object sender, RoutedEventArgs e) => Vm.OpenLogs();
}
