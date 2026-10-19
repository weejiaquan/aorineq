using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using AorinEQ.Core;
using AorinEQ.UI;
using AorinEQ.UI.DesignerFields;
using Xunit.Abstractions;
using Application = System.Windows.Application;
using Color = System.Drawing.Color;
using TextBox = System.Windows.Controls.TextBox;

namespace AorinEQ.Tests;

/// <summary>The Skin Designer as a user drives it: a real window over a real skins folder in a
/// temp directory, edited through its Properties fields and its internal edit API, saved by the
/// real writer and read back by the real loader.</summary>
[Collection("Wpf")]
public class SkinDesignerWindowTests : IDisposable
{
    private readonly StaWpf _sta;
    private readonly ITestOutputHelper _out;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aorineq-designer-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string _src;

    public SkinDesignerWindowTests(StaWpf sta, ITestOutputHelper output)
    {
        _sta = sta;
        _out = output;
        _root = Path.Combine(_dir, "skins");
        _src = Path.Combine(_dir, "src");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_src);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string MakeSkin(string name, string json, bool strip = false, (int W, int H)? noticeArt = null)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        RealPngs.WriteSolid(Path.Combine(folder, "empty.png"), 200, 40, Color.DarkRed);
        RealPngs.WriteSolid(Path.Combine(folder, "full.png"), 200, 40, Color.Lime);
        if (strip)
        {
            RealPngs.WriteSolid(Path.Combine(folder, "airplay-empty.png"), 200, 24, Color.DimGray);
            RealPngs.WriteSolid(Path.Combine(folder, "airplay-full.png"), 200, 24, Color.SteelBlue);
        }
        if (noticeArt is { } a) RealPngs.WriteSolid(Path.Combine(folder, "notice.png"), a.W, a.H, Color.Navy);
        File.WriteAllText(Path.Combine(folder, "skin.json"), json);
        return folder;
    }

    private string Src(string name, int width, int height, Color color)
    {
        var path = Path.Combine(_src, name);
        RealPngs.WriteSolid(path, width, height, color);
        return path;
    }

    /// <summary>Builds and shows a designer on the STA thread over the temp skins folder, and
    /// really closes it afterwards: Dispose first (timers, test OSD, scratch folder, subscriptions),
    /// after which Close no longer hides or prompts - so no test leaks a window.</summary>
    private T WithDesigner<T>(Func<SkinDesignerWindow, T> body, Settings? settings = null) =>
        _sta.Run(() =>
        {
            // The window's XAML resolves this from App.xaml; the fixture's Application is not App.
            Application.Current.Resources["AppFontFamily"] = new System.Windows.Media.FontFamily("Segoe UI");
            var designer = new SkinDesignerWindow(() => settings ?? Settings.Default, _root);
            designer.Show();
            try { return body(designer); }
            finally
            {
                designer.Dispose();
                designer.Close();
            }
        });

    private static IEnumerable<T> Fields<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var nested in Fields<T>(child)) yield return nested;
        }
    }

    private static NumberField Number(SkinDesignerWindow d, string label) =>
        Fields<NumberField>(d.PropertiesHost).First(f => f.Label.Text == label);

    private static ToggleField Toggle(SkinDesignerWindow d, string label) =>
        Fields<ToggleField>(d.PropertiesHost).First(f => (string)f.Switch.Content == label);

    /// <summary>Types into a Properties box and presses Enter. A panel rebuilt by Select is only
    /// templated (and so has a PresentationSource) after a layout turn, as before any real keystroke.</summary>
    private static void Type(TextBox box, string text)
    {
        StaWpf.Pump();
        box.Text = text;
        box.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(box)!, 0, Key.Enter)
        { RoutedEvent = UIElement.PreviewKeyDownEvent });
    }

    private static string N(double value) => value.ToString(System.Globalization.CultureInfo.CurrentCulture);

    [Fact]
    public void Loading_a_skin_makes_it_the_document_and_the_window_is_clean()
    {
        var folder = MakeSkin("plate", """{"scale":1.5,"notice":{"background":"#FF102030","padding":{"x":10,"y":6},"text":{"align":"center"}}}""");
        var r = WithDesigner(d =>
        {
            bool loaded = d.LoadSkin("plate");
            return (loaded, d.Document, Dirty: d.History.IsDirty, d.Title, Selected: (d.SkinSelect.SelectedItem as ComboBoxItem)?.Tag,
                Scale: d.VolumeView?.RenderScale, Padding: d.NoticePreview.PlateBorder.Padding.Left);
        });
        var expected = SkinDocument.FromInfo(SkinLoader.Load(folder));
        _out.WriteLine($"{r.Document}");
        Assert.True(r.loaded);
        Assert.Equal(expected, r.Document);
        Assert.False(r.Dirty);
        Assert.DoesNotContain("•", r.Title);
        Assert.Equal("plate", r.Selected);
        Assert.Equal(1.5, r.Scale);
        Assert.Equal(10, r.Padding);
    }

    [Fact]
    public void The_layers_tree_mirrors_every_layer_and_flags_problems()
    {
        MakeSkin("with-strip", "{}", strip: true);
        var r = WithDesigner(d =>
        {
            d.LoadSkin("with-strip");
            var roots = d.LayersTree.Items.OfType<TreeViewItem>().ToList();
            var tags = roots.SelectMany(n => n.Items.OfType<TreeViewItem>().Prepend(n)).Select(i => i.Tag).OfType<SkinLayer>().ToList();
            d.Commit(d.Document with { FullPath = Path.Combine(_src, "missing.png") });
            var flagged = d.LayersTree.Items.OfType<TreeViewItem>().SelectMany(n => n.Items.OfType<TreeViewItem>())
                .First(i => (SkinLayer)i.Tag == SkinLayer.VolumeFull);
            var header = (StackPanel)flagged.Header;
            return (Roots: roots.Count, Tags: tags, Badge: header.Children.Count, Tip: (header.Children[^1] as FrameworkElement)?.ToolTip);
        });
        _out.WriteLine($"{r.Roots} roots; layers [{string.Join(", ", r.Tags)}]; badge tooltip '{r.Tip}'");
        Assert.Equal(4, r.Roots);
        Assert.Equal(Enum.GetValues<SkinLayer>().OrderBy(l => l), r.Tags.OrderBy(l => l));
        Assert.Equal(2, r.Badge);
        Assert.Equal(new SkinIssue(SkinLayer.VolumeFull, SkinIssueKind.ArtUnreadable, ["missing.png"]).Message, r.Tip);
    }

    [Fact]
    public void A_property_edit_commits_and_undo_restores_it()
    {
        MakeSkin("edit", "{}");
        var r = WithDesigner(d =>
        {
            d.LoadSkin("edit");
            d.Select(SkinLayer.SkinInfo);
            Type(Number(d, Loc.T("designer.text.scale")).Box, N(2));
            var after = (d.Document.Config.Scale, d.VolumeView!.RenderScale, d.History.IsDirty, d.Title, d.UndoButton.IsEnabled);
            d.History.Undo();
            return (after, Undone: d.Document.Config.Scale, UndoneRender: d.VolumeView!.RenderScale, Clean: !d.History.IsDirty);
        });
        _out.WriteLine($"{r}");
        Assert.Equal((2.0, 2.0, true), (r.after.Scale, r.after.RenderScale, r.after.IsDirty));
        Assert.StartsWith("•", r.after.Title);
        Assert.True(r.after.IsEnabled);
        Assert.Equal((1.0, 1.0, true), (r.Undone, r.UndoneRender, r.Clean));
    }

    [Fact]
    public void Saving_writes_the_skin_and_clears_dirty()
    {
        var folder = MakeSkin("save-me", "{}");
        var r = WithDesigner(d =>
        {
            var saved = new List<string>();
            d.SkinSaved += saved.Add;
            d.LoadSkin("save-me");
            d.Commit(SkinEdits.SetScale(d.Document, 2));
            bool ok = d.Save();
            return (ok, Saved: saved.ToArray(), Dirty: d.History.IsDirty, d.Title);
        });
        var json = File.ReadAllText(Path.Combine(folder, "skin.json"));
        _out.WriteLine($"{r}; json {json}");
        Assert.True(r.ok);
        Assert.Equal(new[] { "save-me" }, r.Saved);
        Assert.False(r.Dirty);
        Assert.DoesNotContain("•", r.Title);
        Assert.Equal(2.0, SkinLoader.Load(folder).Scale);
    }

    /// <summary>Review focus: save, then undo one step - what is on screen is not what is on disk,
    /// so leaving must ask again; redo to the saved state and it must not.</summary>
    [Fact]
    public void Undo_after_save_makes_the_designer_ask_again()
    {
        MakeSkin("undo-save", "{}");
        var r = WithDesigner(d =>
        {
            d.LoadSkin("undo-save");
            d.Commit(SkinEdits.SetScale(d.Document, 2));
            d.Save();
            d.History.Undo();
            var afterUndo = (d.History.IsDirty, d.Title);
            d.History.Redo();
            return (afterUndo, Redone: d.History.IsDirty);
        });
        _out.WriteLine($"expected dirty with a • title after undo, clean after redo; actual after undo {r.afterUndo}, after redo dirty={r.Redone}");
        Assert.True(r.afterUndo.IsDirty);
        Assert.StartsWith("•", r.afterUndo.Title);
        Assert.False(r.Redone);
    }

    [Fact]
    public void A_new_document_cannot_be_saved_and_says_why()
    {
        var r = WithDesigner(d =>
        {
            d.NewDocument();
            bool saved = d.Save();
            return (saved, d.SaveButton.IsEnabled, Tip: d.SaveButton.ToolTip as string, d.Document.Name, d.VolumePlaceholder.Text);
        });
        _out.WriteLine($"{r}");
        Assert.False(r.saved);
        Assert.False(r.IsEnabled);
        Assert.Contains(SkinDesignerWindow.LayerName(SkinLayer.VolumeEmpty), r.Tip);
        Assert.Contains(Loc.T("designer.issue.missing-art"), r.Tip);
        Assert.Equal(Loc.T("designer.untitled"), r.Name);
        Assert.Empty(Directory.GetDirectories(_root));
        Assert.Equal(Loc.T("designer.issue.missing-art"), r.Text);
    }

    /// <summary>Review focus: a skin written before the AirPlay strip existed.</summary>
    [Fact]
    public void A_legacy_skin_offers_to_add_a_strip_and_saves_without_one()
    {
        var folder = SkinWriter.Save(_root, "legacy", Src("e.png", 200, 40, Color.Red), Src("f.png", 200, 40, Color.Lime),
            new SkinConfig(new SkinText(true, 12, 8), 1.0));
        var before = File.ReadAllBytes(Path.Combine(folder, "skin.json"));
        var r = WithDesigner(d =>
        {
            d.LoadSkin("legacy");
            var airPlayNode = d.LayersTree.Items.OfType<TreeViewItem>().First(i => (SkinLayer)i.Tag == SkinLayer.AirPlay);
            bool offersAdd = airPlayNode.Items.Count == 1 && airPlayNode.Items[0] is TreeViewItem { Header: Button };
            d.Commit(SkinEdits.SetScale(d.Document, 1.0)); // a no-op: still clean
            d.Save();
            var resaved = File.ReadAllBytes(Path.Combine(folder, "skin.json"));
            var strayFiles = Directory.GetFiles(folder, "airplay-*").Length;
            d.Commit(SkinEdits.AddAirPlay(d.Document, Src("ae.png", 200, 24, Color.Gray), Src("af.png", 200, 24, Color.Blue), new ImageProbe()));
            var children = d.LayersTree.Items.OfType<TreeViewItem>().First(i => (SkinLayer)i.Tag == SkinLayer.AirPlay).Items.Count;
            var nameShown = d.AirPlayPreview.SkinNameText.Visibility;
            d.Save();
            return (offersAdd, resaved, strayFiles, children, nameShown, StripFiles: Directory.GetFiles(folder, "airplay-*").Length);
        });
        _out.WriteLine($"before {Encoding.UTF8.GetString(before)} / after {Encoding.UTF8.GetString(r.resaved)}");
        Assert.True(r.offersAdd);
        Assert.Equal(before, r.resaved);
        Assert.Equal(0, r.strayFiles);
        Assert.Equal(6, r.children);
        Assert.Equal(Visibility.Visible, r.nameShown);
        Assert.Equal(2, r.StripFiles);
    }

    [Fact]
    public void A_hidden_number_keeps_its_styling_through_a_save()
    {
        var folder = MakeSkin("hidden", "{}");
        WithDesigner(d =>
        {
            d.LoadSkin("hidden");
            d.Select(SkinLayer.PercentText);
            Type(Number(d, Loc.T("designer.text.size")).Box, N(30));
            return d.Save();
        });
        var json = File.ReadAllText(Path.Combine(folder, "skin.json"));
        _out.WriteLine(json);
        Assert.Contains("\"percentText\":{\"show\":false,\"x\":0,\"y\":0,\"fontSize\":30}", json);
    }

    [Fact]
    public void The_notice_can_be_switched_on_bordered_and_its_icon_freed_from_the_text_colour()
    {
        MakeSkin("notice", "{}");
        var r = WithDesigner(d =>
        {
            d.LoadSkin("notice");
            d.Select(SkinLayer.Notice);
            Toggle(d, Loc.T("designer.notice.enable.label")).Switch.IsChecked = true;
            var on = d.Document.Config.Notice;
            Toggle(d, Loc.T("designer.notice.border.label")).Switch.IsChecked = true;
            var border = d.Document.Config.Notice!.Border;
            d.Select(SkinLayer.NoticeGlyph);
            Toggle(d, Loc.T("designer.notice.glyph-same.label")).Switch.IsChecked = false;
            return (on, border, Glyph: d.Document.Config.Notice!.Glyph.Color, Undos: d.History.CanUndo);
        });
        _out.WriteLine($"{r}");
        Assert.Equal(SkinLoader.DefaultNotice, r.on);
        Assert.Equal("#FF202020", r.border);
        Assert.Equal(SkinLoader.DefaultNotice.Text.Color, r.Glyph);
        Assert.True(r.Undos);
    }

    /// <summary>A strip on disk that the loader could not load is not silently dropped on save:
    /// the AirPlay layer carries a blocking issue, offers Remove and Add, and the skin saves only
    /// once the user has removed it - and then the save deletes exactly what they removed.</summary>
    [Fact]
    public void A_strip_that_failed_to_load_blocks_saving_until_the_user_removes_it()
    {
        var folder = MakeSkin("broken-strip", "{}", strip: true);
        RealPngs.WriteSolid(Path.Combine(folder, "airplay-full.png"), 180, 24, Color.SteelBlue); // mismatched
        var info = SkinLoader.Load(folder);
        Assert.NotNull(info.AirPlayError);
        var r = WithDesigner(d =>
        {
            d.LoadSkin("broken-strip");
            var node = d.LayersTree.Items.OfType<TreeViewItem>().First(i => (SkinLayer)i.Tag == SkinLayer.AirPlay);
            var header = (StackPanel)node.Header;
            var buttons = node.Items.OfType<TreeViewItem>().Select(i => i.Header).OfType<Button>().ToList();
            var before = (Enabled: d.SaveButton.IsEnabled, Saved: d.Save(),
                Badge: (header.Children[^1] as FrameworkElement)?.ToolTip as string,
                Buttons: buttons.Select(b => (string)b.Content).ToArray(), Files: Directory.GetFiles(folder, "airplay-*").Length);
            buttons.First(b => (string)b.Content == Loc.T("designer.airplay.remove"))
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var after = (Error: d.Document.AirPlayError, Enabled: d.SaveButton.IsEnabled, Saved: d.Save());
            return (before, after);
        });
        var issue = new SkinIssue(SkinLayer.AirPlay, SkinIssueKind.AirPlayUnloadable, [info.AirPlayError!]);
        _out.WriteLine($"expected: save blocked, badge '{issue.Message}', buttons [Remove, Add], 2 strip files kept; " +
                       "after Remove: no error, save enabled and done, no strip files");
        _out.WriteLine($"actual before: enabled={r.before.Enabled} saved={r.before.Saved} badge='{r.before.Badge}' " +
                       $"buttons [{string.Join(", ", r.before.Buttons)}] files={r.before.Files}");
        _out.WriteLine($"actual after : error={r.after.Error ?? "(none)"} enabled={r.after.Enabled} saved={r.after.Saved} " +
                       $"files={Directory.GetFiles(folder, "airplay-*").Length}");
        Assert.False(r.before.Enabled);
        Assert.False(r.before.Saved);
        Assert.Equal(issue.Message, r.before.Badge);
        Assert.Equal(new[] { Loc.T("designer.airplay.remove"), Loc.T("designer.layers.add-airplay") }, r.before.Buttons);
        Assert.Equal(2, r.before.Files);
        Assert.Null(r.after.Error);
        Assert.True(r.after.Enabled);
        Assert.True(r.after.Saved);
        Assert.Empty(Directory.GetFiles(folder, "airplay-*"));
    }

    /// <summary>The same for notice art that cannot be loaded: a blocking issue on the Notice
    /// layer whose Properties offer to turn the notice off or choose new art; turning it off lets
    /// the skin save, and the save records the notice as off and deletes the broken art.</summary>
    [Fact]
    public void A_notice_that_failed_to_load_blocks_saving_until_the_user_turns_it_off()
    {
        var folder = MakeSkin("broken", """{"notice":{"frames":3}}""", noticeArt: (300, 50));
        var info = SkinLoader.Load(folder);
        Assert.NotNull(info.NoticeError);
        var r = WithDesigner(d =>
        {
            d.LoadSkin("broken");
            d.Select(SkinLayer.Notice);
            var before = (Notice: d.Document.Config.Notice, Issues: d.Issues.ToArray(), Enabled: d.SaveButton.IsEnabled,
                Buttons: Fields<Button>(d.PropertiesHost).Select(b => b.Content as string).ToArray(), Status: d.StatusText.Text);
            Fields<Button>(d.PropertiesHost).First(b => b.Content as string == Loc.T("designer.notice.turn-off"))
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var after = (Error: d.Document.NoticeError, Enabled: d.SaveButton.IsEnabled, Saved: d.Save());
            return (before, after);
        });
        var json = File.ReadAllText(Path.Combine(folder, "skin.json"));
        var issue = new SkinIssue(SkinLayer.Notice, SkinIssueKind.NoticeUnloadable, [info.NoticeError!]);
        _out.WriteLine($"expected: no notice, issue '{issue}', save blocked, buttons include turn-off and choose-art");
        _out.WriteLine($"actual before: notice={r.before.Notice?.ToString() ?? "(none)"} issues [{string.Join("; ", r.before.Issues.AsEnumerable())}] " +
                       $"enabled={r.before.Enabled} buttons [{string.Join(", ", r.before.Buttons)}] status '{r.before.Status}'");
        _out.WriteLine($"actual after : error={r.after.Error ?? "(none)"} enabled={r.after.Enabled} saved={r.after.Saved}; json {json}");
        Assert.Null(r.before.Notice);
        Assert.Contains(issue, r.before.Issues);
        Assert.False(r.before.Enabled);
        Assert.Contains(Loc.T("designer.notice.turn-off"), r.before.Buttons);
        Assert.Contains(Loc.T("designer.notice.choose-art"), r.before.Buttons);
        Assert.Equal(Loc.T("designer.status.editing-change-the-name", info.Name), r.before.Status);
        Assert.Null(r.after.Error);
        Assert.True(r.after.Enabled);
        Assert.True(r.after.Saved);
        Assert.Empty(Directory.GetFiles(folder, "notice.*"));
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(System.Text.Json.JsonValueKind.False, doc.RootElement.GetProperty("notice").ValueKind);
    }

    /// <summary>Before Dispose a close only hides (App owns the window); after it, Close really
    /// closes - which is what App's exit and these tests rely on to leak nothing.</summary>
    [Fact]
    public void Close_hides_until_disposed_and_then_really_closes()
    {
        var r = _sta.Run(() =>
        {
            Application.Current.Resources["AppFontFamily"] = new System.Windows.Media.FontFamily("Segoe UI");
            var designer = new SkinDesignerWindow(() => Settings.Default, _root);
            int closed = 0;
            designer.Closed += (_, _) => closed++;
            designer.Show();
            designer.Close();
            var afterFirst = (designer.IsVisible, Closed: closed);
            designer.Dispose();
            designer.Close();
            return (afterFirst, Closed: closed);
        });
        _out.WriteLine($"expected hidden and not closed, then closed once; actual after first Close {r.afterFirst}, " +
                       $"closed after Dispose+Close: {r.Closed}");
        Assert.Equal((false, 0), r.afterFirst);
        Assert.Equal(1, r.Closed);
    }

    [Fact]
    public void Non_finite_numbers_are_rejected_without_an_edit()
    {
        MakeSkin("nan", "{}");
        var r = WithDesigner(d =>
        {
            d.LoadSkin("nan");
            d.Select(SkinLayer.SkinInfo);
            var field = Number(d, Loc.T("designer.text.scale"));
            var error = Record.Exception(() => Type(field.Box, "NaN"));
            return (error, field.IsInvalid, d.History.CanUndo, d.Document.Config.Scale);
        });
        _out.WriteLine($"expected no exception, invalid, no undo, scale 1; actual exception={r.error?.GetType().Name ?? "(none)"} " +
                       $"invalid={r.IsInvalid} canUndo={r.CanUndo} scale={r.Scale}");
        Assert.Null(r.error);
        Assert.True(r.IsInvalid);
        Assert.False(r.CanUndo);
        Assert.Equal(1.0, r.Scale);
    }

    /// <summary>Review focus: art deleted from disk while the designer has it open.</summary>
    [Fact]
    public void Art_deleted_from_disk_while_open_becomes_an_issue_not_a_crash()
    {
        var folder = MakeSkin("vanishing", "{}");
        var r = WithDesigner(d =>
        {
            d.LoadSkin("vanishing");
            File.Delete(Path.Combine(folder, "empty.png"));
            var error = Record.Exception(() => d.Commit(SkinEdits.SetScale(d.Document, 1.25)));
            return (error, d.VolumeView, d.Issues.ToArray(), d.SaveButton.IsEnabled, d.VolumePlaceholder.Text);
        });
        _out.WriteLine(string.Join(Environment.NewLine, r.Item3.AsEnumerable()));
        Assert.Null(r.error);
        Assert.Null(r.VolumeView);
        Assert.Contains(new SkinIssue(SkinLayer.VolumeEmpty, SkinIssueKind.ArtUnreadable, ["empty.png"]), r.Item3);
        Assert.False(r.IsEnabled);
        Assert.Equal(r.Item3.First(i => i.Layer == SkinLayer.VolumeEmpty).Message, r.Text);
    }

    [Fact]
    public void Frames_become_a_sheet_in_the_scratch_folder_and_dispose_removes_it()
    {
        var frames = new[] { Src("f1.png", 60, 20, Color.Red), Src("f2.png", 60, 20, Color.Blue), Src("f3.png", 60, 20, Color.Green) };
        var odd = Src("odd.png", 61, 20, Color.Black);
        string? scratch = null;
        var r = WithDesigner(d =>
        {
            scratch = d.ScratchFolder;
            var sheet = d.WriteSheet(frames);
            var mismatch = d.WriteSheet([frames[0], odd]);
            return (Sheet: sheet, Size: sheet is null ? null : PngHeader.Read(sheet), mismatch, d.StatusText.Text);
        });
        _out.WriteLine($"{r}");
        Assert.StartsWith(scratch!, r.Sheet);
        Assert.Equal((60, 60), r.Size);
        Assert.Null(r.mismatch);
        Assert.Equal(Loc.T("designer.is-but-earlier-frames-are-all", "odd.png", 61, 20, 60, 20), r.Text);
        Assert.False(Directory.Exists(scratch), "Dispose left the scratch folder behind");
    }

    [Fact]
    public void The_preview_strip_drives_the_renderers_without_touching_history()
    {
        MakeSkin("preview", "{}", strip: true);
        var r = WithDesigner(d =>
        {
            d.LoadSkin("preview");
            d.PreviewAirPlayMode.SelectedIndex = 2;
            d.PreviewDevice.Text = "Bathroom";
            d.PreviewLevel.Value = 15;
            d.PreviewFill.Value = 30;
            return (d.AirPlayPreview.State, d.NoticePreview.NoticeText.Text, d.History.CanUndo);
        });
        _out.WriteLine($"{r}");
        Assert.False(r.State.HasDevice);
        Assert.Equal("Bathroom", r.Text);
        Assert.False(r.CanUndo);
    }

    /// <summary>The percent text's colour fields, in panel order: text, outline, shadow.</summary>
    private static List<ColorField> PercentTextColours(SkinDesignerWindow d)
    {
        d.Select(SkinLayer.PercentText);
        StaWpf.Pump(); // the swatches must be loaded for the picker to open on them
        return Fields<ColorField>(d.PropertiesHost).ToList();
    }

    private static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    /// <summary>One opening of the picker is one undo step - including the hex typed into it and
    /// not yet committed when the same field's swatch is clicked again, which belongs to the
    /// opening it was typed in, not the next.</summary>
    [Fact]
    public void Each_opening_of_the_colour_picker_is_one_undo_step()
    {
        MakeSkin("pick", "{}");
        var r = WithDesigner(d =>
        {
            d.LoadSkin("pick");
            var text = PercentTextColours(d)[0];
            Click(text.Swatch);
            d.Picker!.SetColor("#FFFF0000");
            d.Picker.SetColor("#FF00FF00");
            d.Picker.HexBox.Text = "#FF123456"; // typed, not committed
            Click(text.Swatch);                  // a second opening on the same field
            var afterReopen = d.Document.Config.Text?.Color;
            d.Picker.SetColor("#FF0000FF");
            var second = d.Document.Config.Text?.Color;
            d.History.Undo();
            var undoneOnce = d.Document.Config.Text?.Color;
            d.History.Undo();
            return (afterReopen, second, undoneOnce, UndoneTwice: d.Document.Config.Text?.Color, d.History.CanUndo);
        });
        _out.WriteLine("expected: reopen commits #FF123456, second opening #FF0000FF, one undo #FF123456, two undos (none), no more undo");
        _out.WriteLine($"actual  : reopen {r.afterReopen}, second {r.second}, one undo {r.undoneOnce}, " +
                       $"two undos {r.UndoneTwice ?? "(none)"}, canUndo={r.CanUndo}");
        Assert.Equal("#FF123456", r.afterReopen);
        Assert.Equal("#FF0000FF", r.second);
        Assert.Equal("#FF123456", r.undoneOnce);
        Assert.Null(r.UndoneTwice);
        Assert.False(r.CanUndo);
    }

    /// <summary>Opening the picker on another field first commits the hex pending in the last
    /// opening - to the field it was opened on, never to the new one.</summary>
    [Fact]
    public void A_pending_picker_hex_goes_to_the_field_it_was_typed_for()
    {
        MakeSkin("retarget", "{}");
        var r = WithDesigner(d =>
        {
            d.LoadSkin("retarget");
            var colours = PercentTextColours(d);
            Click(colours[0].Swatch);
            d.Picker!.SetColor("#FFFF0000");
            d.Picker.HexBox.Text = "#FF123456"; // typed, not committed
            Click(colours[1].Swatch);            // the outline colour
            var afterOpen = (d.Document.Config.Text?.Color, d.Document.Config.Text?.OutlineColor);
            d.Picker.SetColor("#FF00FF00");
            return (afterOpen, After: (d.Document.Config.Text?.Color, d.Document.Config.Text?.OutlineColor));
        });
        _out.WriteLine("expected: text #FF123456 with no outline on opening the outline picker, then outline #FF00FF00");
        _out.WriteLine($"actual  : on opening (text, outline) = {r.afterOpen}; after picking {r.After}");
        Assert.Equal(("#FF123456", (string?)null), r.afterOpen);
        Assert.Equal(("#FF123456", "#FF00FF00"), r.After);
    }

    /// <summary>Disposing with the picker open applies its pending hex and remembers the colour
    /// right away: nothing may wait for the late Closed, which a disposed window never handles.</summary>
    [Fact]
    public void Disposing_with_the_picker_open_keeps_the_typed_colour()
    {
        MakeSkin("dispose-pick", "{}");
        var r = WithDesigner(d =>
        {
            var recents = new List<IReadOnlyList<string>>();
            d.RecentColorsChanged += recents.Add;
            d.LoadSkin("dispose-pick");
            Click(PercentTextColours(d)[0].Swatch);
            d.Picker!.HexBox.Text = "#FF654321"; // typed, not committed
            d.Dispose();
            var atDispose = (d.Document.Config.Text?.Color, Recents: recents.Count == 0 ? "(none)" : string.Join(" ", recents[^1]));
            StaWpf.Pump(); // the popup's late Closed must change nothing
            return (atDispose, AfterPump: d.Document.Config.Text?.Color, Undos: d.History.CanUndo);
        });
        _out.WriteLine($"expected: #FF654321 in the document and first in the recents at Dispose; actual {r.atDispose}, " +
                       $"after the late Closed {r.AfterPump}, canUndo={r.Undos}");
        Assert.Equal("#FF654321", r.atDispose.Color);
        Assert.StartsWith("#FF654321", r.atDispose.Recents);
        Assert.Equal("#FF654321", r.AfterPump);
        Assert.True(r.Undos);
    }

    [Fact]
    public void Panel_widths_come_from_settings_and_dragging_a_splitter_reports_them()
    {
        var r = WithDesigner(d =>
        {
            (double Layers, double Properties)? reported = null;
            d.PanelWidthsChanged += (l, p) => reported = (l, p);
            var widths = (d.LayersColumn.Width.Value, d.PropertiesColumn.Width.Value);
            d.LayersSplitter.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
            return (widths, reported, Actual: (d.LayersColumn.ActualWidth, d.PropertiesColumn.ActualWidth), d.Width, d.MinWidth);
        }, Settings.Default with { DesignerLayersWidth = 250, DesignerPropertiesWidth = 300 });
        _out.WriteLine($"{r}");
        Assert.Equal((250.0, 300.0), r.widths);
        Assert.Equal(r.Actual, r.reported);
        Assert.Equal((1180.0, 900.0), (r.Width, r.MinWidth));
    }
}
