using System;
using System.Collections.Generic;
using System.Linq;

namespace Heron.ClientFramework.Shell;

/// <summary>
///     侧边栏自定义顺序的持久化名单。存 pageKey 而不是下标：插件集合随版型
///     （如 full / lite / minimal）、外部包和用户开关变化，下标无法表达"这一格原本是谁"。
/// </summary>
public static class SidebarOrder
{
    /// <summary>归一化页面名单，保留输入顺序并移除空值与重复项。</summary>
    public static List<string> Capture(IEnumerable<string> visible) =>
        visible.Where(pageKey => !string.IsNullOrWhiteSpace(pageKey)).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>保留不可见页面的位置，以当前 UI 顺序填充其余位置。</summary>
    public static List<string> Merge(IEnumerable<string> saved, IEnumerable<string> visible)
    {
        var current = Capture(visible);
        var visibleKeys = new HashSet<string>(current, StringComparer.Ordinal);
        var queue = new Queue<string>(current);
        return Capture(saved.Concat(current))
            .Select(pageKey => visibleKeys.Contains(pageKey) ? queue.Dequeue() : pageKey)
            .ToList();
    }
}
