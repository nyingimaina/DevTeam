; DevTeam InnoSetup installer
; Build with: packaging/build-installer.ps1  (which calls: iscc.exe /DAppVersion=<version> devteam.iss)
;
; Packages the broker (back-end), the statically-exported web UI (front-end, delivered inside
; the broker's wwwroot) and the Avalonia/WebView2 desktop shell into ONE per-user artifact:
;   installer/DevTeam-Setup-<version>-win-x64.exe
;
; Requirements implemented here:
;   REQ-1  one artifact packages broker + web UI + shell
;   REQ-2  per-user install, no elevation
;   REQ-3  .NET 10 runtime detected and installed if missing
;   REQ-4  WebView2 runtime detected and offered if missing
;   REQ-7  version comes from the build (ISPP AppVersion define)
;   REQ-8  uninstall offers to remove user data
;   REQ-10 Start Menu entry only
;   REQ-15 loopback only - no inbound-port rules / no elevation
;   REQ-16 no secrets packaged
;   REQ-18 plain-language pages and prompts
;   REQ-19 compressed, framework-dependent (no runtime bundled)

#define AppName "DevTeam"
#define AppPublisher "DevTeam"
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
; REQ-2: per-user only, never elevates.
AppId={{6F1B6C7A-3B2E-4A7E-9C1D-2D9B6E0F5A11}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\DevTeam
DefaultGroupName=DevTeam
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
; SourceDir is the repo root (this .iss lives in packaging\).
SourceDir=..
OutputDir=installer
OutputBaseFilename=DevTeam-Setup-{#AppVersion}-win-x64
; REQ-19: solid LZMA2 keeps the framework-dependent artifact small.
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\DevTeam.Desktop.exe
; REQ-4: the shell is x64, so declare it. Inno Setup otherwise runs in 32-bit mode, where the
; registry lookup is redirected and disagrees with the app about the WebView2 runtime.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Files]
; REQ-1: broker + web UI (wwwroot) + shell all ship together as one product.
; The build script publishes both projects (and the front-end export) into publish\app.
; Excludes bin_verify\* so the verification pipeline's scratch output is never packaged.
Source: "publish\app\*"; DestDir: "{app}"; Excludes: "bin_verify\*"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; REQ-10: Start Menu only - no desktop icon, no launch-on-login entry.
Name: "{autoprograms}\DevTeam"; Filename: "{app}\DevTeam.Desktop.exe"; WorkingDir: "{app}"

[Run]
Filename: "{app}\DevTeam.Desktop.exe"; Description: "Open DevTeam"; Flags: nowait postinstall skipifsilent

[Code]
const
  DotNetRuntimeKey = 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.AspNetCore.App';
  DotNetCoreKey = 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.NETCore.App';
  WebView2ClientKey = 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  { The Evergreen runtime is registered by the 32-bit EdgeUpdate agent, so on x64 it lives here. }
  WebView2ClientKey32 = 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

var
  DotNetPage: TInputOptionWizardPage;
  WebView2Page: TInputOptionWizardPage;

function HasVersion10(const SubKey: string): Boolean;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if RegGetValueNames(HKLM, SubKey, Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if Pos('10.', Names[I]) = 1 then
      begin
        Result := True;
        Exit;
      end;
end;

{ REQ-3: is the .NET 10 runtime (broker needs ASP.NET Core, shell needs the base runtime) present? }
function IsDotNet10Installed: Boolean;
begin
  Result := HasVersion10(DotNetRuntimeKey) and HasVersion10(DotNetCoreKey);
end;

{ REQ-4: is the WebView2 Evergreen runtime present? Checks both registry views so the answer
  matches what the app itself reports. }
function IsWebView2Installed: Boolean;
var
  Version: string;
begin
  Result :=
    RegQueryStringValue(HKLM, WebView2ClientKey, 'pv', Version) or
    RegQueryStringValue(HKLM, WebView2ClientKey32, 'pv', Version) or
    RegQueryStringValue(HKCU, WebView2ClientKey, 'pv', Version) or
    RegQueryStringValue(HKCU, WebView2ClientKey32, 'pv', Version);
end;

procedure InitializeWizard;
begin
  if not IsDotNet10Installed then
  begin
    DotNetPage := CreateInputOptionPage(wpSelectTasks,
      'Microsoft .NET runtime',
      'DevTeam needs a free Microsoft runtime',
      'DevTeam needs the Microsoft .NET 10 runtime to run. It is not installed on this PC yet.'#13#10#13#10 +
      'You can let DevTeam download and install it now, or install it yourself later from ' +
      'https://dotnet.microsoft.com/download/dotnet/10.0 and then open DevTeam again.',
      False, False);
    DotNetPage.Add('Download and install the .NET 10 runtime for me (recommended)');
    DotNetPage.Values[0] := True;
  end;

  if not IsWebView2Installed then
  begin
    WebView2Page := CreateInputOptionPage(wpSelectTasks,
      'In-app browser component',
      'Optional component for the DevTeam window',
      'DevTeam shows its interface inside its own window using the Microsoft WebView2 component, ' +
      'which is not installed on this PC yet.'#13#10#13#10 +
      'If you skip it, DevTeam will open its interface in your normal web browser instead.',
      False, False);
    WebView2Page.Add('Download and install WebView2 (recommended)');
    WebView2Page.Values[0] := True;
  end;
end;

function DownloadAndRun(const Url, FileName, Parameters: string): Boolean;
var
  DownloadPath: string;
  ResultCode: Integer;
begin
  Result := False;
  DownloadPath := ExpandConstant('{tmp}\') + FileName;
  if Exec('powershell.exe',
    '-NoProfile -ExecutionPolicy Bypass -Command "& {[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; Invoke-WebRequest -Uri ''' + Url + ''' -OutFile ''' + DownloadPath + '''}"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    if (ResultCode = 0) and FileExists(DownloadPath) then
      Result := Exec(DownloadPath, Parameters, '', SW_SHOW, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    { REQ-3: offer to install the .NET 10 runtime. }
    if (DotNetPage <> nil) and DotNetPage.Values[0] then
    begin
      if not DownloadAndRun('https://aka.ms/dotnet/10.0/aspnetcore-runtime-win-x64.exe',
        'dotnet-runtime-win-x64.exe', '/install /quiet /norestart') then
        MsgBox('DevTeam could not install the .NET runtime automatically. Please install it from ' +
          'https://dotnet.microsoft.com/download/dotnet/10.0, then open DevTeam.', mbInformation, MB_OK);
    end;

    { REQ-4: offer to install the WebView2 runtime. }
    if (WebView2Page <> nil) and WebView2Page.Values[0] then
    begin
      if not DownloadAndRun('https://go.microsoft.com/fwlink/p/?LinkId=2124703',
        'MicrosoftEdgeWebview2Setup.exe', '/silent /install') then
        MsgBox('DevTeam could not install WebView2 automatically. DevTeam will open its interface ' +
          'in your web browser instead.', mbInformation, MB_OK);
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  { REQ-8: one prompt asking whether to also remove DevTeam user data. }
  if CurUninstallStep = usPostUninstall then
  begin
    if MsgBox('Do you also want to delete your DevTeam data (your workspace history, logs and settings)?'#13#10#13#10 +
      'Choose No to keep your data for a future reinstall.', mbConfirmation, MB_YESNO) = IDYES then
    begin
      DelTree(ExpandConstant('{userprofile}\.devteam'), True, True, True);
      DelTree(ExpandConstant('{localappdata}\DevTeam'), True, True, True);
    end;
  end;
end;
