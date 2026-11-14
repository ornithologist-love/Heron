using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace Heron.ClientFramework.Abstractions;

/// <summary>
///     插件把「诊断面板」交给宿主托管窗口的最小契约：插件只提供控件与生命周期，窗口的创建、
///     唯一性和关闭全归宿主。契约在共享宿主 API 里，插件不引用主 Client。
/// </summary>
public interface IClientDiagnosticsWindow
{
    /// <summary>窗口标题。</summary>
    string Title { get; }

    /// <summary>
    ///     为主机焦点创建面板视图；宿主每次打开窗口调用一次，关闭时释放返回的控件。
    /// </summary>
    IClientDiagnosticsPanel OpenPanel();

    /// <summary>窗口销毁或插件停止时调用：取消采集并退回未初始化状态，下次打开重新采集。</summary>
    ValueTask ClosePanelAsync(IClientDiagnosticsPanel panel, CancellationToken cancellationToken);
}

/// <summary>托管窗口里的一次面板实例；<see cref="View" /> 由插件自己的程序集提供。</summary>
public interface IClientDiagnosticsPanel
{
    Control View { get; }
}
