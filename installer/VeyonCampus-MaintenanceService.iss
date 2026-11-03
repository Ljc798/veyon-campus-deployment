#ifndef AppVersion
  #error AppVersion must be provided by package-maintenance-service.ps1
#endif
#ifndef PublishDirectory
  #error PublishDirectory must be provided by package-maintenance-service.ps1
#endif

[Setup]
AppId={{92106AC9-4F0D-44B4-A901-7B7439479D5E}
AppName=Veyon Campus Maintenance Service
AppVersion={#AppVersion}
AppVerName=Veyon Campus Maintenance Service {#AppVersion}
AppPublisher=Veyon Campus
DefaultDirName={autopf}\Veyon Campus\MaintenanceService\Versions\{#AppVersion}
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Uninstallable=no
OutputDir=.
OutputBaseFilename=VeyonCampus
SetupIconFile={#SourcePath + "\..\src\VeyonCampus.App\Assets\veyon-campus.ico"}
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
SetupLogging=yes
CloseApplications=no
RestartApplications=no
UsePreviousAppDir=no
UninstallRestartComputer=no

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Files]
Source: "{#PublishDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Code]
const
  ServiceImagePathRegistryKey = 'SYSTEM\CurrentControlSet\Services\VeyonCampusMaintenanceService';
  ServiceExecutableName = 'VeyonCampus.MaintenanceService.exe';

var
  ServiceInitiallyInstalled: Boolean;
  ServiceWasStopped: Boolean;
  ServiceSetupCompleted: Boolean;
  PreviousServiceExecutable: String;

function RunMaintenanceServiceControlAt(const ExecutablePath: String; const Parameters: String;
  var ResultCode: Integer): Boolean;
begin
  Result := Exec(ExecutablePath, Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function RunMaintenanceServiceControl(const Parameters: String; var ResultCode: Integer): Boolean;
begin
  Result := RunMaintenanceServiceControlAt(ExpandConstant('{app}\' + ServiceExecutableName),
    Parameters, ResultCode);
end;

function NormalizeServiceImagePath(Value: String): String;
var
  ClosingQuote: Integer;
begin
  Result := Trim(Value);
  if (Length(Result) > 1) and (Result[1] = '"') then
  begin
    Delete(Result, 1, 1);
    ClosingQuote := Pos('"', Result);
    if ClosingQuote > 0 then Delete(Result, ClosingQuote, Length(Result));
  end;
  Result := ExpandFileName(Result);
end;

function IsKnownMaintenanceImagePath(Value: String): Boolean;
var
  Candidate: String;
  CurrentPath: String;
  VersionsPrefix: String;
  LegacyPath: String;
begin
  Result := False;
  Candidate := LowerCase(NormalizeServiceImagePath(Value));
  CurrentPath := LowerCase(ExpandConstant('{autopf}\Veyon Campus\MaintenanceService\Current\' +
    ServiceExecutableName));
  LegacyPath := LowerCase(ExpandConstant('{autopf}\Veyon Campus\Student\MaintenanceService\' +
    ServiceExecutableName));
  if (Candidate = CurrentPath) or (Candidate = LegacyPath) then
  begin
    Result := True;
    Exit;
  end;
  VersionsPrefix := LowerCase(ExpandConstant('{autopf}\Veyon Campus\MaintenanceService\Versions\'));
  Result := (Pos(VersionsPrefix, Candidate) = 1) and
    (Pos('\' + LowerCase(ServiceExecutableName), Candidate) = Length(Candidate) -
      Length(ServiceExecutableName));
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  ServiceImagePath: String;
begin
  Result := '';
  NeedsRestart := False;
  ServiceInitiallyInstalled := False;
  ServiceWasStopped := False;
  ServiceSetupCompleted := False;
  PreviousServiceExecutable := '';

  if not RegQueryStringValue(HKLM64, ServiceImagePathRegistryKey, 'ImagePath', ServiceImagePath) then
    Exit;

  PreviousServiceExecutable := NormalizeServiceImagePath(ServiceImagePath);
  if not IsKnownMaintenanceImagePath(ServiceImagePath) then
  begin
    Result := 'Maintenance 服务注册路径不属于受管版本目录；安装已停止以保留现有服务。';
    Exit;
  end;
  if not FileExists(PreviousServiceExecutable) then
  begin
    Result := 'Maintenance 服务注册的旧版本文件不存在；安装已停止以保留恢复路径。';
    Exit;
  end;

  if not RunMaintenanceServiceControlAt(PreviousServiceExecutable, '--query-service', ResultCode) then
  begin
    Result := '无法检查 Maintenance 服务状态；安装已停止。';
    Exit;
  end;
  if ResultCode = 3 then Exit;
  if ResultCode <> 0 then
  begin
    Result := 'Maintenance 服务状态未知；安装已停止，请先修复服务注册。';
    Exit;
  end;

  ServiceInitiallyInstalled := True;
  if not RunMaintenanceServiceControlAt(PreviousServiceExecutable, '--stop-service', ResultCode) or
     (ResultCode <> 0) then
  begin
    Result := '无法安全停止 Maintenance 服务；安装已停止。';
    Exit;
  end;
  ServiceWasStopped := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    if not RunMaintenanceServiceControl('--install-and-start-service', ResultCode) or (ResultCode <> 0) then
      RaiseException('无法切换并启动签名包中的 Maintenance 服务版本；请保留安装日志并由更新事务执行回滚。');
    ServiceSetupCompleted := True;
    ServiceWasStopped := False;
  end;
end;

procedure DeinitializeSetup;
var
  ResultCode: Integer;
begin
  if ServiceInitiallyInstalled and ServiceWasStopped and not ServiceSetupCompleted then
  begin
    if FileExists(PreviousServiceExecutable) then
    begin
      if not Exec(PreviousServiceExecutable, '--start-service', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or
         (ResultCode <> 0) then
        Log('Could not restart the previous Maintenance service after Setup failure.');
    end
    else
      Log('The previous Maintenance service executable is unavailable; external UpdateHelper recovery is required.');
  end;
end;
