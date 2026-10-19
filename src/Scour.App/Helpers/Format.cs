using System.Globalization;

namespace Scour.App.Helpers;

/// <summary>Number formatting used across pages, and as x:Bind functions in XAML.</summary>
public static class Format
{
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (Math.Abs(value) >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{bytes} B"
            : value.ToString(Math.Abs(value) >= 100 ? "0" : Math.Abs(value) >= 10 ? "0.0" : "0.00", CultureInfo.CurrentCulture) + " " + units[unit];
    }

    public static string Count(long n) => n.ToString("N0", CultureInfo.CurrentCulture);

    public static string Files(long n) => n == 1 ? "1 file" : $"{Count(n)} files";

    public static string Date(DateTime? date) => date?.ToString("d", CultureInfo.CurrentCulture) ?? "";

    public static string Ago(DateTimeOffset when)
    {
        var span = DateTimeOffset.Now - when;
        return span.TotalMinutes < 1 ? "just now"
            : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min ago"
            : span.TotalDays < 1 ? $"{(int)span.TotalHours} h ago"
            : span.TotalDays < 2 ? "yesterday"
            : when.ToString("d", CultureInfo.CurrentCulture);
    }

    public static Microsoft.UI.Xaml.Visibility Show(bool visible) =>
        visible ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public static Microsoft.UI.Xaml.Visibility Hide(bool hidden) => Show(!hidden);

    public static Microsoft.UI.Xaml.Visibility ShowText(string? text) => Show(!string.IsNullOrEmpty(text));
}
