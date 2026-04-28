; AasxEditor.Desktop Inno Setup Script
; Self-contained offline installer for AASX JSON Editor (WPF + BlazorWebView).
;
; Build prerequisites (run from Apps/AasxEditor):
;   1) pwsh -ExecutionPolicy Bypass -File scripts\fetch-monaco.ps1
;      → populates AasxEditor.Core\wwwroot\lib\monaco-editor (~13MB)
;   2) dotnet publish AasxEditor.Desktop\AasxEditor.Desktop.csproj ^
;        -c Release -p:PublishProfile=win-x64
;      → produces self-contained output (no .NET runtime required on target)
;   3) (Optional, for true offline install) Place
;      MicrosoftEdgeWebView2RuntimeInstallerX64.exe into Installer\Redist\
;      (download once from https://developer.microsoft.com/microsoft-edge/webview2/).
;      If absent, the installer falls back to the small Evergreen bootstrapper
;      that requires internet access.
;   4) Compile this script with Inno Setup 6 (ISCC.exe).
;
; build-installer.bat in this folder automates steps 1–4.

#define MyAppName "AASX JSON Editor"
#define MyAppShortName "AasxEditor"
#define MyPublishDir "..\AasxEditor.Desktop\bin\Release\net9.0-windows\win-x64\publish"
#define MyAppExePath MyPublishDir + "\AasxEditor.Desktop.exe"
#define MyAppVersion GetVersionNumbersString(MyAppExePath)
#define MyAppPublisher "Dualsoft"
#define MyAppURL "https://dualsoft.co.kr"
#define MyAppExeName "AasxEditor.Desktop.exe"
#define MyAppIcon "Assets\AasxEditor.ico"

[Setup]
AppId={{B7E2F9A4-3D1C-4F8E-9C2A-6E5B8D4F1A7C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppPublisher}\{#MyAppShortName}
DefaultGroupName={#MyAppName}
OutputDir=Output
OutputBaseFilename=AasxEditor_Setup_{#MyAppVersion}
SetupIconFile={#MyAppIcon}
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
MinVersion=10.0
DisableProgramGroupPage=yes
ChangesAssociations=yes

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "associate";   Description: "AASX(.aasx) 파일을 이 프로그램과 연결"; GroupDescription: "파일 연결:"; Flags: unchecked

[Files]
; Self-contained publish output (.NET runtime, all DLLs, wwwroot incl. Monaco)
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; App icon
Source: "{#MyAppIcon}"; DestDir: "{app}"; Flags: ignoreversion

; WebView2 redistributables: prefer offline standalone (large, ~170 MB) when present;
; otherwise fall back to evergreen bootstrapper (small, requires internet).
; skipifsourcedoesntexist allows the script to compile even if neither file is bundled.
Source: "Redist\MicrosoftEdgeWebView2RuntimeInstallerX64.exe"; DestDir: "{tmp}"; \
  Flags: deleteafterinstall skipifsourcedoesntexist; Check: WebView2NotInstalled
Source: "Redist\MicrosoftEdgeWebview2Setup.exe"; DestDir: "{tmp}"; \
  Flags: deleteafterinstall skipifsourcedoesntexist; Check: WebView2NotInstalled and (not FileExists(ExpandConstant('{tmp}\MicrosoftEdgeWebView2RuntimeInstallerX64.exe')))

[Icons]
Name: "{group}\{#MyAppName}";  Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\AasxEditor.ico"
Name: "{group}\제거 ({#MyAppName})"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\AasxEditor.ico"; Tasks: desktopicon

[Registry]
; .aasx file association — only when the user opted in.
Root: HKA; Subkey: "Software\Classes\.aasx"; ValueType: string; ValueName: ""; ValueData: "AasxEditor.AasxFile"; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\AasxEditor.AasxFile"; ValueType: string; ValueName: ""; ValueData: "AASX File"; Flags: uninsdeletekey; Tasks: associate
Root: HKA; Subkey: "Software\Classes\AasxEditor.AasxFile\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\AasxEditor.ico,0"; Tasks: associate
Root: HKA; Subkey: "Software\Classes\AasxEditor.AasxFile\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: associate

[Run]
; Install WebView2 runtime if missing — silent, blocking.
Filename: "{tmp}\MicrosoftEdgeWebView2RuntimeInstallerX64.exe"; Parameters: "/silent /install"; \
  StatusMsg: "Microsoft Edge WebView2 런타임 설치 중 (오프라인)..."; \
  Flags: waituntilterminated; \
  Check: WebView2NotInstalled and FileExists(ExpandConstant('{tmp}\MicrosoftEdgeWebView2RuntimeInstallerX64.exe'))
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; \
  StatusMsg: "Microsoft Edge WebView2 런타임 다운로드 및 설치 중..."; \
  Flags: waituntilterminated; \
  Check: WebView2NotInstalled and FileExists(ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe'))

; Optional: launch the app after install completes.
Filename: "{app}\{#MyAppExeName}"; Description: "지금 실행"; Flags: postinstall nowait skipifsilent

[Code]
function WebView2NotInstalled(): Boolean;
var
  v: string;
begin
  // Check both per-machine and per-user registry locations for the Evergreen runtime.
  Result := True;
  if RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', v) and (v <> '') then
    Result := False
  else if RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', v) and (v <> '') then
    Result := False
  else if RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', v) and (v <> '') then
    Result := False;
end;

procedure InitializeWizard();
begin
  // Surface a friendly heads-up early when no offline WebView2 redist is bundled
  // and the runtime is not already installed — installer will then need internet.
  if WebView2NotInstalled() and
     (not FileExists(ExpandConstant('{src}\Redist\MicrosoftEdgeWebView2RuntimeInstallerX64.exe'))) and
     (not FileExists(ExpandConstant('{src}\Redist\MicrosoftEdgeWebview2Setup.exe'))) then
  begin
    MsgBox('Microsoft Edge WebView2 런타임이 감지되지 않았고 오프라인 설치 파일도 함께 패키징되지 않았습니다.' + #13#10 +
           '대부분의 Windows 10/11 시스템에는 이미 설치되어 있습니다.' + #13#10 +
           '만약 누락된 경우, 설치 종료 후 앱이 실행되지 않을 수 있으니 별도로 설치해 주세요.',
           mbInformation, MB_OK);
  end;
end;
