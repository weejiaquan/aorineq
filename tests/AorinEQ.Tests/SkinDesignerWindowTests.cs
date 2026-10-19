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

    private static Dictionary<string, byte[]> Snapshot(string folder) =>
        Directory.GetFiles(folder).ToDictionary(f => Path.GetFileName(f), File.ReadAllBytes);

    /// <summary>Art deleted from disk with no edit since: the issue list from the last edit is
    /// stale, so Save must look again - otherwise a same-folder save "succeeds" (the writer skips
    /// copying a file onto itself) and leaves a skin that will not load.</summary>
    [Fact]
    public void Save_looks_at_the_art_again_and_refuses_when_it_has_gone()
    {
        var folder = MakeSkin("gone", """{"scale":1.5}""");
        var r = WithDesigner(d =>
        {
            var saved = new List<string>();
            d.SkinSaved += saved.Add;
            d.LoadSkin("gone");
            File.Delete(Path.Combine(folder, "empty.png"));
            var before = Snapshot(folder);
            bool ok = d.Save();
            return (ok, before, After: Snapshot(folder), Issues: d.Issues.ToArray(), d.SaveButton.IsEnabled,
                d.StatusText.Text, Saved: saved.Count, Dirty: d.History.IsDirty);
        });
        var issue = new SkinIssue(SkinLayer.VolumeEmpty, SkinIssueKind.ArtUnreadable, ["empty.png"]);
        _out.WriteLine($"expected: refused, files unchanged, issue '{issue.Message}' shown, save disabled, no SkinSaved");
        _out.WriteLine($"actual  : saved={r.ok} files [{string.Join(", ", r.After.Keys)}] issues [{string.Join("; ", r.Issues.AsEnumerable())}] " +
                       $"enabled={r.IsEnabled} status '{r.Text}' skinSaved={r.Saved} dirty={r.Dirty}");
        Assert.False(r.ok);
        Assert.Equal(r.before.Keys.Order(), r.After.Keys.Order());
        foreach (var (file, bytes) in r.before) Assert.Equal(bytes, r.After[file]);
        Assert.Contains(issue, r.Issues);
        Assert.False(r.IsEnabled);
        Assert.Contains(issue.Message, r.Text);
        Assert.Equal(0, r.Saved);
    }

    /// <summary>The same under a new name: no half-written copy is left behind.</summary>
    [Fact]
    public void Save_as_with_art_gone_creates_no_folder()
    {
        var folder = MakeSkin("source", "{}");
        var r = WithDesigner(d =>
        {
            d.LoadSkin("source");
            d.Commit(SkinEdits.SetName(d.Document, "copy"));
            File.Delete(Path.Combine(folder, "full.png"));
            return (Saved: d.Save(), Copy: Directory.Exists(Path.Combine(_root, "copy")), Dirty: d.History.IsDirty);
        });
        _out.WriteLine($"expected: refused, no 'copy' folder, still dirty; actual {r}");
        Assert.False(r.Saved);
        Assert.False(r.Copy);
        Assert.True(r.Dirty);
    }

    /// <summary>A write that fails (skin.json held open elsewhere) is not a save: the skin stays
    /// dirty, the status line says why and the app is not told a skin changed.</summary>
    [Fact]
    public void A_save_that_fails_keeps_the_skin_dirty_and_says_why()
    {
        var folder = MakeSkin("locked", "{}");
        var r = WithDesigner(d =>
        {
            var saved = new List<string>();
            d.SkinSaved += saved.Add;
            d.LoadSkin("locked");
            d.Commit(SkinEdits.SetScale(d.Document, 2));
            using var hold = new FileStream(Path.Combine(folder, "skin.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            bool ok = d.Save();
            return (ok, Dirty: d.History.IsDirty, d.Title, d.StatusText.Text, Saved: saved.Count);
        });
        _out.WriteLine($"expected: not saved, dirty, a 'Failed to save skin' status, no SkinSaved; actual {r}");
        Assert.False(r.ok);
        Assert.True(r.Dirty);
        Assert.StartsWith("•", r.Title);
        Assert.Contains("Failed to save skin 'locked'", r.Text);
        Assert.Equal(0, r.Saved);
        Assert.Equal("{}", File.ReadAllText(Path.Combine(folder, "skin.json")));
    }

    /// <summary>A frame count changes the layer's frame size, which must not rebuild the panel
    /// under the field being stepped or scrubbed: the field's ranges follow in place.</summary>
    [Fact]
    public void Stepping_and_scrubbing_frames_keeps_the_same_field()
    {
        MakeSkin("frames", "{}");
        var r = WithDesigner(d =>
        {
            d.LoadSkin("frames");
            d.Select(SkinLayer.VolumeEmpty);
            StaWpf.Pump();
            var field = Number(d, Loc.T("designer.prop.frames"));
            var values = new List<int>();
            field.HandleKey(Key.Up, ModifierKeys.None);
            values.Add(d.Document.Config.EmptyFrames);
            field.HandleKey(Key.Up, ModifierKeys.None);
            values.Add(d.Document.Config.EmptyFrames);
            bool scrubbing = field.BeginScrub(0);
            field.ScrubTo(2 * NumberField.ScrubPixelsPerStep);
            values.Add(d.Document.Config.EmptyFrames);
            field.Label.ReleaseMouseCapture();
            var after = Number(d, Loc.T("designer.prop.frames"));
            return (Same: ReferenceEquals(field, after), Values: values, scrubbing, Undos: d.History.CanUndo);
        });
        _out.WriteLine($"expected: the same field, frames 2, 3 then 5 (scrub +2); actual same={r.Same} frames [{string.Join(", ", r.Values)}] " +
                       $"scrub started={r.scrubbing}");
        Assert.True(r.scrubbing);
        Assert.Equal(new[] { 2, 3, 5 }, r.Values);
        Assert.True(r.Same);
    }

    /// <summary>Undoing "Add AirPlay strip" with one of its parts selected: the part is no longer
    /// in the tree, so the selection falls back to the strip's own node.</summary>
    [Fact]
    public void Undoing_the_strip_moves_a_selected_part_back_to_the_strip_node()
    {
        MakeSkin("undo-strip", "{}");
        var r = WithDesigner(d =>
        {
            d.LoadSkin("undo-strip");
            d.Commit(SkinEdits.AddAirPlay(d.Document, Src("ae.png", 200, 24, Color.Gray), Src("af.png", 200, 24, Color.Blue), new ImageProbe()));
            d.Select(SkinLayer.AirPlayName);
            d.History.Undo();
            var selectedItem = d.LayersTree.Items.OfType<TreeViewItem>().FirstOrDefault(i => i.IsSelected)?.Tag;
            return (d.Selection, SelectedItem: selectedItem, Outlined: d.Overlay.Selected);
        });
        _out.WriteLine($"expected AirPlay selected in the tree and outlined; actual selection {r.Selection}, tree item {r.SelectedItem}, outline {r.Outlined}");
        Assert.Equal(SkinLayer.AirPlay, r.Selection);
        Assert.Equal(SkinLayer.AirPlay, r.SelectedItem);
        Assert.Equal(SkinLayer.AirPlay, r.Outlined);
    }

    /// <summary>Loading a skin starts on Skin info, and the canvas outline follows: the layer
    /// outlined in the previous skin is not left outlined in the new one.</summary>
    [Fact]
    public void Loading_a_skin_clears_the_canvas_outline()
    {
        MakeSkin("first", """{"percentText":{"show":true,"x":10,"y":5}}""");
        MakeSkin("second", """{"percentText":{"show":true,"x":10,"y":5}}""");
        var r = WithDesigner(d =>
        {
            d.LoadSkin("first");
            d.Select(SkinLayer.PercentText);
            var before = d.Overlay.Selected;
            d.LoadSkin("second");
            return (before, d.Selection, After: d.Overlay.Selected);
        });
        _out.WriteLine($"expected PercentText outlined, then SkinInfo selected and outlined; actual {r}");
        Assert.Equal((SkinLayer.PercentText, SkinLayer.SkinInfo, SkinLayer.SkinInfo), r);
    }

    /// <summary>Art that passes the header check but will not decode: the status line keeps the
    /// decode failure rather than the "Editing ..." line written after it.</summary>
    [Fact]
    public void Loading_keeps_a_decode_failure_in_the_status_line()
    {
        var folder = MakeSkin("undecodable", "{}");
        TestPngs.WriteHeaderOnly(Path.Combine(folder, "empty.png"), 200, 40);
        var r = WithDesigner(d => (Loaded: d.LoadSkin("undecodable"), d.StatusText.Text, d.VolumeView));
        _out.WriteLine($"expected status starting '{Loc.T("designer.image-failed-to-decode")}'; actual loaded={r.Loaded} '{r.Text}'");
        Assert.True(r.Loaded);
        Assert.StartsWith(Loc.T("designer.image-failed-to-decode"), r.Text);
        Assert.Null(r.VolumeView);
    }

    /// <summary>A skin chosen in the picker that no longer loads leaves the picker on the skin that
    /// is still open, not on one the window is not showing.</summary>
    [Fact]
    public void A_skin_that_fails_to_load_from_the_picker_puts_the_picker_back()
    {
        MakeSkin("good", "{}");
        var broken = MakeSkin("broken-later", "{}");
        var r = WithDesigner(d =>
        {
            d.LoadSkin("good");
            File.Delete(Path.Combine(broken, "full.png")); // after the picker listed it as valid
            d.SkinSelect.SelectedItem = d.SkinSelect.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == "broken-later");
            return (Picker: (d.SkinSelect.SelectedItem as ComboBoxItem)?.Tag, d.Document.Name, d.StatusText.Text);
        });
        _out.WriteLine($"expected picker and document on 'good' with the load error shown; actual {r}");
        Assert.Equal("good", r.Picker);
        Assert.Equal("good", r.Name);
        Assert.Contains("full.png", r.Text);
    }

    /// <summary>A language change rebuilds the Properties panel, so the picker's pending hex is
    /// applied to its field first and the picker closes.</summary>
    [Fact]
    public void A_language_change_ends_the_picker_opening_first()
    {
        MakeSkin("language", "{}");
        var previous = Loc.Language;
        try
        {
            var r = WithDesigner(d =>
            {
                d.LoadSkin("language");
                Click(PercentTextColours(d)[0].Swatch);
                d.Picker!.HexBox.Text = "#FF2468AC"; // typed, not committed
                Loc.Language = previous == Languages.Ja ? Languages.En : Languages.Ja;
                return (d.Document.Config.Text?.Color, d.Picker.IsOpen);
            });
            _out.WriteLine($"expected #FF2468AC applied and the picker closed; actual {r}");
            Assert.Equal(("#FF2468AC", false), r);
        }
        finally
        {
            _sta.Run(() => Loc.Language = previous);
        }
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

    [Fact]
    public void A_canvas_drag_is_one_undo_step_and_lands_in_the_document()
    {
        MakeSkin("drag", """{"percentText":{"show":true,"x":10,"y":5}}""");
        var r = WithDesigner(d =>
        {
            d.LoadSkin("drag");
            d.BeginDrag();
            d.ApplyDrag(SkinLayer.PercentText, SkinHandle.Move, 5, 2);
            d.ApplyDrag(SkinLayer.PercentText, SkinHandle.Move, 30, 4);
            d.EndDrag();
            var dragged = d.Document.Config.Text;
            d.History.Undo();
            return (dragged, Undone: d.Document.Config.Text, d.History.CanUndo);
        });
        _out.WriteLine($"{r}");
        Assert.Equal((40, 9), (r.dragged!.X, r.dragged.Y));
        Assert.Equal((10, 5), (r.Undone!.X, r.Undone.Y));
        Assert.False(r.CanUndo);
    }

    [Fact]
    public void The_scene_outlines_what_the_renderers_draw()
    {
        MakeSkin("scene", """{"scale":2,"percentText":{"show":true,"x":10,"y":5}}""", strip: true);
        var r = WithDesigner(d =>
        {
            d.LoadSkin("scene");
            d.Select(SkinLayer.PercentText);
            var scene = d.Overlay.Scene;
            return (Boards: scene.Boards.Select(b => (b.Layer, b.Scale, b.Width, b.Height)).ToArray(),
                Text: scene.Elements.Single(e => e.Layer == SkinLayer.PercentText).Bounds,
                Dropdown: scene.Elements.Single(e => e.Layer == SkinLayer.AirPlayDropdown).Bounds,
                d.Overlay.Selected);
        });
        _out.WriteLine($"{string.Join("; ", r.Boards)} text {r.Text} dropdown {r.Dropdown}");
        Assert.Contains((SkinLayer.VolumeBar, 2.0, 200, 40), r.Boards);
        Assert.Contains((SkinLayer.AirPlay, 2.0, 200, 24), r.Boards);
        Assert.Equal((10.0, 5.0), (r.Text.X, r.Text.Y));
        Assert.True(r.Text.Width > 0 && r.Text.Height > 0);
        Assert.Equal(new Rect(0, 0, 200, 24), r.Dropdown);
        Assert.Equal(SkinLayer.PercentText, r.Selected);
    }

    /// <summary>A layer chosen in the Layers tree is outlined on the canvas, as a canvas click
    /// would outline it: both go through the one selection path.</summary>
    [Fact]
    public void Choosing_a_layer_in_the_tree_outlines_it_on_the_canvas()
    {
        MakeSkin("tree-pick", """{"percentText":{"show":true,"x":10,"y":5}}""", strip: true);
        var r = WithDesigner(d =>
        {
            d.LoadSkin("tree-pick");
            var item = d.LayersTree.Items.OfType<TreeViewItem>().SelectMany(n => n.Items.OfType<TreeViewItem>())
                .First(i => i.Tag is SkinLayer.AirPlayDropdown);
            item.IsSelected = true; // what a click in the tree does
            return (d.Selection, Outlined: d.Overlay.Selected);
        });
        _out.WriteLine($"expected AirPlayDropdown selected and outlined; actual selection {r.Selection}, overlay {r.Outlined}");
        Assert.Equal((SkinLayer.AirPlayDropdown, SkinLayer.AirPlayDropdown), (r.Selection, r.Outlined));
    }

    /// <summary>Text dragged past the bar's bottom-right corner stops anchored on the edge, so it
    /// lies outside the bar. On its own bar it is picked; stopped on the bottom edge it overflows
    /// onto the strip, whose element under it keeps the click (the Layers tree still selects the
    /// text); at the far corner, past every artboard, it is picked again - and the canvas grows to
    /// scroll to it.</summary>
    [Fact]
    public void Text_stopped_at_the_far_edge_stays_reachable_and_selectable()
    {
        MakeSkin("far-edge", """{"scale":2,"percentText":{"show":true,"x":10,"y":5}}""", strip: true);
        var r = WithDesigner(d =>
        {
            d.LoadSkin("far-edge");
            var onBar = d.Overlay.HitTest(Centre(d, SkinLayer.PercentText)).Element?.Layer;
            d.BeginDrag();
            d.ApplyDrag(SkinLayer.PercentText, SkinHandle.Move, 0, 10000); // onto the bottom edge, over the strip
            var overStrip = (d.Document.Config.Text!.Y, Picked: d.Overlay.HitTest(Centre(d, SkinLayer.PercentText)).Element?.Layer);
            d.ApplyDrag(SkinLayer.PercentText, SkinHandle.Move, 10000, 10000); // the far corner, past every artboard
            d.EndDrag();
            d.UpdateLayout();
            d.LayersTree.Items.OfType<TreeViewItem>().SelectMany(n => n.Items.OfType<TreeViewItem>())
                .First(i => i.Tag is SkinLayer.PercentText).IsSelected = true;
            var text = d.Overlay.Scene.Elements.Single(e => e.Layer == SkinLayer.PercentText);
            var onCanvas = d.Overlay.Scene.BoardFor(SkinLayer.PercentText)!.ToOverlay(text.Bounds);
            var centre = Centre(d, SkinLayer.PercentText);
            var farCorner = d.Overlay.TranslatePoint(onCanvas.BottomRight, d.ZoomHost);
            var hit = System.Windows.Media.VisualTreeHelper.HitTest(d.Overlay, centre)?.VisualHit;
            return (onBar, overStrip, d.Document.Config.Text, onCanvas, OverlaySize: d.Overlay.RenderSize,
                Picked: d.Overlay.HitTest(centre).Element?.Layer, TakesMouse: ReferenceEquals(hit, d.Overlay),
                Tree: (d.Selection, d.Overlay.Selected), farCorner, Host: (d.ZoomHost.ActualWidth, d.ZoomHost.ActualHeight));
        });
        _out.WriteLine($"on its bar: picked {r.onBar} (expected PercentText)");
        _out.WriteLine($"bottom edge: y {r.overStrip.Y} picked {r.overStrip.Picked} (expected 40, AirPlayDropdown)");
        _out.WriteLine($"far corner: text {r.Text!.X},{r.Text.Y} at {r.onCanvas} on an overlay of {r.OverlaySize}; " +
            $"picked {r.Picked} (expected PercentText), takes the mouse {r.TakesMouse}; tree selection {r.Tree}; corner {r.farCorner} inside host {r.Host}");
        Assert.Equal(SkinLayer.PercentText, r.onBar);
        Assert.Equal((40, SkinLayer.AirPlayDropdown), (r.overStrip.Y, r.overStrip.Picked));
        Assert.Equal((200, 40), (r.Text.X, r.Text.Y));
        Assert.True(r.onCanvas.Right > r.OverlaySize.Width, "the scenario needs text past the overlay's own width");
        Assert.Equal(SkinLayer.PercentText, r.Picked);
        Assert.True(r.TakesMouse);
        Assert.Equal((SkinLayer.PercentText, SkinLayer.PercentText), r.Tree);
        // The handles round it too (half a handle, at the canvas's 100 %).
        double half = DesignerOverlay.HandleScreenSize / 2;
        Assert.True(r.farCorner.X + half <= r.Host.ActualWidth && r.farCorner.Y + half <= r.Host.ActualHeight);
    }

    /// <summary>The centre of a layer's outline, in overlay coordinates.</summary>
    private static Point Centre(SkinDesignerWindow d, SkinLayer layer)
    {
        var box = d.Overlay.Scene.BoardFor(layer)!.ToOverlay(d.Overlay.Scene.Elements.Single(e => e.Layer == layer).Bounds);
        return new Point(box.X + box.Width / 2, box.Y + box.Height / 2);
    }

    /// <summary>The dropdown covers the whole strip by default; the labels drawn on it are still
    /// the ones a click on them picks.</summary>
    [Fact]
    public void The_strip_labels_are_picked_over_its_click_areas()
    {
        MakeSkin("labels", """{"airplay":{"nameText":{"show":true,"x":10,"y":4},"percentText":{"show":true,"x":190,"y":4,"align":"right"}}}""",
            strip: true);
        var r = WithDesigner(d =>
        {
            d.LoadSkin("labels");
            var strip = d.Overlay.Scene.BoardFor(SkinLayer.AirPlay)!;
            SkinLayer? PickedAt(SkinLayer layer)
            {
                var box = strip.ToOverlay(d.Overlay.Scene.Elements.Single(e => e.Layer == layer).Bounds);
                return d.Overlay.HitTest(new Point(box.X + box.Width / 2, box.Y + box.Height / 2)).Element?.Layer;
            }
            return (Name: PickedAt(SkinLayer.AirPlayName), Level: PickedAt(SkinLayer.AirPlayLevel),
                Dropdown: d.Overlay.HitTest(strip.ToOverlay(strip.Width / 2.0, 1)).Element?.Layer);
        });
        _out.WriteLine($"expected (AirPlayName, AirPlayLevel, AirPlayDropdown); actual {r}");
        Assert.Equal((SkinLayer.AirPlayName, SkinLayer.AirPlayLevel, SkinLayer.AirPlayDropdown), (r.Name, r.Level, r.Dropdown));
    }

    /// <summary>The window builds its first scene before it is ever laid out; once it is shown the
    /// overlay must know where the artboards really are.</summary>
    [Fact]
    public void The_scene_follows_the_first_layout()
    {
        var r = WithDesigner(d =>
        {
            StaWpf.Pump();
            var notice = d.Overlay.Scene.BoardFor(SkinLayer.Notice)!;
            return (Scene: (notice.Origin, notice.Width, notice.Height),
                Laid: (d.NoticeBoard.TranslatePoint(new Point(0, 0), d.Overlay), d.NoticeBoard.ActualWidth, d.NoticeBoard.ActualHeight));
        });
        _out.WriteLine($"expected scene board {r.Laid}; actual {r.Scene}");
        Assert.True(r.Laid.Item2 > 0 && r.Laid.Item3 > 0);
        Assert.Equal((r.Laid.Item1, (int)r.Laid.Item2, (int)r.Laid.Item3), r.Scene);
    }

    /// <summary>A slow drag through the overlay, one pixel at a time with snapping on, moves one
    /// pixel per step - it never sticks to where the previous step left it.</summary>
    [Fact]
    public void A_slow_canvas_drag_moves_one_pixel_per_step()
    {
        // y 0: the text's top sits on the bar's top edge, so snapping holds it there exactly.
        MakeSkin("slow", """{"percentText":{"show":true,"x":40,"y":0}}""");
        var r = WithDesigner(d =>
        {
            d.LoadSkin("slow");
            var start = Centre(d, SkinLayer.PercentText);
            bool began = d.Overlay.BeginDragAt(start);
            var steps = new List<(int X, int Y)>();
            foreach (var dx in new[] { 1, 2, 3 })
            {
                d.Overlay.DragTo(start + new Vector(dx, 0), snap: true);
                steps.Add((d.Document.Config.Text!.X, d.Document.Config.Text.Y));
            }
            d.Overlay.FinishDrag();
            d.History.Undo();
            return (began, steps, Undone: d.Document.Config.Text!.X, d.History.CanUndo);
        });
        _out.WriteLine($"expected steps 41,0 42,0 43,0 then one undo back to 40; actual began {r.began}, steps {string.Join(" ", r.steps)}, undone {r.Undone}, can undo {r.CanUndo}");
        Assert.True(r.began);
        Assert.Equal([(41, 0), (42, 0), (43, 0)], r.steps);
        Assert.Equal(40, r.Undone);
        Assert.False(r.CanUndo);
    }

    /// <summary>A drag that snaps draws its magenta guide on the canvas; the same drag with
    /// snapping off (Alt) lands where the pointer is and draws none.</summary>
    [Fact]
    public void A_snapped_canvas_drag_draws_its_guide_and_an_unsnapped_one_does_not()
    {
        MakeSkin("guide", """{"percentText":{"show":true,"x":40,"y":0}}""");
        var r = WithDesigner(d =>
        {
            d.LoadSkin("guide");
            var start = Centre(d, SkinLayer.PercentText);
            d.Overlay.BeginDragAt(start);
            d.Overlay.DragTo(start + new Vector(58, 0), snap: true); // left edge 98: 2 px from the bar's centre line
            var snapped = (d.Document.Config.Text!.X, Guides: d.Overlay.Guides.ToArray(), Magenta: MagentaColumns(d));
            d.Overlay.DragTo(start + new Vector(58, 0), snap: false);
            var free = (d.Document.Config.Text!.X, Guides: d.Overlay.Guides.ToArray(), Magenta: MagentaColumns(d));
            d.Overlay.FinishDrag();
            var after = MagentaColumns(d);
            var guideX = d.Overlay.Scene.BoardFor(SkinLayer.VolumeBar)!.ToOverlay(100, 0).X
                + System.Windows.Media.VisualTreeHelper.GetOffset(d.Overlay).X;
            return (snapped, free, after, guideX);
        });
        _out.WriteLine($"snapped: x {r.snapped.X} (expected 100), guides [{string.Join(", ", r.snapped.Guides.Select(g => g.ToString()))}], " +
            $"magenta columns [{string.Join(", ", r.snapped.Magenta)}] (expected beside {r.guideX})");
        _out.WriteLine($"free: x {r.free.X} (expected 98), guides {r.free.Guides.Length}, magenta columns {r.free.Magenta.Count}; after the drag {r.after.Count}");
        Assert.Equal(100, r.snapped.X);
        Assert.Contains(new SnapTarget(SnapAxis.X, 100, SnapSource.Artboard), r.snapped.Guides);
        Assert.NotEmpty(r.snapped.Magenta);
        Assert.All(r.snapped.Magenta, x => Assert.InRange(x + 0.5, r.guideX - 1, r.guideX + 1)); // pixel centres beside the line
        Assert.Equal(98, r.free.X);
        Assert.Empty(r.free.Guides);
        Assert.Empty(r.free.Magenta);
        Assert.Empty(r.after);
    }

    /// <summary>The bitmap columns holding a magenta (guide) pixel over at least half the bar's
    /// height, with the overlay rendered as the canvas draws it.</summary>
    private static List<int> MagentaColumns(SkinDesignerWindow d)
    {
        d.UpdateLayout();
        int w = (int)Math.Ceiling(d.Artboards.ActualWidth), h = (int)Math.Ceiling(d.Artboards.ActualHeight);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(d.Overlay);
        var bgra = new byte[w * h * 4];
        bitmap.CopyPixels(bgra, w * 4, 0);
        var columns = new List<int>();
        for (int x = 0; x < w; x++)
        {
            int rows = 0;
            for (int y = 0; y < h; y++)
            {
                int i = (y * w + x) * 4;
                if (bgra[i + 3] > 0 && bgra[i + 1] == 0 && bgra[i] == bgra[i + 2] && bgra[i] > 0) rows++; // premultiplied magenta
            }
            if (rows >= d.Info.Height / 2) columns.Add(x);
        }
        return columns;
    }

    /// <summary>The canvas is centred, so growing it for text past the bar's edge mid-drag would
    /// move the overlay under a still pointer. Its growth waits for the drag's end: the text keeps
    /// tracking the pointer exactly.</summary>
    [Fact]
    public void A_drag_past_the_edge_tracks_the_pointer_and_the_canvas_grows_after()
    {
        MakeSkin("track", """{"scale":2,"percentText":{"show":true,"x":10,"y":5}}""");
        var r = WithDesigner(d =>
        {
            d.LoadSkin("track");
            d.UpdateLayout();
            // The pointer lives in window coordinates; where it is over the overlay is what the
            // mouse handler's GetPosition would read after every layout.
            var pointer = d.Overlay.TranslatePoint(Centre(d, SkinLayer.PercentText), d);
            Point OverOverlay(Vector by) { d.UpdateLayout(); return d.TranslatePoint(pointer + by, d.Overlay); }
            double before = d.Artboards.Margin.Right;
            d.Overlay.BeginDragAt(OverOverlay(new Vector(0, 0)));
            d.Overlay.DragTo(OverOverlay(new Vector(380, 0)), snap: false); // x 200: past the bar's right edge
            var during = (Margin: d.Artboards.Margin.Right, Overlay: d.Overlay.TranslatePoint(new Point(0, 0), d));
            d.Overlay.DragTo(OverOverlay(new Vector(360, 0)), snap: false); // back 10 skin px
            int x = d.Document.Config.Text!.X;
            d.Overlay.FinishDrag();
            d.UpdateLayout();
            return (x, before, during, After: (Margin: d.Artboards.Margin.Right, Overlay: d.Overlay.TranslatePoint(new Point(0, 0), d)));
        });
        _out.WriteLine($"expected x 190; actual {r.x}. Margin {r.before} before the drag; during it margin {r.during.Margin}, overlay at {r.during.Overlay}; after it margin {r.After.Margin}, overlay at {r.After.Overlay}");
        Assert.Equal(190, r.x);
        Assert.Equal(r.before, r.during.Margin); // frozen while the drag runs
        Assert.True(r.After.Margin > r.before, "the canvas grows for the text once the drag ends");
        Assert.NotEqual(r.during.Overlay, r.After.Overlay); // the growth really moves the overlay: why it waits
    }

    /// <summary>Undo pressed during a drag ends the drag first: the drag is one undo step, and the
    /// pointer moving on afterwards edits nothing.</summary>
    [Fact]
    public void Undo_during_a_canvas_drag_ends_it_as_one_step()
    {
        MakeSkin("undo-drag", """{"percentText":{"show":true,"x":40,"y":0}}""");
        var r = WithDesigner(d =>
        {
            d.LoadSkin("undo-drag");
            var start = Centre(d, SkinLayer.PercentText);
            d.Overlay.BeginDragAt(start);
            d.Overlay.DragTo(start + new Vector(10, 0), snap: false);
            int dragged = d.Document.Config.Text!.X;
            d.UndoButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            d.Overlay.DragTo(start + new Vector(20, 0), snap: false); // the mouse moving on
            var afterUndo = (d.Document.Config.Text!.X, d.History.CanUndo, d.History.CanRedo);
            d.History.Redo();
            return (dragged, afterUndo, Redone: d.Document.Config.Text!.X);
        });
        _out.WriteLine($"expected dragged 50, after undo (40, no undo, redo), redone 50; actual {r}");
        Assert.Equal(50, r.dragged);
        Assert.Equal((40, false, true), r.afterUndo);
        Assert.Equal(50, r.Redone);
    }

    /// <summary>A language change re-measures the outlines: a label showing translated text (the
    /// strip's no-receiver name) is outlined at its new width.</summary>
    [Fact]
    public void A_language_change_re_measures_the_outlines()
    {
        MakeSkin("lang", """{"airplay":{"nameText":{"show":true,"x":10,"y":4}}}""", strip: true);
        var previous = Loc.Language;
        try
        {
            var r = WithDesigner(d =>
            {
                d.LoadSkin("lang");
                d.PreviewAirPlayMode.SelectedIndex = 2; // no receiver: the name is the localised "no device"
                double Width() => d.Overlay.Scene.Elements.Single(e => e.Layer == SkinLayer.AirPlayName).Bounds.Width;
                double before = Width();
                Loc.Language = previous == Languages.Ja ? Languages.En : Languages.Ja;
                return (before, After: Width(), Shown: d.AirPlayPreview.DisplayName);
            });
            _out.WriteLine($"name outline {r.before} before, {r.After} after the switch (showing '{r.Shown}')");
            Assert.NotEqual(r.before, r.After, 3);
        }
        finally
        {
            _sta.Run(() => Loc.Language = previous);
        }
    }
}
