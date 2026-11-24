using Avalonia.Controls;

namespace Heron.ClientFramework.Infrastructure;

public partial class LogWindow : Window
{
    public LogWindow()
    {
        InitializeComponent();
        Title = ClientHostProfile.DisplayName + " · 实时日志";
    }

    public void AttachLogPanel(Control panel)
    {
        DetachedLogHost.Content = panel;
    }

    public Control? DetachLogPanel()
    {
        var panel = DetachedLogHost.Content as Control;
        DetachedLogHost.Content = null;
        return panel;
    }
}
