using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AorinEQ.Core;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;

namespace AorinEQ.UI;

/// <summary>The Layers tree: a fixed mirror of <see cref="SkinLayer"/>. Optional layers that are
/// not in use are dimmed, a layer with an issue carries ⚠ with the reason, and a skin with no
/// AirPlay strip offers to add one in place of the strip's parts.</summary>
public partial class SkinDesignerWindow
{
    private static readonly (SkinLayer Layer, SkinLayer[] Children)[] LayerTree =
    [
        (SkinLayer.SkinInfo, []),
        (SkinLayer.VolumeBar, [SkinLayer.VolumeEmpty, SkinLayer.VolumeFull, SkinLayer.VolumeMuted, SkinLayer.PercentText]),
        (SkinLayer.AirPlay, [SkinLayer.AirPlayEmpty, SkinLayer.AirPlayFull, SkinLayer.AirPlayName,
            SkinLayer.AirPlayLevel, SkinLayer.AirPlayDropdown, SkinLayer.AirPlayPower]),
        (SkinLayer.Notice, [SkinLayer.NoticeArt, SkinLayer.NoticeText, SkinLayer.NoticeGlyph]),
    ];

    private readonly HashSet<SkinLayer> _collapsed = [];
    private bool _refreshingLayers;

    internal static string LayerName(SkinLayer layer) => layer switch
    {
        SkinLayer.SkinInfo => Loc.T("designer.layer.skin-info"),
        SkinLayer.VolumeBar => Loc.T("designer.layer.volume-bar"),
        SkinLayer.VolumeEmpty or SkinLayer.AirPlayEmpty => Loc.T("designer.layer.empty-art"),
        SkinLayer.VolumeFull or SkinLayer.AirPlayFull => Loc.T("designer.layer.full-art"),
        SkinLayer.VolumeMuted => Loc.T("designer.layer.muted-art"),
        SkinLayer.PercentText => Loc.T("designer.text.percent-text"),
        SkinLayer.AirPlay => Loc.T("designer.layer.airplay"),
        SkinLayer.AirPlayName => Loc.T("designer.layer.name-text"),
        SkinLayer.AirPlayLevel => Loc.T("designer.layer.level-text"),
        SkinLayer.AirPlayDropdown => Loc.T("designer.layer.dropdown-area"),
        SkinLayer.AirPlayPower => Loc.T("designer.layer.power-area"),
        SkinLayer.Notice => Loc.T("designer.text.notice"),
        SkinLayer.NoticeArt => Loc.T("designer.layer.notice-art"),
        SkinLayer.NoticeText => Loc.T("designer.layer.notice-text"),
        _ => Loc.T("designer.layer.notice-icon"),
    };

    /// <summary>Whether an optional layer is in use; one that is not is drawn dimmed.</summary>
    private bool IsLayerSet(SkinLayer layer)
    {
        var doc = _history.Current;
        var c = doc.Config;
        return layer switch
        {
            SkinLayer.VolumeMuted => doc.MutedPath is not null,
            SkinLayer.PercentText => c.Text is { Show: true },
            SkinLayer.AirPlayName => c.AirPlay?.Name is { Show: true },
            SkinLayer.AirPlayLevel => c.AirPlay?.Percent is { Show: true },
            SkinLayer.AirPlayPower => c.AirPlay?.ConnectHit is not null,
            SkinLayer.Notice or SkinLayer.NoticeText or SkinLayer.NoticeGlyph => c.Notice is not null,
            SkinLayer.NoticeArt => c.Notice?.HasArt == true,
            _ => true,
        };
    }

    /// <summary>The node the tree shows <paramref name="layer"/> as: while there is no strip its
    /// parts are not in the tree, so they are the strip's own node.</summary>
    private SkinLayer ShownLayer(SkinLayer layer) =>
        SkinLayers.BoardOf(layer) == SkinLayer.AirPlay && _history.Current.Config.AirPlay is null ? SkinLayer.AirPlay : layer;

    private void RefreshLayers()
    {
        _refreshingLayers = true;
        try
        {
            foreach (var node in LayersTree.Items.OfType<TreeViewItem>())
            {
                if (node.Tag is not SkinLayer layer) continue;
                if (node.IsExpanded) _collapsed.Remove(layer);
                else _collapsed.Add(layer);
            }
            LayersTree.Items.Clear();
            foreach (var (layer, children) in LayerTree)
            {
                var node = LayerItem(layer);
                node.IsExpanded = !_collapsed.Contains(layer);
                if (layer == SkinLayer.AirPlay && _history.Current.Config.AirPlay is null)
                {
                    // A strip the loader could not load: removing it is the explicit decision that
                    // lets the skin save (adding a new one replaces it).
                    if (_history.Current.AirPlayError is not null) node.Items.Add(RemoveAirPlayItem());
                    node.Items.Add(AddAirPlayItem());
                }
                else
                    foreach (var child in children) node.Items.Add(LayerItem(child));
                LayersTree.Items.Add(node);
            }
        }
        finally
        {
            _refreshingLayers = false;
        }
    }

    private TreeViewItem LayerItem(SkinLayer layer)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock { Text = LayerName(layer), Opacity = IsLayerSet(layer) ? 1.0 : 0.5 });
        var issues = _issues.Where(i => i.Layer == layer).ToList();
        if (issues.Count > 0)
            header.Children.Add(new TextBlock
            {
                Text = "⚠",
                Margin = new Thickness(6, 0, 0, 0),
                Foreground = TryFindResource("SystemFillColorCautionBrush") as Brush ?? Brushes.Orange,
                ToolTip = string.Join(Environment.NewLine, issues.Select(i => i.Message)),
            });
        return new TreeViewItem { Header = header, Tag = layer, IsSelected = layer == _selection };
    }

    private TreeViewItem AddAirPlayItem() => ButtonItem(Loc.T("designer.layers.add-airplay"), AddAirPlay);

    private TreeViewItem RemoveAirPlayItem() => ButtonItem(Loc.T("designer.airplay.remove"), RemoveAirPlay);

    private static TreeViewItem ButtonItem(string label, Action run)
    {
        var button = new Button { Content = label, Padding = new Thickness(8, 2, 8, 2) };
        button.Click += (_, _) => run();
        return new TreeViewItem { Header = button, Focusable = false };
    }

    private void RemoveAirPlay()
    {
        Commit(SkinEdits.RemoveAirPlay(_history.Current));
        Select(SkinLayer.AirPlay);
    }

    /// <summary>Asks for the strip's two images and adds it, measured from them.</summary>
    private void AddAirPlay()
    {
        if (AskImage(Loc.T("designer.choose-airplay-empty")) is not { } empty) return;
        if (AskImage(Loc.T("designer.choose-airplay-full")) is not { } full) return;
        Commit(SkinEdits.AddAirPlay(_history.Current, empty, full, _probe));
        Select(SkinLayer.AirPlay);
    }

    /// <summary>A tree click. The tree also reports the item a rebuild marked selected, on a later
    /// turn and so outside <see cref="_refreshingLayers"/>; that is the selection already shown,
    /// and rebuilding Properties for it would replace the field being typed in.</summary>
    private void OnLayerSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_refreshingLayers || LayersTree.SelectedItem is not TreeViewItem { Tag: SkinLayer layer } || layer == _selection)
            return;
        SetSelection(layer);
    }
}
