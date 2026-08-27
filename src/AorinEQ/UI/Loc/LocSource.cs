using System.ComponentModel;
using AorinEQ.Core;

namespace AorinEQ.UI;

/// <summary>The binding source every localised string in XAML points at.
///
/// A binding to an INDEXER can be invalidated wholesale: raising PropertyChanged for "Item[]"
/// tells WPF that every indexed binding on this object is stale, so ONE event repaints every
/// string in every open window. That is what makes switching language immediate rather than
/// "restart to apply" - and "restart to apply" is what users read as an app that cannot really
/// change language.
///
/// Lives in AorinEQ.UI rather than a namespace called Loc on purpose: a namespace segment with
/// that name shadows <see cref="AorinEQ.Core.Loc"/> and forces every call site inside it to spell
/// out Core.Loc.</summary>
public sealed class LocSource : INotifyPropertyChanged
{
    /// <summary>The single instance every binding shares. There is one active language per
    /// process, so a second instance would only be a second thing to keep in step.</summary>
    public static LocSource Instance { get; } = new();

    private LocSource() =>
        Loc.LanguageChanged += () =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));

    /// <summary>The string for a key, in whatever language is active right now.</summary>
    public string this[string key] => Loc.T(key);

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Bindings onto <see cref="LocSource"/>, for text that is created in code.
///
/// Code-created controls are the one place a language switch can leave stale text behind: XAML
/// strings repaint because they are bindings, but a TextBlock whose Text was ASSIGNED keeps
/// whatever it was given. The help affordances, the help panel and the Discover cards are all
/// built in code, so they bind through here instead of assigning.</summary>
public static class LocBinding
{
    /// <summary>A one-way binding to the string for <paramref name="key"/>.</summary>
    public static System.Windows.Data.Binding For(string key) =>
        new($"[{key}]")
        {
            Source = LocSource.Instance,
            Mode = System.Windows.Data.BindingMode.OneWay,
        };

    /// <summary>Applies <see cref="For"/> to a property.</summary>
    public static void Set(System.Windows.DependencyObject target,
        System.Windows.DependencyProperty property, string key) =>
        System.Windows.Data.BindingOperations.SetBinding(target, property, For(key));

    /// <summary>A binding for a string with one substitution, where BOTH the template and the
    /// argument are themselves localised - "Help for {0}" and the control's title.
    ///
    /// A plain StringFormat cannot do this: the format string would be captured once and go stale
    /// on a language change, leaving a sentence half in each language.</summary>
    public static void SetFormatted(System.Windows.DependencyObject target,
        System.Windows.DependencyProperty property, string templateKey, string argumentKey)
    {
        var multi = new System.Windows.Data.MultiBinding { Converter = FormatConverter.Instance };
        multi.Bindings.Add(For(templateKey));
        multi.Bindings.Add(For(argumentKey));
        System.Windows.Data.BindingOperations.SetBinding(target, property, multi);
    }

    private sealed class FormatConverter : System.Windows.Data.IMultiValueConverter
    {
        public static readonly FormatConverter Instance = new();

        public object Convert(object[] values, Type targetType, object? parameter,
            System.Globalization.CultureInfo culture) =>
            values.Length == 2 && values[0] is string template && values[1] is string argument
                ? string.Format(culture, template, argument)
                : "";

        public object[] ConvertBack(object value, Type[] targetTypes, object? parameter,
            System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException("Localised text is one-way.");
    }
}

