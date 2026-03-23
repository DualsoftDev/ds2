; DSPilot Inno Setup Script
; Self-contained installer with Windows Service registration

#define MyAppName "DSPilot"
#define MyAppExePath "..\publish\" + MyAppName + ".exe"
#define MyAppVersion GetVersionNumbersString(MyAppExePath)
#define MyAppPublisher "DualSoft"
#define MyAppURL "https://dualsoft.co.kr"
#define MyAppExeName "DSPilot.exe"
#define MyServiceName "DSPilotService"
#define MyServiceDisplayName "DSPilot Service"
#define MyServiceDescription "DSPilot - PLC Monitoring & Analysis Service"
#define MyDefaultPort "80"
#define MyTrayExeName "DSPilot.Tray.exe"

[Setup]
AppId={{E8A3F2B1-7C4D-4E5F-9A1B-3D6E8F0C2A4B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppPublisher}\{#MyAppName}
DefaultGroupName={#MyAppName}
OutputDir=..\Output
OutputBaseFilename=DSPilot_Setup_{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; Publish output (self-contained, all dependencies included)
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Tray application (self-contained)
Source: "..\publish-tray\*"; DestDir: "{app}\Tray"; Flags: ignoreversion recursesubdirs createallsubdirs
; Icon file for shortcuts
Source: "..\DSPilot\DSPilot.ico"; DestDir: "{app}"; Flags: ignoreversion
; AASX data file (placed in parent directory: ../DsCSV_0318_C.aasx)
Source: "..\DsCSV_0318_C.aasx"; DestDir: "{app}\.."; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{code:GetAppURL}"
Name: "{group}\{#MyAppName} 서비스 시작"; Filename: "{sys}\sc.exe"; Parameters: "start {#MyServiceName}"
Name: "{group}\{#MyAppName} 서비스 중지"; Filename: "{sys}\sc.exe"; Parameters: "stop {#MyServiceName}"
Name: "{group}\{#MyAppName} 제거"; Filename: "{uninstallexe}"
; 바탕화면 바로가기는 [Code] 섹션에서 .url 파일로 직접 생성 (아이콘 포함)
; 시작 프로그램에 트레이 아이콘 등록
Name: "{userstartup}\{#MyAppName} Tray"; Filename: "{app}\Tray\{#MyTrayExeName}"

[Run]
; Install and configure the Windows Service (no --urls, port is in appsettings.json)
Filename: "{sys}\sc.exe"; \
  Parameters: "create {#MyServiceName} binPath=""{app}\{#MyAppExeName}"" start=auto DisplayName=""{#MyServiceDisplayName}"""; \
  Flags: runhidden waituntilterminated; \
  StatusMsg: "서비스 등록 중..."

; Set service description
Filename: "{sys}\sc.exe"; \
  Parameters: "description {#MyServiceName} ""{#MyServiceDescription}"""; \
  Flags: runhidden waituntilterminated; \
  StatusMsg: "서비스 설명 설정 중..."

; Configure failure recovery: restart after 10s on 1st, 2nd, 3rd failure. Reset counter after 1 day.
Filename: "{sys}\sc.exe"; \
  Parameters: "failure {#MyServiceName} reset=86400 actions=restart/10000/restart/10000/restart/30000"; \
  Flags: runhidden waituntilterminated; \
  StatusMsg: "서비스 복구 옵션 설정 중..."

; Add Windows Firewall rule for external access
Filename: "{sys}\netsh.exe"; \
  Parameters: "advfirewall firewall add rule name=""DSPilot Web Service"" dir=in action=allow protocol=tcp localport={code:GetPort}"; \
  Flags: runhidden waituntilterminated; \
  StatusMsg: "방화벽 규칙 추가 중..."

; Start the service
Filename: "{sys}\sc.exe"; \
  Parameters: "start {#MyServiceName}"; \
  Flags: runhidden waituntilterminated; \
  StatusMsg: "서비스 시작 중..."

; Launch tray icon after install
Filename: "{app}\Tray\{#MyTrayExeName}"; \
  Description: "DSPilot 트레이 아이콘 실행"; \
  Flags: postinstall nowait skipifsilent runascurrentuser

; Open browser after install (optional)
Filename: "{code:GetAppURL}"; \
  Description: "DSPilot 웹 대시보드 열기"; \
  Flags: postinstall shellexec nowait skipifsilent unchecked

[UninstallDelete]
Type: files; Name: "{autodesktop}\{#MyAppName}.url"

[UninstallRun]
; Kill tray app before uninstall
Filename: "{cmd}"; \
  Parameters: "/c taskkill /F /IM {#MyTrayExeName} >nul 2>&1"; \
  Flags: runhidden waituntilterminated; \
  RunOnceId: "KillTray"

; Stop the service before uninstall
Filename: "{sys}\sc.exe"; \
  Parameters: "stop {#MyServiceName}"; \
  Flags: runhidden waituntilterminated; \
  RunOnceId: "StopService"

; Wait for service to stop
Filename: "{cmd}"; \
  Parameters: "/c timeout /t 3 /nobreak >nul"; \
  Flags: runhidden waituntilterminated; \
  RunOnceId: "WaitStop"

; Delete the service
Filename: "{sys}\sc.exe"; \
  Parameters: "delete {#MyServiceName}"; \
  Flags: runhidden waituntilterminated; \
  RunOnceId: "DeleteService"

; Remove firewall rule
Filename: "{sys}\netsh.exe"; \
  Parameters: "advfirewall firewall delete rule name=""DSPilot Web Service"""; \
  Flags: runhidden waituntilterminated; \
  RunOnceId: "DeleteFirewall"

[Code]
var
  PortPage: TInputQueryWizardPage;

procedure InitializeWizard();
begin
  PortPage := CreateInputQueryPage(wpSelectDir,
    '포트 설정', '웹 서비스 포트를 설정합니다.',
    'DSPilot 웹 서비스가 사용할 포트 번호를 입력하세요.' + #13#10 +
    '기본값: {#MyDefaultPort} (포트 80은 URL에서 포트 번호 생략 가능)');
  PortPage.Add('포트 번호:', False);
  PortPage.Values[0] := '{#MyDefaultPort}';
end;

function GetPort(Param: String): String;
begin
  Result := PortPage.Values[0];
  if Result = '' then
    Result := '{#MyDefaultPort}';
end;

function GetAppURL(Param: String): String;
var
  Port: String;
begin
  Port := GetPort('');
  if Port = '80' then
    Result := 'http://localhost'
  else
    Result := 'http://localhost:' + Port;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Port: String;
  PortNum: Integer;
begin
  Result := True;
  if CurPageID = PortPage.ID then
  begin
    Port := PortPage.Values[0];
    if (Port = '') then
    begin
      PortPage.Values[0] := '{#MyDefaultPort}';
      Result := True;
      Exit;
    end;
    PortNum := StrToIntDef(Port, -1);
    if (PortNum < 1) or (PortNum > 65535) then
    begin
      MsgBox('포트 번호는 1~65535 사이의 숫자여야 합니다.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

// Write port to appsettings.Production.json after files are installed
// ASP.NET Core automatically loads this and overrides appsettings.json
procedure CurStepChanged(CurStep: TSetupStep);
var
  Port: String;
  UrlsValue: String;
begin
  if CurStep = ssPostInstall then
  begin
    Port := GetPort('');
    UrlsValue := 'http://*:' + Port;
    SaveStringToFile(ExpandConstant('{app}\appsettings.Production.json'),
      '{' + #13#10 +
      '  "Urls": "' + UrlsValue + '"' + #13#10 +
      '}' + #13#10, False);
    // Tray 앱에도 동일한 설정 파일 복사 (포트 정보 공유)
    SaveStringToFile(ExpandConstant('{app}\Tray\appsettings.Production.json'),
      '{' + #13#10 +
      '  "Urls": "' + UrlsValue + '"' + #13#10 +
      '}' + #13#10, False);

    // 바탕화면에 .url 바로가기 생성 (아이콘 포함)
    SaveStringToFile(ExpandConstant('{autodesktop}\{#MyAppName}.url'),
      '[InternetShortcut]' + #13#10 +
      'URL=' + GetAppURL('') + #13#10 +
      'IconFile=' + ExpandConstant('{app}\DSPilot.ico') + #13#10 +
      'IconIndex=0' + #13#10, False);
  end;
end;

// Stop existing service before installation (upgrade scenario)
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  // 업그레이드 시 트레이 앱 종료
  Exec(ExpandConstant('{cmd}'), '/c taskkill /F /IM {#MyTrayExeName} >nul 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\sc.exe'), ExpandConstant('stop {#MyServiceName}'), '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(3000);
  Exec(ExpandConstant('{sys}\sc.exe'), ExpandConstant('delete {#MyServiceName}'), '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  // Remove old firewall rule (re-created with new port after install)
  Exec(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall delete rule name="DSPilot Web Service"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1000);
  Result := '';
end;
