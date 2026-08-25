; Inno Setup template — review service identity, certificate path, signing, and firewall policy before production use.
#define MyAppName "Tempy Management Server"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Tempy"
#define MyAppExeName "Server.exe"
#define PublishDir "..\artifacts\Server"

[Setup]
AppId={{C7FF8BE2-DDFD-4B2A-9A89-FBECD1CE4A65}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\TempyServer
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts\installer
OutputBaseFilename=TempyServerSetup
Compression=lzma2
SolidCompression=yes

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Dirs]
Name: "{commonappdata}\TempyServer"; Permissions: users-readexec
Name: "{commonappdata}\TempyServer\data"; Permissions: users-modify

[Run]
Filename: "{sys}\sc.exe"; Parameters: "create TempyManagementServer binPath= """"{app}\{#MyAppExeName}"" --service"" start= auto"; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "description TempyManagementServer ""Tempy durable LAN management server"""; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "start TempyManagementServer"; Flags: runhidden waituntilterminated

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop TempyManagementServer"; Flags: runhidden waituntilterminated skipifdoesntexist
Filename: "{sys}\sc.exe"; Parameters: "delete TempyManagementServer"; Flags: runhidden waituntilterminated skipifdoesntexist

[Code]
function InitializeSetup(): Boolean;
begin
  MsgBox('Before starting the installed service, place a protected appsettings.Production.json beside Server.exe and configure HTTPS, BootstrapAdmin credentials, and per-device/enrollment tokens. The installer does not create passwords or tokens.', mbInformation, MB_OK);
  Result := True;
end;
