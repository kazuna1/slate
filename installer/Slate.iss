; Slate installer (Inno Setup 6). Built by tools\publish.ps1, which passes AppVersion and SourceExe.
; Per-user install: no admin prompt, installs to %LOCALAPPDATA%\Programs\Slate.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceExe
  #define SourceExe "..\publish\win-x64\Slate.exe"
#endif

[Setup]
AppId={{7B3F0C2E-5A8D-4E61-9C1B-2F4D8A6E9B10}
AppName=Slate
AppVersion={#AppVersion}
AppVerName=Slate {#AppVersion}
AppPublisher=Tuguldur Munkhbat
DefaultDirName={localappdata}\Programs\Slate
DisableDirPage=auto
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\publish
OutputBaseFilename=SlateSetup
SetupIconFile=..\src\Slate\Assets\slate.ico
UninstallDisplayIcon={app}\Slate.exe
UninstallDisplayName=Slate
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes

[Tasks]
Name: "autostart"; Description: "Start Slate when Windows starts (recommended)"; GroupDescription: "Options:"
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Options:"; Flags: unchecked

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Slate"; Filename: "{app}\Slate.exe"
Name: "{autodesktop}\Slate"; Filename: "{app}\Slate.exe"; Tasks: desktopicon

[Registry]
; Same value the app's own "Run at startup" toggle writes, so the two stay in sync.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Slate"; \
    ValueData: """{app}\Slate.exe"""; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\Slate.exe"; Description: "Launch Slate now"; Flags: nowait postinstall skipifsilent
; In-app updates run silently with /RELAUNCH=1, so Slate comes back by itself.
Filename: "{app}\Slate.exe"; Flags: nowait; Check: ShouldRelaunch

[Code]
// Slate runs in the tray, so close it before replacing or removing the exe.
procedure CloseSlate();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Slate.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function ShouldRelaunch(): Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  CloseSlate();
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  CloseSlate();
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // The app can enable autostart itself (tray menu), so remove it even if the task wasn't picked.
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Slate');

    DataDir := ExpandConstant('{userappdata}\Slate');
    if DirExists(DataDir) and not UninstallSilent then
      if MsgBox('Also delete your Slate settings and command history?', mbConfirmation, MB_YESNO) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
