#ifndef AppVersion
  #error AppVersion must be provided by package-windows-offline.ps1
#endif
#ifndef PublishDirectory
  #error PublishDirectory must be provided by package-windows-offline.ps1
#endif
#ifndef UpdateHelperDirectory
  #error UpdateHelperDirectory must be provided by package-windows-offline.ps1
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
; If Windows still has a student-side file open after Restart Manager closes apps,
; defer replacement until reboot instead of aborting the entire upgrade.
Source: "{#PublishDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs restartreplace
Source: "{#UpdateHelperDirectory}\VeyonCampus.UpdateHelper.exe"; DestDir: "{autopf}\Veyon Campus\Updater\Student\{#AppVersion}"; Flags: ignoreversion
Source: "{#UpdateHelperDirectory}\VeyonCampus.UpdateHelper.exe"; DestDir: "{tmp}"; Flags: dontcopy
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
Filename: "{app}\VeyonCampus.StudentSetup.exe"; Description: "启动学生部署工具"; Flags: nowait postinstall skipifsilent runasoriginaluser

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

function RemoveEdgePolicyExecutorTask(): Boolean; forward;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  try
    if not RemoveEdgePolicyExecutorTask() then
    begin
      Result := '无法暂停 Edge 策略执行器的登录任务。请重试安装或检查 Windows 任务计划程序。';
      Exit;
    end;
    ExtractTemporaryFile('VeyonCampus.UpdateHelper.exe');
    if (not Exec(ExpandConstant('{tmp}\VeyonCampus.UpdateHelper.exe'),
         '--stop-installed-role-processes StudentSetup', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)) or
       (ResultCode <> 0) then
      Result := '无法停止学生部署工具、课堂助手或其更新辅助进程。请关闭正在运行的 Veyon Campus 学生端后重试。';
  except
    Log('Student process preflight failed: ' + GetExceptionMessage);
    Result := '无法核对并关闭旧版学生端进程；安装已停止。请先结束 Veyon Campus 学生端进程后重试。';
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
