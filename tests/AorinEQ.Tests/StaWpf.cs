using System.Windows;
using AorinEQ.Core;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;
using Application = System.Windows.Application;

namespace AorinEQ.Tests;

/// <summary>An STA thread with a live <see cref="Application"/>, so a test can build real WPF
/// controls and measure them.
///
/// WHY IT EXISTS. Every other test in this suite reads Core or reads the repository, and that was
/// enough until something had to be proved about LAYOUT - specifically that opening a card's help
/// does not move the page. No amount of reading <c>HelpDecorator.cs</c> can show that; only
/// measuring a real card twice can.
///
/// The STA thread is <see cref="HookThread"/> rather than a new one. It is this repository's one
/// STA-thread-with-a-message-loop primitive, it already marshals work on and rethrows on the
/// caller's thread, and it is already covered by HookThreadTests - so borrowing it here is a
/// smaller thing than a second copy of the same plumbing. It is named for the hooks because that
/// is what it carries in the app; nothing about it is hook-specific.
///
/// The fixture builds its OWN Application and merges the same two WPF-UI dictionaries App.xaml
/// does. Instantiating the real <c>AorinEQ.App</c> instead would start the tray icon, the hooks
/// and the audio session - an actual running copy of the app inside the test run.</summary>
public sealed class StaWpf : IDisposable
{
    private readonly HookThread _thread = new();

    public StaWpf() =>
        _thread.Invoke(() =>
        {
            // Application.Current is process-wide and can only be constructed once. Collections in
            // this suite run one at a time (see xunit.runner.json), but a second fixture instance
            // for a second test class would still hit the same process.
            if (Application.Current is not null) return;

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

            // The same pair App.xaml merges. ThemesDictionary carries the colour brushes
            // HelpDecorator looks up by name; ControlsDictionary carries the implicit styles that
            // give a CardControl its template - without which a card has no visual tree to walk
            // and nothing to measure.
            app.Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ApplicationTheme.Dark });
            app.Resources.MergedDictionaries.Add(new ControlsDictionary());
        });

    /// <summary>Runs <paramref name="work"/> on the STA thread and returns what it returned,
    /// rethrowing on the calling thread whatever it threw.</summary>
    public T Run<T>(Func<T> work) => _thread.Invoke(work);

    public void Run(Action work) => _thread.Invoke(work);

    /// <summary>Runs the dispatcher queue to empty.
    ///
    /// The whole of a test body runs inside a single <see cref="Run"/>, which occupies the message
    /// loop for its duration - so without this nothing queued ever runs: Loaded never fires, the
    /// controls stay unloaded, and WPF refuses to open a Popup whose placement target is not
    /// loaded. IsOpen then reads back false however it is set, including when assigned directly,
    /// which looks exactly like a broken feature and is not.</summary>
    public static void Pump()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ContextIdle,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    public void Dispose() => _thread.Dispose();
}
