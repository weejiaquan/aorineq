using AorinEQ.Core;

// Regenerates the shipped app icon. From the repo root:
//
//     dotnet run --project tools/AppIconGen -- src/AorinEQ/AorinEQ.ico
//
// Deliberately NOT in AorinEQ.slnx: this is build-time art production, not shipped code, and
// keeping it out of the solution means `dotnet build AorinEQ.slnx` and `dotnet test` never see it.
// Everything worth testing is in AorinEQ.Core (AppIconArt, IcoWriter) — this file is only the
// command that runs them, which is why it is allowed to have no tests of its own.

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: AppIconGen <output.ico>");
    return 1;
}

var frames = AppIconArt.FrameSizes.Select(AppIconArt.Draw).ToArray();
byte[] ico;
try
{
    ico = IcoWriter.Write(frames);
}
finally
{
    foreach (var frame in frames) frame.Dispose();
}

// Written to a temp file beside the target and moved into place, never straight over it. The
// destination is a COMMITTED build input — the exe's Win32 icon and a WPF resource — so a write
// that fails halfway (full disk, a scanner holding the file) would leave every later build and
// publish stamping a truncated icon, with nothing failing to say so.
string target = Path.GetFullPath(args[0]);
string temp = target + ".tmp";
File.WriteAllBytes(temp, ico);
File.Move(temp, target, overwrite: true);

Console.WriteLine($"wrote {Path.GetFullPath(args[0])}: "
    + $"{string.Join(", ", AppIconArt.FrameSizes)} px");
return 0;
