using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Scour.App.ViewModels;
using Scour.Core.Startup;

namespace Scour.App.Views;

public sealed partial class StartupPage : Page
{
    public StartupPage()
    {
        InitializeComponent();
    }

    public StartupViewModel Vm => StartupViewModel.Instance;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // Always reload: other tools (and Task Manager) change these too.
        await Vm.LoadAsync();
    }

    private void OnTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem?.Tag is string tag && Enum.TryParse<StartupSource>(tag, out var source))
            Vm.Tab = source;
    }
}
