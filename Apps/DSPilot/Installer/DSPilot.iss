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
; CCTV — MediaMTX(WinSW 래퍼) 서비스. RTSP→WebRTC 게이트웨이. DSPilot 과 별 프로세스로 격리.
#define MyMtxServiceName "DSPilotMediaMtx"
#define MyMtxServiceExe "mediamtx-service.exe"
#define MyWebRtcPort "8889"
#define MyWebRtcUdpPort "8189"
; Promaker · DSPilot 공유 AASX 경로 (DSPilot/Infrastructure/SharedPaths.cs 와 동일)
#define MySharedDir "{commonappdata}\DualSoft\Shared"
#define MySharedAasxName "project.aasx"
; ── Promaker.Agent (옵션 번들) ──
; Promaker 를 따로 설치하지 않고 DSPilot 만 쓰는 환경을 위한 헤드리스 모니터링 백엔드.
; 5051 SignalR Hub + PLC 스캔을 SYSTEM 서비스로 제공한다(= DSPilot 가 client 로 접속하는 hub).
; build-installer.bat 의 [3c] 단계가 publish-agent 폴더를 self-contained 로 채운다.
; AgentPublishDir 에 Promaker.Agent.exe 가 없으면(=에이전트 미publish) Tasks/Files/Run 전부 자동 스킵.
#define AgentPublishDir "..\publish-agent"
#define MyAgentExeName "Promaker.Agent.exe"
#define MyAgentServiceName "PromakerAgentService"
#define MyAgentServiceDisplay "Promaker Agent Service"
#define MyAgentServiceDesc "Promaker headless monitoring agent (5051 SignalR Hub + PLC scan, read-only)"
#define MyAgentPort "5051"
; 모델 업로드 수신 포트 (AgentUploadReceiver, 항상 listen) — 원격 Promaker '네트워크 업로드' 대상.
#define MyAgentUploadPort "5050"
#define HasAgent FileExists(AddBackslash(AgentPublishDir) + MyAgentExeName)

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

[Tasks]
#if HasAgent
; Promaker.Agent 옵션 설치 — 기본 해제(unchecked). Promaker 를 별도로 설치하지 않고 DSPilot 만
; 쓰는 PC 에서 PLC 스캔 + 5051 모니터링 Hub 백엔드를 함께 깔고 싶을 때만 체크한다.
; Promaker 가 이미 설치된 PC 라면 Promaker 가 동일 서비스(PromakerAgentService)를 관리하므로 체크 불필요.
Name: "installagent"; Description: "Promaker Agent 모니터링 서비스 함께 설치 (Promaker 미설치 환경용)"; Flags: unchecked
#endif

[Dirs]
; Promaker · DSPilot 공유 폴더. Users 그룹에 modify 권한 부여 →
; DSPilot(SYSTEM 서비스)과 Promaker(일반 사용자) 양쪽이 같은 파일을 읽고 쓸 수 있도록.
Name: "{#MySharedDir}"; Permissions: users-modify

[Files]
; Publish output (self-contained, all dependencies included)
; uploads 폴더의 사용자 데이터(도면 이미지, 레이아웃 JSON, CCTV 설비 오버레이)는 설치하지 않는다.
; - 신규 설치: 도면 이미지·오버레이 없이 백지로 시작. AASX 최초 import 시 Flow 들이 자동으로 격자에 채워짐.
; - 업그레이드: 기존 사용자 데이터 보존 (Excludes 로 덮어쓰기 방지).
; cctv-overlays.json 은 git 미추적 런타임 산출물이라 로컬 빌드 시 dev 의 파일이 publish 에 섞여
; 들어가 타겟 PC 의 오버레이를 덮어쓰므로(layout-data.json 과 동일 케이스) 함께 제외한다.
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "wwwroot\uploads\blueprint.*,wwwroot\uploads\layout-data.json,wwwroot\uploads\layout-data.json.*,wwwroot\uploads\cctv-overlays.json"
; Icon file for shortcuts
Source: "..\DSPilot\DSPilot.ico"; DestDir: "{app}"; Flags: ignoreversion
; CCTV — MediaMTX 바이너리 + WinSW 래퍼. build-installer.bat 가 mediamtx 폴더를 채운다.
; mediamtx.yml 은 운영자가 손볼 수 있으므로 업그레이드 시 덮어쓰지 않는다(onlyifdoesntexist).
Source: "mediamtx\mediamtx.exe"; DestDir: "{app}\mediamtx"; Flags: ignoreversion
Source: "mediamtx\{#MyMtxServiceExe}"; DestDir: "{app}\mediamtx"; Flags: ignoreversion
Source: "mediamtx\mediamtx-service.xml"; DestDir: "{app}\mediamtx"; Flags: ignoreversion
Source: "mediamtx\mediamtx.yml"; DestDir: "{app}\mediamtx"; Flags: onlyifdoesntexist
; MIT 고지문 — 바이너리 재배포 시 저작권·허가 고지 동봉 의무 (MediaMTX / WinSW)
Source: "mediamtx\LICENSE"; DestDir: "{app}\mediamtx"; Flags: ignoreversion
Source: "mediamtx\LICENSE-winsw.txt"; DestDir: "{app}\mediamtx"; Flags: ignoreversion
#if HasAgent
; Promaker.Agent — "installagent" 태스크 체크 시에만 {app}\Agent 로 번들(self-contained).
; 별도 폴더로 분리해 DSPilot.exe 와 dll 충돌 방지 + Agent 로그(logs\promaker-agent.log) 격리.
Source: "{#AgentPublishDir}\*"; DestDir: "{app}\Agent"; Tasks: installagent; \
  Flags: ignoreversion recursesubdirs createallsubdirs
#endif
; 초기 AASX 는 인스톨러에 번들하지 않음. Promaker 의 "공유 위치에 저장(DSPilot 동기화)" 메뉴로
; 운영 시점에 모델 파일이 생성/갱신됨. 파일이 없을 때 DSPilot 은 빈 상태로 부팅되며
; Settings 페이지에 "파일 없음 — Promaker 에서 먼저 저장하세요" 안내가 표시됨.

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{code:GetAppURL}"
Name: "{group}\{#MyAppName} 서비스 시작"; Filename: "{sys}\sc.exe"; Parameters: "start {#MyServiceName}"
Name: "{group}\{#MyAppName} 서비스 중지"; Filename: "{sys}\sc.exe"; Parameters: "stop {#MyServiceName}"
Name: "{group}\{#MyAppName} 제거"; Filename: "{uninstallexe}"
; 바탕화면 바로가기는 [Code] 섹션에서 .url 파일로 직접 생성 (아이콘 포함)

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

; 주의: DSPilot 본 서비스 시작은 여기(=[Run], 설치 순서 15단계)서 하지 않는다.
; 바인딩 포트가 담긴 appsettings.Hosting.json 은 ssPostInstall(=[Run] 이후)에서 기록되므로,
; [Run] 에서 시작하면 첫 부팅이 포트 파일 부재 상태로 떠 Kestrel 기본 포트(5000)에 바인딩되고
; (reloadOnChange:false 라 이후 파일 생성도 반영 안 됨) 사용자가 고른 포트로 접속이 안 된다.
; → 포트 파일을 먼저 쓴 뒤 CurStepChanged(ssPostInstall)에서 sc start 한다.
; start=auto 는 "다음 부팅 시 자동시작"일 뿐 create 시점에 즉시 시작하지 않으므로, 이 사이 서비스는 뜨지 않는다.

; ── CCTV: MediaMTX 서비스 (WinSW 래퍼로 등록 + 시작) ──
Filename: "{app}\mediamtx\{#MyMtxServiceExe}"; \
  Parameters: "install"; \
  Flags: runhidden waituntilterminated; \
  StatusMsg: "CCTV(MediaMTX) 서비스 등록 중..."

Filename: "{app}\mediamtx\{#MyMtxServiceExe}"; \
  Parameters: "start"; \
  Flags: runhidden waituntilterminated; \
  StatusMsg: "CCTV(MediaMTX) 서비스 시작 중..."

; WebRTC 시청 포트 (TCP 8889 = WHEP/시그널링, UDP 8189 = ICE 미디어)
Filename: "{sys}\netsh.exe"; \
  Parameters: "advfirewall firewall add rule name=""DSPilot CCTV WebRTC TCP"" dir=in action=allow protocol=tcp localport={#MyWebRtcPort}"; \
  Flags: runhidden waituntilterminated; \
  StatusMsg: "CCTV 방화벽 규칙 추가 중 (TCP)..."

Filename: "{sys}\netsh.exe"; \
  Parameters: "advfirewall firewall add rule name=""DSPilot CCTV WebRTC UDP"" dir=in action=allow protocol=udp localport={#MyWebRtcUdpPort}"; \
  Flags: runhidden waituntilterminated; \
  StatusMsg: "CCTV 방화벽 규칙 추가 중 (UDP)..."

; WebRTC 미디어 TCP 폴백 (UDP 8189 와 동일 포트, 프로토콜만 다름). UDP 차단망에서 외부 시청 대비.
; 외부 접속 주소를 설정하면 MediaMTX 가 이 TCP 리스너도 켠다(webrtcLocalTCPAddress).
Filename: "{sys}\netsh.exe"; \
  Parameters: "advfirewall firewall add rule name=""DSPilot CCTV WebRTC TCP Media"" dir=in action=allow protocol=tcp localport={#MyWebRtcUdpPort}"; \
  Flags: runhidden waituntilterminated; \
  StatusMsg: "CCTV 방화벽 규칙 추가 중 (TCP 미디어 폴백)..."

; ── Promaker.Agent 서비스 등록 + 시작 (installagent 태스크 체크 시에만) ──
; 업그레이드 시 기존 서비스는 PrepareToInstall 이 미리 stop+delete 하므로 create 부터 진행.
#if HasAgent
Filename: "{sys}\sc.exe"; \
  Parameters: "create {#MyAgentServiceName} binPath=""{app}\Agent\{#MyAgentExeName}"" start=auto DisplayName=""{#MyAgentServiceDisplay}"""; \
  Flags: runhidden waituntilterminated; Tasks: installagent; \
  StatusMsg: "Promaker Agent 서비스 등록 중..."

Filename: "{sys}\sc.exe"; \
  Parameters: "description {#MyAgentServiceName} ""{#MyAgentServiceDesc}"""; \
  Flags: runhidden waituntilterminated; Tasks: installagent; \
  StatusMsg: "Promaker Agent 서비스 설명 설정 중..."

; 실패 복구: 10s, 10s, 30s 후 자동 재시작. 카운터는 1일 후 리셋. (DSPilot 본 서비스와 동일 정책)
Filename: "{sys}\sc.exe"; \
  Parameters: "failure {#MyAgentServiceName} reset=86400 actions=restart/10000/restart/10000/restart/30000"; \
  Flags: runhidden waituntilterminated; Tasks: installagent; \
  StatusMsg: "Promaker Agent 서비스 복구 옵션 설정 중..."

; 방화벽 인바운드 5051 — DSPilot 가 같은 머신 localhost 로 접속하지만 원격 모니터링 확장 대비 허용.
Filename: "{sys}\netsh.exe"; \
  Parameters: "advfirewall firewall add rule name=""Promaker Agent Monitoring"" dir=in action=allow protocol=tcp localport={#MyAgentPort}"; \
  Flags: runhidden waituntilterminated; Tasks: installagent; \
  StatusMsg: "Promaker Agent 방화벽 규칙 추가 중..."

; 방화벽 인바운드 5050 — 모델 업로드 수신(AgentUploadReceiver). 원격 Promaker 의 '네트워크 업로드' 대상.
Filename: "{sys}\netsh.exe"; \
  Parameters: "advfirewall firewall add rule name=""Promaker Agent Upload"" dir=in action=allow protocol=tcp localport={#MyAgentUploadPort}"; \
  Flags: runhidden waituntilterminated; Tasks: installagent; \
  StatusMsg: "Promaker Agent 업로드 방화벽 규칙 추가 중..."

Filename: "{sys}\sc.exe"; \
  Parameters: "start {#MyAgentServiceName}"; \
  Flags: runhidden waituntilterminated; Tasks: installagent; \
  StatusMsg: "Promaker Agent 서비스 시작 중..."
#endif

; Open browser after install (optional)
Filename: "{code:GetAppURL}"; \
  Description: "DSPilot 웹 대시보드 열기"; \
  Flags: postinstall shellexec nowait skipifsilent unchecked

[UninstallDelete]
Type: files; Name: "{autodesktop}\{#MyAppName}.url"

[UninstallRun]
#if HasAgent
; ── Promaker.Agent 서비스 정리 ──
; Check=ShouldRemoveAgent: 이 설치본이 {app}\Agent 에 Agent 를 실제로 깔았을 때만 stop/delete.
; (Agent 옵션 미선택으로 {app}\Agent 가 없으면 건너뛴다 — 같은 PC 에 Promaker 가 설치돼 있어
;  Promaker 가 등록한 PromakerAgentService 를 DSPilot 제거가 잘못 지우는 것을 막기 위함.)
Filename: "{sys}\sc.exe"; Parameters: "stop {#MyAgentServiceName}"; \
  Flags: runhidden waituntilterminated; Check: ShouldRemoveAgent; RunOnceId: "StopAgentService"
Filename: "{sys}\sc.exe"; Parameters: "delete {#MyAgentServiceName}"; \
  Flags: runhidden waituntilterminated; Check: ShouldRemoveAgent; RunOnceId: "DeleteAgentService"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Promaker Agent Monitoring"""; \
  Flags: runhidden waituntilterminated; Check: ShouldRemoveAgent; RunOnceId: "DeleteAgentFirewall"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Promaker Agent Upload"""; \
  Flags: runhidden waituntilterminated; Check: ShouldRemoveAgent; RunOnceId: "DeleteAgentUploadFirewall"
#endif

; ── CCTV: MediaMTX 서비스 정지 + 등록 해제 (WinSW) ──
Filename: "{app}\mediamtx\{#MyMtxServiceExe}"; \
  Parameters: "stop"; \
  Flags: runhidden waituntilterminated; \
  RunOnceId: "StopMtxService"

Filename: "{app}\mediamtx\{#MyMtxServiceExe}"; \
  Parameters: "uninstall"; \
  Flags: runhidden waituntilterminated; \
  RunOnceId: "UninstallMtxService"

; Remove CCTV firewall rules
Filename: "{sys}\netsh.exe"; \
  Parameters: "advfirewall firewall delete rule name=""DSPilot CCTV WebRTC TCP"""; \
  Flags: runhidden waituntilterminated; \
  RunOnceId: "DeleteMtxFirewallTcp"

Filename: "{sys}\netsh.exe"; \
  Parameters: "advfirewall firewall delete rule name=""DSPilot CCTV WebRTC UDP"""; \
  Flags: runhidden waituntilterminated; \
  RunOnceId: "DeleteMtxFirewallUdp"

Filename: "{sys}\netsh.exe"; \
  Parameters: "advfirewall firewall delete rule name=""DSPilot CCTV WebRTC TCP Media"""; \
  Flags: runhidden waituntilterminated; \
  RunOnceId: "DeleteMtxFirewallTcpMedia"

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

// netstat -ano -p tcp 출력에서 지정 포트가 LISTENING 상태로 잡혀있는지 검사.
// ':80 ' 처럼 포트 뒤 공백까지 매칭해 ':8080' 같은 부분일치를 회피한다.
function IsPortInUse(Port: Integer): Boolean;
var
  TempFile: String;
  Lines: TArrayOfString;
  i: Integer;
  ResultCode: Integer;
  PortToken: String;
begin
  Result := False;
  TempFile := ExpandConstant('{tmp}\dspilot_netstat.txt');
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

// 서비스가 STOPPED 상태(또는 존재하지 않음)인지 확인.
// RUNNING/PENDING 상태가 보이면 False.
function IsServiceStopped(ServiceName: String): Boolean;
var
  TempFile: String;
  Lines: TArrayOfString;
  i: Integer;
  ResultCode: Integer;
begin
  Result := True;
  TempFile := ExpandConstant('{tmp}\dspilot_sc_query.txt');
  Exec(ExpandConstant('{cmd}'),
    '/c sc query ' + ServiceName + ' > "' + TempFile + '"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if not LoadStringsFromFile(TempFile, Lines) then
  begin
    DeleteFile(TempFile);
    Exit;
  end;
  for i := 0 to GetArrayLength(Lines) - 1 do
  begin
    if (Pos('RUNNING', Lines[i]) > 0) or
       (Pos('STOP_PENDING', Lines[i]) > 0) or
       (Pos('START_PENDING', Lines[i]) > 0) then
    begin
      Result := False;
      Break;
    end;
  end;
  DeleteFile(TempFile);
end;

// sc stop 후 STOPPED 까지 polling. 고정 Sleep(3000) 대비 race condition 회피.
function WaitForServiceStopped(ServiceName: String; TimeoutSec: Integer): Boolean;
var
  i: Integer;
begin
  for i := 0 to (TimeoutSec * 2) - 1 do
  begin
    if IsServiceStopped(ServiceName) then
    begin
      Result := True;
      Exit;
    end;
    Sleep(500);
  end;
  Result := False;
end;

#if HasAgent
// 이 설치본이 {app}\Agent 에 Promaker.Agent 를 실제로 깔았는지 — UninstallRun 의 Check.
// 우리 Agent 폴더가 존재할 때만 PromakerAgentService 를 stop/delete 한다(코멘트는 [UninstallRun] 참조).
function ShouldRemoveAgent: Boolean;
begin
  Result := FileExists(ExpandConstant('{app}\Agent\{#MyAgentExeName}'));
end;
#endif

procedure InitializeWizard();
var
  DefaultPort: String;
  PortHint: String;
begin
  DefaultPort := '{#MyDefaultPort}';
  PortHint := '기본값: {#MyDefaultPort} (포트 80은 URL에서 포트 번호 생략 가능)';
  // 80이 점유중인데 그게 구버전 우리 서비스가 아니라면 8080을 권장.
  // 구버전 우리 서비스라면 PrepareToInstall 에서 stop 후 정리되므로 80 그대로 두어도 됨.
  if (not IsServiceStopped('{#MyServiceName}')) then
  begin
    // 우리 구버전이 잡고 있음 → 80 유지
  end
  else if IsPortInUse(80) then
  begin
    DefaultPort := '8080';
    PortHint := '포트 80 이 다른 프로세스에 의해 사용 중이라 기본값을 8080 으로 변경했습니다.';
  end;

  PortPage := CreateInputQueryPage(wpSelectDir,
    '포트 설정', '웹 서비스 포트를 설정합니다.',
    'DSPilot 웹 서비스가 사용할 포트 번호를 입력하세요.' + #13#10 + PortHint);
  PortPage.Add('포트 번호:', False);
  PortPage.Values[0] := DefaultPort;
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
  OurServiceRunning: Boolean;
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
      Exit;
    end;
    // 우리 구버전 서비스가 잡고 있는 경우는 PrepareToInstall 에서 stop 처리되므로 점유 무시.
    OurServiceRunning := not IsServiceStopped('{#MyServiceName}');
    if (not OurServiceRunning) and IsPortInUse(PortNum) then
    begin
      if MsgBox('포트 ' + Port + ' 가 이미 다른 프로세스에 의해 사용 중입니다.' + #13#10 +
                '계속 진행하면 DSPilot 서비스 시작이 실패할 수 있습니다.' + #13#10#13#10 +
                '그래도 이 포트로 진행하시겠습니까?',
                mbConfirmation, MB_YESNO) = IDNO then
        Result := False;
    end;
  end;
end;

// Write port to appsettings.Hosting.json after files are installed.
// Program.cs 가 이 파일을 명시적으로 로드(AddJsonFile)하며, 매 설치마다 선택 포트로 강제 갱신한다.
// 사용자 설정 저장소(appsettings.Production.json)는 건드리지 않는다 — 카메라/이상치/시프트 등 보존.
// (포트만 분리 보관하므로 사용자 설정 보존과 포트 갱신이 충돌하지 않는다.)
procedure CurStepChanged(CurStep: TSetupStep);
var
  Port: String;
  UrlsValue: String;
  HostingJsonPath: String;
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    Port := GetPort('');
    UrlsValue := 'http://*:' + Port;
    HostingJsonPath := ExpandConstant('{app}\appsettings.Hosting.json');
    SaveStringToFile(HostingJsonPath,
      '{' + #13#10 +
      '  "Urls": "' + UrlsValue + '"' + #13#10 +
      '}' + #13#10, False);

    // 포트 파일(appsettings.Hosting.json)을 기록한 *뒤에* 서비스를 시작한다 — 이 순서가 중요.
    // [Run](설치 순서 15단계)에서 시작하면 포트 파일 부재 상태로 떠 기본 포트(5000)에 바인딩되는 버그가 있었다.
    Exec(ExpandConstant('{sys}\sc.exe'), 'start {#MyServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

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
  OldAppSettings: String;
  PortNum: Integer;
begin
  Exec(ExpandConstant('{sys}\sc.exe'), ExpandConstant('stop {#MyServiceName}'), '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  // 고정 Sleep 대신 STOPPED polling — Sleep(3000) 으로는 부족한 환경 race condition 대비.
  WaitForServiceStopped('{#MyServiceName}', 10);
  Exec(ExpandConstant('{sys}\sc.exe'), ExpandConstant('delete {#MyServiceName}'), '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // CCTV(MediaMTX) 서비스도 업그레이드 전 정지/제거. WinSW 가 등록한 일반 Windows 서비스라
  // sc 로 직접 정리 가능(구버전 래퍼 exe 존재 여부에 의존하지 않음). mediamtx.exe 파일 잠금 해제 목적.
  Exec(ExpandConstant('{sys}\sc.exe'), ExpandConstant('stop {#MyMtxServiceName}'), '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  WaitForServiceStopped('{#MyMtxServiceName}', 10);
  Exec(ExpandConstant('{sys}\sc.exe'), ExpandConstant('delete {#MyMtxServiceName}'), '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

#if HasAgent
  // Promaker.Agent 옵션을 (재)설치하는 경우에만 기존 Agent 서비스를 정지/제거 — {app}\Agent\*.dll 파일
  // 잠금 해제 + 새 바이너리로 재등록 준비. 태스크 미선택 시엔 건드리지 않아, 같은 PC 의 Promaker 가
  // 등록·운영 중인 PromakerAgentService 를 DSPilot 설치가 끊지 않도록 한다.
  if WizardIsTaskSelected('installagent') then
  begin
    Exec(ExpandConstant('{sys}\sc.exe'), ExpandConstant('stop {#MyAgentServiceName}'), '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    WaitForServiceStopped('{#MyAgentServiceName}', 10);
    Exec(ExpandConstant('{sys}\sc.exe'), ExpandConstant('delete {#MyAgentServiceName}'), '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
#endif
  // Remove old firewall rule (re-created with new port after install)
  Exec(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall delete rule name="DSPilot Web Service"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // appsettings.json(번들 기본값 베이스)만 제거 — [Files] 가 새 번들로 덮어쓰지만, 구버전의 stale 추가 키까지
  // 확실히 정리하려고 먼저 지운다. 없으면 첫 부팅 시 AppSettingsService.EnsureSettingsFiles 가 코드 기본값으로 재생성.
  //
  // appsettings.Production.json 은 *보존*한다 — 사용자 설정(카메라/이상치/시프트/OEE 등) 영속 저장소이므로
  //   업그레이드 때 날리면 안 된다(과거 이 파일 삭제가 카메라 등록 유실의 원인이었음).
  // 포트(Urls)는 사용자 설정과 분리해 CurStepChanged 가 appsettings.Hosting.json 에 따로 기록한다.
  // 구버전에서 넘어온 설정이 문제를 일으키면, 사용자가 설정 페이지의 "설정 초기화"로 직접 기본값 복원한다.
  OldAppSettings := ExpandConstant('{app}\appsettings.json');
  if FileExists(OldAppSettings) then DeleteFile(OldAppSettings);

  Sleep(1000);

  // 우리 서비스가 stop 된 이후에도 포트가 잡혀있다면 외부 프로세스 점유. 안내만 띄우고 진행.
  // (NextButtonClick 에서 한 차례 확인했지만 마법사 중 외부 프로세스가 80 을 잡았을 수 있어 안전망.)
  PortNum := StrToIntDef(GetPort(''), -1);
  if (PortNum > 0) and IsPortInUse(PortNum) then
  begin
    MsgBox('포트 ' + IntToStr(PortNum) + ' 가 외부 프로세스에 의해 사용 중입니다.' + #13#10 +
           '설치는 계속되지만 DSPilot 서비스 시작이 실패할 수 있습니다.' + #13#10 +
           '실패 시 해당 프로세스를 종료한 뒤 서비스를 다시 시작하세요.',
           mbInformation, MB_OK);
  end;

  Result := '';
end;
