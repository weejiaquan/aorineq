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
