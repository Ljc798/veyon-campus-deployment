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
#ifndef EdgeExtensionId
  #define EdgeExtensionId ""
#endif
#ifndef EdgeExtensionForceInstall
  #define EdgeExtensionForceInstall "yes"
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
CloseApplications=no
RestartApplications=no
UsePreviousAppDir=no
UninstallRestartComputer=no

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："

[Files]
; A push transaction must observe every replacement before committing. Do not queue
; locked files for reboot; let Setup fail so the transaction can restore the prior tree.
Source: "{#PublishDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#UpdateHelperDirectory}\VeyonCampus.UpdateHelper.exe"; DestDir: "{autopf}\Veyon Campus\Updater\Student\{#AppVersion}"; Flags: ignoreversion
Source: "{#UpdateHelperDirectory}\VeyonCampus.UpdateHelper.exe"; DestDir: "{tmp}"; Flags: dontcopy
Source: "{#DotNetRuntimeInstallerPath}"; DestDir: "{tmp}"; Flags: dontcopy
Source: "EdgePolicyExecutor.task.xml"; DestDir: "{tmp}"; Flags: dontcopy

[Registry]
Root: HKLM64; Subkey: "SOFTWARE\Microsoft\Edge\NativeMessagingHosts\fun.kidscode.veyoncampus.policy"; ValueType: string; ValueName: ""; ValueData: "{app}\EdgeNativeHost\fun.kidscode.veyoncampus.policy.json"; Check: IsEdgeExtensionConfigured; Flags: uninsdeletevalue

[Icons]
Name: "{group}\Veyon Campus 学生部署工具"; Filename: "{app}\VeyonCampus.StudentSetup.exe"
Name: "{group}\Veyon Campus 学生课堂助手"; Filename: "{app}\StudentCompanion\VeyonCampus.StudentCompanion.exe"
Name: "{commonstartup}\Veyon Campus 学生课堂助手"; Filename: "{app}\StudentCompanion\VeyonCampus.StudentCompanion.exe"; Parameters: "--startup"; WorkingDir: "{app}\StudentCompanion"
Name: "{commonstartup}\Veyon Campus Edge 策略执行器"; Filename: "{app}\EdgePolicyExecutor\VeyonCampus.EdgePolicyExecutor.exe"; Parameters: "--startup"; WorkingDir: "{app}\EdgePolicyExecutor"
Name: "{autodesktop}\Veyon Campus 学生部署工具"; Filename: "{app}\VeyonCampus.StudentSetup.exe"; Tasks: desktopicon

[Run]
; Interactive programs must use the original non-elevated user token; the all-users Startup links cover later logins.
Filename: "{app}\EdgePolicyExecutor\VeyonCampus.EdgePolicyExecutor.exe"; Parameters: "--startup"; WorkingDir: "{app}\EdgePolicyExecutor"; Flags: nowait runasoriginaluser skipifsilent
Filename: "{app}\StudentCompanion\VeyonCampus.StudentCompanion.exe"; Parameters: "--startup"; WorkingDir: "{app}\StudentCompanion"; Flags: nowait runasoriginaluser skipifsilent
; StudentSetup requires elevation; run it as the current (elevated Setup) user, never the original unelevated token.
Filename: "{app}\VeyonCampus.StudentSetup.exe"; Description: "启动学生部署工具"; Flags: nowait postinstall skipifsilent runascurrentuser

[UninstallDelete]
Type: filesandordirs; Name: "{autopf}\Veyon Campus\Updater\Student"
Type: filesandordirs; Name: "{autopf}\Veyon Campus\UpdateRecovery\Student"
Type: filesandordirs; Name: "{app}\Worker\Staging"
Type: files; Name: "{app}\EdgeNativeHost\fun.kidscode.veyoncampus.policy.json"

[Code]
const
  CompiledEdgeExtensionId = '{#EdgeExtensionId}';
  CompiledEdgeExtensionForceInstall = '{#EdgeExtensionForceInstall}';
  EdgePolicyExecutorTaskName = 'Veyon Campus Edge Policy Executor';
  EdgeForceListRegistryKey = 'SOFTWARE\Policies\Microsoft\Edge\ExtensionInstallForcelist';
  EdgeForceListMarkerKey = 'SOFTWARE\VeyonCampus\StudentSetup\EdgeRealtime';
  EdgeStoreUpdateUrl = 'https://edge.microsoft.com/extensionwebstorebase/v1/crx';
  StudentUpdateLeaseName = 'Local\VeyonCampus_StudentSetup_InstallUpdate';
  WaitObject0 = 0;
  WaitTimeout = 258;

var
  UpdateProgressForm: TSetupForm;
  UpdateProgressText: TNewStaticText;
  UpdateProgressStatus: TNewStaticText;
  UpdateProgressBar: TNewProgressBar;
  UpdateLeaseHandle: THandle;

function CreateSemaphoreW(lpSemaphoreAttributes: LongWord; lInitialCount, lMaximumCount: LongInt;
  lpName: String): THandle;
  external 'CreateSemaphoreW@kernel32.dll stdcall';

function WaitForSingleObject(hHandle: THandle; dwMilliseconds: LongWord): LongWord;
  external 'WaitForSingleObject@kernel32.dll stdcall';

function ReleaseSemaphore(hSemaphore: THandle; lReleaseCount: LongInt;
  var lpPreviousCount: LongInt): LongBool;
  external 'ReleaseSemaphore@kernel32.dll stdcall';

function CloseHandle(hObject: THandle): LongBool;
  external 'CloseHandle@kernel32.dll stdcall';

function IsSelfUpdateSetup(): Boolean;
var
  SetupPath, UpdateStagingPath: String;
begin
  try
    SetupPath := LowerCase(ExpandConstant('{srcexe}'));
    UpdateStagingPath := LowerCase(ExpandConstant('{autopf}\Veyon Campus\UpdateStaging') + '\');
    Result := Pos(UpdateStagingPath, SetupPath) = 1;
    if Result and (CompareText(GetEnv('VEYONCAMPUS_SELF_UPDATE'), '1') <> 0) then
      Log('Recognized a staged self-update from a legacy updater without the self-update marker.');
  except
    Log('Could not resolve the self-update source executable; using the normal student install/update lease.');
    Result := False;
  end;
end;

function AcquireInstallUpdateLease(): Boolean;
var
  WaitResult: LongWord;
begin
  if IsSelfUpdateSetup() then
  begin
    Result := True;
    Exit;
  end;

  UpdateLeaseHandle := CreateSemaphoreW(0, 1, 1, StudentUpdateLeaseName);
  if UpdateLeaseHandle = 0 then
  begin
    Log('Could not open student install/update semaphore.');
    Result := False;
    Exit;
  end;

  WaitResult := WaitForSingleObject(UpdateLeaseHandle, 0);
  Result := WaitResult = WaitObject0;
  if not Result then
  begin
    Log('Student install/update semaphore is already held or could not be acquired.');
    CloseHandle(UpdateLeaseHandle);
    UpdateLeaseHandle := 0;
  end;
end;

procedure ReleaseInstallUpdateLease;
var
  PreviousCount: LongInt;
begin
  if UpdateLeaseHandle = 0 then Exit;
  if not ReleaseSemaphore(UpdateLeaseHandle, 1, PreviousCount) then
    Log('Could not release student install/update semaphore.');
  CloseHandle(UpdateLeaseHandle);
  UpdateLeaseHandle := 0;
end;

function InitializeSetup(): Boolean;
begin
  Result := AcquireInstallUpdateLease();
  if not Result then
    MsgBox('另一个学生端安装或应用内更新正在进行。请等待它结束后重新运行安装包。', mbError, MB_OK);
end;

procedure DeinitializeSetup;
begin
  ReleaseInstallUpdateLease();
end;

function IsVerySilentSetup(): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if (CompareText(ParamStr(I), '/VERYSILENT') = 0) or
       (CompareText(ParamStr(I), '-VERYSILENT') = 0) then
    begin
      Result := True;
      Exit;
    end;
end;

function StartUpdateProgressWindow(const RoleName: String): Boolean;
var
  ProductName: String;
begin
  Result := False;
  if RoleName = 'TeacherConsole' then ProductName := '教师端'
  else ProductName := '学生部署工具';
  try
    UpdateProgressForm := CreateCustomForm(ScaleX(500), ScaleY(162), False, False);
    UpdateProgressForm.Caption := 'Veyon Campus ' + ProductName + '更新';
    UpdateProgressForm.BorderStyle := bsDialog;
    UpdateProgressForm.BorderIcons := [];
    UpdateProgressForm.FormStyle := fsStayOnTop;
    UpdateProgressForm.Position := poScreenCenter;

    UpdateProgressText := TNewStaticText.Create(UpdateProgressForm);
    UpdateProgressText.Parent := UpdateProgressForm;
    UpdateProgressText.Caption := '正在更新 Veyon Campus ' + ProductName;
    UpdateProgressText.Left := ScaleX(24);
    UpdateProgressText.Top := ScaleY(20);
    UpdateProgressText.Width := ScaleX(452);
    UpdateProgressText.Height := ScaleY(27);

    UpdateProgressStatus := TNewStaticText.Create(UpdateProgressForm);
    UpdateProgressStatus.Parent := UpdateProgressForm;
    UpdateProgressStatus.Caption := '正在安装更新，请不要关闭电脑。';
    UpdateProgressStatus.Left := ScaleX(24);
    UpdateProgressStatus.Top := ScaleY(56);
    UpdateProgressStatus.Width := ScaleX(452);
    UpdateProgressStatus.Height := ScaleY(36);

    UpdateProgressBar := TNewProgressBar.Create(UpdateProgressForm);
    UpdateProgressBar.Parent := UpdateProgressForm;
    UpdateProgressBar.Left := ScaleX(24);
    UpdateProgressBar.Top := ScaleY(99);
    UpdateProgressBar.Width := ScaleX(452);
    UpdateProgressBar.Height := ScaleY(20);
    UpdateProgressBar.Min := 0;
    UpdateProgressBar.Max := 100;
    UpdateProgressBar.Style := npbstMarquee;

    UpdateProgressForm.Show;
    Result := True;
  except
    if UpdateProgressForm <> nil then
    begin
      UpdateProgressForm.Free;
      UpdateProgressForm := nil;
    end;
    UpdateProgressText := nil;
    UpdateProgressStatus := nil;
    UpdateProgressBar := nil;
  end;
end;

procedure CurInstallProgressChanged(CurProgress, MaxProgress: Integer);
var
  Percent: Integer;
begin
  if UpdateProgressBar = nil then Exit;
  if MaxProgress <= 0 then Exit;
  Percent := Round((CurProgress / MaxProgress) * 100);
  if Percent < 0 then Percent := 0;
  if Percent > 100 then Percent := 100;
  UpdateProgressBar.Style := npbstNormal;
  UpdateProgressBar.Position := Percent;
  UpdateProgressStatus.Caption := '正在安装更新…… ' + IntToStr(Percent) + '%';
end;

function RemoveEdgePolicyExecutorTask(): Boolean; forward;

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
    Log('Student runtime prerequisite failed: ' + GetExceptionMessage);
    Result := '无法安装 Microsoft ' + RuntimeDescription + ' ' + '{#RuntimeVersion}' +
      '。安装已停止，请检查系统空间和管理员权限后重试。';
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  ProcessArguments: String;
begin
  Result := '';
  try
    if not RemoveEdgePolicyExecutorTask() then
    begin
      Result := '无法暂停 Edge 策略执行器的登录任务。请重试安装或检查 Windows 任务计划程序。';
      Exit;
    end;
    Result := InstallRuntimeInstaller('dotnet-runtime-{#RuntimeVersion}-win-x64.exe',
      'Microsoft.NETCore.App', '.NET Runtime', NeedsRestart);
    if Result <> '' then Exit;
    ExtractTemporaryFile('VeyonCampus.UpdateHelper.exe');
    ProcessArguments := '--stop-installed-role-processes StudentSetup';
    if IsSelfUpdateSetup() then ProcessArguments := ProcessArguments + ' --preserve-update-helper';
    if not Exec(ExpandConstant('{tmp}\VeyonCampus.UpdateHelper.exe'),
         ProcessArguments, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    begin
      Log('Could not start student process cleanup helper.');
      Result := '无法启动自动关闭学生端程序的清理助手。安装已停止，请重启 Windows 后重新运行安装包。';
    end
    else if ResultCode = 2 then
    begin
      Log('A student update installer is already running.');
      Result := '检测到学生端更新安装器正在运行。请等待 Veyon Campus 学生端更新进度完成后再重试；如果看不到进度窗口，请重启 Windows 后再运行安装包。';
    end
    else if ResultCode <> 0 then
    begin
      Log('Student process cleanup helper failed with exit code ' + IntToStr(ResultCode) + '.');
      Result := '安装器已尝试自动关闭学生端主程序和后台进程，但未能安全完成。安装已停止，请重启 Windows 后重新运行安装包。';
    end;
    if (Result = '') and IsSelfUpdateSetup() and IsVerySilentSetup() and
       (not StartUpdateProgressWindow('StudentSetup')) then
    begin
      Result := '无法显示学生端更新进度窗口；安装已停止，请手动运行学生端安装包。';
      Log('Student update progress window failed to start.');
    end;
  except
    Log('Student process preflight failed: ' + GetExceptionMessage);
    Result := '自动核验并关闭学生端后台进程时发生错误。安装已停止，请重启 Windows 后重新运行安装包。';
  end;
end;

function XmlEscapePath(Value: String): String;
var
  Index: Integer;
begin
  Result := '';
  for Index := 1 to Length(Value) do
  begin
    if Value[Index] = '&' then
      Result := Result + '&amp;'
    else if Value[Index] = '<' then
      Result := Result + '&lt;'
    else if Value[Index] = '>' then
      Result := Result + '&gt;'
    else if Value[Index] = '"' then
      Result := Result + '&quot;'
    else if Value[Index] = '''' then
      Result := Result + '&apos;'
    else
      Result := Result + Value[Index];
  end;
end;

function ReplaceAll(Source, Search, Replacement: String): String;
var
  Position: Integer;
begin
  Result := Source;
  Position := Pos(Search, Result);
  while Position > 0 do
  begin
    Delete(Result, Position, Length(Search));
    Insert(Replacement, Result, Position);
    Position := Pos(Search, Result);
  end;
end;

function RemoveEdgePolicyExecutorTask(): Boolean;
var
  ResultCode: Integer;
  QueryStarted: Boolean;
begin
  Result := False;
  QueryStarted := Exec(ExpandConstant('{sys}\schtasks.exe'),
    '/Query /TN "' + EdgePolicyExecutorTaskName + '"', '', SW_HIDE,
    ewWaitUntilTerminated, ResultCode);
  if not QueryStarted then Exit;
  if ResultCode <> 0 then
  begin
    Log('Edge policy executor scheduled task is not registered.');
    Result := True;
    Exit;
  end;

  Exec(ExpandConstant('{sys}\schtasks.exe'),
    '/End /TN "' + EdgePolicyExecutorTaskName + '"', '', SW_HIDE,
    ewWaitUntilTerminated, ResultCode);
  if not Exec(ExpandConstant('{sys}\schtasks.exe'),
       '/Delete /F /TN "' + EdgePolicyExecutorTaskName + '"', '', SW_HIDE,
       ewWaitUntilTerminated, ResultCode) then Exit;
  Result := ResultCode = 0;
end;

function RegisterEdgePolicyExecutorTask(): Boolean;
var
  TemplatePath: String;
  RuntimeTaskPath: String;
  TaskXml: AnsiString;
  TaskXmlText: String;
  ExecutorDirectory: String;
  ExecutorPath: String;
  ResultCode: Integer;
begin
  Result := False;
  RuntimeTaskPath := '';
  try
    ExtractTemporaryFile('EdgePolicyExecutor.task.xml');
    TemplatePath := ExpandConstant('{tmp}\EdgePolicyExecutor.task.xml');
    RuntimeTaskPath := ExpandConstant('{tmp}\EdgePolicyExecutor-installed.task.xml');
    if not LoadStringFromFile(TemplatePath, TaskXml) then Exit;

    TaskXmlText := TaskXml;
    ExecutorDirectory := ExpandConstant('{app}\EdgePolicyExecutor');
    ExecutorPath := ExecutorDirectory + '\VeyonCampus.EdgePolicyExecutor.exe';
    TaskXmlText := ReplaceAll(TaskXmlText, '__EXECUTOR_COMMAND__', XmlEscapePath(ExecutorPath));
    TaskXmlText := ReplaceAll(TaskXmlText, '__EXECUTOR_WORKING_DIR__', XmlEscapePath(ExecutorDirectory));
    TaskXml := AnsiString(TaskXmlText);
    if Pos('__EXECUTOR_', TaskXmlText) > 0 then Exit;
    if not SaveStringToFile(RuntimeTaskPath, TaskXml, False) then Exit;

    if not Exec(ExpandConstant('{sys}\schtasks.exe'),
         '/Create /F /TN "' + EdgePolicyExecutorTaskName + '" /XML "' + RuntimeTaskPath + '"',
         '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then Exit;
    Result := ResultCode = 0;
  except
    Log('Edge policy executor scheduled task registration failed: ' + GetExceptionMessage);
    Result := False;
  end;
  if RuntimeTaskPath <> '' then DeleteFile(RuntimeTaskPath);
end;

function IsEdgeExtensionConfigured(): Boolean;
begin
  Result := CompiledEdgeExtensionId <> '';
end;

function ShouldForceInstallEdgeExtension(): Boolean;
begin
  Result := IsEdgeExtensionConfigured() and
    (CompareText(CompiledEdgeExtensionForceInstall, 'yes') = 0);
end;

function JsonEscapePath(Value: String): String;
var
  Index: Integer;
begin
  Result := '';
  for Index := 1 to Length(Value) do
  begin
    if Value[Index] = '\' then
      Result := Result + '\\'
    else if Value[Index] = '"' then
      Result := Result + '\"'
    else
      Result := Result + Value[Index];
  end;
end;

function EdgeForceListValue: String;
begin
  Result := CompiledEdgeExtensionId + ';' + EdgeStoreUpdateUrl;
end;

procedure RemoveOwnedEdgeExtensionPolicy;
var
  Slot: String;
  OwnedValue: String;
  CurrentValue: String;
begin
  if RegQueryStringValue(HKLM64, EdgeForceListMarkerKey, 'PolicySlot', Slot) and
     RegQueryStringValue(HKLM64, EdgeForceListMarkerKey, 'PolicyValue', OwnedValue) then
  begin
    if RegQueryStringValue(HKLM64, EdgeForceListRegistryKey, Slot, CurrentValue) and
       (CurrentValue = OwnedValue) then
      RegDeleteValue(HKLM64, EdgeForceListRegistryKey, Slot);
  end;
  RegDeleteKeyIncludingSubkeys(HKLM64, EdgeForceListMarkerKey);
end;

procedure RegisterEdgeExtensionPolicy;
var
  Index: Integer;
  Slot: String;
  ExistingValue: String;
  OwnedValue: String;
begin
  if not ShouldForceInstallEdgeExtension() then Exit;
  OwnedValue := EdgeForceListValue();
  if RegQueryStringValue(HKLM64, EdgeForceListMarkerKey, 'PolicyValue', ExistingValue) and
     (ExistingValue <> OwnedValue) then
    RemoveOwnedEdgeExtensionPolicy();

  for Index := 1 to 999 do
  begin
    Slot := IntToStr(Index);
    if RegQueryStringValue(HKLM64, EdgeForceListRegistryKey, Slot, ExistingValue) then
    begin
      if ExistingValue = OwnedValue then Exit;
    end
    else if not RegValueExists(HKLM64, EdgeForceListRegistryKey, Slot) then
    begin
      if not RegWriteStringValue(HKLM64, EdgeForceListMarkerKey, 'PolicySlot', Slot) or
         not RegWriteStringValue(HKLM64, EdgeForceListMarkerKey, 'PolicyValue', OwnedValue) then
        RaiseException('无法保存 Edge 扩展安装策略的归属记录。');
      if not RegWriteStringValue(HKLM64, EdgeForceListRegistryKey, Slot, OwnedValue) then
      begin
        RemoveOwnedEdgeExtensionPolicy();
        RaiseException('无法写入 Edge Add-ons 强制安装策略。');
      end;
      Exit;
    end;
  end;
  RaiseException('Edge 扩展安装策略没有可用的数值位置；没有覆盖现有扩展策略。');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  HostPath: String;
  ManifestPath: String;
  ManifestText: String;
begin
  if CurStep = ssPostInstall then
  begin
    if not RegisterEdgePolicyExecutorTask() then
      Log('Edge policy executor crash recovery task could not be installed; the all-users Startup shortcut remains as a fallback.');
    if IsEdgeExtensionConfigured() then
    begin
      HostPath := JsonEscapePath(ExpandConstant('{app}\EdgeNativeHost\VeyonCampus.EdgeNativeHost.exe'));
      ManifestPath := ExpandConstant('{app}\EdgeNativeHost\fun.kidscode.veyoncampus.policy.json');
      ManifestText := '{"name":"fun.kidscode.veyoncampus.policy","description":"Veyon Campus Edge real-time website policy bridge","path":"' +
        HostPath + '","type":"stdio","allowed_origins":["chrome-extension://' + CompiledEdgeExtensionId + '/"]}';
      if not SaveStringToFile(ManifestPath, ManifestText, False) then
        RaiseException('无法写入 Edge Native Messaging Host 注册清单。');
      if ShouldForceInstallEdgeExtension() then
        RegisterEdgeExtensionPolicy()
      else
        RemoveOwnedEdgeExtensionPolicy();
    end;
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpFinished then
    WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10 + #13#10 +
      '如果网站实时策略推送尚未生效，请注销当前 Windows 用户并重新登录；无需重启电脑。';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    RemoveOwnedEdgeExtensionPolicy();
    if not RemoveEdgePolicyExecutorTask() then
      Log('Could not remove Edge policy executor scheduled task during uninstall.');
  end;
  if CurUninstallStep = usPostUninstall then
  begin
    RemoveDir(ExpandConstant('{autopf}\Veyon Campus\Updater\Student'));
    RemoveDir(ExpandConstant('{autopf}\Veyon Campus\Updater'));
    RemoveDir(ExpandConstant('{autopf}\Veyon Campus\UpdateRecovery\Student'));
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
    if not AcquireInstallUpdateLease() then
    begin
      SuppressibleMsgBox('另一个学生端安装或应用内更新正在进行。请等待它结束后重新运行卸载程序。',
        mbError, MB_OK, IDOK);
      Result := False;
      Exit;
    end;
    UserChoice := SuppressibleMsgBox('卸载学生部署工具。是否保留本机配置？' + #13#10 + #13#10 +
         '是：保留本机 Veyon Campus 配置和 Veyon 地点、名单及认证密钥，方便重新安装。' + #13#10 +
         '否：清除本机相关配置；同一台电脑上的教师端配置也会一并清除。' + #13#10 +
         '两种选择都会停止学生端后台 Agent、恢复已部署限制并移除 Edge 启动集成。' + #13#10 +
         '取消：不卸载。', mbConfirmation, MB_YESNOCANCEL or MB_DEFBUTTON1, IDYES);
    if UserChoice = IDCANCEL then
    begin
      Result := False;
      Exit;
    end;
    KeepConfiguration := UserChoice = IDYES;
    if not RemoveEdgePolicyExecutorTask() then
    begin
      Log('Student uninstall stopped: could not remove Edge policy executor scheduled task.');
      SuppressibleMsgBox('无法停止或移除 Edge 策略执行器的登录任务。为避免卸载后残留后台进程，卸载已停止。',
        mbError, MB_OK, IDOK);
      Result := False;
      Exit;
    end;
    DiagnosticPath := ExpandConstant('{tmp}\VeyonCampus-Student-Uninstall.txt');
    DeleteFile(DiagnosticPath);

    if not FileExists(ExpandConstant('{app}\VeyonCampus.StudentSetup.exe')) then
    begin
      Log('Student uninstall preflight stopped: cleanup executable is missing.');
      SuppressibleMsgBox('找不到学生端清理程序。请先修复或覆盖安装学生端，再重新卸载。',
        mbError, MB_OK, IDOK);
      Result := False;
      Exit;
    end;

    Log('Starting Veyon Campus student cleanup helper.');
    UninstallArguments := '--uninstall-student-runtime "' + DiagnosticPath + '"';
    if KeepConfiguration then UninstallArguments := UninstallArguments + ' --preserve-config';
    if not Exec(ExpandConstant('{app}\VeyonCampus.StudentSetup.exe'),
         UninstallArguments, ExpandConstant('{app}'),
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
    SuppressibleMsgBox('学生端后台 Agent 或策略未能安全清理，已停止卸载并保留安装文件。' + #13#10 + #13#10 +
      FailureDetail + #13#10 + #13#10 + '请在学生部署工具中查看“管理员维护”记录，解决后重试。',
      mbError, MB_OK, IDOK);
    Result := False;
  except
    Log('Student uninstaller preflight exception: ' + GetExceptionMessage);
    SuppressibleMsgBox('学生端卸载预检查异常，已保留安装文件。卸载日志会记录异常详情，请检查后重试。',
      mbError, MB_OK, IDOK);
    Result := False;
  end;
end;

procedure DeinitializeUninstall;
begin
  ReleaseInstallUpdateLease();
end;
