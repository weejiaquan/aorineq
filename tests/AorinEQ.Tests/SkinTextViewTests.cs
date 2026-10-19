using System.Windows;
using System.Windows.Media;
using AorinEQ.Core;
using AorinEQ.UI;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>The layout-sized skin text the notice name is drawn in, on its own.</summary>
[Collection("Wpf")]
public class SkinTextViewTests
{
    private readonly StaWpf _sta;
    private readonly ITestOutputHelper _out;

    public SkinTextViewTests(StaWpf sta, ITestOutputHelper output)
    {
        _sta = sta;
        _out = output;
    }

    /// <summary>Its glyphs are built for the PixelsPerDip it was measured at, so moving to a
    /// monitor with another DPI must lay it out again - and the new layout uses the new DPI.</summary>
    [Fact]
    public void A_dpi_change_lays_the_text_out_again_at_the_new_dpi()
    {
        var r = _sta.Run(() =>
        {
            var text = new SkinTextView { Text = "Speakers" };
            text.Apply(SkinLoader.DefaultNotice.Text, 1.0);
            // In a real window: WPF tells an element its DPI changed only when it is hosted.
            var window = new Window { Content = new System.Windows.Controls.Border { Child = text }, Width = 300, Height = 100, ShowActivated = false };
            window.Show();
            try
            {
                window.UpdateLayout();
                bool validBefore = text.IsMeasureValid;
                var before = VisualTreeHelper.GetDpi(text).PixelsPerDip;

                var next = new DpiScale(before * 1.75, before * 1.75);
                VisualTreeHelper.SetRootDpi(window, next);
                bool validAfterChange = text.IsMeasureValid;
                window.UpdateLayout();
                return (validBefore, before, validAfterChange, After: VisualTreeHelper.GetDpi(text).PixelsPerDip, validAfterRemeasure: text.IsMeasureValid);
            }
            finally { window.Close(); }
        });
        _out.WriteLine($"measure valid before {r.validBefore} at {r.before} ppd; right after the change to {r.After} ppd: valid {r.validAfterChange}; after a new measure: valid {r.validAfterRemeasure}");
        Assert.True(r.validBefore);
        Assert.Equal(r.before * 1.75, r.After, 6);
        Assert.False(r.validAfterChange, "the text kept the layout it built for the old DPI");
        Assert.True(r.validAfterRemeasure);
    }
}
