using System.IO;
using System.Windows;
using System.Windows.Controls;
using AorinEQ.Core;

namespace AorinEQ.UI;

/// <summary>Search-and-import over the AutoEq results index. The index is fetched once and
/// cached on disk (refresh button refetches); Import downloads the selected model's
/// ParametricEQ file into the presets folder and closes with <see cref="ImportedPreset"/>
/// set. All network work is async — the dialog stays responsive.</summary>
public partial class AutoEqImportDialog : Window
{
    private const int MaxResults = 200;

    private static string IndexCachePath => Path.Combine(ApoPaths.GetStateRoot(), "autoeq-index.md");

    private IReadOnlyList<AutoEqEntry> _entries = Array.Empty<AutoEqEntry>();
    private bool _busy;

    /// <summary>The preset that was downloaded and saved, when DialogResult is true.</summary>
    public EqPreset? ImportedPreset { get; private set; }

    private sealed record ResultItem(AutoEqEntry Entry)
    {
        public override string ToString() =>
            Entry.Source.Length > 0 ? $"{Entry.Name}  —  {Entry.Source}" : Entry.Name;
    }

    /// <summary><paramref name="initialSearch"/> pre-fills the search box — how an
    /// <c>aorineq://autoeq?model=…</c> deep link lands the user on their headphone. It only
    /// narrows the list; the download still needs an explicit pick and Import.</summary>
    public AutoEqImportDialog(string initialSearch = "")
    {
        InitializeComponent();
        SearchBox.Text = initialSearch;
        Loaded += async (_, _) =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            await LoadIndexAsync(refresh: false);
        };
    }

    /// <summary>Index load/refresh. Catches EVERYTHING: this runs from async void handlers
    /// (Loaded, the refresh button), where an escaping exception would crash the process
    /// rather than fail a dialog — and the finally must always clear <see cref="_busy"/> or
    /// the dialog would be stuck refusing every later action.</summary>
    private async Task LoadIndexAsync(bool refresh)
    {
        if (_busy)
            return;
        _busy = true;
        StatusText.Text = refresh ? "Refreshing index…" : "Loading index…";
        try
        {
            var text = await AutoEqIndex.FetchIndexAsync(IndexCachePath, refresh);
            _entries = AutoEqIndex.ParseIndex(text);
            StatusText.Text = $"{_entries.Count} profiles available.";
            UpdateResults();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex is InvalidOperationException
                ? ex.Message
                : $"Couldn't load the AutoEq index: {ex.Message}";
        }
        finally
        {
            _busy = false;
        }
    }

    private void UpdateResults()
    {
        var hits = AutoEqIndex.Search(_entries, SearchBox.Text, MaxResults);
        ResultsList.ItemsSource = hits.Select(h => new ResultItem(h)).ToArray();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => UpdateResults();

    private async void OnRefreshIndex(object sender, RoutedEventArgs e) =>
        await LoadIndexAsync(refresh: true);

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ImportButton.IsEnabled = ResultsList.SelectedItem is ResultItem && !_busy;

    /// <summary>async void by necessity (a WPF event handler), so nothing may escape: any
    /// failure becomes a status line, and the busy/button state is always restored. Success
    /// closes the dialog, in which case _busy staying true is correct.</summary>
    private async void OnImport(object sender, RoutedEventArgs e)
    {
        if (_busy || ResultsList.SelectedItem is not ResultItem item)
            return;
        _busy = true;
        ImportButton.IsEnabled = false;
        StatusText.Text = $"Downloading {item.Entry.Name}…";
        try
        {
            ImportedPreset = await AutoEqIndex.DownloadPresetAsync(item.Entry, ApoPaths.GetPresetsRoot());
            DialogResult = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Import failed: {ex.Message}";
            _busy = false;
            ImportButton.IsEnabled = ResultsList.SelectedItem is not null;
        }
    }
}
