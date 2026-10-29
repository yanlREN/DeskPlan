using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DeskPlan.App.Views;

public static class Ui
{
    public static TextBlock Text(string text, double size = 13, string? color = null)
    {
        var block = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        if (color is not null) block.SetResourceReference(TextBlock.ForegroundProperty, color);
        return block;
    }
    public static Button Button(string title, Action action, bool primary = false)
    {
        var button = new Button { Content = title, Margin = new(0, 0, 8, 0) };
        if (primary) button.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
        button.Click += (_, _) => action(); return button;
    }
    public static void Field(Panel panel, string label, FrameworkElement input)
    {
        var heading = Text(label, 11, "Muted"); heading.Margin = new(0, 12, 0, 5);
        panel.Children.Add(heading); panel.Children.Add(input);
    }
    public static ComboBox Combo(params string[] options) => new() { ItemsSource = options, SelectedIndex = 0, MinWidth = 120 };
    public static Border Card(UIElement content, Thickness? padding = null)
    {
        var border = new Border { Child = content, CornerRadius = new(10), Padding = padding ?? new(14), BorderThickness = new(1), Margin = new(0, 0, 0, 10) };
        border.SetResourceReference(Border.BackgroundProperty, "Card"); border.SetResourceReference(Border.BorderBrushProperty, "Line"); return border;
    }
    public static void Error(Window? owner, Exception error)
    {
        if (owner is { IsVisible: true }) MessageBox.Show(owner, error.Message, "DeskPlan · 操作未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
        else MessageBox.Show(error.Message, "DeskPlan · 操作未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
