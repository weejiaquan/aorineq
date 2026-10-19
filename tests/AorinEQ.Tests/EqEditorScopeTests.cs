using System.Windows;
using AorinEQ.Core;
using AorinEQ.UI;
using Application = System.Windows.Application;

namespace AorinEQ.Tests;

/// <summary><see cref="EqEditorWindow.SelectScope"/> — AirPlay's "Edit EQ" opening the editor on
/// VB-Cable's tab — on the real editor window with a real (shared, reference-counted) audio
/// pipeline. App calls it right after showing the editor, which may or may not have loaded by then,
/// so both orders are driven. The "active device" the editor is handed is null, which makes its own
/// first choice the Global tab: a selection that lands anywhere else came from SelectScope. Any real
/// endpoint stands in for the cable, since the tabs list only real endpoints.</summary>
[Collection("Wpf")]
public class EqEditorScopeTests
{
    private readonly StaWpf _sta;
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public EqEditorScopeTests(StaWpf sta, Xunit.Abstractions.ITestOutputHelper output)
    {
        _sta = sta;
        _out = output;
    }

    /// <summary>Builds the editor, runs <paramref name="beforeShow"/>, shows it off-screen and lets it
    /// load, runs <paramref name="afterLoad"/>, and returns the selected tab's device id (null =
    /// Global). The window is closed whatever happens: Closed releases its pipeline consumer.</summary>
    private (string? Selected, bool Loaded) Drive(Action<EqEditorWindow> beforeShow, Action<EqEditorWindow> afterLoad) =>
        _sta.Run(() =>
        {
            Application.Current.Resources["AppFontFamily"] = new System.Windows.Media.FontFamily("Segoe UI");
            using var pipeline = new SharedAudioPipeline();
            var editor = new EqEditorWindow(() => Settings.Default, () => null, _ => null, pipeline)
            {
                Left = -32000,
                Top = -32000,
                ShowInTaskbar = false,
                ShowActivated = false,
            };
            try
            {
                beforeShow(editor);
                editor.Show();
                StaWpf.Pump();
                afterLoad(editor);
                StaWpf.Pump();
                return ((editor.ScopeTabs.SelectedItem as EqEditorWindow.ScopeTab)?.DeviceId, editor.IsLoaded);
            }
            finally
            {
                editor.Close();
            }
        });

    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void With_no_choice_the_editor_starts_on_Global_when_no_device_is_active()
    {
        var (selected, loaded) = Drive(_ => { }, _ => { });
        _out.WriteLine($"loaded={loaded}; selected tab device={selected ?? "(Global)"}");
        Assert.True(loaded);
        Assert.Null(selected);
    }

    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void A_scope_chosen_before_the_editor_loads_is_the_tab_it_opens_on()
    {
        var cable = AudioEndpoint.GetRenderEndpoints().First().Id;
        var (selected, loaded) = Drive(e => e.SelectScope(cable), _ => { });
        _out.WriteLine($"SelectScope({cable}) before load; loaded={loaded}; selected={selected ?? "(Global)"}");
        Assert.True(loaded);
        Assert.Equal(cable, selected);
    }

    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void A_scope_chosen_on_an_open_editor_moves_the_tab_to_it()
    {
        var cable = AudioEndpoint.GetRenderEndpoints().First().Id;
        var (selected, loaded) = Drive(_ => { }, e => e.SelectScope(cable));
        _out.WriteLine($"SelectScope({cable}) after load; loaded={loaded}; selected={selected ?? "(Global)"}");
        Assert.True(loaded);
        Assert.Equal(cable, selected);
    }
}
