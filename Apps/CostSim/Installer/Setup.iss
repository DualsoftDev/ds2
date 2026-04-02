; CostSim installer script
; Requires: Inno Setup 6.4+
; Build steps:
; 1) Run publish.bat
; 2) Or publish manually:
;    dotnet publish ..\CostSim.csproj -c Release -r win-x64 --self-contained false -o ..\bin\Release\net9.0-windows\win-x64\publish-framework-dependent
; 3) Open this file in Inno Setup and build

; InnoDependencyInstaller (.NET Desktop Runtime install for framework-dependent mode)
#include "CodeDependencies.iss"

#ifndef PublishDir
  #define PublishDir "..\bin\Release\net9.0-windows\win-x64\publish-self-contained"
#endif
#ifndef SelfContainedMode
  #define SelfContainedMode "true"
#endif
#ifndef OutputSuffix
  #define OutputSuffix "_sc"
#endif

#define AppExePath AddBackslash(PublishDir) + "CostSim.exe"
#define SetupIconPath "..\Assets\CostSim.ico"
#define MyAppName "CostSim"
#define MyAppVersion GetVersionNumbersString(AppExePath)
#define MyAppPublisher "Dualsoft"
#define MyAppURL "https://dualsoft.co.kr"
#define MyExeName "CostSim.exe"

[Setup]
AppId={{EE970244-E8D0-4DA4-8D15-D62136FE6E91}
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
OutputBaseFilename=CostSim_Setup_{#MyAppVersion}{#OutputSuffix}
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
; self-contained mode: .NET runtime bundled
#else
; framework-dependent mode: install .NET 9 Desktop Runtime when missing
function InitializeSetup: Boolean;
begin
  Dependency_AddDotNet90Desktop;
  Result := True;
end;
#endif
