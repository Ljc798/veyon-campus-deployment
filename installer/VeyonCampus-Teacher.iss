#ifndef AppVersion
  #error AppVersion must be provided by package-windows-offline.ps1
#endif
#ifndef PublishDirectory
  #error PublishDirectory must be provided by package-windows-offline.ps1
#endif
#ifndef UpdateHelperDirectory
  #error UpdateHelperDirectory must be provided by package-windows-offline.ps1
#endif
#ifndef RuntimeVersion
  #error RuntimeVersion must be provided by package-windows-offline.ps1
#endif
#ifndef DotNetRuntimeInstallerPath
  #error DotNetRuntimeInstallerPath must be provided by package-windows-offline.ps1
#endif
#ifndef AspNetCoreRuntimeInstallerPath
  #error AspNetCoreRuntimeInstallerPath must be provided by package-windows-offline.ps1
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
SignedUninstaller=no
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
; Force-close processes that still hold files in the teacher installation after
; the preflight helper verifies process paths, without a Restart Manager prompt.
CloseApplications=no
RestartApplications=no
UsePreviousAppDir=no
UninstallRestartComputer=no

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："

[Files]
Source: "{#PublishDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#UpdateHelperDirectory}\VeyonCampus.UpdateHelper.exe"; DestDir: "{autopf}\Veyon Campus\Updater\Teacher\{#AppVersion}"; Flags: ignoreversion
Source: "{#UpdateHelperDirectory}\VeyonCampus.UpdateHelper.exe"; DestDir: "{tmp}"; Flags: dontcopy
Source: "{#DotNetRuntimeInstallerPath}"; DestDir: "{tmp}"; Flags: dontcopy
Source: "{#AspNetCoreRuntimeInstallerPath}"; DestDir: "{tmp}"; Flags: dontcopy

[Icons]
Name: "{group}\Veyon Campus 教师控制台"; Filename: "{app}\VeyonCampus.Teacher.exe"
Name: "{autodesktop}\Veyon Campus 教师控制台"; Filename: "{app}\VeyonCampus.Teacher.exe"; Tasks: desktopicon

[Run]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""VeyonCampusStudentUpdate"" dir=in action=allow protocol=TCP localport=39175 remoteip=LocalSubnet profile=domain,private enable=yes"; Flags: runhidden waituntilterminated
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""VeyonCampusTeacherMobileControl"" dir=in action=allow protocol=TCP localport=39176 remoteip=LocalSubnet profile=any enable=yes"; Flags: runhidden waituntilterminated
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""VeyonCampusTeacherMobileBootstrap"" dir=in action=allow protocol=TCP localport=39177 remoteip=LocalSubnet profile=any enable=yes"; Flags: runhidden waituntilterminated
Filename: "{app}\VeyonCampus.Teacher.exe"; Description: "启动教师控制台"; Flags: nowait postinstall skipifsilent runascurrentuser

[UninstallDelete]
Type: filesandordirs; Name: "{autopf}\Veyon Campus\Updater\Teacher"
Type: filesandordirs; Name: "{autopf}\Veyon Campus\UpdateRecovery\Teacher"
Type: filesandordirs; Name: "{app}\Worker\Staging"

[Code]
type
  TNativeRect = record
    Left: Integer;
    Top: Integer;
    Right: Integer;
    Bottom: Integer;
  end;
  TNativeMonitorInfo = record
    cbSize: Cardinal;
    rcMonitor: TNativeRect;
    rcWork: TNativeRect;
    dwFlags: Cardinal;
  end;

const
  MONITOR_DEFAULTTONEAREST = $00000002;

function MonitorFromWindow(hWnd: HWND; dwFlags: Cardinal): THandle;
  external 'MonitorFromWindow@user32.dll stdcall';

function GetMonitorInfoW(hMonitor: THandle; var lpmi: TNativeMonitorInfo): Integer;
  external 'GetMonitorInfoW@user32.dll stdcall';

function HasSharedFramework(const FrameworkName, Version: String): Boolean;
begin
  Result := FileExists(ExpandConstant('{autopf}\dotnet\dotnet.exe')) and
    DirExists(ExpandConstant('{autopf}\dotnet\shared\' + FrameworkName + '\' + Version));
end;

function InstallRuntimeInstaller(const InstallerName, FrameworkName, RuntimeDescription: String;
  var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  if HasSharedFramework(FrameworkName, '{#RuntimeVersion}') then
  begin
    Log('Required runtime already installed: ' + FrameworkName + ' {#RuntimeVersion}.');
    Exit;
  end;

  try
    ExtractTemporaryFile(InstallerName);
    if not Exec(ExpandConstant('{tmp}\' + InstallerName), '/install /quiet /norestart', '',
         SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      ResultCode := -1;
    if ResultCode = 0 then
    begin
      if HasSharedFramework(FrameworkName, '{#RuntimeVersion}') then Exit;
      Result := 'Microsoft ' + RuntimeDescription + ' 安装程序报告成功，但未检测到已安装的运行时。安装已停止，请重新运行此安装包。';
      Exit;
    end;
    if ResultCode = 3010 then
    begin
      NeedsRestart := True;
      if not HasSharedFramework(FrameworkName, '{#RuntimeVersion}') then
      begin
        Result := 'Microsoft ' + RuntimeDescription + ' 安装需要先重启 Windows 才能完成。请重启后重新运行此安装包。';
        Exit;
      end;
      Exit;
    end;
    Result := '无法安装 Microsoft ' + RuntimeDescription + ' ' + '{#RuntimeVersion}' +
      '（退出代码 ' + IntToStr(ResultCode) + '）。请关闭正在运行的 Veyon Campus 程序后重试。';
  except
    Log('Teacher runtime prerequisite failed: ' + GetExceptionMessage);
    Result := '无法安装 Microsoft ' + RuntimeDescription + ' ' + '{#RuntimeVersion}' +
      '。安装已停止，请检查系统空间和管理员权限后重试。';
  end;
end;

procedure PositionWizardForm;
var
  Monitor: THandle;
  MonitorInfo: TNativeMonitorInfo;
  WorkWidth, WorkHeight: Integer;
begin
  Monitor := MonitorFromWindow(WizardForm.Handle, MONITOR_DEFAULTTONEAREST);
  if Monitor = 0 then
  begin
    WizardForm.Position := poScreenCenter;
    Exit;
  end;

  MonitorInfo.cbSize := SizeOf(MonitorInfo);
  if GetMonitorInfoW(Monitor, MonitorInfo) = 0 then
  begin
    WizardForm.Position := poScreenCenter;
    Exit;
  end;

  WorkWidth := MonitorInfo.rcWork.Right - MonitorInfo.rcWork.Left;
  WorkHeight := MonitorInfo.rcWork.Bottom - MonitorInfo.rcWork.Top;
  WizardForm.Position := poDesigned;
  if (WizardForm.Width > WorkWidth) or (WizardForm.Height > WorkHeight) then
  begin
    WizardForm.Left := MonitorInfo.rcWork.Left;
    WizardForm.Top := MonitorInfo.rcWork.Top;
  end
  else
  begin
    WizardForm.Left := MonitorInfo.rcWork.Left + (WorkWidth - WizardForm.Width) div 2;
    WizardForm.Top := MonitorInfo.rcWork.Top + (WorkHeight - WizardForm.Height) div 2;
  end;
end;

procedure InitializeWizard;
begin
  WizardForm.CenterOnShow := False;
  PositionWizardForm;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  try
    Result := InstallRuntimeInstaller('dotnet-runtime-{#RuntimeVersion}-win-x64.exe',
      'Microsoft.NETCore.App', '.NET Runtime', NeedsRestart);
    if Result <> '' then Exit;
    Result := InstallRuntimeInstaller('aspnetcore-runtime-{#RuntimeVersion}-win-x64.exe',
      'Microsoft.AspNetCore.App', 'ASP.NET Core Runtime', NeedsRestart);
    if Result <> '' then Exit;
    ExtractTemporaryFile('VeyonCampus.UpdateHelper.exe');
    if (not Exec(ExpandConstant('{tmp}\VeyonCampus.UpdateHelper.exe'),
         '--stop-installed-role-processes TeacherConsole', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)) or
       (ResultCode <> 0) then
      Result := '无法停止教师控制台或其更新辅助进程。请关闭正在运行的 Veyon Campus 教师端后重试。';
  except
    Log('Teacher process preflight failed: ' + GetExceptionMessage);
    Result := '无法核对并关闭旧版教师端进程；安装已停止。请先结束 Veyon Campus 教师端进程后重试。';
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RemoveDir(ExpandConstant('{autopf}\Veyon Campus\Updater\Teacher'));
    RemoveDir(ExpandConstant('{autopf}\Veyon Campus\Updater'));
    RemoveDir(ExpandConstant('{autopf}\Veyon Campus\UpdateRecovery\Teacher'));
    RemoveDir(ExpandConstant('{autopf}\Veyon Campus\UpdateRecovery'));
    if not DirExists(ExpandConstant('{autopf}\Veyon Campus\Student')) and
       not DirExists(ExpandConstant('{autopf}\Veyon Campus\Teacher')) then
      RemoveDir(ExpandConstant('{autopf}\Veyon Campus\UpdateStaging'));
    RemoveDir(ExpandConstant('{autopf}\Veyon Campus'));
  end;
end;

function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
  DiagnosticPath: String;
  UninstallArguments: String;
  DiagnosticLines: TArrayOfString;
  DiagnosticIndex: Integer;
  FailureDetail: String;
  KeepConfiguration: Boolean;
  UserChoice: Integer;
begin
  try
    Result := True;
    UserChoice := SuppressibleMsgBox('卸载教师控制台。是否保留本机配置？' + #13#10 + #13#10 +
         '是：保留本机 Veyon Campus 配置和 Veyon 地点、名单及认证密钥，方便重新安装。' + #13#10 +
         '否：清除本机相关配置；同一台电脑上的学生端配置也会一并清除。' + #13#10 +
         '取消：不卸载。', mbConfirmation, MB_YESNOCANCEL or MB_DEFBUTTON1, IDYES);
    if UserChoice = IDCANCEL then
    begin
      Result := False;
      Exit;
    end;
    KeepConfiguration := UserChoice = IDYES;

    DiagnosticPath := ExpandConstant('{tmp}\VeyonCampus-Teacher-Uninstall.txt');
    DeleteFile(DiagnosticPath);
    if not FileExists(ExpandConstant('{app}\VeyonCampus.Teacher.exe')) then
    begin
      Log('Teacher uninstall preflight stopped: cleanup executable is missing.');
      SuppressibleMsgBox('找不到教师端清理程序。请先修复或覆盖安装教师端，再重新卸载。',
        mbError, MB_OK, IDOK);
      Result := False;
      Exit;
    end;

    Log('Starting Veyon Campus teacher cleanup helper.');
    UninstallArguments := '--uninstall-teacher-runtime "' + DiagnosticPath + '"';
    if KeepConfiguration then UninstallArguments := UninstallArguments + ' --preserve-config';
    if not Exec(ExpandConstant('{app}\VeyonCampus.Teacher.exe'),
         UninstallArguments, ExpandConstant('{app}'),
         SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      ResultCode := -1;
    Log('Teacher cleanup helper exit code: ' + IntToStr(ResultCode));
    if ResultCode = 0 then
    begin
      DeleteFile(DiagnosticPath);
      Exit;
    end;

    FailureDetail := '';
    if FileExists(DiagnosticPath) and LoadStringsFromFile(DiagnosticPath, DiagnosticLines) then
      for DiagnosticIndex := 0 to GetArrayLength(DiagnosticLines) - 1 do
        FailureDetail := FailureDetail + DiagnosticLines[DiagnosticIndex] + #13#10;
    DeleteFile(DiagnosticPath);
    if FailureDetail = '' then FailureDetail := '无法确认教师端专属文件、证书和防火墙规则已清理。';
    Log('Teacher cleanup helper failed: ' + FailureDetail);
    SuppressibleMsgBox('教师端清理未完成，已停止卸载并保留安装文件。' + #13#10 + #13#10 +
      FailureDetail + #13#10 + #13#10 + '解决问题后再重新卸载。', mbError, MB_OK, IDOK);
    Result := False;
  except
    Log('Teacher uninstaller preflight exception: ' + GetExceptionMessage);
    SuppressibleMsgBox('教师端卸载预检查异常，已保留安装文件。卸载日志会记录异常详情，请检查后重试。',
      mbError, MB_OK, IDOK);
    Result := False;
  end;
end;

procedure ConfigureMobileFirewallRule(const RuleName, Port: String);
var
  Parameters: String;
  ResultCode: Integer;
begin
  Parameters := 'advfirewall firewall set rule name="' + RuleName + '" new dir=in action=allow protocol=TCP localport=' + Port +
    ' remoteip=LocalSubnet profile=any enable=yes';
  if Exec(ExpandConstant('{sys}\netsh.exe'), Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and
     (ResultCode = 0) then
    Exit;

  Parameters := 'advfirewall firewall add rule name="' + RuleName + '" dir=in action=allow protocol=TCP localport=' + Port +
    ' remoteip=LocalSubnet profile=any enable=yes';
  if (not Exec(ExpandConstant('{sys}\netsh.exe'), Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode)) or
     (ResultCode <> 0) then
  begin
    Log('Could not configure mobile-control firewall rule ' + RuleName + '.');
    SuppressibleMsgBox('无法配置教师手机控制的 Windows 防火墙端口。请让管理员允许本地子网访问 TCP 39176 和 39177。',
      mbError, MB_OK, IDOK);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    ConfigureMobileFirewallRule('VeyonCampusTeacherMobileControl', '39176');
    ConfigureMobileFirewallRule('VeyonCampusTeacherMobileBootstrap', '39177');
  end;
end;
