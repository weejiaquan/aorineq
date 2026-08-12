namespace AorinEQ.Core;

/// <summary>The kernel-object names that identify a running AorinEQ to anything outside the
/// process. They live in Core, not in <c>App</c>, because they are a CONTRACT with code that is
/// not the app: the Inno Setup script's <c>AppMutex</c> is how Setup and Uninstall detect that
/// AorinEQ is running before they replace or delete its exe, and it can only match by spelling the
/// name out again. <c>InstallerScriptTests</c> binds the two together, so renaming the mutex here
/// fails the build rather than silently letting an installer overwrite a running app.
///
/// Both names are unprefixed, so Windows resolves them in the current session's namespace
/// (<c>Local\</c>). That is deliberate: elevated and non-elevated instances in the same logon
/// session must still see each other (RunAsAdmin bounces the app through UAC and back), while two
/// different users signed in at once each get their own app.</summary>
public static class AppIdentity
{
    /// <summary>Held for the lifetime of the running instance; owning it means "I am AorinEQ".</summary>
    public const string SingleInstanceMutexName = "AorinEQ_SingleInstance";

    /// <summary>Signalled by a second launch to make the running instance show its OSD.</summary>
    public const string ShowOsdEventName = "AorinEQ_ShowOsd";
}
