; Windows installer for the add-in (Inno Setup 6).
;   iscc /DAppVersion=0.2.0 installer\SolidWorksAgent.iss      -> dist\SolidWorks-Agent-Setup-0.2.0.exe
; Built by .github/workflows/release.yml for every v* tag; scripts/make-installer.ps1 builds it locally.
;
; Installs machine-wide (registering a SolidWorks add-in writes HKLM), registers the COM add-in with RegAsm,
; and installs the WebView2 runtime when it's missing. Updates run this same installer silently
; (see src/SwAgent/update.ps1), so it must work unattended: /VERYSILENT /SUPPRESSMSGBOXES.

; Product name in one place: change it here (and in the add-in's [Title]) to rebrand.
#define AppName "SolidWorks Agent"
#define AppPublisher "msmith297"
#define AppUrl "https://github.com/msmith297/solidworks-agent"
#define OutputName "SolidWorks-Agent-Setup"
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
; Windows file versions are numbers only: a prerelease such as 0.2.0-nightly.20261015 passes /DFileVersion=0.2.0.
#ifndef FileVersion
  #define FileVersion AppVersion
#endif
; Inno reads "{" as a constant: the GUID's opening brace is doubled.
#define AddInGuid "{{6D2B1C1E-4A0F-4F1E-9C7B-3E5A8D2F7B41}"
#define SourceDir "..\src\SwAgent\bin\Release\net48"

[Setup]
AppId={{9C4E2A71-5B3D-4F8E-A6C2-1D7B0E93F5A4}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#FileVersion}
DefaultDirName={autopf}\{#AppName}
; Updates from the old Unify3D Agent folder move here (see [InstallDelete]).
UsePreviousAppDir=no
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\dist
OutputBaseFilename={#OutputName}-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; SolidWorks locks the add-in while it runs: let Windows' Restart Manager offer to close it.
CloseApplications=yes
RestartApplications=no
UninstallDisplayName={#AppName}
SetupIconFile=..\assets\brand\solidworks-agent.ico
UninstallDisplayIcon={app}\solidworks-agent.ico

[Files]
Source: "..\assets\brand\solidworks-agent.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,*.old,*.xml"; Flags: ignoreversion recursesubdirs createallsubdirs
; Evergreen bootstrapper (downloaded by the release workflow); only used when WebView2 is missing.
Source: "redist\MicrosoftEdgeWebview2Setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall skipifsourcedoesntexist; Check: NeedsWebView2

[InstallDelete]
; The folder of versions from before the rename to SolidWorks Agent.
Type: filesandordirs; Name: "{autopf}\Unify3D Agent"
; Old UI bundles (file names change every build).
Type: filesandordirs; Name: "{app}\wwwroot"
; Built-in tools are replaced as a whole (a tool removed from the add-in must not linger).
Type: filesandordirs; Name: "{app}\builtin-tools"
; The built-in plugin marketplace likewise (installed plugins live in %APPDATA%\SwAgent\plugins, not here).
Type: filesandordirs; Name: "{app}\builtin-plugins"

[Registry]
; Load at SolidWorks start-up by default for every user (RegAsm adds the add-in entry itself).
Root: HKLM; Subkey: "SOFTWARE\SolidWorks\Addins\{#AddInGuid}"; ValueType: dword; ValueName: ""; ValueData: 1; Flags: uninsdeletekey

[Run]
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "Installing the Microsoft Edge WebView2 runtime..."; Check: NeedsWebView2; Flags: waituntilterminated skipifdoesntexist
Filename: "{dotnet4064}\RegAsm.exe"; Parameters: "/codebase /nologo ""{app}\SwAgent.dll"""; StatusMsg: "Registering the add-in with SolidWorks..."; Flags: runhidden waituntilterminated

[UninstallRun]
Filename: "{dotnet4064}\RegAsm.exe"; Parameters: "/u /nologo ""{app}\SwAgent.dll"""; Flags: runhidden waituntilterminated; RunOnceId: "UnregisterAddIn"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
const
  WebView2Key = 'Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  MinSolidWorksYear = 2024;

function DotNet48Installed: Boolean;
var
  Release: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release) and (Release >= 528040);
end;

function HasWebView2(Root: Integer; Key: String): Boolean;
var
  Version: String;
begin
  Result := RegQueryStringValue(Root, Key, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0');
end;

function NeedsWebView2: Boolean;
begin
  Result := not (HasWebView2(HKLM, 'SOFTWARE\WOW6432Node\' + WebView2Key) or HasWebView2(HKLM, 'SOFTWARE\' + WebView2Key) or
                 HasWebView2(HKCU, 'Software\' + WebView2Key));
end;

{ Newest installed SOLIDWORKS release year from HKLM\SOFTWARE\SolidWorks\SOLIDWORKS 20xx, or 0. }
function NewestSolidWorksYear: Integer;
var
  Names: TArrayOfString;
  I, Year: Integer;
begin
  Result := 0;
  if RegGetSubkeyNames(HKLM, 'SOFTWARE\SolidWorks', Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if Pos('SOLIDWORKS 20', Uppercase(Names[I])) = 1 then
      begin
        Year := StrToIntDef(Copy(Names[I], 12, 4), 0);
        if Year > Result then Result := Year;
      end;
end;

function InitializeSetup: Boolean;
var
  Year: Integer;
begin
  Result := True;
  if not DotNet48Installed then
  begin
    SuppressibleMsgBox('{#AppName} needs the .NET Framework 4.8. Install it from Microsoft (Windows Update usually has it), then run this installer again.',
      mbCriticalError, MB_OK, IDOK);
    Result := False;
    Exit;
  end;
  Year := NewestSolidWorksYear;
  if Year = 0 then
    Result := SuppressibleMsgBox('SOLIDWORKS doesn''t seem to be installed on this computer. {#AppName} is a SOLIDWORKS add-in and only works inside SOLIDWORKS ' +
      IntToStr(MinSolidWorksYear) + ' or newer.' + #13#10#13#10 + 'Install anyway?', mbConfirmation, MB_YESNO, IDYES) = IDYES
  else if Year < MinSolidWorksYear then
    Result := SuppressibleMsgBox('{#AppName} supports SOLIDWORKS ' + IntToStr(MinSolidWorksYear) + ' and newer; this computer has SOLIDWORKS ' + IntToStr(Year) +
      '. It may not work correctly.' + #13#10#13#10 + 'Install anyway?', mbConfirmation, MB_YESNO, IDYES) = IDYES;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    Log('Installed {#AppName} {#AppVersion} for SOLIDWORKS ' + IntToStr(NewestSolidWorksYear));
end;
