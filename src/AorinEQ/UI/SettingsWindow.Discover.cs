using System.Windows;
using System.Windows.Controls;
using AorinEQ.Core;
using Wpf.Ui.Controls;
using Orientation = System.Windows.Controls.Orientation;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = Wpf.Ui.Controls.TextBlock;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;

namespace AorinEQ.UI;

/// <summary>The Discover page: what AorinEQ can do, and the language picker.
///
/// Built in code rather than declared in XAML because the cards come from
/// <see cref="FeatureCatalogue"/> - a feature added there appears here without this file or the
/// XAML being touched - and because each card shows LIVE state, which XAML cannot read.</summary>
public partial class SettingsWindow
{
    /// <summary>Raised when the user picks a language. App persists it; the switch itself has
    /// already been applied by the time this fires, because bound strings repaint from
    /// <see cref="Loc.LanguageChanged"/>.</summary>
    public event Action<string>? LanguageChanged;

    /// <summary>Raised the first time Discover is shown, so App can persist HasSeenDiscover and
    /// stop nudging.</summary>
    public event Action? DiscoverSeen;

    private bool _discoverSeen;

    /// <summary>Reads live app state for a feature card. Set by App, because none of this is
    /// reachable from Core: whether a receiver is connected, which skin is loaded, how many
    /// widgets exist.</summary>
    public Func<Feature, FeatureState>? FeatureStateProvider { get; set; }

    private void BuildDiscover()
    {
        PopulateLanguages();
        BuildFeatureCards();
    }

    private void PopulateLanguages()
    {
        LanguageCombo.Items.Clear();
        LanguageCombo.Items.Add(new ComboBoxItem
        {
            Content = Loc.T("settings.language.auto"),
            Tag = Languages.Auto,
        });

        foreach (var language in Languages.All)
        {
            LanguageCombo.Items.Add(new ComboBoxItem
            {
                // Each language names ITSELF, in its own script. A list of languages written in
                // English is no use to the person who needs the list.
                Content = Loc.T($"language.{language.ToLowerInvariant()}.endonym"),
                Tag = language,
            });
        }

        SelectByTag(LanguageCombo, _language);
        UpdateCorrectionLink();
    }

    /// <summary>Shows the "suggest a correction" link for any language other than English, with a
    /// prefilled issue behind it.
    ///
    /// The four translations ship machine-drafted and unreviewed, deliberately. That only works if
    /// reporting a bad one is a single click - a correction route nobody can find produces no
    /// corrections, and the translation stays wrong forever.</summary>
    private void UpdateCorrectionLink()
    {
        var active = Loc.Language;

        if (active == Languages.En)
        {
            TranslationCorrectionLink.Visibility = Visibility.Collapsed;
            return;
        }

        TranslationCorrectionLink.Visibility = Visibility.Visible;
        TranslationCorrectionLink.NavigateUri =
            "https://github.com/weejiaquan/aorineq/issues/new" +
            $"?labels={Uri.EscapeDataString("translation," + active)}" +
            $"&title={Uri.EscapeDataString($"[{active}] Translation correction")}" +
            $"&body={Uri.EscapeDataString(CorrectionIssueBody(active))}";
    }

    /// <summary>Prefilled so a report is triageable without the reporter having to know what to
    /// include - which is most of why bug reports go unanswered.</summary>
    private string CorrectionIssueBody(string language) =>
        $"AorinEQ {VersionText.Text}\n" +
        $"Language: {language}\n" +
        $"Windows: {Environment.OSVersion.Version}\n\n" +
        "Where in the app (screen and label):\n\n" +
        "What it currently says:\n\n" +
        "What it should say:\n";

    private void BuildFeatureCards()
    {
        DiscoverCards.Children.Clear();

        foreach (var feature in FeatureCatalogue.Features)
        {
            var state = FeatureStateProvider?.Invoke(feature) ?? new FeatureState(false, "");

            var text = new StackPanel();
            text.Children.Add(new TextBlock
            {
                Text = feature.Title,
                Style = (Style)Resources["RowTitle"],
            });
            text.Children.Add(new TextBlock
            {
                Text = feature.Pitch,
                Style = (Style)Resources["RowSubtitle"],
            });
            text.Children.Add(new TextBlock
            {
                Text = state.IsOn
                    ? (state.Detail.Length > 0
                        ? Loc.T("discover.state.on-detail", state.Detail)
                        : Loc.T("discover.state.on"))
                    : Loc.T("discover.state.off"),
                Style = (Style)Resources["RowSubtitle"],
                Margin = new Thickness(0, 6, 0, 0),
            });

            var go = new Wpf.Ui.Controls.Button
            {
                // "Set up" when it is off and "Open" when it is on: the label says what the click
                // is for, which is the difference between a card that reads as an advert and one
                // that reads as a next step.
                Content = state.IsOn ? Loc.T("discover.open") : Loc.T("discover.set-up"),
                MinWidth = 92,
            };
            var target = feature.PageTag;
            go.Click += (_, _) => Navigate(target);

            DiscoverCards.Children.Add(new CardControl
            {
                Header = text,
                Content = go,
                Icon = new SymbolIcon { Symbol = ParseSymbol(feature.Glyph) },
                Margin = (Thickness)Resources["RowMargin"],
            });
        }
    }

    /// <summary>The catalogue stores a symbol by NAME so Core does not reference WPF-UI. An
    /// unknown name would otherwise throw while building the page, so it degrades to a neutral
    /// glyph - and FeatureCatalogueTests fails the build on one that does not parse, which is
    /// where a typo should be caught.</summary>
    private static SymbolRegular ParseSymbol(string glyph) =>
        Enum.TryParse<SymbolRegular>(glyph, out var symbol) ? symbol : SymbolRegular.Circle24;

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        if (LanguageCombo.SelectedItem is not ComboBoxItem { Tag: string setting }) return;

        _language = setting;
        App.ApplyLanguage(setting);
        LanguageChanged?.Invoke(setting);

        // Bound strings repaint themselves; these are built in code and have to be redone.
        // PopulateLanguages included: "Follow Windows" is itself a translated string, so without
        // this the picker keeps the name it had in the language you just left.
        //
        // _initializing guards the rebuild, because clearing and refilling the combo raises
        // SelectionChanged again and would re-enter this handler.
        _initializing = true;
        try
        {
            PopulateLanguages();
        }
        finally
        {
            _initializing = false;
        }

        UpdateCorrectionLink();
        BuildFeatureCards();
    }

    /// <summary>Called whenever Discover is navigated to. The first time, it tells App - which
    /// stops the one-time nudge and moves the landing page to Volume from then on.</summary>
    private void OnDiscoverShown()
    {
        BuildFeatureCards();

        if (_discoverSeen) return;
        _discoverSeen = true;
        DiscoverSeen?.Invoke();
    }
}
