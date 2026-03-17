; Promaker installer script
; Requires: Inno Setup 6.4+
; Build steps:
; 1) Run publish.bat
; 2) Or publish manually:
;    dotnet publish ..\Promaker\Promaker.csproj -c Release -r win-x64 --self-contained false -o ..\Promaker\bin\Release\net9.0-windows\win-x64\publish
; 3) Open this file in Inno Setup and build

; InnoDependencyInstaller (fd 모드에서 .NET 런타임 자동 설치)
#include "CodeDependencies.iss"

#ifndef PublishDir
  #define PublishDir "..\Promaker\bin\Release\net9.0-windows\win-x64\publish-self-contained"
#endif
#ifndef SelfContainedMode
  #define SelfContainedMode "true"
#endif
#ifndef OutputSuffix
  #define OutputSuffix "_sc"
#endif

#define AppExePath AddBackslash(PublishDir) + "Promaker.exe"
#define SetupIconPath "..\Promaker\Assets\PromakerTemp.ico"
#define MyAppName "Promaker"
#define MyAppVersion GetVersionNumbersString(AppExePath)
#define MyAppPublisher "Dualsoft"
#define MyAppURL "https://dualsoft.co.kr"
#define MyExeName "Promaker.exe"

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
OutputBaseFilename=Promaker_Setup_{#MyAppVersion}{#OutputSuffix}
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

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent unchecked

#if SelfContainedMode == "true"
; sc 모드: .NET 런타임이 번들되어 있으므로 추가 설치 불필요
#else
; fd 모드: .NET 9 Desktop Runtime이 없으면 자동 다운로드/설치
function InitializeSetup: Boolean;
begin
  Dependency_AddDotNet90Desktop;
  Result := True;
end;
#endif
