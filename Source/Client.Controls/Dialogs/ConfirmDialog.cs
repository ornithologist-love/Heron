using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Heron.ClientControls.Dialogs;

/// <summary>
/// A lightweight confirmation <see cref="Window"/> with configurable title, message,
/// confirm/cancel labels and danger styling. Callers own all display copy.
/// </summary>
public class ConfirmDialog : Window
{
    private readonly SelectableTextBlock _messageText;
    private readonly Button _okButton;
    private readonly Button _cancelButton;

    public ConfirmDialog()
    {
        Title = "确认";
        Width = 420;
        Height = 180;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowDecorations = WindowDecorations.Full;

        _okButton = new Button
        {
            Content = "确认",
            MinWidth = 80,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        _okButton.Classes.AddRange(["contentButton", "buttonPrimary", "buttonMedium"]);
        _okButton.Click += (_, _) =>
        {
            Result = true;
            Close();
        };

        _cancelButton = new Button
        {
            Content = "取消",
            MinWidth = 80,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        _cancelButton.Classes.AddRange(["contentButton", "buttonSecondary", "buttonMedium"]);
        _cancelButton.Click += (_, _) =>
        {
            Result = false;
            Close();
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 12,
            Margin = new Thickness(0, 16, 0, 0),
        };
        buttons.Children.Add(_okButton);
        buttons.Children.Add(_cancelButton);
        DockPanel.SetDock(buttons, Dock.Bottom);

        _messageText = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var root = new DockPanel { Margin = new Thickness(24) };
        root.Children.Add(buttons);
        root.Children.Add(_messageText);
        Content = root;
    }

    public ConfirmDialog(
        string title,
        string message,
        string confirmText = "确认",
        bool isDanger = false,
        string cancelText = "取消") : this()
    {
        Title = title;
        _messageText.Text = message;
        _okButton.Content = confirmText;
        _cancelButton.Content = cancelText;

        _okButton.Classes.Clear();
        _okButton.Classes.AddRange(["contentButton", isDanger ? "buttonDanger" : "buttonPrimary", "buttonMedium"]);
    }

    public bool Result { get; private set; }
}
