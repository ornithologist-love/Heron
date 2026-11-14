namespace Heron.ClientFramework.Abstractions;

/// <summary>向宿主请求选中已注册页面，不持有宿主窗口或 ViewModel。</summary>
public interface IClientPageNavigation
{
    /// <summary>实际选中的功能页；未完成初始导航时为空。</summary>
    string? CurrentPageId { get; }
    event Action<string?>? CurrentPageChanged;
    void NavigateToPage(string pageId);
}
