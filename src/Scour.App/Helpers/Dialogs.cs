using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Scour.App.Helpers;

/// <summary>Small wrappers around ContentDialog, which needs a XamlRoot in WinUI 3.</summary>
public static class Dialogs
{
    public static async Task<ContentDialogResult> ShowAsync(XamlRoot root, string title, object content,
        string primary, string? secondary = null, string close = "Cancel", ContentDialogButton defaultButton = ContentDialogButton.Primary)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = content is string text ? new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap } : content,
            PrimaryButtonText = primary,
            SecondaryButtonText = secondary ?? "",
            CloseButtonText = close,
            DefaultButton = defaultButton,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
            RequestedTheme = (root.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,
        };
        return await dialog.ShowAsync();
    }

    public static Task<ContentDialogResult> InfoAsync(XamlRoot root, string title, string message) =>
        ShowAsync(root, title, message, primary: "", close: "OK", defaultButton: ContentDialogButton.Close);
}
