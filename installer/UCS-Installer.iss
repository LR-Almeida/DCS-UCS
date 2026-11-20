; =============================================================================
;  DCS-UCS (Universal Control Sync) - Inno Setup script
;  Build: run build.bat first (creates ..\publish), then compile this file with
;  Inno Setup 7 (ISCC.exe installer\UCS-Installer.iss). Output: installer\Output\
; =============================================================================

#define AppName "DCS-UCS"
#define AppFullName "DCS-UCS (Universal Control Sync)"
#ifndef AppVersion
  #define AppVersion "0.1.10-alpha"
#endif
#define AppPublisher "DCS-UCS contributors"
; Numeric x.y.z.0 for the Windows version resource ("0.1.10-alpha" -> "0.1.10.0")
#define DashPos Pos("-", AppVersion)
#if DashPos > 0
  #define AppNumVersion Copy(AppVersion, 1, DashPos - 1) + ".0"
#else
  #define AppNumVersion AppVersion + ".0"
#endif
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
; Privacy policy shown during installation (network connections can be turned off in the next pages)
InfoBeforeFile=..\PRIVACY.md
; Version resource: same product name and version in every signed file (SignPath metadata rules)
VersionInfoProductName=DCS-UCS
VersionInfoProductTextVersion={#AppVersion}
VersionInfoProductVersion={#AppNumVersion}
VersionInfoTextVersion={#AppVersion}
VersionInfoVersion={#AppNumVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription=DCS-UCS Setup
VersionInfoCopyright=MIT License
OutputDir=Output
OutputBaseFilename=DCS-UCS-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\assets\ucs.ico
SetupArchitecture=x64
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UninstallDisplayIcon={app}\UCS-Client.exe
UninstallDisplayName={#AppFullName}
; In-app updates run this installer with /SILENT /UPDATE=1: close the running client, reuse previous choices
CloseApplications=yes
RestartApplications=no
UsePreviousTasks=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "dcsclient"; Description: "Configure DCS World client (Saved Games\DCS and DCS.openbeta): UCS-Hook.lua, UCS-Export.lua and Export.lua injection"; GroupDescription: "DCS integration:"
Name: "dcsserverhook"; Description: "Install UCS-ServerHook.lua (only if THIS machine hosts a DCS server - SRS-style auto-connect)"; GroupDescription: "DCS integration:"; Flags: unchecked
Name: "netupdates"; Description: "Check for DCS-UCS updates on GitHub (api.github.com) - client and server"; GroupDescription: "Network connections (see the privacy policy):"
Name: "netpublicip"; Description: "Let UCS-Server and the DCS server hook detect this PC's public IP address (api.ipify.org)"; GroupDescription: "Network connections (see the privacy policy):"
Name: "firewall"; Description: "Open UDP port 5055 in Windows Firewall (only if this machine runs UCS-Server.exe)"; GroupDescription: "Network:"; Flags: unchecked
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\publish\UCS-Client.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\publish\UCS-Server.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\publish\UCS-ProfileGen.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\src\Lua\UCS-Hook.lua"; DestDir: "{app}\Lua"; Flags: ignoreversion
Source: "..\src\Lua\UCS-Export.lua"; DestDir: "{app}\Lua"; Flags: ignoreversion
Source: "..\src\Lua\UCS-ServerHook.lua"; DestDir: "{app}\Server-Files"; Flags: ignoreversion
Source: "..\Server-Files\UCS-AutoConnect.cfg"; DestDir: "{app}\Server-Files"; Flags: onlyifdoesntexist
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\PRIVACY.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\UCS Client"; Filename: "{app}\UCS-Client.exe"
Name: "{group}\UCS Server"; Filename: "{app}\UCS-Server.exe"
Name: "{group}\UCS Profile Generator (help)"; Filename: "{cmd}"; Parameters: "/k ""{app}\UCS-ProfileGen.exe"" --help"; WorkingDir: "{app}"
Name: "{group}\Server Files folder"; Filename: "{app}\Server-Files"
Name: "{group}\README"; Filename: "{app}\README.md"
Name: "{group}\Privacy policy"; Filename: "{app}\PRIVACY.md"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\UCS Client"; Filename: "{app}\UCS-Client.exe"; Tasks: desktopicon

[Run]
; delete first so updates/reinstalls never create duplicate rules
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""DCS-UCS Server"""; Flags: runhidden; Tasks: firewall
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""DCS-UCS Server"" dir=in action=allow protocol=UDP localport=5055"; Flags: runhidden; Tasks: firewall
Filename: "{app}\UCS-Client.exe"; Description: "{cm:LaunchProgram,UCS Client}"; Flags: nowait postinstall skipifsilent
; reopen the client after an in-app update, as the normal (non-elevated) user
Filename: "{app}\UCS-Client.exe"; Flags: nowait runasoriginaluser; Check: IsInAppUpdate

[UninstallDelete]
Type: files; Name: "{app}\install-options.ini"

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

{ Export.lua is shared with other tools (SRS, Tacview, DCS-BIOS...). It is never replaced:
  one line is appended at the end, and the rest of the file is kept byte for byte. }
procedure InjectExport(const FileName: String);
var
  Raw: AnsiString;
begin
  if FileExists(FileName) then
  begin
    if not LoadStringFromFile(FileName, Raw) then
    begin
      Log('Could not read ' + FileName + ' - Export.lua left untouched');
      Exit;
    end;
    if Pos('UCS-Export.lua', String(Raw)) > 0 then
    begin
      Log('Export.lua already loads UCS: ' + FileName);
      Exit;
    end;
    FileCopy(FileName, FileName + '.ucs-backup', False);
  end;
  if SaveStringToFile(FileName, #13#10 + UCS_EXPORT_LINE + #13#10, True) then
    Log('Export.lua: UCS line appended to ' + FileName)
  else
    Log('Could not write ' + FileName);
end;

{ Removes only the UCS line; everything else stays exactly as it was. }
procedure RemoveExportLine(const FileName: String);
var
  Raw: AnsiString;
  Text, Block: String;
  Count: Integer;
begin
  if not FileExists(FileName) then Exit;
  if not LoadStringFromFile(FileName, Raw) then Exit;
  Text := String(Raw);
  Block := #13#10 + UCS_EXPORT_LINE + #13#10;
  Count := 0;
  { Our line is still the last one (the usual case): cut exactly what the installer appended }
  if (Length(Text) >= Length(Block)) and (Copy(Text, Length(Text) - Length(Block) + 1, Length(Block)) = Block) then
  begin
    SetLength(Text, Length(Text) - Length(Block));
    Count := 1;
  end;
  { Lines were added after ours: remove only our line, keep the line break between neighbours }
  if Count = 0 then Count := StringChangeEx(Text, Block, #13#10, True);
  if Count = 0 then Count := StringChangeEx(Text, #13#10 + UCS_EXPORT_LINE, '', True);
  if Count = 0 then Count := StringChangeEx(Text, UCS_EXPORT_LINE + #13#10, '', True);
  if Count = 0 then Count := StringChangeEx(Text, UCS_EXPORT_LINE, '', True);
  if Count > 0 then
  begin
    FileCopy(FileName, FileName + '.ucs-uninstall-backup', False);
    SaveStringToFile(FileName, AnsiString(Text), False);
  end;
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

{ "Detect public IP" unchecked -> the server hook never contacts api.ipify.org }
procedure SetPublicIpOption(const Cfg: String);
var
  Raw: AnsiString;
  S: String;
begin
  if not LoadStringFromFile(Cfg, Raw) then Exit;
  S := String(Raw);
  StringChangeEx(S, 'DetectPublicIp=false' + #13#10, '', True);
  if not WizardIsTaskSelected('netpublicip') then
    S := S + 'DetectPublicIp=false' + #13#10;
  SaveStringToFile(Cfg, AnsiString(S), False);
end;

{ Network choices for UCS-Client.exe / UCS-Server.exe (see InstallOptions.cs) }
procedure WriteInstallOptions();
var
  S: String;
begin
  S := '; DCS-UCS installer choices. Run Setup again to change them.' + #13#10;
  if WizardIsTaskSelected('netupdates') then S := S + 'CheckForUpdates=1' + #13#10
  else S := S + 'CheckForUpdates=0' + #13#10;
  if WizardIsTaskSelected('netpublicip') then S := S + 'DetectPublicIp=1' + #13#10
  else S := S + 'DetectPublicIp=0' + #13#10;
  SaveStringToFile(ExpandConstant('{app}\install-options.ini'), AnsiString(S), False);
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
  SetPublicIpOption(Cfg);
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

{ True when started by UCS-Client's updater (/UPDATE=1) }
function IsInAppUpdate(): Boolean;
begin
  Result := ExpandConstant('{param:UPDATE|0}') = '1';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    WriteInstallOptions();
    if WizardIsTaskSelected('dcsclient') then InstallClient();
    if WizardIsTaskSelected('dcsserverhook') then InstallServerHook();
  end;
end;
