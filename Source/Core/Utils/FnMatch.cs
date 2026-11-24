using System.Text.RegularExpressions;

namespace Heron.Core.Utils;

/// <summary>
///     fnmatch 通配符匹配工具类（支持 * 和 ? 通配符，大小写不敏感）。
/// </summary>
public static class FnMatch
{
    public static bool Match(string input, string pattern)
    {
        var regexPattern = "^" + Regex.Escape(pattern)
            .Replace(@"\*", ".*")
            .Replace(@"\?", ".") + "$";
        return Regex.IsMatch(input, regexPattern, RegexOptions.IgnoreCase);
    }
}
