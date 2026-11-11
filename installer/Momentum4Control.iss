; Inno-Setup-Skript für MOMENTUM Control (inoffiziell).
; Erzeugt ein setup.exe, das die self-contained AOT-App per-User installiert (kein Admin/UAC).
; Aufruf über tools/make-release.ps1; die Variablen kommen per /D von ISCC:
;   ISCC /DAppVersion=1.1.0 /DSourceDir="...\publish\app" /DRepoDir="X:\src\sennheiser" /DOutputDir="...\dist" installer\Momentum4Control.iss
; Fallback-Werte, damit sich das Skript auch direkt in der Inno-IDE öffnen lässt:
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\..\AppData\Local\Momentum4Control.build\publish\app"
#endif
#ifndef RepoDir
  #define RepoDir ".."
#endif
#ifndef OutputDir
  #define OutputDir "dist"
#endif

#define AppName "MOMENTUM Control"
; Name bis 1.1.x – dessen Verknüpfungen räumt [InstallDelete] bei Upgrades weg
#define OldAppName "MOMENTUM 4 Control"
#define AppExe "Momentum4Control.exe"
#define AppPublisher "Ariazonaa"
#define AppUrl "https://github.com/Ariazonaa/momentum-control"

[Setup]
; Stabile AppId (nie ändern – daran erkennt Windows Upgrades und Deinstallation)
AppId={{A1F2C3D4-5E6F-47A8-9B0C-1D2E3F405162}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
VersionInfoVersion={#AppVersion}
; Per-User-Installation ohne Adminrechte → {autopf} = %LOCALAPPDATA%\Programs
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
; Ordnername bleibt wie bis 1.1.x, damit Upgrades und der Release-Smoke-Test denselben Pfad nutzen
DefaultDirName={autopf}\Momentum 4 Control
DefaultGroupName=MOMENTUM Control
; sonst behält ein Upgrade die Startmenü-Gruppe mit dem alten Namen
UsePreviousGroup=no
DisableProgramGroupPage=yes
DisableDirPage=auto
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
SetupIconFile={#RepoDir}\src\Momentum4.App\Assets\App.ico
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=Momentum4Control-Setup-{#AppVersion}
; Laufende Tray-App vor dem Überschreiben schließen (bei Upgrades)
CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll

[Languages]
Name: "de"; MessagesFile: "compiler:Languages\German.isl"

[Tasks]
Name: "desktopicon"; Description: "Desktop-Verknüpfung anlegen"; GroupDescription: "Verknüpfungen:"; Flags: unchecked
Name: "autostart"; Description: "Mit Windows starten (minimiert ins Infobereich-Symbol)"; GroupDescription: "Autostart:"; Flags: unchecked

[Files]
; Der komplette self-contained Ordner – ohne Portable-Marker/Daten (die gehören nur in die portable Variante)
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "Momentum4Control.portable,Data\*"; Flags: recursesubdirs createallsubdirs ignoreversion

[InstallDelete]
; Verknüpfungen unter dem alten Namen, die ein Upgrade sonst neben den neuen liegen ließe
Type: files; Name: "{autoprograms}\{#OldAppName}\{#OldAppName}.lnk"
Type: files; Name: "{autoprograms}\{#OldAppName}\{#OldAppName} deinstallieren.lnk"
Type: files; Name: "{autodesktop}\{#OldAppName}.lnk"
Type: dirifempty; Name: "{autoprograms}\{#OldAppName}"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{#AppName} deinstallieren"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Autostart-Eintrag (dieselbe Stelle/Wert, den auch die App-Einstellungen verwalten)
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Momentum4Control"; ValueData: """{app}\{#AppExe}"" --minimized"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#AppExe}"; Description: "{#AppName} jetzt starten"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Falls die App noch läuft, vor dem Entfernen beenden
Filename: "{cmd}"; Parameters: "/C taskkill /IM {#AppExe} /F"; Flags: runhidden; RunOnceId: "KillApp"

[Code]
// Die App sitzt ohne sichtbares Fenster im Infobereich; der Restart Manager (CloseApplications) bekommt sie so nicht
// zuverlässig beendet und Setup scheitert dann an gesperrten Dateien. PrepareToInstall läuft vor der Prüfung auf
// verwendete Dateien, deshalb werden App und Kommandozeile hier direkt beendet.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM m4ctl.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(500);
  Result := '';
end;
