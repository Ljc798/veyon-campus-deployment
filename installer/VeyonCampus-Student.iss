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
AppId={{7F925A0D-75E5-4CE8-B2D4-BF87BDFD7C91}
AppName=Veyon Campus 学生部署工具
AppVersion={#AppVersion}
AppVerName=Veyon Campus 学生部署工具 {#AppVersion}
AppPublisher=Veyon Campus
DefaultDirName={autopf}\Veyon Campus\Student
DefaultGroupName=Veyon Campus\学生部署工具
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Uninstallable=yes
UninstallDisplayName=Veyon Campus 学生部署工具
UninstallDisplayIcon={app}\VeyonCampus.StudentSetup.exe
OutputDir=.
; The package script sets the versioned name through ISCC -f.
OutputBaseFilename=VeyonCampus
SetupIconFile={#SourcePath + "\..\src\VeyonCampus.App\Assets\veyon-campus.ico"}
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
SetupLogging=yes
CloseApplications=force
RestartApplications=yes
UsePreviousAppDir=no
UninstallRestartComputer=no

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Flags: unchecked

[Files]
; If Windows still has a student-side file open after Restart Manager closes apps,
; defer replacement until reboot instead of aborting the entire upgrade.
Source: "{#PublishDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs restartreplace
Source: "{#UpdateHelperDirectory}\VeyonCampus.UpdateHelper.exe"; DestDir: "{autopf}\Veyon Campus\Updater\Student\{#AppVersion}"; Flags: ignoreversion

[Icons]
Name: "{group}\Veyon Campus 学生部署工具"; Filename: "{app}\VeyonCampus.StudentSetup.exe"
Name: "{group}\Veyon Campus 学生课堂助手"; Filename: "{app}\StudentCompanion\VeyonCampus.StudentCompanion.exe"
Name: "{commonstartup}\Veyon Campus 学生课堂助手"; Filename: "{app}\StudentCompanion\VeyonCampus.StudentCompanion.exe"; Parameters: "--startup"; WorkingDir: "{app}\StudentCompanion"
Name: "{autodesktop}\Veyon Campus 学生部署工具"; Filename: "{app}\VeyonCampus.StudentSetup.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\VeyonCampus.StudentSetup.exe"; Description: "启动学生部署工具"; Flags: nowait postinstall skipifsilent runascurrentuser

[UninstallDelete]
Type: filesandordirs; Name: "{autopf}\Veyon Campus\Updater\Student"
Type: filesandordirs; Name: "{app}\Worker\Staging"
