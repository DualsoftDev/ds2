; Promaker installer script
; Requires: Inno Setup 6.4+
; Build entry point:
;   make -C installer/Apps/Promaker dist-installer        ; .exe only
;   /dist skill                                           ; full release (zip + scp + tag + push)
; This .iss accepts PublishDir / SelfContainedMode / OutputSuffix
; as /D arguments (see #ifndef defaults below).
; Direct ISCC compile: caller (Makefile) must run `dotnet publish` first.
; CodeDependencies.iss 는 [Code] 섹션을 제공하므로 파일 맨 아래에서 include —
; 그래야 사이에 끼는 ;-주석/디파인이 Pascal 코드로 오해되지 않는다 (Inno Setup 컨벤션).

#ifndef PublishDir
  #define PublishDir "..\Promaker\bin\Release\net9.0-windows\win-x64\publish-self-contained"
#endif
#ifndef SelfContainedMode
  #define SelfContainedMode "true"
#endif
#ifndef OutputSuffix
  #define OutputSuffix "_sc"
#endif
; Promaker.Agent publish 경로. Makefile 의 publish-agent 타겟이 동일 모드(sc/fd)로 산출.
; AgentPublishDir 이 비어 있거나 Promaker.Agent.exe 가 없으면 Agent 번들/서비스 등록 모두 스킵.
#ifndef AgentPublishDir
  #define AgentPublishDir "..\Promaker.Agent\bin\Release\net9.0\win-x64\publish-self-contained"
#endif
; Promaker.AgentTray publish 경로 — Agent 상태 노출용 사용자 컨텍스트 트레이.
#ifndef AgentTrayPublishDir
  #define AgentTrayPublishDir "..\Promaker.AgentTray\bin\Release\net9.0-windows\win-x64\publish-self-contained"
#endif
; Ds2.LightHouseService publish 경로 — Setup.iss 가 [Components] ai 옵션 (default checked) 에서
; {app}\LightHouseService\ 로 번들. Promaker UI 의 "로컬 LightHouse 활성화" 가 사후 호출 → cert/PSK/service 등록.
#ifndef LightHousePublishDir
  #define LightHousePublishDir "..\..\..\Solutions\Tools\Ds2.LightHouseService\bin\Release\net9.0\win-x64\publish-self-contained"
#endif
; LightHouseService scripts (install-ollama / generate-dev-cert / install-service ps1).
#ifndef LightHouseScriptsDir
  #define LightHouseScriptsDir "..\..\..\Solutions\Tools\Ds2.LightHouseService\scripts"
#endif

#define AppExePath AddBackslash(PublishDir) + "Promaker.exe"
#define AgentExePath AddBackslash(AgentPublishDir) + "Promaker.Agent.exe"
#define AgentTrayExePath AddBackslash(AgentTrayPublishDir) + "Promaker.AgentTray.exe"
#define LightHouseExePath AddBackslash(LightHousePublishDir) + "Ds2.LightHouseService.exe"
#define HasAgent FileExists(AgentExePath)
#define HasAgentTray FileExists(AgentTrayExePath)
#define HasLightHouse FileExists(LightHouseExePath)
#define SetupIconPath "..\Promaker\Assets\Promaker.ico"
#define MyAppName "Promaker"
#define MyAppVersion GetVersionNumbersString(AppExePath)
; OutputVersion: 산출물 파일명(Promaker_Setup_<ver>) 에 쓰는 버전. Makefile 의 iscc 타깃이
; BuildVersion.txt(3-part) 값을 /DOutputVersion 으로 주입 → 파일명 SSOT 를 BuildVersion.txt 로
; 일원화. 직접 ISCC 호출(Makefile 미경유) 시엔 exe file-version(4-part)인 MyAppVersion fallback.
#ifndef OutputVersion
  #define OutputVersion MyAppVersion
#endif
#define MyAppPublisher "Dualsoft"
#define MyAppURL "https://dualsoft.co.kr"
#define MyExeName "Promaker.exe"
#define MyAgentExeName "Promaker.Agent.exe"
#define MyAgentTrayExeName "Promaker.AgentTray.exe"
#define MyAgentServiceName "PromakerAgentService"
#define MyAgentServiceDisplay "Promaker Agent Service"
#define MyAgentServiceDesc "Promaker headless monitoring agent (5051 SignalR Hub + PLC scan, read-only)"
#define MyAgentPort "5051"

[Setup]
AppId={{7B74787E-6F09-4AB9-AE16-4C9D5F8B3D31}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir=Output
OutputBaseFilename=Promaker_Setup_{#OutputVersion}{#OutputSuffix}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupIconFile={#SetupIconPath}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll
RestartApplications=yes
UninstallDisplayIcon={app}\{#MyExeName}

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

; AI 기능 (LightHouse KB Service + Ollama 설치 스크립트) — default checked.
; 인스톨러는 파일 배치만 수행. service 등록 / PSK 생성 / cert 발급 / Ollama 설치 / firewall rule
; 일체는 Promaker UI 의 "로컬 LightHouse 서비스 활성화" 버튼이 사후 트리거한다.
#if HasLightHouse
[Components]
Name: "ai"; Description: "AI 기능 활성화 (LightHouse KB Service + Ollama 설치 파일 — 사후 UI 에서 활성화)"; \
  Types: full custom; Flags: checkablealone
#endif

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
; Promaker.Agent (Windows Service) + AgentTray 는 옵션 없이 항상 설치/등록. 본체 Promaker 가
; Monitoring + 실 PLC 시 Agent 에 위임(active.flag) 하는 구조라 Agent 가 누락되면 PLAY 가 차단된다.

; fd install 시 이전 sc 빌드 잔재 정리 — sc 시절 박제된 native dll 들이 app-local 에 살아남으면:
; (a) hostfxr.dll 잔재 → "self-contained" 모드로 hostfxr 가 app-local runtime 만 검색 → "install .NET" dialog
; (b) wpfgfx_cor3.dll 등 옛 WPF native 잔재 → 새 PresentationCore 가 EntryPointNotFoundException
;     ('WpfGfx_SetDisableBoundsCheckProtection' 등 새 API 호출 시) → WPF 첫 XAML load 도중 fatal UI error
; fd publish output 에는 본래 미박제이므로 안전 삭제. sc install 시는 본 블록 미적용 (필요 파일).
; root + 3 subfolder (Agent / AgentTray / LightHouseService) 모두 커버 — 모든 .exe 가 같은 sc 잔재 surface.
#if SelfContainedMode != "true"
[InstallDelete]
; .NET host / runtime native — hostfxr 가 app-local 우선 검색 회피
Type: files; Name: "{app}\hostfxr.dll"
Type: files; Name: "{app}\hostpolicy.dll"
Type: files; Name: "{app}\coreclr.dll"
Type: files; Name: "{app}\clrjit.dll"
Type: files; Name: "{app}\clrcompression.dll"
Type: files; Name: "{app}\Microsoft.DiaSymReader.Native.amd64.dll"
; WPF native — wpfgfx_cor3.dll 잔재가 새 PresentationCore 와 API mismatch (실측 회귀, 2026-05-26)
Type: files; Name: "{app}\wpfgfx_cor3.dll"
Type: files; Name: "{app}\PresentationNative_cor3.dll"
Type: files; Name: "{app}\D3DCompiler_47_cor3.dll"
Type: files; Name: "{app}\vcruntime140_cor3.dll"
; sc Microsoft.WindowsDesktop.App 의 framework dll wildcard (PresentationCore.dll 등은 시스템 dll 이라
; 위에서 명시 native 만 — wildcard 는 안전 영역만 선택. WindowsDesktop.App.* 는 sc 만 박제하므로 fd 에서 미생성).
Type: files; Name: "{app}\Microsoft.WindowsDesktop.App.*"
Type: filesandordirs; Name: "{app}\shared"

; Agent subfolder — net9.0 console, WPF 미사용 → host/runtime native 만
#if HasAgent
Type: files; Name: "{app}\Agent\hostfxr.dll"
Type: files; Name: "{app}\Agent\hostpolicy.dll"
Type: files; Name: "{app}\Agent\coreclr.dll"
Type: files; Name: "{app}\Agent\clrjit.dll"
Type: files; Name: "{app}\Agent\clrcompression.dll"
Type: files; Name: "{app}\Agent\Microsoft.DiaSymReader.Native.amd64.dll"
Type: filesandordirs; Name: "{app}\Agent\shared"
#endif

; AgentTray subfolder — net9.0-windows WPF, root 와 동일 surface
#if HasAgentTray
Type: files; Name: "{app}\AgentTray\hostfxr.dll"
Type: files; Name: "{app}\AgentTray\hostpolicy.dll"
Type: files; Name: "{app}\AgentTray\coreclr.dll"
Type: files; Name: "{app}\AgentTray\clrjit.dll"
Type: files; Name: "{app}\AgentTray\clrcompression.dll"
Type: files; Name: "{app}\AgentTray\Microsoft.DiaSymReader.Native.amd64.dll"
Type: files; Name: "{app}\AgentTray\wpfgfx_cor3.dll"
Type: files; Name: "{app}\AgentTray\PresentationNative_cor3.dll"
Type: files; Name: "{app}\AgentTray\D3DCompiler_47_cor3.dll"
Type: files; Name: "{app}\AgentTray\vcruntime140_cor3.dll"
Type: files; Name: "{app}\AgentTray\Microsoft.WindowsDesktop.App.*"
Type: filesandordirs; Name: "{app}\AgentTray\shared"
#endif

; LightHouseService subfolder — net9.0 console + Kestrel, WPF 미사용
#if HasLightHouse
Type: files; Name: "{app}\LightHouseService\hostfxr.dll"
Type: files; Name: "{app}\LightHouseService\hostpolicy.dll"
Type: files; Name: "{app}\LightHouseService\coreclr.dll"
Type: files; Name: "{app}\LightHouseService\clrjit.dll"
Type: files; Name: "{app}\LightHouseService\clrcompression.dll"
Type: files; Name: "{app}\LightHouseService\Microsoft.DiaSymReader.Native.amd64.dll"
Type: filesandordirs; Name: "{app}\LightHouseService\shared"
#endif
#endif

[Dirs]
; Promaker · DSPilot 공유 폴더. Promaker 의 "공유 위치에 저장(DSPilot 동기화)" 메뉴와
; DSPilot 서비스(SYSTEM)가 같은 경로(%ProgramData%\DualSoft\Shared\project.aasx)를 읽기/쓰기.
; Users 그룹 modify 권한이 있어야 일반 사용자로 실행되는 Promaker 가 덮어쓸 수 있음.
Name: "{commonappdata}\DualSoft\Shared"; Permissions: users-modify

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; SDF 파일 전용 아이콘 복사
Source: "..\Promaker\Assets\SdfFile.ico"; DestDir: "{app}"; Flags: ignoreversion
#if HasAgent
; Promaker.Agent — 항상 번들 + [Run] 에서 무조건 서비스 등록.
; 별도 폴더 {app}\Agent 로 분리해 Promaker.exe 와 dll 충돌 방지 + 로그 디렉터리(logs/promaker-agent.log) 격리.
Source: "{#AgentPublishDir}\*"; DestDir: "{app}\Agent"; Flags: ignoreversion recursesubdirs createallsubdirs
#endif
#if HasAgentTray
; Promaker.AgentTray — 사용자 컨텍스트 트레이. HKCU\Run 으로 사용자 로그온 시 자동 실행 (옵션 없음).
Source: "{#AgentTrayPublishDir}\*"; DestDir: "{app}\AgentTray"; Flags: ignoreversion recursesubdirs createallsubdirs
#endif
#if HasLightHouse
; Ds2.LightHouseService — Components ai (default checked) 일 때만 번들. {app}\LightHouseService\ 로 격리.
; service 등록 / PSK 생성 / cert / Ollama 설치는 모두 Promaker UI 가 사후 트리거 (인스톨러 미수행).
Source: "{#LightHousePublishDir}\*"; DestDir: "{app}\LightHouseService"; \
  Components: ai; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#LightHouseScriptsDir}\*"; DestDir: "{app}\LightHouseService\scripts"; \
  Components: ai; Flags: ignoreversion
#endif

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyExeName}"; Tasks: desktopicon

[Registry]
; SDF 파일 확장자 등록
Root: HKCR; Subkey: ".sdf"; ValueType: string; ValueName: ""; ValueData: "Promaker.SDF"; Flags: uninsdeletevalue
Root: HKCR; Subkey: "Promaker.SDF"; ValueType: string; ValueName: ""; ValueData: "Software Defined Factory File"; Flags: uninsdeletekey
Root: HKCR; Subkey: "Promaker.SDF\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\SdfFile.ico"
Root: HKCR; Subkey: "Promaker.SDF\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyExeName}"" ""%1"""
#if HasAgentTray
; AgentTray 사용자 로그온 시 자동 시작 — HKCU\Run. Agent 가 설치되면 트레이도 항상 따라 등록.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
  ValueName: "PromakerAgentTray"; ValueData: """{app}\AgentTray\{#MyAgentTrayExeName}"""; \
  Flags: uninsdeletevalue
#endif

[Run]
#if HasAgent
; ── Promaker.Agent 서비스 무조건 등록 + 시작. 업그레이드 호환 — 이미 떠 있으면 stop+delete 후 재등록. ──
; 기존 서비스 정지/삭제 (없으면 sc 가 비-0 반환하나 runhidden 으로 무시).
Filename: "{sys}\sc.exe"; Parameters: "stop {#MyAgentServiceName}"; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "delete {#MyAgentServiceName}"; Flags: runhidden
; start=auto — Windows 부팅 시 자동 시작.
Filename: "{sys}\sc.exe"; \
  Parameters: "create {#MyAgentServiceName} binPath= ""{app}\Agent\{#MyAgentExeName}"" start= auto DisplayName= ""{#MyAgentServiceDisplay}"""; \
  Flags: runhidden waituntilterminated; \
  StatusMsg: "Promaker Agent 서비스 등록 중..."
Filename: "{sys}\sc.exe"; Parameters: "description {#MyAgentServiceName} ""{#MyAgentServiceDesc}"""; \
  Flags: runhidden waituntilterminated
; 실패 복구 정책 — 10s, 10s, 30s 후 자동 재시작. 카운터는 1일 후 리셋.
Filename: "{sys}\sc.exe"; \
  Parameters: "failure {#MyAgentServiceName} reset= 86400 actions= restart/10000/restart/10000/restart/30000"; \
  Flags: runhidden waituntilterminated
; 방화벽 인바운드 5051. DSPilot 가 같은 머신 localhost 만 접속하지만, 원격 모니터링 확장 대비 미리 허용.
Filename: "{sys}\netsh.exe"; \
  Parameters: "advfirewall firewall add rule name=""Promaker Agent Monitoring"" dir=in action=allow protocol=tcp localport={#MyAgentPort}"; \
  Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "start {#MyAgentServiceName}"; \
  Flags: runhidden waituntilterminated; \
  StatusMsg: "Promaker Agent 서비스 시작 중..."
#endif
#if HasLightHouse
; ── 설치 전 LightHouse 서비스가 RUNNING 이었으면 재시작. ──
; LightHouseWasRunning Check = (설치 전 RUNNING) AND (이번 설치에서 ai 컴포넌트 선택). PrepareToInstall 이
; [Files] 직전 stop 했으므로, 사용자가 켜 두었던 경우에만 설치 후 자동 복원한다. 미등록 / STOPPED 였으면
; Check=False 로 미실행 → 현 상태 유지. service 등록 자체는 Promaker UI 'AI 활성화' 가 수행하며, 여기서는
; 기존 등록분을 새 바이너리로 재기동만 한다 (start 가 비-0 이어도 runhidden 으로 무시 — best-effort).
Filename: "{sys}\sc.exe"; Parameters: "start Ds2.LightHouseService"; \
  Components: ai; Check: LightHouseWasRunning; Flags: runhidden waituntilterminated; \
  StatusMsg: "LightHouse 서비스 재시작 중..."
#endif
#if HasAgentTray
; 설치 직후 한 번 트레이 띄움 — 체크박스 없이 무조건 실행. 다음 부팅부터는 HKCU\Run 이 자동 실행.
; runasoriginaluser: 인스톨러는 admin 으로 elevated 되어 있어도 트레이는 로그온한 사용자 컨텍스트로
; 실행해야 알림 영역에 떠 보인다 (admin 세션은 사용자 데스크톱과 별도).
Filename: "{app}\AgentTray\{#MyAgentTrayExeName}"; \
  Flags: nowait skipifsilent runasoriginaluser
#endif
Filename: "{app}\{#MyExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent unchecked

[UninstallRun]
#if HasAgentTray
; 트레이 프로세스 정지 — uninstall 시 파일 lock 회피.
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#MyAgentTrayExeName}"; Flags: runhidden; RunOnceId: "KillAgentTray"
#endif
#if HasAgent
; 서비스가 등록되어 있지 않으면 비-0 반환하지만 runhidden 으로 무시 — 옵트인 안 했어도 안전하게 정리.
Filename: "{sys}\sc.exe"; Parameters: "stop {#MyAgentServiceName}"; Flags: runhidden; RunOnceId: "StopAgentService"
Filename: "{sys}\sc.exe"; Parameters: "delete {#MyAgentServiceName}"; Flags: runhidden; RunOnceId: "DeleteAgentService"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Promaker Agent Monitoring"""; \
  Flags: runhidden; RunOnceId: "DeleteAgentFirewall"
#endif
#if HasLightHouse
; LightHouseService best-effort cleanup — UI 가 sc create 한 적이 있을 수 있으니 stop+delete.
; Components: ai 가드 — ai 미선택 install 에서는 service 미등록이라 cleanup 자체 불필요.
; Ollama 설치 / bge-m3 모델 / service.pfx / %PROGRAMDATA% config.json 은 사용자 자산 → 보존.
Filename: "{sys}\sc.exe"; Parameters: "stop Ds2.LightHouseService"; Components: ai; Flags: runhidden; RunOnceId: "StopLightHouse"
Filename: "{sys}\sc.exe"; Parameters: "delete Ds2.LightHouseService"; Components: ai; Flags: runhidden; RunOnceId: "DeleteLightHouse"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Ds2 LightHouse Service"""; \
  Components: ai; Flags: runhidden; RunOnceId: "DeleteLightHouseFirewall"
#endif

; ── [Code] 섹션 ──
; InnoDependencyInstaller (fd 모드에서 .NET 런타임 자동 설치). 헤더 `[Code]` 포함.
#include "CodeDependencies.iss"

// 설치 직전 LightHouseService 의 RUNNING 여부 — PrepareToInstall 이 stop 전에 기록하고,
// 설치 후 [Run] 의 LightHouseWasRunning Check 가 이 값을 보고 service 재시작 여부를 결정한다.
var
  GLightHouseWasRunning: Boolean;

// [Run] 의 'sc start Ds2.LightHouseService' Check. 설치 전 RUNNING 이었고 + 이번 설치에서 AI 컴포넌트를
// 선택(= LightHouseService 파일 갱신)한 경우에만 재시작 → 사용자가 켜 두었던 서비스를 설치 후 자동 복원.
// 설치 전 미등록 / STOPPED 였으면 False → 미실행으로 현 상태 유지.
function LightHouseWasRunning: Boolean;
begin
  Result := GLightHouseWasRunning and WizardIsComponentSelected('ai');
end;

// install 진입 시 자동 stop — service / process 가 살아있으면 dll/exe file lock 으로 [Files] copy fail.
// CloseApplications=yes 는 user-mode process 만 — Windows Service 미해당. 본 함수가 service stop 보완.
// sc/taskkill 모두 미설치 / 미실행 시 비-0 반환하지만 결과 무시 (best-effort).
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  // stop 으로 상태가 바뀌기 전에 RUNNING 여부를 먼저 포착 (설치 후 [Run] 이 조건부 재시작에 사용).
  // cmd 의 findstr exit code 로 판정: 'sc query' 출력에 RUNNING 이 있으면 findstr=0,
  // STOPPED / 미등록(1060) 이면 비-0. Exec 자체 실패(=cmd 미기동) 시에도 안전하게 False.
  if Exec(ExpandConstant('{cmd}'), '/C sc query Ds2.LightHouseService | findstr /C:"RUNNING"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    GLightHouseWasRunning := (ResultCode = 0)
  else
    GLightHouseWasRunning := False;
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop Ds2.LightHouseService', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop PromakerAgentService',  '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Promaker.exe',           '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Promaker.AgentTray.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Promaker.Agent.exe',     '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Ds2.LightHouseService.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  // SCM 의 stop 처리 + DLL handle close 시간 확보 (Kestrel + SQLite 정합). 1.5s = 경험적 최소.
  Sleep(1500);
  Result := '';
end;

#if SelfContainedMode != "true"
// fd 모드: .NET 9 Desktop Runtime이 없으면 자동 다운로드/설치
function InitializeSetup: Boolean;
begin
  Dependency_AddDotNet90Desktop;
  Result := True;
end;
#endif
