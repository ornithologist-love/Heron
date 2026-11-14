using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Heron.ClientControls.Gallery.Pages;

public partial class ModalDialogPage : UserControl
{
    public ModalDialogPage()
    {
        InitializeComponent();
        BackgroundPage.IsEnabled = false;
    }

    internal void ApplyState(string state)
    {
        SampleText.Text = state == "long"
            ? string.Join("\n\n", Enumerable.Repeat("长内容示例：缩小窗口时正文滚动，标题、关闭和底部操作保持可见。", 24))
            : "内容和底部按钮由调用方提供，关闭按钮与 Esc 由公共组件处理。";
        DemoDialog.DialogHeight = state == "long" ? 600 : double.NaN;
    }

    private void Open_Click(object? sender, RoutedEventArgs e) => Open("normal");
    private void Long_Click(object? sender, RoutedEventArgs e) => Open("long");
    private void Open(string state)
    {
        ApplyState(state);
        BackgroundPage.IsEnabled = false;
        DemoDialog.IsOpen = true;
    }
    private void Close_Click(object? sender, RoutedEventArgs e) => DemoDialog.Close();
    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        Feedback.Text = "已保存示例（不写入真实设置）";
        DemoDialog.Close();
    }
    private void Dialog_Closed(object? sender, EventArgs e) => BackgroundPage.IsEnabled = true;
}
