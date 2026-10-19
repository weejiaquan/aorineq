using System.Windows;
using System.Windows.Controls;
using AorinEQ.Core;

namespace AorinEQ.UI;

/// <summary>The canvas: the volume bar, the AirPlay strip directly under it and the notice below,
/// each drawn by the production control fed from <see cref="SkinDocument.ToInfo"/> - so what the
/// designer shows is what the OSD will draw - plus the preview strip, which is view state only.</summary>
public partial class SkinDesignerWindow
{
    private SkinView? _skinView;
    private (SkinNotice Look, double Scale)? _shownNotice;

    internal SkinView? VolumeView => _skinView;

    private int PreviewPercent => (int)PreviewFill.Value;

    private bool PreviewMuted => PreviewMute.IsChecked == true;

    private string PreviewDeviceName =>
        string.IsNullOrWhiteSpace(PreviewDevice.Text) ? Loc.T("designer.notice.sample") : PreviewDevice.Text;

    private void RefreshCanvas()
    {
        PreviewFillLabel.Text = PreviewPercent + "%";
        RefreshVolumeBoard();
        RefreshAirPlayBoard();
        RefreshNoticeBoard();
    }

    private void RefreshVolumeBoard()
    {
        if (!_info.IsValid)
        {
            ShowVolumePlaceholder();
            return;
        }
        try
        {
            if (_skinView is null)
            {
                _skinView = new SkinView(_info);
                VolumeBoard.Child = _skinView;
            }
            else
            {
                _skinView.Update(_info);
            }
            _skinView.SetVolume(PreviewPercent, PreviewMuted);
        }
        catch (Exception ex) when (SkinFrames.IsImagingFailure(ex))
        {
            ShowVolumePlaceholder();
            StatusText.Text = Loc.T("designer.image-failed-to-decode") + ex.Message;
        }
    }

    /// <summary>No drawable volume bar: say what is missing, in the bar's own place.</summary>
    private void ShowVolumePlaceholder()
    {
        _skinView?.StopAnimations();
        _skinView = null;
        VolumePlaceholder.Text = _issues.FirstOrDefault(i =>
                SkinLayers.BoardOf(i.Layer) == SkinLayer.VolumeBar)?.Message
            ?? Loc.T("designer.canvas.no-art");
        VolumeBoard.Child = VolumePlaceholder;
    }

    private void RefreshAirPlayBoard()
    {
        bool skinned = _info.AirPlay is not null;
        try
        {
            AirPlayPreview.SetSkin(_info.AirPlay, _info.Scale);
        }
        catch (Exception ex) when (SkinFrames.IsImagingFailure(ex))
        {
            skinned = false;
            AirPlayPreview.SetSkin(null, 1.0);
            StatusText.Text = Loc.T("designer.image-failed-to-decode") + ex.Message;
        }
        // A native strip takes its host's width: the volume bar's, as in SkinOsdWindow.
        AirPlayBoard.Width = skinned ? double.NaN
            : _info.IsValid ? _info.Width * _info.Scale : OsdWindow.DarkPillWidth;
        AirPlayPreview.SetState(PreviewAirPlayState());
    }

    /// <summary>The notice as the OSD draws it: art at the skin's scale, a plate at 1 - and while
    /// the skin describes none, the app's own dark pill.</summary>
    private void RefreshNoticeBoard()
    {
        var look = _info.Notice ?? SkinLoader.DefaultNotice;
        (SkinNotice Look, double Scale) shown = (look, look.HasArt ? _info.Scale : 1.0);
        if (shown != _shownNotice)
        {
            _shownNotice = shown;
            NoticePreview.Apply(shown.Look, shown.Scale);
        }
        NoticePreview.SetText(PreviewDeviceName);
    }

    /// <summary>The strip's preview state: a live session, a chosen receiver not connected, or no
    /// receiver at all.</summary>
    internal AirPlayBarState PreviewAirPlayState() => PreviewAirPlayMode.SelectedIndex switch
    {
        0 => new AirPlayBarState(true, PreviewDeviceName, true, true, (int)PreviewLevel.Value),
        1 => new AirPlayBarState(true, PreviewDeviceName, false, false, (int)PreviewLevel.Value),
        _ => new AirPlayBarState(true, "", false, false, 0),
    };

    private void OnPreviewValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_ready) RefreshCanvas();
    }

    private void OnPreviewToggled(object sender, RoutedEventArgs e)
    {
        if (_ready) RefreshCanvas();
    }

    private void OnPreviewModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready) RefreshCanvas();
    }

    private void OnPreviewDeviceChanged(object sender, TextChangedEventArgs e)
    {
        if (_ready) RefreshCanvas();
    }
}
