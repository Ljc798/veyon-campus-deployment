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
SignedUninstaller=no
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
UninstallLogging=yes
CloseApplications=force
RestartApplications=yes
UsePreviousAppDir=no
UninstallRestartComputer=no

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："

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
; Start the interactive-session companion immediately so it can consume policy-restart requests
; during the current login session; the Common Startup shortcut covers later logins.
Filename: "{app}\StudentCompanion\VeyonCampus.StudentCompanion.exe"; Parameters: "--startup"; WorkingDir: "{app}\StudentCompanion"; Flags: nowait runascurrentuser skipifsilent
Filename: "{app}\VeyonCampus.StudentSetup.exe"; Description: "启动学生部署工具"; Flags: nowait postinstall skipifsilent runascurrentuser

[UninstallDelete]
Type: filesandordirs; Name: "{autopf}\Veyon Campus\Updater\Student"
Type: filesandordirs; Name: "{app}\Worker\Staging"

[Code]
function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
  DiagnosticPath: String;
  DiagnosticLines: TArrayOfString;
  DiagnosticIndex: Integer;
  FailureDetail: String;
begin
  try
    Result := True;
    DiagnosticPath := ExpandConstant('{tmp}\VeyonCampus-Student-Uninstall.txt');
    DeleteFile(DiagnosticPath);

    if not FileExists(ExpandConstant('{app}\VeyonCampus.StudentSetup.exe')) then
    begin
      Log('Student uninstall preflight stopped: cleanup executable is missing.');
      MsgBox('找不到学生端清理程序。请先修复或覆盖安装学生端，再重新卸载。', mbError, MB_OK);
      Result := False;
      Exit;
    end;

    Log('Starting Veyon Campus student cleanup helper.');
    if not Exec(ExpandConstant('{app}\VeyonCampus.StudentSetup.exe'),
         '--uninstall-student-runtime "' + DiagnosticPath + '"', ExpandConstant('{app}'),
         SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      ResultCode := -1;
    Log('Student cleanup helper exit code: ' + IntToStr(ResultCode));

    if (ResultCode = 0) then
    begin
      DeleteFile(DiagnosticPath);
      Exit;
    end;

    FailureDetail := '';
    if FileExists(DiagnosticPath) and LoadStringsFromFile(DiagnosticPath, DiagnosticLines) then
      for DiagnosticIndex := 0 to GetArrayLength(DiagnosticLines) - 1 do
        FailureDetail := FailureDetail + DiagnosticLines[DiagnosticIndex] + #13#10;
    DeleteFile(DiagnosticPath);
    if FailureDetail = '' then
      FailureDetail := '无法确认后台 Agent 和策略已安全清理。';

    Log('Student cleanup helper failed: ' + FailureDetail);
    MsgBox('学生端后台 Agent 或策略未能安全清理，已停止卸载并保留安装文件。' + #13#10 + #13#10 +
      FailureDetail + #13#10 + #13#10 + '请在学生部署工具中查看“管理员维护”记录，解决后重试。',
      mbError, MB_OK);
    Result := False;
  except
    Log('Student uninstaller preflight exception: ' + GetExceptionMessage);
    MsgBox('学生端卸载预检查异常，已保留安装文件。卸载日志会记录异常详情，请检查后重试。',
      mbError, MB_OK);
    Result := False;
  end;
end;
