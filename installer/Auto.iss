; Inno Setup script for Auto's installer. Compile with ISCC.exe (Inno Setup 6+):
;   iscc installer\Auto.iss
; Expects a self-contained `dotnet publish` output at publish\win-x64 -- see
; installer/README.md for the exact publish command and full build steps.
;
; Per-user install (PrivilegesRequired=lowest), no UAC prompt, no admin needed --
; matches the app's own per-user %AppData%\Auto / %LocalAppData%\Auto usage.
; WizardStyle=modern + the generated brand assets (installer/assets/) give this
; the same dark-ink/periwinkle-glow identity as the app's own reply panel and
; the product homepage, rather than the stock Inno Setup look.

#define AppName "Auto"
#define AppVersion "1.0.0"
#define AppPublisher "Auto"
#define AppExeName "Auto.exe"
#define PublishDir "..\publish\win-x64"

[Setup]
AppId={{B4BB7BAC-699C-4EFA-9768-0A9255D4456D}}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\installer-output
OutputBaseFilename=AutoSetup
Compression=lzma2
SolidCompression=yes
SetupLogging=yes

; ── Branding ──────────────────────────────────────────────────────────────
WizardStyle=modern
WizardImageFile=assets\wizard-banner.bmp
WizardSmallImageFile=assets\wizard-small.bmp
SetupIconFile=assets\setup.ico
UninstallDisplayIcon={app}\{#AppExeName}
WizardImageStretch=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName} now"; Flags: nowait postinstall skipifsilent

; Deliberately no [UninstallDelete] for %AppData%\Auto / %LocalAppData%\Auto --
; that's the user's settings.json (API key) and cached Whisper model, not
; installed program files. Uninstalling shouldn't silently throw those away;
; a reinstall should just pick the existing settings back up.
