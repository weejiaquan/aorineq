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
try
{
    File.WriteAllBytes(args[0], IcoWriter.Write(frames));
}
finally
{
    foreach (var frame in frames) frame.Dispose();
}

Console.WriteLine($"wrote {Path.GetFullPath(args[0])}: "
    + $"{string.Join(", ", AppIconArt.FrameSizes)} px");
return 0;
