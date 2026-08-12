; AorinEQ Windows installer (Inno Setup 6).
;
; Built by publish.ps1 straight after the portable exe, from that exact exe. To compile it by hand:
;   "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" installer\AorinEQ.iss
;
; WHY THIS EXISTS: the release used to be a bare 74 MB exe. On Windows people expect a Setup.exe,
; a Start Menu entry and an Apps & Features entry; a lone exe reads as suspicious and is the
; biggest non-technical drop-off after SmartScreen. The portable exe still ships under its exact
; name (AorinEQ.exe + AorinEQ.exe.sha256) because the in-app updater and the website's download
; links are a contract on those names.
;
; FOUR DECISIONS THAT ARE LOAD-BEARING - do not "improve" them:
;
;  1. PER-USER INSTALL to {localappdata}\Programs\AorinEQ, PrivilegesRequired=lowest. No UAC prompt
;     at install time (the app's whole design is that admin is opt-in), and - the part that would
;     break silently - the v1.9.0 in-app updater swaps the running exe IN PLACE. It probes the
;     install directory with UpdateApplier.CanWriteTo and falls back to "open the release page
;     yourself" when the directory is not writable. Installing into Program Files would demote
;     every user to that fallback, and nothing would say why.
;
;  2. NO AUTOSTART ENTRY IS EVER WRITTEN. The app owns that decision: Settings' "Start with
;     Windows" picks between an HKCU Run value and a scheduled task depending on RunAsAdmin, and
;     reconciles the two. An installer-written Run key would be a third writer fighting them.
;     Uninstall does REMOVE such a value, but only when it points into the directory being
;     deleted - see RemoveStalePointersInto below.
;
;  3. NOTHING IS DONE TO EQUALIZER APO, AND THE aorineq:// SCHEME IS NEVER REGISTERED HERE. The app
;     creates aorineq.txt, adds the Include line to config.txt, and registers/re-points the URL
;     scheme at runtime for whichever exe is running. Uninstall removes the scheme only when it
;     still points into the directory being deleted, so a second, portable copy that has since
;     taken the registration keeps it.
;
;  4. AppMutex is the app's REAL single-instance mutex, kept in sync with AorinEQ.Core.AppIdentity
;     by InstallerScriptTests. It is how Setup and Uninstall notice AorinEQ is running before they
;     overwrite or delete an exe that is execute-locked.
;
; The version is never written here. It is read out of the exe being packaged, which the .NET SDK
; stamps from the csproj's <Version> - so the csproj stays the single source of truth and this
; script cannot drift from the binary it ships.

#define AppName "AorinEQ"
#define AppExeName "AorinEQ.exe"
#define AppExePath AddBackslash(SourcePath) + "..\publish\" + AppExeName
#define AppUrl "https://github.com/weejiaquan/aorineq"

#ifnexist AppExePath
  #error publish\AorinEQ.exe not found - run publish.ps1, which compiles this script itself
#endif

#define VerMajor
#define VerMinor
#define VerRevision
#define VerBuild
#expr GetVersionComponents(AppExePath, VerMajor, VerMinor, VerRevision, VerBuild)
#define AppVersion Str(VerMajor) + "." + Str(VerMinor) + "." + Str(VerRevision)

[Setup]
; A fixed GUID, generated once: it is this install's identity, so future versions UPGRADE it (same
; directory, same Apps & Features entry) instead of stacking beside it. Never change it.
AppId={{CB5FD219-9B9D-4DE1-9FEC-E94B0963E6A1}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoCompany={#AppName}
VersionInfoDescription={#AppName} Setup

; Per-user, no elevation. See decision 1 above.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
LicenseFile=..\LICENSE
SetupIconFile=..\src\AorinEQ\AorinEQ.ico
WizardStyle=modern

; The payload is a self-contained single-file bundle whose managed assemblies are already
; compressed, so lzma2/max buys little - but "little" is still free at every user's download.
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
OutputDir=..\publish
OutputBaseFilename=AorinEQ-Setup

; Setup and Uninstall refuse to touch the files while an instance holds this mutex, and - because
; the running exe is execute-locked - Restart Manager is asked to close it first. See decision 4.
AppMutex=AorinEQ_SingleInstance
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; Offered, not assumed: the app lives in the tray, so a desktop icon is of limited use.
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; ignoreversion, not the default version comparison. The in-app updater may already have put a
; NEWER exe in this directory, and Inno's default would then SKIP the copy - leaving a 3.4.0 exe
; behind a 3.3.0 Apps & Features entry, i.e. an installed version that lies about itself. The
; alternative it is traded against is a real one: running an older Setup does downgrade a
; newer installed exe. That case self-heals, because the app's own update check re-lands the newer
; build, and it leaves the entry and the exe agreeing in the meantime.
Source: "{#AppExePath}"; DestDir: "{app}"; Flags: ignoreversion

[InstallDelete]
; UpdateApplier renames the running exe aside as AorinEQ.exe.old before moving the downloaded one
; in, and cleans it up at the next start. Installing over a directory the updater has touched
; should not leave a stray 74 MB file behind.
Type: files; Name: "{app}\{#AppExeName}.old"

[UninstallDelete]
Type: files; Name: "{app}\{#AppExeName}.old"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Messages]
; The finish page is the one place a first-time user is guaranteed to read, so it is where the
; deliberate absence of an autostart entry gets explained (decision 2).
FinishedLabel=Setup has finished installing [name] on your computer. The application may be launched by selecting the installed shortcuts.%n%n[name] does not add itself to Windows startup. To start it with Windows, open its tray menu, choose Settings, and turn on "Start with Windows" - [name] manages that itself so it keeps working whether or not you run it as administrator.
FinishedLabelNoIcons=Setup has finished installing [name] on your computer.%n%n[name] does not add itself to Windows startup. To start it with Windows, open its tray menu, choose Settings, and turn on "Start with Windows" - [name] manages that itself so it keeps working whether or not you run it as administrator.

[Code]
{ The app points two per-user registry entries at whichever exe is running: the "Start with
  Windows" Run value, and the aorineq:// handler. Neither is created here - but once this exe is
  deleted, one that still names it is a broken autorun and a link scheme that opens nothing.

  Both are removed ONLY when they point INSIDE the directory being uninstalled, matched with a
  trailing backslash so a sibling like ...\Programs\AorinEQ2 cannot match. That guard is the whole
  point: the user may be running a portable copy that has since taken over both entries, and it
  must keep them.

  DELIBERATELY NOT HANDLED: a "Start with Windows" SCHEDULED TASK, which the app uses instead of
  the Run value when Run-as-administrator is on. Its run level is HIGHEST, and by Windows' rules
  creating or deleting such a task requires elevation - which this uninstaller never asks for, by
  design. A task left naming a deleted exe is inert (Task Scheduler logs a failed start and stops
  there); turning "Start with Windows" off before uninstalling removes it properly. }
procedure RemoveStalePointersInto(AppDir: String);
var
  RunKey, SchemeKey, Value: String;
begin
  AppDir := Lowercase(AddBackslash(AppDir));
  RunKey := 'Software\Microsoft\Windows\CurrentVersion\Run';
  SchemeKey := 'Software\Classes\aorineq';

  if RegQueryStringValue(HKEY_CURRENT_USER, RunKey, '{#AppName}', Value) then
    if Pos(AppDir, Lowercase(Value)) > 0 then
      RegDeleteValue(HKEY_CURRENT_USER, RunKey, '{#AppName}');

  if RegQueryStringValue(HKEY_CURRENT_USER, SchemeKey + '\shell\open\command', '', Value) then
    if Pos(AppDir, Lowercase(Value)) > 0 then
      RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER, SchemeKey);
end;

{ %APPDATA%\AorinEQ holds settings.json, EQ presets and - the reason this asks rather than just
  deleting - the user's SKINS, which are artwork they may have spent hours on and which no
  reinstall can bring back. Keeping is the default, including for a silent uninstall:
  SuppressibleMsgBox returns the default answer when message boxes are suppressed. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RemoveStalePointersInto(ExpandConstant('{app}'));

    DataDir := ExpandConstant('{userappdata}\{#AppName}');
    if DirExists(DataDir) then
      if SuppressibleMsgBox(
           'Keep your {#AppName} skins, EQ presets and settings?' + #13#10 + #13#10 +
           DataDir + #13#10 + #13#10 +
           'Choose Yes to keep them, so reinstalling {#AppName} finds everything where you left it.' + #13#10 +
           'Choose No to delete them permanently.',
           mbConfirmation, MB_YESNO, IDYES) = IDNO then
        DelTree(DataDir, True, True, True);
  end;
end;
