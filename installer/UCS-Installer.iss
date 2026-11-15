; =============================================================================
;  DCS-UCS (Universal Control Sync) - Inno Setup script
;  Build: run build.bat first (creates ..\publish), then compile this file with
;  Inno Setup 6 (ISCC.exe installer\UCS-Installer.iss). Output: installer\Output\
; =============================================================================

#define AppName "DCS-UCS"
#define AppFullName "DCS-UCS (Universal Control Sync)"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppPublisher "DCS-UCS contributors"
#define AppURL "https://github.com/LR-Almeida/DCS-UCS"

[Setup]
AppId={{411ACDA7-5051-4E79-B6BD-F3CB0AB09F27}
AppName={#AppFullName}
AppVersion={#AppVersion}
AppVerName={#AppFullName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
AppUpdatesURL={#AppURL}/releases
DefaultDirName={autopf}\DCS-UCS
DefaultGroupName=DCS-UCS
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir=Output
OutputBaseFilename=DCS-UCS-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UninstallDisplayIcon={app}\UCS-Client.exe
UninstallDisplayName={#AppFullName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "dcsclient"; Description: "Configure DCS World client (Saved Games\DCS and DCS.openbeta): UCS-Hook.lua, UCS-Export.lua and Export.lua injection"; GroupDescription: "DCS integration:"
Name: "dcsserverhook"; Description: "Install UCS-ServerHook.lua (only if THIS machine hosts a DCS server - SRS-style auto-connect)"; GroupDescription: "DCS integration:"; Flags: unchecked
Name: "firewall"; Description: "Open UDP port 5055 in Windows Firewall (only if this machine runs UCS-Server.exe)"; GroupDescription: "Network:"; Flags: unchecked
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\publish\UCS-Client.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\publish\UCS-Server.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\publish\UCS-ProfileGen.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\profiles\*.json"; DestDir: "{app}\profiles"; Flags: ignoreversion
Source: "..\src\Lua\UCS-Hook.lua"; DestDir: "{app}\Lua"; Flags: ignoreversion
Source: "..\src\Lua\UCS-Export.lua"; DestDir: "{app}\Lua"; Flags: ignoreversion
Source: "..\src\Lua\UCS-ServerHook.lua"; DestDir: "{app}\Server-Files"; Flags: ignoreversion
Source: "..\Server-Files\UCS-AutoConnect.cfg"; DestDir: "{app}\Server-Files"; Flags: onlyifdoesntexist
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{group}\UCS Client"; Filename: "{app}\UCS-Client.exe"
Name: "{group}\UCS Server"; Filename: "{app}\UCS-Server.exe"
Name: "{group}\UCS Profile Generator (help)"; Filename: "{cmd}"; Parameters: "/k ""{app}\UCS-ProfileGen.exe"" --help"; WorkingDir: "{app}"
Name: "{group}\Server Files folder"; Filename: "{app}\Server-Files"
Name: "{group}\README"; Filename: "{app}\README.md"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\UCS Client"; Filename: "{app}\UCS-Client.exe"; Tasks: desktopicon

[Run]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""DCS-UCS Server"" dir=in action=allow protocol=UDP localport=5055"; Flags: runhidden; Tasks: firewall
Filename: "{app}\UCS-Client.exe"; Description: "{cm:LaunchProgram,UCS Client}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""DCS-UCS Server"""; Flags: runhidden; RunOnceId: "DelFirewall"

[Code]
const
  UCS_EXPORT_LINE = 'pcall(function() local ucsLfs=require(''lfs''); dofile(ucsLfs.writedir()..[[Scripts\UCS\UCS-Export.lua]]); end) -- DCS-UCS';
  SAVED_GAMES_GUID = '{4C5C32FF-BB9D-43b0-B5B4-2D72E54EAAA4}';

var
  ServerAddrPage: TInputQueryWizardPage;

function SavedGamesDir(): String;
var
  P: String;
begin
  if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders', SAVED_GAMES_GUID, P) then
  begin
    StringChangeEx(P, '%USERPROFILE%', GetEnv('USERPROFILE'), True);
    Result := P;
  end
  else
    Result := GetEnv('USERPROFILE') + '\Saved Games';
end;

{ ---------------------------------------------------------------- Export.lua }

procedure InjectExport(const FileName: String);
var
  Lines: TArrayOfString;
  I: Integer;
  Found: Boolean;
begin
  Found := False;
  if FileExists(FileName) then
  begin
    if LoadStringsFromFile(FileName, Lines) then
      for I := 0 to GetArrayLength(Lines) - 1 do
        if Pos('UCS-Export.lua', Lines[I]) > 0 then
          Found := True;
    if not Found then
      FileCopy(FileName, FileName + '.ucs-backup', False);
  end;
  if not Found then
  begin
    if SaveStringToFile(FileName, #13#10 + UCS_EXPORT_LINE + #13#10, True) then
      Log('Export.lua injected: ' + FileName)
    else
      Log('Could not write ' + FileName);
  end;
end;

procedure RemoveExportLine(const FileName: String);
var
  Lines, Kept: TArrayOfString;
  I, N: Integer;
begin
  if not FileExists(FileName) then Exit;
  if not LoadStringsFromFile(FileName, Lines) then Exit;
  N := 0;
  SetArrayLength(Kept, GetArrayLength(Lines));
  for I := 0 to GetArrayLength(Lines) - 1 do
    if Pos('UCS-Export.lua', Lines[I]) = 0 then
    begin
      Kept[N] := Lines[I];
      N := N + 1;
    end;
  SetArrayLength(Kept, N);
  SaveStringsToFile(FileName, Kept, False);
end;

{ ---------------------------------------------------------------- client }

procedure InstallClientInto(const Base: String);
begin
  ForceDirectories(Base + '\Scripts\Hooks');
  ForceDirectories(Base + '\Scripts\UCS');
  FileCopy(ExpandConstant('{app}\Lua\UCS-Hook.lua'), Base + '\Scripts\Hooks\UCS-Hook.lua', False);
  FileCopy(ExpandConstant('{app}\Lua\UCS-Export.lua'), Base + '\Scripts\UCS\UCS-Export.lua', False);
  InjectExport(Base + '\Scripts\Export.lua');
end;

procedure InstallClient();
var
  SG: String;
  Done: Boolean;
begin
  SG := SavedGamesDir();
  Done := False;
  if DirExists(SG + '\DCS') then begin InstallClientInto(SG + '\DCS'); Done := True; end;
  if DirExists(SG + '\DCS.openbeta') then begin InstallClientInto(SG + '\DCS.openbeta'); Done := True; end;
  if not Done then
    InstallClientInto(SG + '\DCS');
end;

{ ---------------------------------------------------------------- server hook }

function ServerProfileNames(): TArrayOfString;
begin
  SetArrayLength(Result, 5);
  Result[0] := 'DCS.server';
  Result[1] := 'DCS_server';
  Result[2] := 'DCS.openbeta_server';
  Result[3] := 'DCS.openbeta.server';
  Result[4] := 'DCS.release_server';
end;

procedure InstallServerHookInto(const Base: String);
var
  Cfg, Addr: String;
begin
  ForceDirectories(Base + '\Scripts\Hooks');
  FileCopy(ExpandConstant('{app}\Server-Files\UCS-ServerHook.lua'), Base + '\Scripts\Hooks\UCS-ServerHook.lua', False);
  Cfg := Base + '\Scripts\Hooks\UCS-AutoConnect.cfg';
  Addr := Trim(ServerAddrPage.Values[0]);
  if Addr <> '' then
    SaveStringToFile(Cfg, '# DCS-UCS Auto-Connect: address of the UCS-Server.exe for this DCS server' + #13#10 + Addr + #13#10, False)
  else if not FileExists(Cfg) then
    FileCopy(ExpandConstant('{app}\Server-Files\UCS-AutoConnect.cfg'), Cfg, True);
end;

procedure InstallServerHook();
var
  SG: String;
  Names: TArrayOfString;
  I: Integer;
  Done: Boolean;
begin
  SG := SavedGamesDir();
  Names := ServerProfileNames();
  Done := False;
  for I := 0 to GetArrayLength(Names) - 1 do
    if DirExists(SG + '\' + Names[I]) then
    begin
      InstallServerHookInto(SG + '\' + Names[I]);
      Done := True;
    end;
  if not Done then
  begin
    { No dedicated server profile found: the DCS server is hosted from the game itself }
    if DirExists(SG + '\DCS.openbeta') then InstallServerHookInto(SG + '\DCS.openbeta');
    InstallServerHookInto(SG + '\DCS');
  end;
end;

{ ---------------------------------------------------------------- uninstall }

procedure CleanSavedGames(const Base: String);
begin
  DeleteFile(Base + '\Scripts\Hooks\UCS-Hook.lua');
  DeleteFile(Base + '\Scripts\Hooks\UCS-ServerHook.lua');
  DeleteFile(Base + '\Scripts\UCS\UCS-Export.lua');
  RemoveDir(Base + '\Scripts\UCS');
  RemoveExportLine(Base + '\Scripts\Export.lua');
  { UCS-AutoConnect.cfg is kept on purpose (server admin configuration) }
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  SG: String;
  Names: TArrayOfString;
  I: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    SG := SavedGamesDir();
    if DirExists(SG + '\DCS') then CleanSavedGames(SG + '\DCS');
    if DirExists(SG + '\DCS.openbeta') then CleanSavedGames(SG + '\DCS.openbeta');
    Names := ServerProfileNames();
    for I := 0 to GetArrayLength(Names) - 1 do
      if DirExists(SG + '\' + Names[I]) then
        CleanSavedGames(SG + '\' + Names[I]);
  end;
end;

{ ---------------------------------------------------------------- wizard }

procedure InitializeWizard();
begin
  ServerAddrPage := CreateInputQueryPage(wpSelectTasks,
    'UCS Auto-Connect', 'Address of the UCS-Server.exe announced to players',
    'Players joining this DCS server will be connected automatically to this UCS server. ' +
    'Use host:port (e.g. 192.168.1.50:5055 or myserver.ddns.net:5055). ' +
    'Leave empty to auto-detect this machine''s public IP on port 5055.');
  ServerAddrPage.Add('UCS server address:', False);
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = ServerAddrPage.ID) and not WizardIsTaskSelected('dcsserverhook');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if WizardIsTaskSelected('dcsclient') then InstallClient();
    if WizardIsTaskSelected('dcsserverhook') then InstallServerHook();
  end;
end;
