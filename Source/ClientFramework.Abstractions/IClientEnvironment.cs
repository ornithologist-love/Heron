namespace Heron.ClientFramework.Abstractions;

/// <summary>Stable host endpoints, resolved when an operation starts.</summary>
public interface IClientEnvironment
{
    string ServerUrl { get; }
    string WebBaseUrl => ServerUrl;
    string Username => "";

    /// <summary>本次启动的宿主版型标识（由消费方自定义，如 desktop / agent / lite）；宿主未提供时为空字符串。</summary>
    string Edition => "";
    string Channel => "stable";
    string HostVersion => "";

    /// <summary>Set before stopping plugins for an application update; ordinary plugin stops return false.</summary>
    bool IsExitingForUpdate => false;

    event Func<Task>? Changed { add { } remove { } }
}
