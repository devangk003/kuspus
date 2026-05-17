; KusPus installer — Inno Setup 6 script.
; Phase 12 — see TECH_SPEC §30 + docs/APP_DESIGN.md.
;
; Architectural decisions (per Phase 12 best-practices research 2026-05-17):
;   - PrivilegesRequired=lowest         per-user install, no UAC prompt
;   - {autopf}\KusPus                   resolves to %LOCALAPPDATA%\Programs\KusPus
;                                         under PrivilegesRequired=lowest
;   - ArchitecturesAllowed=x64compatible Inno 6.3+ token; covers ARM64 x64 emulation
;   - MinVersion=10.0.17763             Windows 10 1809 — DWM Mica/rounded-corners
;                                         APIs become usable here (gracefully fall
;                                         back on Win10 1809-21H2; Mica on Win11)
;   - Compression=lzma2/max + solid     research showed solid is the bigger lever
;   - No HKLM writes                    autostart is opt-in via the app's own
;                                         Preferences UI (HKCU\...\Run\KusPus)
;   - Uninstaller leaves user data      %APPDATA%\KusPus and %LOCALAPPDATA%\KusPus
;                                         are NEVER touched on uninstall in v1
;
; Unsigned in perpetuity (PRD §9.9 / N-11). SmartScreen + Defender + SAC friction
; is documented for testers in docs/INSTALL.md. MotW preservation note: testers
; should be told to right-click the downloaded setup.exe → Properties → Unblock
; → Apply BEFORE running, per the Phase 12 SAC/SmartScreen research.
;
; Build pipeline:
;   1. tools\build-whisper-windows.ps1  → installer\payload\whisper\
;   2. dotnet publish src\KusPus.App ...  → publish\win-x64\
;   3. iscc.exe installer\KusPus.iss /DAppVersion=v1.0.0  → installer\Output\KusPus-Setup-v1.0.0.exe
;
; AppId is FIXED — never regenerate. Upgrade detection across versions depends
; on this GUID staying stable for the lifetime of the product.

#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif

#define AppName        "KusPus"
#define AppPublisher   "Devang Kumawat"
#define AppPublisherURL "https://github.com/devangk003/kuspus"
#define AppExeName     "KusPus.exe"

[Setup]
AppId={{7E263B33-A253-4E7D-B1A1-1B9D29405A02}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppPublisherURL}
AppSupportURL={#AppPublisherURL}/issues
AppUpdatesURL={#AppPublisherURL}/releases
VersionInfoCompany={#AppPublisher}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} installer

; Per-user, no UAC. {autopf} resolves to {userpf} = %LOCALAPPDATA%\Programs.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
DisableReadyPage=no
UsePreviousAppDir=yes
UsePreviousPrivileges=yes

; Inno 6.3+ token. Covers x64 + ARM64-x64-emulation; future-proof vs deprecated "x64".
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Win10 1809 (build 17763) floor — earliest version where DWM rounded-corner +
; immersive-dark-mode attributes the pill and MainWindow set are honoured.
MinVersion=10.0.17763

WizardStyle=modern
OutputDir=Output
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
SetupIconFile=..\icons\icon.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}

; Solid LZMA2 — research showed solid mode is the bigger lever than compression
; level for our payload mix (.NET runtime DLLs + whisper.exe + DLLs share
; redundancy across files). Separate-process speeds up CI on multi-core runners.
Compression=lzma2/max
SolidCompression=yes
LZMAUseSeparateProcess=yes

; CloseApplications + RestartApplicationsIfNeeded=no — kill any running KusPus
; before overwriting files; do not auto-restart (we have no in-process update
; flow yet, and an installer-spawned launch wouldn't pick up the new files
; cleanly in self-contained-single-file mode anyway).
CloseApplications=force
RestartApplicationsIfNeeded=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; Desktop shortcut — off by default. Pill + tray are the canonical surfaces;
; a Desktop icon is noise for the typical KusPus user, but power users on
; locked-down corp machines may want it.
Name: "desktopicon"; Description: "Create a &desktop shortcut"; \
    GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
; Published self-contained single-file output (see src\KusPus.App\KusPus.App.csproj
; PublishSingleFile + SelfContained properties). Recurses to cover any side-by-side
; native libraries .NET 10 extracted at build time despite IncludeNativeLibraries
; ForSelfExtract=true (some WPF rasterizers can't be packed inside the single
; file). flags: ignoreversion = always overwrite, no version compare; the
; published EXE doesn't carry monotonically-increasing FileVersion across builds.
Source: "..\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; Whisper native subprocess + DLLs + SHA256SUMS. Lives in {app}\whisper\
; (matches AppPaths.WhisperDir convention). Excludes the .tag marker — it's a
; build-time idempotency hint for tools\build-whisper-windows.ps1, not a
; runtime concern.
Source: "payload\whisper\whisper.exe"; DestDir: "{app}\whisper"; Flags: ignoreversion
Source: "payload\whisper\*.dll"; DestDir: "{app}\whisper"; Flags: ignoreversion
Source: "payload\whisper\SHA256SUMS"; DestDir: "{app}\whisper"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; Optional post-install launch. NoIcon avoids registering KusPus as the verb's
; handler — the EXE is launched once on Finish, that's it.
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; \
    Flags: nowait postinstall skipifsilent

[UninstallRun]
; Best-effort: kill the running app before uninstalling so file delete doesn't
; trip on locked DLLs. Errors ignored — if KusPus isn't running, taskkill
; returns non-zero but it's not interesting.
Filename: "{cmd}"; Parameters: "/C taskkill /F /IM {#AppExeName}"; Flags: runhidden; RunOnceId: "KillKusPus"

; INTENTIONALLY no [UninstallDelete] entries on user data paths:
;   %APPDATA%\KusPus\settings.json
;   %LOCALAPPDATA%\KusPus\history.db
;   %LOCALAPPDATA%\KusPus\logs\
;   %LOCALAPPDATA%\KusPus\models\
;   %LOCALAPPDATA%\KusPus\failed\
; All survive uninstall. Friends-only audience often reinstalls (e.g. testing
; a new build); preserving settings + history + downloaded models avoids the
; "lost my dictation history" surprise. A future v1.1+ may add an opt-in
; uninstall task "Also remove my data" — deliberately not in v1.0 to prevent
; accidental-tick data loss.
