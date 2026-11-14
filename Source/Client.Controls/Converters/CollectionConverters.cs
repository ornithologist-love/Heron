using System.Collections;
using Avalonia.Data.Converters;

namespace Heron.ClientControls.Converters;

/// <summary>
/// Shared collection emptiness converters for Popup empty-state binding
/// (see Client.Controls.Gallery "Popup 弹出规范" page, group 2).
/// Business ViewModels bind IsEnabled="{Binding Items, Converter={x:Static
/// CollectionConverters.IsNotEmpty}}" instead of adding a dedicated HasXxx
/// property when the ItemsSource collection itself is the only signal needed.
/// </summary>
public static class CollectionConverters
{
    /// <summary>True when the bound value is null, not an IEnumerable, or an empty collection.</summary>
    public static readonly FuncValueConverter<object?, bool> IsEmpty =
        new(value => !HasAny(value));

    /// <summary>True when the bound value is a non-null, non-empty collection.</summary>
    public static readonly FuncValueConverter<object?, bool> IsNotEmpty =
        new(HasAny);

    private static bool HasAny(object? value)
    {
        if (value is not IEnumerable enumerable)
        {
            return false;
        }

        var enumerator = enumerable.GetEnumerator();
        try
        {
            return enumerator.MoveNext();
        }
        finally
        {
            if (enumerator is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
