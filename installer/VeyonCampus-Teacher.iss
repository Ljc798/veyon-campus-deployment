#ifndef AppVersion
  #error AppVersion must be provided by package-windows-offline.ps1
#endif
#ifndef PublishDirectory
  #error PublishDirectory must be provided by package-windows-offline.ps1
#endif
#ifndef UpdateHelperDirectory
  #error UpdateHelperDirectory must be provided by package-windows-offline.ps1
#endif

[Setup]
AppId={{6E4F5C42-ACD1-4096-A80F-62CCF9416E7B}
AppName=Veyon Campus 教师控制台
AppVersion={#AppVersion}
AppVerName=Veyon Campus 教师控制台 {#AppVersion}
AppPublisher=Veyon Campus
DefaultDirName={autopf}\Veyon Campus\Teacher
DefaultGroupName=Veyon Campus\教师控制台
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Uninstallable=yes
UninstallDisplayName=Veyon Campus 教师控制台
UninstallDisplayIcon={app}\VeyonCampus.Teacher.exe
OutputDir=.
OutputBaseFilename=VeyonCampus
SetupIconFile={#SourcePath + "\..\src\VeyonCampus.App\Assets\veyon-campus.ico"}
LicenseFile={#SourcePath + "\Teacher-Data-Notice.txt"}
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
SetupLogging=yes
CloseApplications=yes
RestartApplications=no
UsePreviousAppDir=no
UninstallRestartComputer=no

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Flags: unchecked

[Files]
Source: "{#PublishDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#UpdateHelperDirectory}\VeyonCampus.UpdateHelper.exe"; DestDir: "{autopf}\Veyon Campus\Updater\Teacher\{#AppVersion}"; Flags: ignoreversion

[Icons]
Name: "{group}\Veyon Campus 教师控制台"; Filename: "{app}\VeyonCampus.Teacher.exe"
Name: "{autodesktop}\Veyon Campus 教师控制台"; Filename: "{app}\VeyonCampus.Teacher.exe"; Tasks: desktopicon

[Run]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""VeyonCampusStudentUpdate"" dir=in action=allow protocol=TCP localport=39175 remoteip=LocalSubnet profile=domain,private enable=yes"; Flags: runhidden waituntilterminated
Filename: "{app}\VeyonCampus.Teacher.exe"; Description: "启动教师控制台"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""VeyonCampusStudentUpdate"""; Flags: runhidden waituntilterminated; RunOnceId: "RemoveVeyonCampusStudentUpdateFirewallRule"

[UninstallDelete]
Type: filesandordirs; Name: "{autopf}\Veyon Campus\Updater\Teacher"
Type: filesandordirs; Name: "{app}\Worker\Staging"
