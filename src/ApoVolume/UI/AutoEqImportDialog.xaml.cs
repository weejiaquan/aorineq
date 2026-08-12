using System.IO;
using System.Windows;
using System.Windows.Controls;
using ApoVolume.Core;

namespace ApoVolume.UI;

/// <summary>Search-and-import over the AutoEq results index. The index is fetched once and
/// cached on disk (refresh button refetches); Import downloads the selected model's
/// ParametricEQ file into the presets folder and closes with <see cref="ImportedPreset"/>
/// set. All network work is async — the dialog stays responsive.</summary>
public partial class AutoEqImportDialog : Window
{
    private const int MaxResults = 200;

    private static string IndexCachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "apo-volume", "autoeq-index.md");

    private IReadOnlyList<AutoEqEntry> _entries = Array.Empty<AutoEqEntry>();
    private bool _busy;

    /// <summary>The preset that was downloaded and saved, when DialogResult is true.</summary>
    public EqPreset? ImportedPreset { get; private set; }

    private sealed record ResultItem(AutoEqEntry Entry)
    {
        public override string ToString() =>
            Entry.Source.Length > 0 ? $"{Entry.Name}  —  {Entry.Source}" : Entry.Name;
    }

    public AutoEqImportDialog()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadIndexAsync(refresh: false);
    }

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
        catch (InvalidOperationException ex)
        {
            StatusText.Text = ex.Message;
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
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException
            or IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Import failed: {ex.Message}";
            _busy = false;
            ImportButton.IsEnabled = ResultsList.SelectedItem is not null;
        }
    }
}
