; Inno Setup script for the three installers:
;   ISCC /DEdition=Teacher installer\ClassroomControl.iss   -> artifacts\ClassroomControl-Teacher-Setup.exe
;   ISCC /DEdition=Student installer\ClassroomControl.iss   -> artifacts\ClassroomControl-Student-Setup.exe
;   ISCC /DEdition=Full    installer\ClassroomControl.iss   -> artifacts\ClassroomControl-Setup.exe   (both programs, choose components)
; Expects the self-contained publish output in .\publish\teacher and .\publish\student (see build\publish.ps1).
;
; Administrator rights are needed ONLY for: copying to Program Files and adding the Windows Firewall rules.
; Both programs always run as the normal user.

#ifndef Edition
  #define Edition "Full"
#endif

#define Version "1.0.0"
#define StudentExe "ClassroomControl.Student.exe"
#define TeacherExe "ClassroomControl.Teacher.exe"
#define StudentRule "ClassroomControl Student (discovery replies, local subnet)"
#define TeacherUdpRule "ClassroomControl Teacher (UDP 39500 discovery, local subnet)"
#define TeacherTcpRule "ClassroomControl Teacher (TCP 39501 sessions, local subnet)"
#define RunValue "ClassroomControl.StudentAgent"

#if Edition == "Full"
  #define TeacherSub "Teacher\"
  #define StudentSub "Student\"
  #define TeacherComp "; Components: teacher"
  #define StudentComp "; Components: student"
#else
  #define TeacherSub ""
  #define StudentSub ""
  #define TeacherComp ""
  #define StudentComp ""
#endif

#if Edition == "Teacher"
  #define AppTitle "Classroom Control - Teacher"
  #define OutName "ClassroomControl-Teacher-Setup"
  #define Guid "{{3D0B6B1E-7F0A-4C55-9B7A-5A4E1C2D8F10}"
  #define Dir "ClassroomControl\Teacher"
#elif Edition == "Student"
  #define AppTitle "Classroom Control - Student Agent"
  #define OutName "ClassroomControl-Student-Setup"
  #define Guid "{{B7C8E1A2-5D3F-4E6A-9C21-7A1F0E2D4B55}"
  #define Dir "ClassroomControl\StudentAgent"
#else
  #define AppTitle "Classroom Control"
  #define OutName "ClassroomControl-Setup"
  #define Guid "{{9A2F4C77-18B3-4E0D-A6C9-0E5B7D3F6A21}"
  #define Dir "ClassroomControl"
#endif

[Setup]
AppId={#Guid}
AppName={#AppTitle}
AppVersion={#Version}
AppVerName={#AppTitle} {#Version}
AppPublisher=Mirjalol Murodov Nomoz o'g'li
AppContact=mirjalol.murodov09@gmail.com
DefaultDirName={autopf}\{#Dir}
DefaultGroupName=Classroom Control
DisableProgramGroupPage=yes
OutputDir=..\artifacts
OutputBaseFilename={#OutName}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UninstallDisplayName={#AppTitle}
SetupIconFile=..\src\StudentAgent\Resources\Icons\agent.ico
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
#if Edition == "Teacher"
UninstallDisplayIcon={app}\{#TeacherExe}
#else
UninstallDisplayIcon={app}\Student\{#StudentExe}
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

#if Edition == "Full"
[Components]
Name: "teacher"; Description: "Teacher (for the teacher's computer)"; Types: full custom
Name: "student"; Description: "Student Agent (for every student computer)"; Types: full custom
#endif

[Tasks]
#if Edition != "Teacher"
Name: "startup"; Description: "Start the Student Agent when &Windows starts"; GroupDescription: "Startup:"
#endif
Name: "desktopicon"; Description: "Create &desktop shortcuts"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
#if Edition == "Teacher"
Source: "..\publish\teacher\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
#elif Edition == "Student"
Source: "..\publish\student\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
#else
Source: "..\publish\teacher\*"; DestDir: "{app}\Teacher"; Flags: recursesubdirs createallsubdirs ignoreversion; Components: teacher
Source: "..\publish\student\*"; DestDir: "{app}\Student"; Flags: recursesubdirs createallsubdirs ignoreversion; Components: student
#endif
Source: "..\docs\USER_MANUAL.md"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "..\docs\INSTALLATION.md"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "..\docs\NETWORK.md"; DestDir: "{app}\docs"; Flags: ignoreversion

[Icons]
#if Edition == "Teacher"
Name: "{group}\Classroom Control Teacher"; Filename: "{app}\{#TeacherExe}"
Name: "{autodesktop}\Classroom Control Teacher"; Filename: "{app}\{#TeacherExe}"; Tasks: desktopicon
#elif Edition == "Student"
Name: "{group}\Student Agent"; Filename: "{app}\{#StudentExe}"
Name: "{autodesktop}\Student Agent"; Filename: "{app}\{#StudentExe}"; Tasks: desktopicon
#else
Name: "{group}\Classroom Control Teacher"; Filename: "{app}\Teacher\{#TeacherExe}"; Components: teacher
Name: "{group}\Student Agent"; Filename: "{app}\Student\{#StudentExe}"; Components: student
Name: "{autodesktop}\Classroom Control Teacher"; Filename: "{app}\Teacher\{#TeacherExe}"; Tasks: desktopicon; Components: teacher
Name: "{autodesktop}\Student Agent"; Filename: "{app}\Student\{#StudentExe}"; Tasks: desktopicon; Components: student
#endif
Name: "{group}\Uninstall {#AppTitle}"; Filename: "{uninstallexe}"

#if Edition != "Teacher"
[Registry]
; Per-user start with Windows for the installing user (other users get it from the agent's own "Start with Windows" setting).
#if Edition == "Student"
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#RunValue}"; ValueData: """{app}\{#StudentExe}"" --minimized"; Tasks: startup; Flags: uninsdeletevalue
#else
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#RunValue}"; ValueData: """{app}\Student\{#StudentExe}"" --minimized"; Tasks: startup; Components: student; Flags: uninsdeletevalue
#endif
#endif

[Run]
; Windows Firewall: only these programs, only these protocols and ports, only the local subnet. Nothing is opened to the internet.
#if Edition != "Student"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""{#TeacherUdpRule}"" dir=in action=allow program=""{app}\{#TeacherSub}{#TeacherExe}"" protocol=UDP localport=39500 remoteip=LocalSubnet profile=any"; Flags: runhidden; StatusMsg: "Adding Windows Firewall rules (local subnet only)..."{#TeacherComp}
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""{#TeacherTcpRule}"" dir=in action=allow program=""{app}\{#TeacherSub}{#TeacherExe}"" protocol=TCP localport=39501 remoteip=LocalSubnet profile=any"; Flags: runhidden{#TeacherComp}
#endif
#if Edition != "Teacher"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""{#StudentRule}"" dir=in action=allow program=""{app}\{#StudentSub}{#StudentExe}"" protocol=UDP remoteip=LocalSubnet profile=any"; Flags: runhidden{#StudentComp}
#endif
#if Edition == "Teacher"
Filename: "{app}\{#TeacherExe}"; Description: "Start Classroom Control Teacher"; Flags: nowait postinstall skipifsilent
#elif Edition == "Student"
Filename: "{app}\{#StudentExe}"; Parameters: "--disable-autostart"; Description: "Start the Student Agent"; Flags: nowait postinstall skipifsilent; Tasks: not startup
Filename: "{app}\{#StudentExe}"; Description: "Start the Student Agent"; Flags: nowait postinstall skipifsilent; Tasks: startup
#else
Filename: "{app}\Teacher\{#TeacherExe}"; Description: "Start Classroom Control Teacher"; Flags: nowait postinstall skipifsilent unchecked; Components: teacher
Filename: "{app}\Student\{#StudentExe}"; Description: "Start the Student Agent"; Flags: nowait postinstall skipifsilent unchecked; Components: student
#endif

[UninstallRun]
#if Edition != "Student"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#TeacherExe}"; Flags: runhidden; RunOnceId: "StopTeacher"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#TeacherUdpRule}"""; Flags: runhidden; RunOnceId: "RemoveTeacherUdp"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#TeacherTcpRule}"""; Flags: runhidden; RunOnceId: "RemoveTeacherTcp"
#endif
#if Edition != "Teacher"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#StudentExe}"; Flags: runhidden; RunOnceId: "StopStudent"
#if Edition == "Student"
Filename: "{app}\{#StudentExe}"; Parameters: "--unregister-startup"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveStartup"
#else
Filename: "{app}\Student\{#StudentExe}"; Parameters: "--unregister-startup"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "RemoveStartup"
#endif
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#StudentRule}"""; Flags: runhidden; RunOnceId: "RemoveStudentRule"
#endif

[Code]
function DeleteDataRequested: Boolean;
var
  i: Integer;
begin
  Result := False;
  for i := 1 to ParamCount do
    if CompareText(ParamStr(i), '/DELETEUSERDATA') = 0 then Result := True;
end;

procedure RemoveDataDir(const Name: String);
var
  Dir: String;
begin
  Dir := ExpandConstant('{localappdata}\ClassroomControl\') + Name;
  if DirExists(Dir) then DelTree(Dir, True, True, True);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  AnyData: Boolean;
  Delete: Boolean;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  AnyData := DirExists(ExpandConstant('{localappdata}\ClassroomControl\StudentAgent')) or DirExists(ExpandConstant('{localappdata}\ClassroomControl\Teacher'));
  if not AnyData then Exit;
  if UninstallSilent then
    Delete := DeleteDataRequested
  else
    Delete := MsgBox('Do you also want to delete the configuration, database, screenshots and logs (Device ID, classroom data, settings)?' + #13#10 +
                     'Choose No to keep them for a future reinstall.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
  if Delete then
  begin
    RemoveDataDir('StudentAgent');
    RemoveDataDir('Teacher');
  end;
end;
