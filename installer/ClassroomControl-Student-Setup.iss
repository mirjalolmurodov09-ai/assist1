; Inno Setup script for "ClassroomControl-Student-Setup.exe".
; Build:  ISCC.exe installer\ClassroomControl-Student-Setup.iss   (after build\publish.ps1 created .\publish)
;
; Administrator rights are needed ONLY for: copying to Program Files and adding the Windows Firewall rule.
; The agent itself always runs as the normal user.

#define AppName "Classroom Control - Student Agent"
#define AppVersion "1.0.0"
#define ExeName "ClassroomControl.StudentAgent.exe"
#define FirewallRule "ClassroomControl Student Agent (discovery replies, local subnet)"
#define RunValue "ClassroomControl.StudentAgent"

[Setup]
AppId={{B7C8E1A2-5D3F-4E6A-9C21-7A1F0E2D4B55}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Mirjalol Murodov Nomoz o'g'li
AppContact=mirjalol.murodov09@gmail.com
DefaultDirName={autopf}\ClassroomControl\StudentAgent
DefaultGroupName=Classroom Control
DisableProgramGroupPage=yes
OutputDir=..\artifacts
OutputBaseFilename=ClassroomControl-Student-Setup
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UninstallDisplayIcon={app}\{#ExeName}
UninstallDisplayName={#AppName}
SetupIconFile=..\StudentAgent\Resources\Icons\agent.ico
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Start the Student Agent when &Windows starts"; GroupDescription: "Startup:"
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\Student Agent"; Filename: "{app}\{#ExeName}"
Name: "{group}\Uninstall Student Agent"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Student Agent"; Filename: "{app}\{#ExeName}"; Tasks: desktopicon

[Registry]
; Per-user start with Windows for the installing user (other users get it from the agent's own "Start with Windows" setting).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#RunValue}"; ValueData: """{app}\{#ExeName}"" --minimized"; Tasks: startup; Flags: uninsdeletevalue

[Run]
; Windows Firewall: only this program, only UDP, only the local subnet (the Teacher's discovery replies). No public/internet access.
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""{#FirewallRule}"" dir=in action=allow program=""{app}\{#ExeName}"" protocol=UDP remoteip=LocalSubnet profile=any"; Flags: runhidden; StatusMsg: "Adding Windows Firewall rule (local subnet only)..."
Filename: "{app}\{#ExeName}"; Parameters: "--disable-autostart"; Description: "Start the Student Agent"; Flags: nowait postinstall skipifsilent; Tasks: not startup
Filename: "{app}\{#ExeName}"; Description: "Start the Student Agent"; Flags: nowait postinstall skipifsilent; Tasks: startup

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#ExeName}"; Flags: runhidden; RunOnceId: "StopAgent"
Filename: "{app}\{#ExeName}"; Parameters: "--unregister-startup"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveStartup"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#FirewallRule}"""; Flags: runhidden; RunOnceId: "RemoveFirewallRule"

[Code]
function DeleteDataRequested: Boolean;
var
  i: Integer;
begin
  Result := False;
  for i := 1 to ParamCount do
    if CompareText(ParamStr(i), '/DELETEUSERDATA') = 0 then Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  DataDir := ExpandConstant('{localappdata}\ClassroomControl\StudentAgent');
  if not DirExists(DataDir) then Exit;
  if UninstallSilent then
  begin
    if DeleteDataRequested then DelTree(DataDir, True, True, True);
  end
  else if MsgBox('Do you also want to delete the Student Agent configuration and logs (Device ID, settings, log files)?' + #13#10 +
                 'Choose No to keep them for a future reinstall.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    DelTree(DataDir, True, True, True);
end;
