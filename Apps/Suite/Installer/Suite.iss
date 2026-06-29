; Setup Dualsoft - 통합 설치 마법사 (오케스트레이터 부트스트래퍼)
; ---------------------------------------------------------------------------
; 이 스크립트는 파일을 직접 설치하지 않는다. 빌드 시점에 갓 만들어진
; DSPilot / Promaker 개별 설치본(.exe)을 번들로 끼워 넣고, 설치 단계에서
; 두 설치본을 /SILENT 로 순차 호출(체이닝)하는 얇은 상위 마법사다.
;
; 따라서:
;   - 설치 경로는 각 서브 설치본의 기본 경로를 그대로 따른다(통합본 자체 {app} 는 uninstaller 만 보관).
;   - 설치 로직(서비스/포트/MediaMTX/Agent/sc·fd)은 각 .iss 가 SSOT 로 유지 → 여기엔 중복하지 않는다.
;   - Promaker 설치본이 Promaker.Agent + AgentTray 를 항상 동봉/소유(PromakerAgentService).
;     → DSPilot 서브설치는 installagent 태스크를 건드리지 않아(기본 OFF) 서비스 이중 등록을 피한다.
;
; 빌드: ../build-suite.sh 가 두 서브 설치본을 만든 뒤 아래 /D 인자를 주입해 ISCC 컴파일.
;   ISCC /DDsPilotSetup=<...exe> /DPromakerSetup=<...exe> /DSuiteVersion=<x.y.z.w> Installer\Suite.iss

; ── 빌드 스크립트가 /D 로 주입하는 값들 (직접 ISCC 호출 시 fallback 기본값) ──
#ifndef SuiteVersion
  #define SuiteVersion "1.0.0.0"
#endif
#ifndef DsPilotSetup
  #define DsPilotSetup "..\..\DSPilot\Output\DSPilot_Setup_1.0.1.27.exe"
#endif
#ifndef PromakerSetup
  #define PromakerSetup "..\..\Promaker\Installer\Output\Promaker_Setup_0.1.21_sc.exe"
#endif

; 런타임에 {tmp} 로 풀린 뒤 실행할 파일명(컴파일 시점에 경로에서 basename 추출).
#define DsPilotSetupName ExtractFileName(DsPilotSetup)
#define PromakerSetupName ExtractFileName(PromakerSetup)

#define MyAppName "Setup Dualsoft"
#define MyAppPublisher "Dualsoft"
#define MyAppURL "https://dualsoft.co.kr"
#define MyDefaultPort "80"

; 서브 설치본 AppId — 통합 제거 시 QuietUninstallString 을 찾기 위한 키(_is1).
;   DSPilot.iss  / Promaker Setup.iss 의 [Setup] AppId 와 반드시 일치해야 한다.
#define DsPilotAppId "{E8A3F2B1-7C4D-4E5F-9A1B-3D6E8F0C2A4B}"
#define PromakerAppId "{7B74787E-6F09-4AB9-AE16-4C9D5F8B3D31}"

[Setup]
; 신규 통합본 전용 AppId (서브 AppId 와 절대 겹치면 안 됨).
AppId={{2E9D7A41-5B6C-4F3E-A1D2-8C0B3E6F9A52}
AppName={#MyAppName}
AppVersion={#SuiteVersion}
AppVerName={#MyAppName} {#SuiteVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
; 통합본 자체는 파일을 거의 설치하지 않으나 uninstaller 보관용 {app} 가 필요.
DefaultDirName={autopf}\{#MyAppPublisher}\Setup Dualsoft
DefaultGroupName={#MyAppName}
DisableDirPage=yes
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=Setup_Dualsoft_{#SuiteVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\Assets\Suite.ico
UninstallDisplayIcon={app}\Suite.ico
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
MinVersion=10.0

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; 갓 빌드된 두 서브 설치본을 번들 → 설치 단계에서 {tmp} 로 풀어 실행 후 자동 삭제.
; 두 .exe 는 이미 lzma 압축본이라 재압축 무이득 → nocompression.
Source: "{#DsPilotSetup}";  DestDir: "{tmp}"; Flags: deleteafterinstall nocompression
Source: "{#PromakerSetup}"; DestDir: "{tmp}"; Flags: deleteafterinstall nocompression
; 통합 제거 항목 아이콘.
Source: "..\Assets\Suite.ico"; DestDir: "{app}"; Flags: ignoreversion

[Run]
; 설치 완료 페이지의 옵션 체크박스 — 클릭 시 DSPilot 웹 대시보드 열기. 사일런트 설치에선 생략.
Filename: "{code:GetDashboardUrl}"; Description: "DSPilot 웹 대시보드 열기"; \
  Flags: postinstall shellexec nowait skipifsilent

[Code]
var
  PortPage: TInputQueryWizardPage;

// ── netstat 기반 포트 점유 검사 (DSPilot.iss 의 동일 헬퍼를 재사용 복사) ──
// ':80 ' 처럼 포트 뒤 공백까지 매칭해 ':8080' 부분일치를 회피한다.
function IsPortInUse(Port: Integer): Boolean;
var
  TempFile: String;
  Lines: TArrayOfString;
  i: Integer;
  ResultCode: Integer;
  PortToken: String;
begin
  Result := False;
  TempFile := ExpandConstant('{tmp}\suite_netstat.txt');
  if not Exec(ExpandConstant('{cmd}'),
       '/c netstat -ano -p tcp > "' + TempFile + '"',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Exit;
  if not LoadStringsFromFile(TempFile, Lines) then
  begin
    DeleteFile(TempFile);
    Exit;
  end;
  PortToken := ':' + IntToStr(Port);
  for i := 0 to GetArrayLength(Lines) - 1 do
  begin
    if (Pos('LISTENING', Lines[i]) > 0) and
       (Pos(PortToken + ' ', Lines[i] + ' ') > 0) then
    begin
      Result := True;
      Break;
    end;
  end;
  DeleteFile(TempFile);
end;

procedure InitializeWizard();
var
  DefaultPort: String;
  PortHint: String;
  SummaryPage, NoticePage: TOutputMsgMemoWizardPage;
begin
  // ── 페이지 1: 설치 구성 요약 (무엇이 설치되는지) ──
  SummaryPage := CreateOutputMsgMemoPage(wpWelcome,
    '설치 구성 요약', '이 마법사가 설치하는 구성 요소입니다.',
    '아래 구성 요소가 한 번에 설치됩니다. 각 구성 요소는 기본 설치 경로에 설치됩니다.',
    '■ DSPilot  —  웹 기반 PLC 모니터링/분석' + #13#10 +
    '    · DSPilot 웹 서비스 (Windows 서비스, 시스템 시작 시 자동 실행)' + #13#10 +
    '    · CCTV 중계 게이트웨이 (MediaMTX, Windows 서비스)' + #13#10 +
    '    · 설치 경로: C:\Program Files\DualSoft\DSPilot' + #13#10#13#10 +
    '■ Promaker  —  설비 모델 저작 데스크톱 앱' + #13#10 +
    '    · Promaker 본체  (C:\Program Files\Promaker)' + #13#10 +
    '    · Promaker Agent (헤드리스 모니터링 서비스, 자동 실행 · ...\Promaker\Agent)' + #13#10 +
    '    · Promaker Agent Tray (알림 영역 상태 표시 · ...\Promaker\AgentTray)' + #13#10 +
    '■ 공통' + #13#10 +
    '    · 공유 폴더: %ProgramData%\DualSoft\Shared (모델 파일 공유)' + #13#10 +
    '    · 필요한 Windows 방화벽 인바운드 규칙 자동 등록');

  // ── 페이지 2: 설치 전 안내 / 오픈소스 고지 ──
  NoticePage := CreateOutputMsgMemoPage(SummaryPage.ID,
    '설치 안내', '설치 전 확인해 주세요.',
    '서비스 자동 실행 · 방화벽 · 오픈소스 고지 안내입니다.',
    '[Windows 서비스]' + #13#10 +
    '  · 설치되는 서비스(DSPilot / CCTV / Promaker Agent)는 시스템 시작 시 자동 실행됩니다.' + #13#10#13#10 +
    '[방화벽 — 아래 인바운드 규칙이 자동 등록됩니다]' + #13#10 +
    '  · DSPilot 웹: TCP (다음 단계에서 선택한 포트, 기본 80)' + #13#10 +
    '  · CCTV(WebRTC): TCP 8889, UDP 8189' + #13#10 +
    '  · Promaker Agent: TCP 5051(모니터링) / 5050(모델 업로드)' + #13#10#13#10 +
    '[오픈소스 고지]' + #13#10 +
    '  본 제품은 CCTV 영상 중계를 위해 아래 오픈소스를 포함/재배포합니다.' + #13#10 +
    '  · MediaMTX (MIT License)  https://github.com/bluenviron/mediamtx' + #13#10 +
    '  · WinSW (MIT License)     https://github.com/winsw/winsw' + #13#10 +
    '  라이선스 전문은 설치 후 DSPilot 설치 폴더의 mediamtx\LICENSE,' + #13#10 +
    '  mediamtx\LICENSE-winsw.txt 에서 확인할 수 있습니다.');

  // ── 페이지 3: DSPilot 포트 설정 ──
  DefaultPort := '{#MyDefaultPort}';
  PortHint := '기본값: {#MyDefaultPort} (포트 80은 URL에서 포트 번호 생략 가능)';
  // 80 이 이미 점유돼 있으면 8080 을 권장. (구버전 DSPilot 서비스 점유는 DSPilot 설치본의
  //  PrepareToInstall 이 정리하지만, 통합 마법사 단계에선 단순히 사용 중이면 8080 제안.)
  if IsPortInUse(80) then
  begin
    DefaultPort := '8080';
    PortHint := '포트 80 이 사용 중이라 기본값을 8080 으로 제안합니다.';
  end;

  PortPage := CreateInputQueryPage(NoticePage.ID,
    'DSPilot 포트 설정',
    'DSPilot 웹 서비스가 사용할 포트를 설정합니다.',
    'DSPilot 웹 대시보드가 사용할 포트 번호를 입력하세요.' + #13#10 + PortHint);
  PortPage.Add('포트 번호:', False);
  PortPage.Values[0] := DefaultPort;
end;

function GetSuitePort(): String;
begin
  Result := PortPage.Values[0];
  if Result = '' then
    Result := '{#MyDefaultPort}';
end;

function GetDashboardUrl(Param: String): String;
var
  Port: String;
begin
  Port := GetSuitePort();
  if Port = '80' then
    Result := 'http://localhost'
  else
    Result := 'http://localhost:' + Port;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  PortNum: Integer;
begin
  Result := True;
  if CurPageID = PortPage.ID then
  begin
    if PortPage.Values[0] = '' then
    begin
      PortPage.Values[0] := '{#MyDefaultPort}';
      Exit;
    end;
    PortNum := StrToIntDef(PortPage.Values[0], -1);
    if (PortNum < 1) or (PortNum > 65535) then
    begin
      MsgBox('포트 번호는 1~65535 사이의 숫자여야 합니다.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

// 서브 설치본 1개를 /SILENT 로 실행. 0 이 아닌 종료코드는 경고만(설치 계속).
procedure RunChildInstaller(ExeName, ExtraParams, Title: String);
var
  ExePath, Params, LogPath: String;
  ResultCode: Integer;
begin
  ExePath := ExpandConstant('{tmp}\' + ExeName);
  LogPath := ExpandConstant('{tmp}\') + ExeName + '.install.log';
  // /SILENT — 서브 설치본의 진행 막대는 보이되 마법사 페이지는 생략(사용자에게 진행 피드백).
  Params := '/SILENT /SUPPRESSMSGBOXES /NORESTART /LOG="' + LogPath + '"';
  if ExtraParams <> '' then
    Params := Params + ' ' + ExtraParams;

  WizardForm.StatusLabel.Caption := Title;
  if not Exec(ExePath, Params, '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
  begin
    MsgBox(Title + #13#10#13#10 +
           '설치 프로그램 실행에 실패했습니다:' + #13#10 + ExePath,
           mbCriticalError, MB_OK);
    Exit;
  end;
  if ResultCode <> 0 then
    MsgBox(Title + #13#10#13#10 +
           '설치가 코드 ' + IntToStr(ResultCode) + ' (으)로 종료되었습니다.' + #13#10 +
           '로그: ' + LogPath,
           mbError, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  // ssPostInstall = 번들된 서브 설치본이 {tmp} 로 풀린 뒤 단계. 여기서 순차 체이닝한다.
  if CurStep = ssPostInstall then
  begin
    // 1) DSPilot 먼저 — /Port 로 선택 포트 전달(DSPilot.iss GetPort 가 {param:Port} 우선 사용).
    //    installagent 태스크는 전달하지 않음(기본 OFF) → Agent 소유권은 Promaker 가 가짐.
    RunChildInstaller('{#DsPilotSetupName}',
      '/Port=' + GetSuitePort(),
      'DSPilot 설치 중... (웹 서비스 + CCTV)');

    // 2) Promaker 다음 — Agent + AgentTray 동봉/소유. 추가 인자 없음(sc 모드 그대로).
    RunChildInstaller('{#PromakerSetupName}',
      '',
      'Promaker 설치 중... (Promaker + Agent + Tray)');
  end;
end;

// ── 통합 제거: 서브 설치본의 uninstaller 를 /SILENT 로 체이닝 ──
// Inno 가 기록한 _is1 키에서 UninstallString(따옴표로 감싼 unins exe 경로)을 읽어 실행.
// 64-bit / 32-bit 레지스트리 뷰 모두 시도.
function GetSubUninstallExe(AppId: String): String;
var
  KeyPath, S: String;
begin
  Result := '';
  KeyPath := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' + AppId + '_is1';
  if RegQueryStringValue(HKLM64, KeyPath, 'UninstallString', S) then
    Result := RemoveQuotes(S)
  else if RegQueryStringValue(HKLM32, KeyPath, 'UninstallString', S) then
    Result := RemoveQuotes(S);
end;

procedure RunChildUninstaller(AppId, Title: String);
var
  Exe: String;
  ResultCode: Integer;
begin
  Exe := GetSubUninstallExe(AppId);
  if (Exe <> '') and FileExists(Exe) then
    Exec(Exe, '/SILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_SHOW, ewWaitUntilTerminated, ResultCode);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    RunChildUninstaller('{#DsPilotAppId}', 'DSPilot 제거');
    RunChildUninstaller('{#PromakerAppId}', 'Promaker 제거');
  end;
end;
