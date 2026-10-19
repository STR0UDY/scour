using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Scour.App.Services;
using Scour.App.Views;
using Windows.Graphics;

namespace Scour.App;

public sealed partial class MainWindow : Window
{
    private static readonly Dictionary<string, Type> Pages = new()
    {
        ["home"] = typeof(HomePage),
        ["clean"] = typeof(CleanPage),
        ["analyse"] = typeof(AnalysePage),
        ["startup"] = typeof(StartupPage),
        ["apps"] = typeof(AppsPage),
        ["settings"] = typeof(SettingsPage),
    };

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "scour.ico"));
        AppWindow.Resize(new SizeInt32(1240, 820));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 860;
            presenter.PreferredMinimumHeight = 600;
        }

        ElevatedBadge.Visibility = AppServices.Engine.IsElevated ? Visibility.Visible : Visibility.Collapsed;
        ApplyTheme(AppServices.Settings.Theme);
        Nav.SelectedItem = Nav.MenuItems[0];
        if (AppServices.Settings.CheckForUpdates)
            _ = CheckForUpdatesAsync();
    }

    private async Task CheckForUpdatesAsync()
    {
        var info = await Scour.Core.Updates.UpdateChecker.CheckAsync();
        if (info is not { IsNewer: true })
            return;
        UpdateBar.Message = $"Version {info.Latest} is on GitHub. You have {Scour.Core.Updates.UpdateChecker.CurrentVersion.ToString(3)}.";
        UpdateLink.NavigateUri = new Uri(info.Url);
        UpdateBar.IsOpen = true;
    }

    public nint Hwnd => Win32Interop.GetWindowFromWindowId(AppWindow.Id);

    public XamlRoot XamlRoot => Content.XamlRoot;

    public void ApplyTheme(string theme)
    {
        Root.RequestedTheme = theme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }

    /// <summary>Lets one page send the user to another (e.g. Home's "Review" button).</summary>
    public void NavigateTo(string tag)
    {
        var item = Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == tag);
        if (item is not null)
            Nav.SelectedItem = item;
        else if (tag == "settings")
            Nav.SelectedItem = Nav.SettingsItem;
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = args.IsSettingsSelected ? "settings" : (args.SelectedItem as NavigationViewItem)?.Tag as string;
        if (tag is null || !Pages.TryGetValue(tag, out var page) || ContentFrame.CurrentSourcePageType == page)
            return;
        ContentFrame.Navigate(page, null, new EntranceNavigationTransitionInfo());
    }
}
