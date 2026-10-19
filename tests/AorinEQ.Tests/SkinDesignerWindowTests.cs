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
    public void Only_the_volume_bars_art_says_it_must_match_the_empty_and_full_size()
    {
        // The strip and the notice art have frame sizes of their own: telling the user otherwise
        // there would be a wrong instruction.
        MakeSkin("all-art", "{}", strip: true, noticeArt: (120, 30));
        var hint = Loc.T("designer.text.png-sprite-sheet-png");
        SkinLayer[] layers = [SkinLayer.VolumeEmpty, SkinLayer.VolumeFull, SkinLayer.VolumeMuted,
            SkinLayer.AirPlayEmpty, SkinLayer.AirPlayFull, SkinLayer.NoticeArt];
        var r = WithDesigner(d =>
        {
            Assert.True(d.LoadSkin("all-art"));
            return layers.ToDictionary(l => l, l =>
            {
                d.Select(l);
                return Fields<TextBlock>(d.PropertiesHost).Any(t => t.Text == hint);
            });
        });
        foreach (var (layer, shown) in r)
            _out.WriteLine($"{layer}: frame-size hint expected {SkinLayers.BoardOf(layer) == SkinLayer.VolumeBar}, actual {shown}");
        Assert.True(r[SkinLayer.VolumeEmpty]);
        Assert.True(r[SkinLayer.VolumeFull]);
        Assert.True(r[SkinLayer.VolumeMuted]);
        Assert.False(r[SkinLayer.AirPlayEmpty]);
        Assert.False(r[SkinLayer.AirPlayFull]);
        Assert.False(r[SkinLayer.NoticeArt]);
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
            bool began = d.Overlay.BeginDragAt(() => start);
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
            d.Overlay.BeginDragAt(() => start);
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
            d.Overlay.BeginDragAt(() => OverOverlay(new Vector(0, 0)));
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
            d.Overlay.BeginDragAt(() => start);
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

    [Fact]
    public void Zoom_is_clamped_and_drives_the_canvas_and_the_overlay()
    {
        MakeSkin("zoom", "{}");
        var r = WithDesigner(d =>
        {
            d.LoadSkin("zoom");
            d.SetZoom(20);
            // Named: d.Zoom and d.Overlay.Zoom would both infer "Zoom", and C# then names neither.
            var high = (Zoom: d.Zoom, ScaleX: d.ZoomTransform.ScaleX, OverlayZoom: d.Overlay.Zoom, Text: d.ZoomText.Text,
                Mode: System.Windows.Media.RenderOptions.GetBitmapScalingMode(d.Artboards));
            d.SetZoom(0.01);
            return (high, Low: d.Zoom, LowText: d.ZoomText.Text);
        });
        _out.WriteLine($"expected 800% everywhere with nearest-neighbour scaling, then 25%; actual {r}");
        Assert.Equal((8.0, 8.0, 8.0, "800%"), (r.high.Zoom, r.high.ScaleX, r.high.OverlayZoom, r.high.Text));
        Assert.Equal(System.Windows.Media.BitmapScalingMode.NearestNeighbor, r.high.Mode);
        Assert.Equal((0.25, "25%"), (r.Low, r.LowText));
    }

    /// <summary>Fit is decided by the skin, not by the boards that are always there (the 300 px
    /// notice pill, the native strip): a skin wider than them fits at a zoom those boards alone
    /// could never produce, and a tiny skin fits larger than that.</summary>
    [Fact]
    public void Fit_is_decided_by_the_skins_size()
    {
        foreach (var (name, width, height) in new[] { ("tiny", 8, 2), ("wide", 1200, 40) })
        {
            var folder = Path.Combine(_root, name);
            Directory.CreateDirectory(folder);
            RealPngs.WriteSolid(Path.Combine(folder, "empty.png"), width, height, Color.Red);
            RealPngs.WriteSolid(Path.Combine(folder, "full.png"), width, height, Color.Lime);
        }
        var r = WithDesigner(d =>
        {
            d.LoadSkin("tiny");
            d.ZoomToFit();
            double tiny = d.Zoom;
            d.LoadSkin("wide");
            d.ZoomToFit();
            return (Tiny: tiny, Wide: d.Zoom, Viewport: d.CanvasScroll.ViewportWidth, Boards: d.Artboards.ActualWidth);
        });
        // The 1200 px skin has to fit across the viewport: no zoom above this can show it whole.
        double widest = CanvasZoom.Clamp(r.Viewport / 1200.0);
        _out.WriteLine($"fit: tiny {r.Tiny}, wide {r.Wide}; viewport {r.Viewport}, artboards {r.Boards} px unzoomed; " +
                       $"expected wide in [{CanvasZoom.Min}, {widest}] and tiny > wide");
        Assert.True(r.Boards >= 1200, $"the artboards are {r.Boards} px wide, narrower than the 1200 px skin");
        Assert.InRange(r.Wide, CanvasZoom.Min, widest);
        Assert.True(r.Tiny > r.Wide, $"a tiny skin fitted at {r.Tiny}, no larger than a 1200 px one at {r.Wide}");
    }

    [Fact]
    public void Keyboard_shortcuts_edit_the_canvas_and_leave_text_fields_alone()
    {
        MakeSkin("keys", """{"percentText":{"show":true,"x":10,"y":5}}""");
        var muted = Src("m.png", 200, 40, Color.Gray);
        var r = WithDesigner(d =>
        {
            d.LoadSkin("keys");
            d.Select(SkinLayer.PercentText);
            d.HandleShortcut(Key.Right, ModifierKeys.None, SkinDesignerWindow.KeyboardTarget.Canvas);
            d.HandleShortcut(Key.Down, ModifierKeys.Shift, SkinDesignerWindow.KeyboardTarget.Canvas);
            var nudged = (d.Document.Config.Text!.X, d.Document.Config.Text.Y);
            bool typedArrow = d.HandleShortcut(Key.Left, ModifierKeys.None, SkinDesignerWindow.KeyboardTarget.Text);
            d.HandleShortcut(Key.Z, ModifierKeys.Control, SkinDesignerWindow.KeyboardTarget.Other);
            var undone = d.Document.Config.Text!.Y;
            d.HandleShortcut(Key.Z, ModifierKeys.Control | ModifierKeys.Shift, SkinDesignerWindow.KeyboardTarget.Other);
            var redone = d.Document.Config.Text!.Y;
            bool undoInText = d.HandleShortcut(Key.Z, ModifierKeys.Control, SkinDesignerWindow.KeyboardTarget.Text);
            d.Commit(SkinEdits.SetArt(d.Document, SkinLayer.VolumeMuted, muted, 1, new ImageProbe()));
            d.Select(SkinLayer.VolumeMuted);
            d.HandleShortcut(Key.Delete, ModifierKeys.None, SkinDesignerWindow.KeyboardTarget.Canvas);
            var mutedAfterDelete = d.Document.MutedPath;
            d.HandleShortcut(Key.Escape, ModifierKeys.None, SkinDesignerWindow.KeyboardTarget.Canvas);
            d.SetZoom(3); // off 100 %, so Ctrl+1 has something to do
            d.HandleShortcut(Key.D1, ModifierKeys.Control, SkinDesignerWindow.KeyboardTarget.Text);
            bool saved = d.HandleShortcut(Key.S, ModifierKeys.Control, SkinDesignerWindow.KeyboardTarget.Text);
            return (nudged, typedArrow, undone, redone, undoInText, mutedAfterDelete, d.Selection, d.Zoom, saved, Dirty: d.History.IsDirty);
        });
        _out.WriteLine($"{r}");
        Assert.Equal((11, 15), r.nudged);
        Assert.False(r.typedArrow);
        Assert.Equal(5, r.undone);
        Assert.Equal(15, r.redone);
        Assert.False(r.undoInText);         // Ctrl+Z inside a text box is the text box's
        Assert.Null(r.mutedAfterDelete);
        Assert.Equal(SkinLayer.SkinInfo, r.Selection);
        Assert.Equal(1.0, r.Zoom);
        Assert.True(r.saved);
        Assert.False(r.Dirty);
    }

    /// <summary>Each arrow press is its own undo step; Ctrl+Y redoes like Ctrl+Shift+Z; Ctrl+0
    /// fits and Ctrl+1 goes back to 100 %; Delete clears the power area; keys that are not the
    /// canvas's are left to whoever has the keyboard.</summary>
    [Fact]
    public void Every_nudge_is_one_step_and_the_other_shortcuts_do_their_one_thing()
    {
        MakeSkin("steps", """{"airplay":{"connectHit":{"x":150,"y":4,"w":40,"h":16}}}""", strip: true);
        var r = WithDesigner(d =>
        {
            d.LoadSkin("steps");
            d.Select(SkinLayer.AirPlayPower);
            var loaded = d.Document.Config.AirPlay!.ConnectHit;
            foreach (var key in new[] { Key.Left, Key.Left, Key.Up })
                d.HandleShortcut(key, ModifierKeys.None, SkinDesignerWindow.KeyboardTarget.Canvas);
            var nudged = d.Document.Config.AirPlay!.ConnectHit;
            d.History.Undo();
            var oneBack = d.Document.Config.AirPlay!.ConnectHit;
            d.HandleShortcut(Key.Y, ModifierKeys.Control, SkinDesignerWindow.KeyboardTarget.Canvas);
            var redone = d.Document.Config.AirPlay!.ConnectHit;
            d.HandleShortcut(Key.Delete, ModifierKeys.None, SkinDesignerWindow.KeyboardTarget.Canvas);
            var deleted = d.Document.Config.AirPlay!.ConnectHit;
            d.HandleShortcut(Key.D0, ModifierKeys.Control, SkinDesignerWindow.KeyboardTarget.Canvas);
            double fitted = d.Zoom;
            d.SetZoom(1);
            d.ZoomToFit();
            double fit = d.Zoom;
            d.HandleShortcut(Key.NumPad1, ModifierKeys.Control, SkinDesignerWindow.KeyboardTarget.Canvas);
            bool deleteInTree = d.HandleShortcut(Key.Delete, ModifierKeys.None, SkinDesignerWindow.KeyboardTarget.Other);
            bool plainKey = d.HandleShortcut(Key.A, ModifierKeys.None, SkinDesignerWindow.KeyboardTarget.Canvas);
            bool ctrlKey = d.HandleShortcut(Key.Q, ModifierKeys.Control, SkinDesignerWindow.KeyboardTarget.Canvas);
            return (loaded, nudged, oneBack, redone, deleted, fitted, fit, Actual: d.Zoom, deleteInTree, plainKey, ctrlKey);
        });
        _out.WriteLine($"expected loaded 150,4; nudged 148,3; one undo 148,4; redone 148,3; deleted null; Ctrl+0 = fit; then 1; actual {r}");
        Assert.Equal((150, 4), (r.loaded!.X, r.loaded.Y));
        Assert.Equal((148, 3), (r.nudged!.X, r.nudged.Y));
        Assert.Equal((148, 4), (r.oneBack!.X, r.oneBack.Y));
        Assert.Equal(r.nudged, r.redone);
        Assert.Null(r.deleted);
        Assert.NotEqual(1.0, r.fit);
        Assert.Equal(r.fit, r.fitted);
        Assert.Equal(1.0, r.Actual);
        Assert.False(r.deleteInTree);
        Assert.False(r.plainKey);
        Assert.False(r.ctrlKey);
    }

    /// <summary>What has the keyboard decides who a key is for: the canvas (the overlay, the window
    /// itself, nothing), a text input, or anything else - a number field's scrub label included,
    /// so its own Esc and arrow keys stay its own.</summary>
    [Fact]
    public void The_keyboard_target_is_what_has_the_focus()
    {
        MakeSkin("target", """{"percentText":{"show":true,"x":10,"y":5}}""");
        var r = WithDesigner(d =>
        {
            d.LoadSkin("target");
            d.Select(SkinLayer.PercentText);
            var field = Number(d, "X");
            return (Overlay: d.TargetOf(d.Overlay), Window: d.TargetOf(d), Nothing: d.TargetOf(null),
                Box: d.TargetOf(field.Box), Device: d.TargetOf(d.PreviewDevice),
                Label: d.TargetOf(field.Label), Tree: d.TargetOf(d.LayersTree), Button: d.TargetOf(d.UndoButton));
        });
        _out.WriteLine($"expected canvas x3, text x2, other x3; actual {r}");
        var canvas = SkinDesignerWindow.KeyboardTarget.Canvas;
        var text = SkinDesignerWindow.KeyboardTarget.Text;
        var other = SkinDesignerWindow.KeyboardTarget.Other;
        Assert.Equal((canvas, canvas, canvas), (r.Overlay, r.Window, r.Nothing));
        Assert.Equal((text, text), (r.Box, r.Device));
        Assert.Equal((other, other, other), (r.Label, r.Tree, r.Button));
    }

    /// <summary>Ctrl+wheel keeps the skin pixel under the cursor where it is - in and out, with
    /// the canvas's margin and centring in play; a plain wheel is left to scroll.</summary>
    [Fact]
    public void Zooming_at_the_cursor_keeps_the_skin_under_it_still()
    {
        MakeSkin("anchor", """{"scale":2}""");
        var r = WithDesigner(d =>
        {
            d.LoadSkin("anchor");
            d.SetZoom(2);
            d.UpdateLayout();
            var cursor = new Point(d.CanvasScroll.ViewportWidth * 0.7, d.CanvasScroll.ViewportHeight * 0.4);
            (Point Skin, Point Now, double H, double V) Step(int delta)
            {
                var skin = d.CanvasScroll.TranslatePoint(cursor, d.Artboards);
                bool zoomed = d.HandleWheel(delta, ModifierKeys.Control, cursor);
                Assert.True(zoomed);
                d.UpdateLayout();
                return (skin, d.Artboards.TranslatePoint(skin, d.CanvasScroll), d.CanvasScroll.HorizontalOffset, d.CanvasScroll.VerticalOffset);
            }
            var zoomIn = Step(120);
            double inZoom = d.Zoom;
            var zoomOut = Step(-120);
            double outZoom = d.Zoom;
            bool plain = d.HandleWheel(120, ModifierKeys.None, cursor);
            return (cursor, zoomIn, inZoom, zoomOut, outZoom, plain, After: d.Zoom, OverlayZoom: d.Overlay.Zoom,
                Viewport: (d.CanvasScroll.ViewportWidth, d.CanvasScroll.ViewportHeight));
        });
        _out.WriteLine($"cursor {r.cursor} in viewport {r.Viewport}; in to {r.inZoom}: skin {r.zoomIn.Skin} now at {r.zoomIn.Now} " +
                       $"(offsets {r.zoomIn.H},{r.zoomIn.V}); out to {r.outZoom}: skin {r.zoomOut.Skin} now at {r.zoomOut.Now} " +
                       $"(offsets {r.zoomOut.H},{r.zoomOut.V}); plain wheel taken {r.plain}, zoom after {r.After}, overlay {r.OverlayZoom}");
        Assert.Equal((3.0, 2.0), (r.inZoom, r.outZoom));
        Assert.True(r.zoomIn.H > 0, "the zoomed-in canvas never scrolled: the test proves nothing");
        foreach (var step in new[] { r.zoomIn, r.zoomOut })
        {
            Assert.InRange(step.Now.X, r.cursor.X - 1, r.cursor.X + 1); // layout rounding of the offsets
            Assert.InRange(step.Now.Y, r.cursor.Y - 1, r.cursor.Y + 1);
        }
        Assert.False(r.plain);
        Assert.Equal((2.0, 2.0), (r.After, r.OverlayZoom));
    }

    /// <summary>Middle-drag pans; a left drag pans only with Space held; letting go (the capture
    /// lost) ends the pan, and the pointer moving on scrolls nothing.</summary>
    [Fact]
    public void Middle_drag_and_space_drag_pan_the_canvas()
    {
        MakeSkin("pan", """{"scale":2}""");
        var r = WithDesigner(d =>
        {
            d.LoadSkin("pan");
            d.SetZoom(4);
            d.UpdateLayout();
            d.CanvasScroll.ScrollToHorizontalOffset(100);
            d.CanvasScroll.ScrollToVerticalOffset(100);
            d.UpdateLayout();
            var start = (d.CanvasScroll.HorizontalOffset, d.CanvasScroll.VerticalOffset);
            bool leftAlone = d.BeginPan(MouseButton.Left, spaceHeld: false, new Point(200, 200));
            bool middle = d.BeginPan(MouseButton.Middle, spaceHeld: false, new Point(200, 200));
            var cursor = d.CanvasScroll.Cursor;
            d.PanTo(new Point(150, 170));
            d.UpdateLayout();
            var panned = (d.CanvasScroll.HorizontalOffset, d.CanvasScroll.VerticalOffset);
            d.CanvasScroll.ReleaseMouseCapture();
            d.PanTo(new Point(0, 0));
            d.UpdateLayout();
            var released = (d.CanvasScroll.HorizontalOffset, d.CanvasScroll.VerticalOffset, Cursor: d.CanvasScroll.Cursor);
            bool space = d.BeginPan(MouseButton.Left, spaceHeld: true, new Point(200, 200));
            d.PanTo(new Point(210, 200));
            d.UpdateLayout();
            var spacePanned = d.CanvasScroll.HorizontalOffset;
            d.CanvasScroll.ReleaseMouseCapture();
            return (start, leftAlone, middle, cursor, panned, released, space, spacePanned);
        });
        _out.WriteLine($"expected start (100,100), left alone not a pan, middle pans to (150,130), release keeps it, " +
                       $"space-left pans back 10; actual {r}");
        Assert.Equal((100.0, 100.0), r.start);
        Assert.False(r.leftAlone);
        Assert.True(r.middle);
        Assert.Equal(System.Windows.Input.Cursors.ScrollAll, r.cursor);
        Assert.Equal((150.0, 130.0), r.panned);
        Assert.Equal((150.0, 130.0), (r.released.HorizontalOffset, r.released.VerticalOffset));
        Assert.Null(r.released.Cursor);
        Assert.True(r.space);
        Assert.Equal(140.0, r.spacePanned);
    }

    /// <summary>An arrow pressed while a canvas drag runs ends the drag first, as Undo does: the
    /// drag stays one undo step, the nudge another on top, and the pointer moving on edits nothing.</summary>
    [Fact]
    public void A_nudge_during_a_canvas_drag_ends_the_drag_first()
    {
        MakeSkin("nudge-drag", """{"percentText":{"show":true,"x":40,"y":0}}""");
        var r = WithDesigner(d =>
        {
            d.LoadSkin("nudge-drag");
            var start = Centre(d, SkinLayer.PercentText);
            d.Overlay.BeginDragAt(() => start);
            d.Overlay.DragTo(start + new Vector(10, 0), snap: false);
            d.HandleShortcut(Key.Right, ModifierKeys.None, SkinDesignerWindow.KeyboardTarget.Canvas);
            int nudged = d.Document.Config.Text!.X;
            d.Overlay.DragTo(start + new Vector(20, 0), snap: false); // the mouse moving on
            int afterMove = d.Document.Config.Text!.X;
            d.History.Undo();
            int oneBack = d.Document.Config.Text!.X;
            d.History.Undo();
            return (nudged, afterMove, oneBack, TwoBack: d.Document.Config.Text!.X, d.History.CanUndo);
        });
        _out.WriteLine($"expected nudged 51, still 51 after the move, undo to 50 then 40 with nothing left; actual {r}");
        Assert.Equal((51, 51, 50, 40, false), r);
    }

    /// <summary>The mouse-down point is read after the canvas takes the focus: that focus change is
    /// what commits the field being typed in, which can re-lay the canvas, so a point read before
    /// it could be stale.</summary>
    [Fact]
    public void A_canvas_press_reads_its_point_after_taking_the_focus()
    {
        MakeSkin("press", """{"percentText":{"show":true,"x":40,"y":0}}""");
        var r = WithDesigner(d =>
        {
            d.LoadSkin("press");
            d.Select(SkinLayer.PercentText);
            StaWpf.Pump();
            Number(d, "X").Box.Focus();
            bool overlayFocusedBefore = d.Overlay.IsFocused;
            bool? focusedWhenRead = null;
            var start = Centre(d, SkinLayer.PercentText);
            bool began = d.Overlay.BeginDragAt(() => { focusedWhenRead = d.Overlay.IsFocused; return start; });
            d.Overlay.FinishDrag();
            return (overlayFocusedBefore, focusedWhenRead, began);
        });
        _out.WriteLine($"expected the overlay unfocused before, focused when the point is read, a drag begun; actual {r}");
        Assert.False(r.overlayFocusedBefore);
        Assert.True(r.focusedWhenRead);
        Assert.True(r.began);
    }

    /// <summary>The canvas leaves room past the artboards only for what lies past them - text at a
    /// far edge and its handles, whose size is screen pixels and so grows in skin pixels as the
    /// canvas zooms out. A skin with nothing past its boards gets no margin at all.</summary>
    [Fact]
    public void The_room_past_the_artboards_is_only_for_elements_and_follows_the_zoom()
    {
        MakeSkin("plain", "{}");
        MakeSkin("edge", """{"percentText":{"show":true,"x":195,"y":30}}""");
        var r = WithDesigner(d =>
        {
            d.LoadSkin("plain");
            d.UpdateLayout();
            var plain = d.Artboards.Margin;
            d.LoadSkin("edge");
            d.SetZoom(8);
            var near = d.Artboards.Margin;
            d.SetZoom(0.25);
            var far = d.Artboards.Margin;
            return (plain, near, far, Expected: Math.Ceiling(d.Overlay.HitArea.Right - d.Overlay.ActualWidth));
        });
        _out.WriteLine($"plain skin margin {r.plain} (expected 0); edge text margin {r.near} at 800%, {r.far} at 25% (expected right {r.Expected})");
        Assert.Equal(new Thickness(0), r.plain);
        Assert.True(r.near.Right > 0, "text past the bar's right edge got no room");
        Assert.True(r.far.Right > r.near.Right, "the handle room did not grow as the canvas zoomed out");
        Assert.Equal(r.Expected, r.far.Right);
    }

    /// <summary>A key pressed on <paramref name="target"/> the way WPF's input manager delivers it:
    /// PreviewKeyDown tunnels from the window down to it, then - unless that was handled - KeyDown
    /// bubbles back up. Modifiers are the real keyboard's (none held). Whether the key was handled.</summary>
    private static bool Press(UIElement target, Key key)
    {
        var source = PresentationSource.FromVisual(target)!;
        var preview = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(preview);
        if (preview.Handled) return true;
        var down = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyDownEvent };
        target.RaiseEvent(down);
        return down.Handled;
    }

    /// <summary>Gives <paramref name="element"/> the keyboard focus for real, activating the window
    /// first; false (and why) when the desktop will not let the test window have it.</summary>
    private bool TakeKeyboardFocus(SkinDesignerWindow d, UIElement element)
    {
        d.Activate();
        StaWpf.Pump();
        Keyboard.Focus(element);
        if (element.IsKeyboardFocused) return true;
        _out.WriteLine($"keyboard focus refused: window active {d.IsActive}, focused {Keyboard.FocusedElement?.GetType().Name ?? "(none)"}");
        return false;
    }

    /// <summary>A clicked toolbar or zoom button does not keep the keyboard: the canvas keeps it, so
    /// Space held for a pan is not a second click of the button, and the canvas still takes its keys.</summary>
    [Fact]
    public void Clicked_buttons_leave_the_keyboard_with_the_canvas()
    {
        MakeSkin("buttons", """{"scale":2,"percentText":{"show":true,"x":10,"y":5}}""");
        var r = WithDesigner(d =>
        {
            d.LoadSkin("buttons");
            d.Select(SkinLayer.PercentText);
            var buttons = new ButtonBase[] { d.NewButton, d.ImportZipButton, d.ExportZipButton, d.UndoButton, d.RedoButton,
                d.ShareButton, d.TestButton, d.ShowNoticeButton, d.SaveButton, d.ZoomOutButton, d.ZoomInButton, d.ZoomFitButton, d.ZoomActualButton };
            var focusable = buttons.Where(b => b.Focusable).Select(b => b.Name).ToList();
            bool focused = TakeKeyboardFocus(d, d.Overlay);
            d.ZoomInButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            double zoomed = d.Zoom;
            bool buttonTookFocus = d.ZoomInButton.Focus();
            var after = (OnButton: d.ZoomInButton.IsKeyboardFocused, OnCanvas: d.Overlay.IsKeyboardFocused,
                Target: d.TargetOf(Keyboard.FocusedElement));
            // The canvas still gets the keys: Esc, whose effect (deselect) no Shift or Alt changes.
            bool keyTaken = Press(d.Overlay, Key.Escape);
            var selection = d.Selection;
            d.UpdateLayout();
            bool panned = d.BeginPan(MouseButton.Left, spaceHeld: true, new Point(100, 100));
            d.CanvasScroll.ReleaseMouseCapture();
            return (focusable, focused, zoomed, buttonTookFocus, after, keyTaken, selection, panned, Modifiers: Keyboard.Modifiers);
        });
        _out.WriteLine($"expected no focusable buttons, the canvas focused before and after the click, zoom 1.5, Esc on the canvas deselects, a pan; actual " +
                       $"focusable [{string.Join(", ", r.focusable)}], {r.focused}, {r.zoomed}, button took focus {r.buttonTookFocus}, " +
                       $"{r.after}, Esc taken {r.keyTaken}, selection {r.selection}, panned {r.panned}; keyboard modifiers {r.Modifiers}");
        Assert.Empty(r.focusable);
        Assert.True(r.focused, "the test window could not take the keyboard focus (see output)");
        Assert.Equal(1.5, r.zoomed);
        Assert.False(r.buttonTookFocus);
        Assert.Equal((false, true, SkinDesignerWindow.KeyboardTarget.Canvas), r.after);
        Assert.True(r.keyTaken);
        Assert.Equal(SkinLayer.SkinInfo, r.selection);
        Assert.True(r.panned);
    }

    /// <summary>Esc during a canvas drag ends the drag before deselecting: one undo step, and the
    /// pointer moving on edits nothing.</summary>
    [Fact]
    public void Esc_during_a_canvas_drag_ends_it_before_deselecting()
    {
        MakeSkin("esc-drag", """{"percentText":{"show":true,"x":40,"y":0}}""");
        var r = WithDesigner(d =>
        {
            d.LoadSkin("esc-drag");
            var start = Centre(d, SkinLayer.PercentText);
            d.Overlay.BeginDragAt(() => start);
            d.Overlay.DragTo(start + new Vector(10, 0), snap: false);
            bool taken = d.HandleShortcut(Key.Escape, ModifierKeys.None, SkinDesignerWindow.KeyboardTarget.Canvas);
            var afterEsc = (d.Document.Config.Text!.X, d.Selection, d.Overlay.IsMouseCaptured);
            d.Overlay.DragTo(start + new Vector(20, 0), snap: false); // the mouse moving on
            int afterMove = d.Document.Config.Text!.X;
            d.History.Undo();
            return (taken, afterEsc, afterMove, Undone: d.Document.Config.Text!.X, d.History.CanUndo);
        });
        _out.WriteLine($"expected taken, (50, SkinInfo, no capture), 50 after the move, one undo back to 40; actual {r}");
        Assert.True(r.taken);
        Assert.Equal((50, SkinLayer.SkinInfo, false), r.afterEsc);
        Assert.Equal(50, r.afterMove);
        Assert.Equal(40, r.Undone);
        Assert.False(r.CanUndo);
    }

    /// <summary>Keys through the real window: a number box's Up steps the box and not the canvas,
    /// Esc during a scrub cancels the scrub (the selection stays), a text box keeps its arrows and
    /// Esc, and Esc with the canvas focused deselects. Raised key events carry the REAL keyboard's
    /// modifiers, so nothing here asserts a modifier-dependent amount: the field's step is checked
    /// as "it stepped" (1, or 10 with Shift held, are both routing to the field), the scrub and the
    /// Esc revert are relative to that, and the canvas is checked with Esc, which no modifier but
    /// Ctrl changes. Shift steps and nudges are covered with explicit modifiers by the HandleKey and
    /// HandleShortcut tests.</summary>
    [Fact]
    public void Keys_reach_the_field_that_has_them_and_the_canvas_otherwise()
    {
        MakeSkin("route", """{"percentText":{"show":true,"x":10,"y":5}}""");
        var r = WithDesigner(d =>
        {
            d.LoadSkin("route");
            d.Select(SkinLayer.PercentText);
            StaWpf.Pump();
            var field = Number(d, "Y");
            bool boxFocused = TakeKeyboardFocus(d, field.Box);
            bool upTaken = Press(field.Box, Key.Up);
            var afterUp = (Box: field.Box.Text, Y: d.Document.Config.Text!.Y, X: d.Document.Config.Text.X);
            field = Number(d, "Y"); // a commit may have rebuilt the panel
            bool labelFocused = TakeKeyboardFocus(d, field.Label);
            bool scrubbing = field.BeginScrub(0);
            field.ScrubTo(10 * NumberField.ScrubPixelsPerStep);
            int scrubbed = d.Document.Config.Text!.Y;
            bool escTaken = Press(field.Label, Key.Escape);
            var afterEsc = (Y: d.Document.Config.Text!.Y, d.Selection, Captured: field.Label.IsMouseCaptured);
            bool textFocused = TakeKeyboardFocus(d, d.PreviewDevice);
            Press(d.PreviewDevice, Key.Left);
            Press(d.PreviewDevice, Key.Escape);
            var inText = (X: d.Document.Config.Text!.X, d.Selection, d.History.CanUndo);
            bool canvasFocused = TakeKeyboardFocus(d, d.Overlay);
            bool escOnCanvas = Press(d.Overlay, Key.Escape);
            return (boxFocused, upTaken, afterUp, labelFocused, scrubbing, scrubbed, escTaken, afterEsc,
                textFocused, inText, canvasFocused, escOnCanvas, Deselected: d.Selection, Modifiers: Keyboard.Modifiers);
        });
        _out.WriteLine($"expected box focused, Up taken, Y stepped up from 5 with the box showing it and x still 10; label focused, " +
                       $"scrub 10 steps past that, Esc taken, back to the stepped Y with PercentText still selected; in a text box Left " +
                       $"and Esc are the box's (x 10, PercentText); canvas focused, Esc taken and SkinInfo selected; actual (Modifiers last) {r}");
        Assert.True(r.boxFocused && r.labelFocused && r.textFocused && r.canvasFocused, "the test window could not take the keyboard focus (see output)");
        Assert.True(r.upTaken);
        Assert.True(r.afterUp.Y > 5, "Up in the box did not step the field");    // 6, or 15 with Shift held: both the field's
        Assert.Equal((r.afterUp.Y.ToString(System.Globalization.CultureInfo.CurrentCulture), 10), (r.afterUp.Box, r.afterUp.X)); // the canvas did not nudge the text
        Assert.True(r.scrubbing);
        Assert.Equal(r.afterUp.Y + 10, r.scrubbed);
        Assert.True(r.escTaken);
        Assert.Equal((r.afterUp.Y, SkinLayer.PercentText, false), r.afterEsc);
        Assert.Equal((10, SkinLayer.PercentText), (r.inText.X, r.inText.Selection));
        Assert.True(r.escOnCanvas);
        Assert.Equal(SkinLayer.SkinInfo, r.Deselected);
    }

    /// <summary>The picker's eyedropper samples the canvas into the field the picker is open on,
    /// with the picker pinned open while it waits for the click (the click would close it), and the
    /// pick is part of that opening's one undo step.</summary>
    [Fact]
    public void The_eyedropper_picks_a_colour_off_the_canvas_into_the_open_field()
    {
        MakeSkin("dropper", """{"percentText":{"show":true,"x":150,"y":5}}""");
        var r = WithDesigner(d =>
        {
            d.LoadSkin("dropper");
            var before = d.Document.Config.Text!.Color;
            Click(PercentTextColours(d)[0].Swatch);
            d.Picker!.SetColor("#FFFF0000");
            var status = d.StatusText.Text;
            Click(d.Picker.EyedropperButton);
            var active = (d.Overlay.EyedropperActive, d.Picker.StaysOpen, Magnifier: d.Overlay.Magnifier is not null, d.StatusText.Text);
            // 5 px into the volume bar, where the fill (62 %) shows the full layer: lime.
            var at = d.VolumeBoard.TranslatePoint(new Point(5, 5), d.Overlay);
            d.PickSample(at);
            var after = (Colour: d.Document.Config.Text!.Color, d.Overlay.EyedropperActive, d.Picker.StaysOpen,
                Magnifier: d.Overlay.Magnifier is not null, d.Picker.IsOpen, d.Selection);
            var statusAfter = (Before: status, After: d.StatusText.Text);
            d.History.Undo();
            return (before, active, after, statusAfter, Undone: d.Document.Config.Text!.Color, d.History.CanUndo);
        });
        _out.WriteLine($"expected: eyedropper on, picker pinned, magnifier set, status {Loc.T("designer.status.eyedropper")}; " +
                       "after the pick #FF00FF00 with the eyedropper off, the picker unpinned and still open on PercentText; " +
                       $"one undo back to {r.before} with nothing left to undo");
        _out.WriteLine($"actual  : on {r.active}; after {r.after}; status before/after {r.statusAfter}; undone {r.Undone}, canUndo={r.CanUndo}");
        Assert.Equal((true, true, true, Loc.T("designer.status.eyedropper")), r.active);
        Assert.Equal(r.statusAfter.Before, r.statusAfter.After);
        Assert.Equal(("#FF00FF00", false, false, false, true, SkinLayer.PercentText), r.after);
        Assert.Equal(r.before, r.Undone);
        Assert.False(r.CanUndo);
    }

    /// <summary>Esc while the eyedropper waits cancels it - and only it: the selection, the
    /// history and the picker are left as they were. The next Esc deselects as usual.</summary>
    [Fact]
    public void Esc_cancels_the_eyedropper_before_it_deselects()
    {
        MakeSkin("dropper-esc", """{"percentText":{"show":true}}""");
        var r = WithDesigner(d =>
        {
            d.LoadSkin("dropper-esc");
            Click(PercentTextColours(d)[0].Swatch);
            Click(d.Picker!.EyedropperButton);
            bool focused = d.Overlay.IsKeyboardFocused;
            bool taken = d.HandleShortcut(Key.Escape, ModifierKeys.None, SkinDesignerWindow.KeyboardTarget.Canvas);
            var first = (taken, d.Overlay.EyedropperActive, d.Overlay.Magnifier is null, d.Picker.IsOpen, d.Picker.StaysOpen,
                d.Selection, d.History.CanUndo);
            d.HandleShortcut(Key.Escape, ModifierKeys.None, SkinDesignerWindow.KeyboardTarget.Canvas);
            return (focused, first, Second: d.Selection);
        });
        _out.WriteLine("expected: canvas focused for Esc; first Esc taken, eyedropper off, magnifier dropped, picker open and unpinned, " +
                       "PercentText still selected, no history; second Esc selects SkinInfo");
        _out.WriteLine($"actual  : focused={r.focused}; first {r.first}; second {r.Second}");
        Assert.True(r.focused);
        Assert.Equal((true, false, true, true, false, SkinLayer.PercentText, false), r.first);
        Assert.Equal(SkinLayer.SkinInfo, r.Second);
    }

    /// <summary>Whatever ends the picker's opening ends the eyedropper with it - another
    /// selection, another swatch - so the picker is never left pinned open and the canvas never
    /// left sampling for a field that has gone.</summary>
    [Fact]
    public void Ending_the_opening_ends_the_eyedropper()
    {
        MakeSkin("dropper-end", """{"percentText":{"show":true}}""");
        var r = WithDesigner(d =>
        {
            d.LoadSkin("dropper-end");
            var colours = PercentTextColours(d);
            Click(colours[0].Swatch);
            Click(d.Picker!.EyedropperButton);
            Click(colours[1].Swatch);  // the outline colour, while the eyedropper waits
            var reopened = (d.Overlay.EyedropperActive, d.Picker.StaysOpen, d.Picker.IsOpen);
            Click(d.Picker.EyedropperButton);
            d.Select(SkinLayer.VolumeBar);
            var reselected = (d.Overlay.EyedropperActive, d.Picker.StaysOpen, d.Picker.IsOpen, d.Overlay.Magnifier is null);
            return (reopened, reselected, Text: d.Document.Config.Text);
        });
        _out.WriteLine("expected: another swatch -> eyedropper off, picker unpinned and open; another selection -> " +
                       "eyedropper off, picker unpinned and closed, magnifier dropped; colours untouched");
        _out.WriteLine($"actual  : swatch {r.reopened}; selection {r.reselected}; text {r.Text}");
        Assert.Equal((false, false, true), r.reopened);
        Assert.Equal((false, false, false, true), r.reselected);
        Assert.Null(r.Text!.OutlineColor);
    }

    /// <summary>Switching to another app while the eyedropper waits ends it: the pinned picker
    /// would otherwise float over that app. Needs a desktop that can activate windows.</summary>
    [Fact]
    public void Leaving_the_window_ends_the_eyedropper()
    {
        MakeSkin("dropper-away", """{"percentText":{"show":true}}""");
        var r = WithDesigner(d =>
        {
            d.LoadSkin("dropper-away");
            d.Activate();
            Click(PercentTextColours(d)[0].Swatch);
            Click(d.Picker!.EyedropperButton);
            var before = (d.IsActive, d.Overlay.EyedropperActive);
            // Its own empty style: WPF-UI's implicit window style (the fixture's ControlsDictionary)
            // reached it after Show here and set AllowsTransparency, which then throws.
            var other = new Window { Width = 100, Height = 100, Style = new Style(typeof(Window)) };
            other.Show();
            try
            {
                other.Activate();
                StaWpf.Pump();
                return (before, After: (d.IsActive, d.Overlay.EyedropperActive, d.Picker.StaysOpen));
            }
            finally
            {
                other.Close();
                StaWpf.Pump();
            }
        });
        _out.WriteLine($"expected: active with the eyedropper on, then inactive with it off and the picker unpinned; actual {r.before} then {r.After}");
        Assert.Equal((true, true), r.before);
        Assert.Equal((false, false, false), r.After);
    }

    /// <summary>The overlay spans the gap rows between the artboards and the room beside narrower
    /// ones, where nothing is drawn: a click there picks nothing (not a transparent black) and the
    /// eyedropper keeps waiting for a click on art.</summary>
    [Fact]
    public void A_click_between_the_artboards_picks_nothing_and_keeps_waiting()
    {
        MakeSkin("dropper-gap", """{"percentText":{"show":true,"x":150,"y":5}}""");
        var r = WithDesigner(d =>
        {
            d.LoadSkin("dropper-gap");
            var before = d.Document.Config.Text!.Color;
            Click(PercentTextColours(d)[0].Swatch);
            Click(d.Picker!.EyedropperButton);
            var gap = d.AirPlayBoard.TranslatePoint(new Point(2, d.AirPlayBoard.ActualHeight + 12), d.Overlay);
            bool gapOnABoard = d.Overlay.Scene.Boards.Any(b => b.Bounds.Contains(gap));
            d.PickSample(gap);
            var inGap = (d.Document.Config.Text!.Color, d.Overlay.EyedropperActive, d.Picker.StaysOpen, d.History.CanUndo);
            d.PickSample(d.VolumeBoard.TranslatePoint(new Point(5, 5), d.Overlay));
            return (before, gap, gapOnABoard, inGap, OnArt: (d.Document.Config.Text!.Color, d.Overlay.EyedropperActive));
        });
        _out.WriteLine($"expected: the gap point is on no board; there colour {r.before} kept, still waiting and pinned, no history; " +
                       "then on the bar #FF00FF00 and done");
        _out.WriteLine($"actual  : gap {r.gap} on a board={r.gapOnABoard}; in the gap {r.inGap}; on the art {r.OnArt}");
        Assert.False(r.gapOnABoard);
        Assert.Equal((r.before, true, true, false), r.inGap);
        Assert.Equal(("#FF00FF00", false), r.OnArt);
    }

    /// <summary>The artboards' checkerboard is the canvas's, not the art's: translucent art is
    /// picked as itself, alpha and all, not blended with the checker grey behind it.</summary>
    [Fact]
    public void Translucent_art_is_picked_with_its_own_alpha()
    {
        var folder = MakeSkin("dropper-alpha", """{"percentText":{"show":true,"x":150,"y":5}}""");
        RealPngs.WriteSolid(Path.Combine(folder, "full.png"), 200, 40, Color.FromArgb(0x80, 0xFF, 0, 0));
        var r = WithDesigner(d =>
        {
            d.LoadSkin("dropper-alpha");
            Click(PercentTextColours(d)[0].Swatch);
            Click(d.Picker!.EyedropperButton);
            d.PickSample(d.VolumeBoard.TranslatePoint(new Point(5, 5), d.Overlay));
            return (Picked: d.Document.Config.Text!.Color, Checker: ReferenceEquals(d.VolumeBoard.Background, DesignerBrushes.Checkerboard));
        });
        _out.WriteLine($"expected #80FF0000 with the checkerboard back behind the bar; actual {r}");
        Assert.Equal("#80FF0000", r.Picked);
        Assert.True(r.Checker);
    }

    /// <summary>The canvas can change while the eyedropper waits - Ctrl+Z with the canvas focused,
    /// a preview slider. The pick is of what is on screen then, not of the picture taken before.</summary>
    [Fact]
    public void Undo_while_the_eyedropper_waits_picks_what_is_shown_after_it()
    {
        MakeSkin("dropper-undo", """{"percentText":{"show":true,"x":150,"y":5}}""");
        var r = WithDesigner(d =>
        {
            d.LoadSkin("dropper-undo");
            d.Commit(d.Document with { FullPath = Src("blue.png", 200, 40, Color.Blue) });
            Click(PercentTextColours(d)[0].Swatch);
            Click(d.Picker!.EyedropperButton);
            var at = d.VolumeBoard.TranslatePoint(new Point(5, 5), d.Overlay);
            var magnified = d.Overlay.Magnifier!(at)!; // takes the snapshot, as the first move over the canvas does
            var centre = new byte[4];
            magnified.CopyPixels(new Int32Rect(magnified.PixelWidth / 2, magnified.PixelHeight / 2, 1, 1), centre, 4, 0);
            bool undone = d.HandleShortcut(Key.Z, ModifierKeys.Control, SkinDesignerWindow.KeyboardTarget.Canvas);
            var waiting = d.Overlay.EyedropperActive;
            d.PickSample(at);
            return (Before: string.Join(",", centre), undone, waiting, Picked: d.Document.Config.Text!.Color);
        });
        _out.WriteLine($"expected: magnifier showed blue (BGRA 255,0,0,255) before the undo; undo taken with the eyedropper still waiting; picked lime #FF00FF00; actual {r}");
        Assert.Equal("255,0,0,255", r.Before);
        Assert.True(r.undone);
        Assert.True(r.waiting);
        Assert.Equal("#FF00FF00", r.Picked);
    }

    /// <summary>Esc cancels the eyedropper whatever has the keyboard - a Properties text box
    /// included, through the real key routing - and the box keeps what it shows.</summary>
    [Fact]
    public void Esc_in_a_text_box_cancels_the_eyedropper()
    {
        MakeSkin("dropper-box", """{"percentText":{"show":true,"x":40}}""");
        var r = WithDesigner(d =>
        {
            d.LoadSkin("dropper-box");
            Click(PercentTextColours(d)[0].Swatch);
            Click(d.Picker!.EyedropperButton);
            var box = Number(d, "X").Box;
            bool focused = TakeKeyboardFocus(d, box);
            bool handled = Press(box, Key.Escape);
            return (focused, handled, d.Overlay.EyedropperActive, d.Selection, box.Text, d.History.CanUndo);
        });
        _out.WriteLine($"expected: box focused, Esc handled, eyedropper off, PercentText still selected, box still {N(40)}, no history; actual {r}");
        Assert.True(r.focused);
        Assert.Equal((true, false, SkinLayer.PercentText, N(40), false), (r.handled, r.EyedropperActive, r.Selection, r.Text, r.CanUndo));
    }

    /// <summary>A real click - routed MouseLeftButtonDown, then MouseLeftButtonUp - on the canvas
    /// in eyedropper mode leaves the picker open. Raised events cannot reproduce the popup's own
    /// capture closing it on a real release; that part is a manual check.</summary>
    [Fact]
    public void A_click_on_the_canvas_in_eyedropper_mode_leaves_the_picker_open()
    {
        MakeSkin("dropper-click", """{"percentText":{"show":true}}""");
        var r = WithDesigner(d =>
        {
            d.LoadSkin("dropper-click");
            Click(PercentTextColours(d)[0].Swatch);
            Click(d.Picker!.EyedropperButton);
            d.Overlay.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
            var afterDown = (d.Overlay.EyedropperActive, d.Selection, d.Overlay.IsMouseCaptured);
            d.Overlay.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            StaWpf.Pump();
            return (afterDown, Open: d.Picker.IsOpen, d.Selection);
        });
        _out.WriteLine($"expected: after the press still waiting on PercentText, no capture; after the release the picker open on PercentText; actual {r}");
        Assert.Equal((true, SkinLayer.PercentText, false), r.afterDown);
        Assert.True(r.Open);
        Assert.Equal(SkinLayer.PercentText, r.Selection);
    }

    /// <summary>Clicking the eyedropper a second time while it waits starts nothing new: the status
    /// line it put up is not taken for the one to restore, so ending it brings back what was there
    /// before the first click.</summary>
    [Fact]
    public void A_second_eyedropper_click_still_restores_the_status_line()
    {
        MakeSkin("dropper-twice", """{"percentText":{"show":true}}""");
        var r = WithDesigner(d =>
        {
            d.LoadSkin("dropper-twice");
            Click(PercentTextColours(d)[0].Swatch);
            var before = d.StatusText.Text;
            Click(d.Picker!.EyedropperButton);
            Click(d.Picker.EyedropperButton);
            var waiting = (d.Overlay.EyedropperActive, d.StatusText.Text);
            d.StopEyedropper();
            return (before, waiting, After: d.StatusText.Text, d.Overlay.EyedropperActive);
        });
        _out.WriteLine($"expected: waiting with '{Loc.T("designer.status.eyedropper")}', then '{r.before}' back and the eyedropper off; actual {r}");
        Assert.Equal((true, Loc.T("designer.status.eyedropper")), r.waiting);
        Assert.Equal(r.before, r.After);
        Assert.False(r.EyedropperActive);
    }

    /// <summary>Moved to a monitor of another scale, the window shows other device pixels than the
    /// eyedropper's snapshot holds: the next sample takes a new one.</summary>
    [Fact]
    public void A_dpi_change_retakes_the_eyedropper_snapshot()
    {
        MakeSkin("dropper-dpi", """{"percentText":{"show":true}}""");
        var r = WithDesigner(d =>
        {
            d.LoadSkin("dropper-dpi");
            Click(PercentTextColours(d)[0].Swatch);
            Click(d.Picker!.EyedropperButton);
            var at = d.VolumeBoard.TranslatePoint(new Point(5, 5), d.Overlay);
            System.Windows.Media.Imaging.BitmapSource Sampled() =>
                ((System.Windows.Media.Imaging.CroppedBitmap)d.Overlay.Magnifier!(at)!).Source;
            var first = Sampled();
            bool cached = ReferenceEquals(first, Sampled());
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(d);
            // DpiChangedEventArgs has no public constructor; this is the event WPF raises on WM_DPICHANGED.
            var args = (DpiChangedEventArgs)typeof(DpiChangedEventArgs)
                .GetConstructor(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
                    [typeof(DpiScale), typeof(DpiScale), typeof(RoutedEvent), typeof(object)], null)!
                .Invoke([dpi, new DpiScale(dpi.DpiScaleX * 1.5, dpi.DpiScaleY * 1.5), Window.DpiChangedEvent, d]);
            d.RaiseEvent(args);
            bool retaken = !ReferenceEquals(first, Sampled());
            return (cached, retaken);
        });
        _out.WriteLine($"expected the same snapshot twice before the DPI change and a new one after it; actual {r}");
        Assert.True(r.cached, "the snapshot was retaken with nothing changed, so this test cannot see an invalidation");
        Assert.True(r.retaken, "the eyedropper kept sampling the snapshot taken at the old DPI");
    }

    /// <summary>Review focus: a skin written before the strip existed is tested with the app's
    /// native strip under it, as the real OSD will draw it, and the test copy gains no strip files.</summary>
    [Fact]
    public void Test_on_desktop_gives_a_legacy_skin_the_native_strip()
    {
        MakeSkin("legacy-test", "{}");
        string? folder = null;
        var r = WithDesigner(d =>
        {
            d.LoadSkin("legacy-test");
            bool shown = d.TestOnDesktop();
            folder = d.TestFolder;
            return (shown, d.TestOsd!.IsVisible, d.TestOsd.Height, StripFiles: Directory.GetFiles(folder!, "airplay-*").Length);
        });
        _out.WriteLine($"{r}; test folder {folder}");
        Assert.True(r.shown);
        Assert.True(r.IsVisible);
        Assert.Equal(40 + 30, r.Height);     // the bar plus the native strip
        Assert.Equal(0, r.StripFiles);
        Assert.False(Directory.Exists(folder), "the test folder outlived the designer");
    }

    [Fact]
    public void Test_on_desktop_shows_the_skins_own_strip()
    {
        MakeSkin("strip-test", "{}", strip: true);
        var height = WithDesigner(d =>
        {
            d.LoadSkin("strip-test");
            d.PreviewAirPlayMode.SelectedIndex = 1; // a chosen receiver, not connected: still shown
            d.TestOnDesktop();
            return d.TestOsd!.Height;
        });
        _out.WriteLine($"expected 64 (40 bar + 24 strip), actual {height}");
        Assert.Equal(40 + 24, height);
    }

    [Fact]
    public void Show_notice_puts_the_real_notice_on_the_desktop_with_the_sample_name()
    {
        MakeSkin("notice-test", """{"notice":{"background":"#FF102030"}}""");
        var r = WithDesigner(d =>
        {
            d.LoadSkin("notice-test");
            d.PreviewDevice.Text = "Den";
            bool shown = d.ShowTestNotice();
            var notice = (NoticeView)d.TestNotice!.FindName("NoticeRoot");
            var seen = (shown, d.TestNotice.IsVisible, notice.NoticeText.Text,
                Plate: ((System.Windows.Media.SolidColorBrush)notice.PlateBorder.Background).Color);
            var window = d.TestNotice!;
            int closed = 0;
            window.Closed += (_, _) => closed++;
            d.Dispose();
            return (seen, StillVisible: window.IsVisible, Closed: closed, Released: d.TestNotice is null);
        });
        _out.WriteLine($"expected shown, visible, 'Den', #FF102030, then closed once and released at Dispose; actual {r}");
        Assert.True(r.seen.shown);
        Assert.True(r.seen.IsVisible);
        Assert.Equal("Den", r.seen.Text);
        Assert.Equal(System.Windows.Media.Color.FromArgb(0xFF, 0x10, 0x20, 0x30), r.seen.Plate);
        Assert.False(r.StillVisible);
        Assert.Equal(1, r.Closed);
        Assert.True(r.Released, "the designer kept the closed notice window");
    }

    /// <summary>Test on desktop and Show notice write the draft through the writer, which is only
    /// handed a clean document: SaveConfig would drop a broken strip. With such an issue both
    /// buttons are disabled and say why, as Save is, and a call anyway writes nothing, shows
    /// nothing and puts the reason on the status line.</summary>
    [Fact]
    public void Test_on_desktop_and_show_notice_refuse_a_document_with_issues()
    {
        MakeSkin("issues-test", "{}", strip: true);
        var r = WithDesigner(d =>
        {
            d.LoadSkin("issues-test");
            d.Commit(SkinEdits.SetFillRange(d.Document, SkinLayer.AirPlay, 150, 50, new ImageProbe()));
            var issues = d.Issues.ToArray();
            var buttons = (Valid: d.Info.IsValid, Test: d.TestButton.IsEnabled, Notice: d.ShowNoticeButton.IsEnabled,
                TestTip: d.TestButton.ToolTip as string, NoticeTip: d.ShowNoticeButton.ToolTip as string);
            bool tested = d.TestOnDesktop();
            var afterTest = (d.TestOsd, d.TestFolder, d.StatusText.Text);
            bool noticed = d.ShowTestNotice();
            return (issues, buttons, tested, afterTest, noticed, Notice: d.TestNotice, d.TestFolder, d.StatusText.Text);
        });
        var issue = r.issues.Single();
        var blocked = Loc.T("designer.test.blocked", "");
        _out.WriteLine($"expected one AirPlay FillRangeInverted issue; the skin draws but both buttons are disabled with '{issue.Message}' " +
                       "in their tooltips; both calls refused with no window or folder and the reason in the status");
        _out.WriteLine($"actual  : issues [{string.Join("; ", r.issues.AsEnumerable())}] buttons {r.buttons}; test {r.tested} {r.afterTest}; " +
                       $"notice {r.noticed} window {r.Notice?.ToString() ?? "(none)"} folder {r.TestFolder ?? "(none)"} status '{r.Text}'");
        Assert.Equal((SkinLayer.AirPlay, SkinIssueKind.FillRangeInverted), (issue.Layer, issue.Kind));
        Assert.Equal((true, false, false), (r.buttons.Valid, r.buttons.Test, r.buttons.Notice));
        foreach (var tip in new[] { r.buttons.TestTip, r.buttons.NoticeTip })
        {
            Assert.StartsWith(blocked, tip);
            Assert.Contains(issue.Message, tip);
        }
        Assert.False(r.tested);
        Assert.Null(r.afterTest.TestOsd);
        Assert.Null(r.afterTest.TestFolder);
        Assert.StartsWith(blocked, r.afterTest.Text);
        Assert.Contains(issue.Message, r.afterTest.Text);
        Assert.False(r.noticed);
        Assert.Null(r.Notice);
        Assert.Null(r.TestFolder);
        Assert.StartsWith(blocked, r.Text);
        Assert.Contains(issue.Message, r.Text);
    }

    /// <summary>The test copy is saved under a fresh name, so a name that cannot be saved - the
    /// name box cleared while typing - blocks Save but not Test on desktop or Show notice.</summary>
    [Fact]
    public void A_bad_name_alone_still_tests_and_shows_the_notice()
    {
        MakeSkin("name-test", "{}");
        var r = WithDesigner(d =>
        {
            d.LoadSkin("name-test");
            d.Commit(SkinEdits.SetName(d.Document, ""));
            var issues = d.Issues.ToArray();
            var buttons = (Save: d.SaveButton.IsEnabled, Test: d.TestButton.IsEnabled, Notice: d.ShowNoticeButton.IsEnabled,
                TestTip: d.TestButton.ToolTip as string, NoticeTip: d.ShowNoticeButton.ToolTip as string);
            bool tested = d.TestOnDesktop();
            bool osdShown = d.TestOsd?.IsVisible == true;
            bool noticed = d.ShowTestNotice();
            return (issues, buttons, tested, osdShown, noticed, NoticeShown: d.TestNotice?.IsVisible == true, d.StatusText.Text);
        });
        _out.WriteLine($"expected one InvalidName issue; Save disabled, Test and Show notice enabled with no blocked tooltip " +
                       $"('{Loc.T("designer.show-notice.tooltip")}' on Show notice); both shown; status '{Loc.T("designer.status.showing-notice")}'");
        _out.WriteLine($"actual  : issues [{string.Join("; ", r.issues.AsEnumerable())}] buttons {r.buttons}; test {r.tested} shown {r.osdShown}; " +
                       $"notice {r.noticed} shown {r.NoticeShown}; status '{r.Text}'");
        Assert.Equal(SkinIssueKind.InvalidName, r.issues.Single().Kind);
        Assert.Equal((false, true, true), (r.buttons.Save, r.buttons.Test, r.buttons.Notice));
        Assert.Null(r.buttons.TestTip);
        Assert.Equal(Loc.T("designer.show-notice.tooltip"), r.buttons.NoticeTip);
        Assert.True(r.tested && r.osdShown, "Test on desktop was blocked by the name alone");
        Assert.True(r.noticed && r.NoticeShown, "Show notice was blocked by the name alone");
        Assert.Equal(Loc.T("designer.status.showing-notice"), r.Text);
    }

    /// <summary>The notice preview is hidden - its hide timer stopped - when Test on desktop
    /// takes over and when the designer is closed (which only hides it), not left on the desktop.</summary>
    [Fact]
    public void Test_on_desktop_and_closing_the_designer_hide_the_test_notice()
    {
        MakeSkin("notice-hide", "{}");
        var r = WithDesigner(d =>
        {
            d.LoadSkin("notice-hide");
            d.ShowTestNotice();
            var notice = d.TestNotice!;
            var shown = (notice.IsVisible, Timer: notice.HideTimerRunning);
            d.TestOnDesktop();
            var afterTest = (notice.IsVisible, Timer: notice.HideTimerRunning, Osd: d.TestOsd?.IsVisible == true);
            d.ShowTestNotice();
            var reshown = (notice.IsVisible, Same: ReferenceEquals(notice, d.TestNotice));
            d.Close();
            var afterClose = (notice.IsVisible, Timer: notice.HideTimerRunning, Designer: d.IsVisible);
            return (shown, afterTest, reshown, afterClose);
        });
        _out.WriteLine("expected: notice shown with its timer running; Test on desktop hides it, timer stopped, the test OSD up; " +
                       $"shown again in the same window; closing the designer hides it, timer stopped, designer hidden; actual {r}");
        Assert.Equal((true, true), r.shown);
        Assert.Equal((false, false, true), r.afterTest);
        Assert.Equal((true, true), r.reshown);
        Assert.Equal((false, false, false), r.afterClose);
    }

    /// <summary>Until the skin draws (a new document, no art yet) there is nothing to test:
    /// both buttons are disabled and Show notice opens no window.</summary>
    [Fact]
    public void Show_notice_is_disabled_until_the_skin_draws()
    {
        MakeSkin("draws", "{}");
        var r = WithDesigner(d =>
        {
            d.NewDocument();
            var empty = (Valid: d.Info.IsValid, Test: d.TestButton.IsEnabled, Notice: d.ShowNoticeButton.IsEnabled,
                Shown: d.ShowTestNotice(), Window: d.TestNotice is not null);
            d.LoadSkin("draws");
            var loaded = (Valid: d.Info.IsValid, Test: d.TestButton.IsEnabled, Notice: d.ShowNoticeButton.IsEnabled);
            return (empty, loaded);
        });
        _out.WriteLine($"expected: new document invalid, both disabled, no notice shown or window made; loaded skin valid, both enabled; actual {r}");
        Assert.Equal((false, false, false, false, false), r.empty);
        Assert.Equal((true, true, true), r.loaded);
    }

    [Fact]
    public void Unsaved_choices_save_discard_or_stay()
    {
        var folder = MakeSkin("choices", "{}");
        var r = WithDesigner(d =>
        {
            d.LoadSkin("choices");
            bool cleanAsks = d.NeedsUnsavedPrompt;
            d.Commit(SkinEdits.SetScale(d.Document, 2));
            bool dirtyAsks = d.NeedsUnsavedPrompt;
            bool cancel = d.ApplyUnsavedChoice(SkinDesignerWindow.UnsavedChoice.Cancel);
            bool discard = d.ApplyUnsavedChoice(SkinDesignerWindow.UnsavedChoice.Discard);
            double diskAfterDiscard = SkinLoader.Load(folder).Scale;
            bool save = d.ApplyUnsavedChoice(SkinDesignerWindow.UnsavedChoice.Save);
            return (cleanAsks, dirtyAsks, cancel, discard, diskAfterDiscard, save,
                Disk: SkinLoader.Load(folder).Scale, AsksAfterSave: d.NeedsUnsavedPrompt);
        });
        _out.WriteLine($"{r}");
        Assert.False(r.cleanAsks);
        Assert.True(r.dirtyAsks);
        Assert.False(r.cancel);
        Assert.True(r.discard);
        Assert.Equal(1.0, r.diskAfterDiscard);
        Assert.True(r.save);
        Assert.Equal(2.0, r.Disk);
        Assert.False(r.AsksAfterSave);
    }

    [Fact]
    public void A_save_that_cannot_happen_keeps_the_user_where_they_are()
    {
        var r = WithDesigner(d =>
        {
            d.NewDocument();
            d.Commit(SkinEdits.SetScale(d.Document, 2));
            return (d.NeedsUnsavedPrompt, Go: d.ApplyUnsavedChoice(SkinDesignerWindow.UnsavedChoice.Save));
        });
        _out.WriteLine($"expected a prompt and Save refusing to leave (an untitled skin with no art cannot save); actual {r}");
        Assert.True(r.NeedsUnsavedPrompt);
        Assert.False(r.Go);
    }

    /// <summary>Review focus: after "Don't save" on close, the designer reopens on what is on disk.</summary>
    [Fact]
    public void Discarding_reverts_to_what_is_on_disk()
    {
        var folder = MakeSkin("revert", "{}");
        var r = WithDesigner(d =>
        {
            d.LoadSkin("revert");
            d.Commit(SkinEdits.SetScale(d.Document, 2));
            d.Save();
            d.Commit(SkinEdits.SetScale(d.Document, 3));
            d.RevertToSaved();
            var reverted = (d.Document, d.NeedsUnsavedPrompt, d.History.CanUndo);
            d.NewDocument();
            d.Commit(SkinEdits.SetName(d.Document, "never saved"));
            d.RevertToSaved();
            return (reverted, Fresh: d.Document);
        });
        _out.WriteLine($"{r}");
        Assert.Equal(SkinDocument.FromInfo(SkinLoader.Load(folder)), r.reverted.Document);
        Assert.Equal(2.0, r.reverted.Document.Config.Scale);
        Assert.False(r.reverted.NeedsUnsavedPrompt);
        Assert.False(r.reverted.CanUndo); // the discarded edits are not one undo away
        Assert.Equal(SkinDocument.New(Loc.T("designer.untitled")), r.Fresh);
    }

    /// <summary>The prompt never comes up mid-interaction: a running drag ends as one step, the
    /// picker's typed hex reaches the document (so a clean skin with only that pending edit asks),
    /// and the eyedropper stops.</summary>
    [Fact]
    public void Leaving_ends_a_drag_the_picker_and_the_eyedropper_first()
    {
        MakeSkin("interactions", """{"percentText":{"show":true,"x":40,"y":0}}""");
        var r = WithDesigner(d =>
        {
            d.LoadSkin("interactions");
            var start = Centre(d, SkinLayer.PercentText);
            d.Overlay.BeginDragAt(() => start);
            d.Overlay.DragTo(start + new Vector(10, 0), snap: false);
            d.EndInteractions();
            d.Overlay.DragTo(start + new Vector(20, 0), snap: false); // the mouse moving on edits nothing
            var drag = (d.Document.Config.Text!.X, d.NeedsUnsavedPrompt);

            d.LoadSkin("interactions");
            Click(PercentTextColours(d)[0].Swatch);
            d.Picker!.HexBox.Text = "#FF654321"; // typed, not committed
            d.StartEyedropper();
            bool askedBefore = d.NeedsUnsavedPrompt;
            d.EndInteractions();
            var picker = (d.Document.Config.Text?.Color, askedBefore, After: d.NeedsUnsavedPrompt,
                PickerOpen: d.Picker.IsOpen, Eyedropper: d.Overlay.EyedropperActive);
            return (drag, picker);
        });
        _out.WriteLine("expected: drag ended at x 50 and dirty; picker hex #FF654321 committed, clean before and dirty after, picker shut, eyedropper off");
        _out.WriteLine($"actual  : drag {r.drag}; picker {r.picker}");
        Assert.Equal((50, true), r.drag);
        Assert.Equal(("#FF654321", false, true, false, false), r.picker);
    }

    /// <summary>The user's answer, as the box would give it, counting how often it was asked.</summary>
    private sealed class Answer(SkinDesignerWindow.UnsavedChoice choice)
    {
        public int Asked { get; private set; }

        public Task<SkinDesignerWindow.UnsavedChoice> Ask()
        {
            Asked++;
            return Task.FromResult(choice);
        }
    }

    /// <summary>What ConfirmLeaveAsync (App's quit, a skin switch, New, Import) lets through. The
    /// answer completes at once, so the whole decision runs before the call returns.</summary>
    private static (bool Completed, bool Go, int Asked) Leave(SkinDesignerWindow d, SkinDesignerWindow.UnsavedChoice choice)
    {
        var answer = new Answer(choice);
        var task = d.ConfirmLeaveAsync(answer.Ask);
        return (task.IsCompleted, task.IsCompleted && task.Result, answer.Asked);
    }

    [Fact]
    public void Leaving_goes_on_only_when_clean_saved_or_discarded()
    {
        var folder = MakeSkin("quit", "{}");
        var r = WithDesigner(d =>
        {
            d.LoadSkin("quit");
            var clean = Leave(d, SkinDesignerWindow.UnsavedChoice.Cancel);
            d.Commit(SkinEdits.SetScale(d.Document, 2));
            var before = Snapshot(folder);
            var cancel = Leave(d, SkinDesignerWindow.UnsavedChoice.Cancel);
            var discard = Leave(d, SkinDesignerWindow.UnsavedChoice.Discard);
            var after = Snapshot(folder);
            bool diskUnchanged = after.Count == before.Count && before.All(kv => after[kv.Key].SequenceEqual(kv.Value));
            d.NewDocument();
            d.Commit(SkinEdits.SetScale(d.Document, 2)); // no art: Save cannot happen
            var failedSave = Leave(d, SkinDesignerWindow.UnsavedChoice.Save);
            return (clean, cancel, discard, diskUnchanged, failedSave, StillDirty: d.NeedsUnsavedPrompt);
        });
        _out.WriteLine("expected (completed, go, asked): clean (T,T,0), cancel (T,F,1), discard (T,T,1) with the disk unchanged, failed save (T,F,1) still dirty");
        _out.WriteLine($"actual  : {r}");
        Assert.Equal((true, true, 0), r.clean);
        Assert.Equal((true, false, 1), r.cancel);
        Assert.Equal((true, true, 1), r.discard);
        Assert.True(r.diskUnchanged);
        Assert.Equal((true, false, 1), r.failedSave);
        Assert.True(r.StillDirty);
    }

    /// <summary>App exit and Windows shutdown dispose the designer first: a close then must never
    /// put up the box, which a shutting-down app could never show or answer. A box that did come
    /// up runs a nested message loop, so the queued check below would find it and answer Cancel.</summary>
    [Fact]
    public void A_disposed_designer_closes_with_unsaved_edits_without_asking()
    {
        MakeSkin("exit", "{}");
        var r = _sta.Run(() =>
        {
            Application.Current.Resources["AppFontFamily"] = new System.Windows.Media.FontFamily("Segoe UI");
            var designer = new SkinDesignerWindow(() => Settings.Default, _root);
            int closed = 0, boxes = 0;
            designer.Closed += (_, _) => closed++;
            designer.Show();
            designer.LoadSkin("exit");
            designer.Commit(SkinEdits.SetScale(designer.Document, 2));
            bool dirty = designer.NeedsUnsavedPrompt;
            designer.Dispose();
            designer.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
            {
                foreach (var box in Application.Current.Windows.OfType<Wpf.Ui.Controls.MessageBox>().ToList())
                {
                    boxes++;
                    box.TemplateButtonCommand.Execute(Wpf.Ui.Controls.MessageBoxButton.Close); // its Cancel, as a click
                }
            });
            designer.Close();
            StaWpf.Pump();
            return (dirty, Boxes: boxes, Closed: closed);
        });
        _out.WriteLine($"expected dirty, no box, closed once; actual {r}");
        Assert.Equal((true, 0, 1), r);
    }

    /// <summary>While the document has issues the box does not offer a Save that cannot happen:
    /// the button is disabled and the box lists what blocks it.</summary>
    [Fact]
    public void The_box_offers_save_only_when_it_can_happen()
    {
        MakeSkin("box", "{}");
        var r = WithDesigner(d =>
        {
            d.NewDocument(); // no art: blocked
            var blockedBox = d.CreateUnsavedBox();
            var blocked = (blockedBox.IsPrimaryButtonEnabled, Content: (string)blockedBox.Content, d.SaveButton.ToolTip);
            d.LoadSkin("box");
            var okBox = d.CreateUnsavedBox();
            var ok = (okBox.IsPrimaryButtonEnabled, Content: (string)okBox.Content, okBox.IsSecondaryButtonEnabled);
            return (blocked, ok);
        });
        _out.WriteLine($"expected: blocked box Save off with the blocked list; loaded box Save on, just the question. actual {r}");
        Assert.False(r.blocked.IsPrimaryButtonEnabled);
        Assert.StartsWith(Loc.T("designer.unsaved.message", Loc.T("designer.untitled")), r.blocked.Content);
        Assert.EndsWith((string)r.blocked.ToolTip!, r.blocked.Content); // the same list Save's tooltip shows
        Assert.True(r.ok.IsPrimaryButtonEnabled);
        Assert.Equal(Loc.T("designer.unsaved.message", "box"), r.ok.Content);
        Assert.True(r.ok.IsSecondaryButtonEnabled);
    }

    /// <summary>"Don't save" for a skin that no longer loads leaves a new document, and the status
    /// line still says why the skin could not be opened.</summary>
    [Fact]
    public void Reverting_to_a_skin_that_no_longer_loads_keeps_the_reason()
    {
        var folder = MakeSkin("unloadable", "{}");
        var r = WithDesigner(d =>
        {
            d.LoadSkin("unloadable");
            d.Commit(SkinEdits.SetScale(d.Document, 2));
            File.Delete(Path.Combine(folder, "full.png"));
            d.RevertToSaved();
            return (d.Document, d.StatusText.Text, Reason: SkinLoader.Load(folder).Error);
        });
        _out.WriteLine($"expected a new document and the status '{r.Reason}'; actual status '{r.Text}', document {r.Document}");
        Assert.False(string.IsNullOrEmpty(r.Reason));
        Assert.Equal(r.Reason, r.Text);
        Assert.Equal(SkinDocument.New(Loc.T("designer.untitled")), r.Document);
    }

    /// <summary>A close answered with Save that fails on I/O (skin.json held open by another
    /// process) keeps the window open and dirty; answered "Don't save" it hides and comes back on
    /// what is on disk.</summary>
    [Fact]
    public void A_close_whose_save_fails_on_io_stays_open_and_dirty()
    {
        var folder = MakeSkin("locked", "{}");
        var r = WithDesigner(d =>
        {
            d.LoadSkin("locked");
            d.Commit(SkinEdits.SetScale(d.Document, 2));
            (bool Visible, bool Dirty, string Status) locked;
            using (new FileStream(Path.Combine(folder, "skin.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                d.CloseAfterAsking(new Answer(SkinDesignerWindow.UnsavedChoice.Save).Ask);
                locked = (d.IsVisible, d.NeedsUnsavedPrompt, d.StatusText.Text);
            }
            double disk = SkinLoader.Load(folder).Scale;
            d.CloseAfterAsking(new Answer(SkinDesignerWindow.UnsavedChoice.Discard).Ask);
            var discarded = (d.IsVisible, d.NeedsUnsavedPrompt, d.Document.Config.Scale);
            return (locked, disk, discarded);
        });
        _out.WriteLine($"expected: locked save stays visible and dirty with the I/O error, disk scale 1; discard hides, clean, scale 1. actual {r}");
        Assert.True(r.locked.Visible);
        Assert.True(r.locked.Dirty);
        Assert.Contains("'locked'", r.locked.Status); // "Failed to save skin 'locked': ..."
        Assert.Equal(1.0, r.disk);
        Assert.Equal((false, false, 1.0), r.discarded);
    }
}
