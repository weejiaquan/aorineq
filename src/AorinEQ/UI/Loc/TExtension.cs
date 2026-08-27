// Aliased: this project ships WindowsForms too, and System.Windows.Forms.Binding is a
// different thing entirely.
using Binding = System.Windows.Data.Binding;
using BindingMode = System.Windows.Data.BindingMode;
using System.Windows.Markup;

namespace AorinEQ.UI;

/// <summary>`Text="{loc:T settings.nav.volume}"` - a one-way binding to LocSource's indexer.
///
/// It returns a Binding rather than a string, and that is the whole point. A string would be
/// resolved once when the window was built and would stay wrong for the rest of the session after
/// a language switch, which is exactly the bug that makes most apps demand a restart.
///
/// LocXamlLiteralTests fails the build on any user-facing attribute that is NOT one of these, so
/// the 265th string added next year cannot quietly ship untranslated.</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }

    public TExtension(string key) => Key = key;

    /// <summary>The string-table key. ConstructorArgument is what allows the terse
    /// `{loc:T some.key}` form instead of `{loc:T Key=some.key}`.</summary>
    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]")
        {
            Source = LocSource.Instance,
            Mode = BindingMode.OneWay,
        }.ProvideValue(serviceProvider);
}
