using Heron.ClientFramework.Abstractions;

namespace Heron.ClientFramework.Shell;

/// <summary>Placeholder page shown when no navigation entry is active or startup failed.</summary>
public sealed class EmptyWorkspacePageViewModel : ViewModelBase
{
    public EmptyWorkspacePageViewModel(
        string? error = null,
        string defaultTitle = "选择需要的功能",
        string errorTitle = "客户端功能不可用",
        string defaultMessage = "点击右上角“设置”开启功能，重启后即可使用。")
    {
        HasError = !string.IsNullOrWhiteSpace(error);
        Title = HasError ? errorTitle : defaultTitle;
        Message = HasError ? error! : defaultMessage;
    }

    public bool HasError { get; }
    public string Title { get; }
    public string Message { get; }
}
