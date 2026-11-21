; OvenTracker's Windows installer (Inno Setup 6). Built by packaging/build-installer.ps1, which passes:
;   /DAppVersion=1.2.3         the version
;   /DSourceDir=<dir>          the published framework-dependent win-x64 app (artifacts/publish/win-x64)
;   /DOutputDir=<dir>          where the installer goes (artifacts)
;   /DSign + /Soventracker=... to sign the installer and its uninstaller (omitted: unsigned)
;
; It installs for the current user only (no administrator rights), under %LOCALAPPDATA%\Programs\OvenTracker,
; with a Start menu entry and, optionally, OvenTracker on the user's PATH. The app needs the .NET 11 runtime:
; if it isn't installed, the installer offers to download Microsoft's runtime installer and run it. That one
; step asks Windows for permission, because the runtime is installed for everyone on the PC.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

#define AppName "OvenTracker"
#define AppExe "OvenTracker.exe"
#define RuntimeMajor "11"

[Setup]
AppId={{E1DFFEAD-578A-440C-B8F1-859C4A67701B}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=The Schnauzer Group LLC
AppPublisherURL=https://github.com/TGoodhew/OvenTracker
AppSupportURL=https://github.com/TGoodhew/OvenTracker/issues
AppCopyright=© The Schnauzer Group LLC
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} setup
; Per user: {autopf} is %LOCALAPPDATA%\Programs when installing without administrator rights.
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename={#AppName}-{#AppVersion}-win-x64-setup
SetupIconFile=icon\oventracker.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName} {#AppVersion}
LicenseFile=..\LICENSE
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ChangesEnvironment=yes
#ifdef Sign
SignTool=oventracker
SignedUninstaller=yes
#endif

[Tasks]
Name: "addtopath"; Description: "Add OvenTracker to my PATH, so it runs from any terminal"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; A console app: the shortcut opens a Command Prompt in Documents, with the app's help shown.
; cmd /k strips the outer pair of quotes, so the whole command is wrapped in one more.
Name: "{group}\{#AppName}"; Filename: "{cmd}"; Parameters: "/k """"{app}\{#AppExe}"" --help"""; WorkingDir: "{userdocs}"; IconFilename: "{app}\{#AppExe}"; Comment: "Measure an oven's temperature accuracy"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"

[Code]
const
  RuntimeUrl = 'https://aka.ms/dotnet/{#RuntimeMajor}.0/dotnet-runtime-win-x64.exe';
  RuntimeFile = 'dotnet-runtime-{#RuntimeMajor}-win-x64.exe';
  RuntimePage = 'https://dotnet.microsoft.com/download/dotnet/{#RuntimeMajor}.0';
  EnvironmentKey = 'Environment';

var
  DownloadPage: TDownloadWizardPage;

{ Whether a folder holds a Microsoft.NETCore.App {#RuntimeMajor}.x runtime. }
function HasRuntimeIn(DotnetDir: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if DotnetDir = '' then Exit;
  if FindFirst(AddBackslash(DotnetDir) + 'shared\Microsoft.NETCore.App\{#RuntimeMajor}.*', FindRec) then
  begin
    Result := True;
    FindClose(FindRec);
  end;
end;

{ The .NET {#RuntimeMajor} runtime, where the app's launcher looks for it: DOTNET_ROOT, the location the .NET
  installer registers, then Program Files. }
function RuntimeInstalled: Boolean;
var
  Location: String;
begin
  Result := HasRuntimeIn(GetEnv('DOTNET_ROOT')) or HasRuntimeIn(ExpandConstant('{commonpf64}\dotnet'));
  if not Result and RegQueryStringValue(HKLM32, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64', 'InstallLocation', Location) then
    Result := HasRuntimeIn(Location);
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), nil);
end;

{ Before installing: if the runtime is missing, offer to fetch Microsoft's installer and run it, elevated. The
  app is installed either way; without the runtime it says so, and how to get it. }
function NextButtonClick(CurPageID: Integer): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  if (CurPageID <> wpReady) or RuntimeInstalled then Exit;

  if WizardSilent then
  begin
    Log('The .NET {#RuntimeMajor} runtime is not installed; a silent install leaves it to the caller.');
    Exit;
  end;

  if MsgBox('OvenTracker needs the .NET {#RuntimeMajor} runtime, which isn''t installed on this PC.' + #13#10#13#10 +
            'Download it from Microsoft and install it now? Windows will ask for permission, because the runtime ' +
            'is installed for everyone on the PC.', mbConfirmation, MB_YESNO) <> IDYES then
  begin
    MsgBox('OvenTracker will be installed, but it won''t run until the .NET {#RuntimeMajor} runtime is installed: ' + RuntimePage,
           mbInformation, MB_OK);
    Exit;
  end;

  DownloadPage.Clear;
  DownloadPage.Add(RuntimeUrl, RuntimeFile, '');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
    except
      MsgBox('Couldn''t download the .NET {#RuntimeMajor} runtime: ' + GetExceptionMessage + #13#10#13#10 +
             'OvenTracker will be installed; get the runtime from ' + RuntimePage, mbError, MB_OK);
      Exit;
    end;
  finally
    DownloadPage.Hide;
  end;

  if not ShellExec('runas', ExpandConstant('{tmp}\' + RuntimeFile), '/install /quiet /norestart', '', SW_SHOW,
                   ewWaitUntilTerminated, ResultCode) then
    MsgBox('The .NET {#RuntimeMajor} runtime wasn''t installed (' + SysErrorMessage(ResultCode) + '). ' +
           'OvenTracker will be installed; get the runtime from ' + RuntimePage, mbError, MB_OK)
  else if (ResultCode <> 0) and (ResultCode <> 3010) then { 3010: installed, restart needed }
    MsgBox('The .NET {#RuntimeMajor} runtime installer stopped with code ' + IntToStr(ResultCode) + '. ' +
           'OvenTracker will be installed; get the runtime from ' + RuntimePage, mbError, MB_OK);
end;

{ The user's PATH, as Windows keeps it (HKCU\Environment\Path). }
function UserPath: String;
begin
  if not RegQueryStringValue(HKCU, EnvironmentKey, 'Path', Result) then Result := '';
end;

function PathHas(Paths, Dir: String): Boolean;
begin
  Result := Pos(';' + Uppercase(RemoveBackslashUnlessRoot(Dir)) + ';', ';' + Uppercase(Paths) + ';') > 0;
end;

procedure AddToUserPath(Dir: String);
var
  Paths: String;
begin
  Paths := UserPath;
  if PathHas(Paths, Dir) then Exit;
  if (Paths <> '') and (Copy(Paths, Length(Paths), 1) <> ';') then Paths := Paths + ';';
  RegWriteExpandStringValue(HKCU, EnvironmentKey, 'Path', Paths + Dir);
end;

procedure RemoveFromUserPath(Dir: String);
var
  Paths, Entry, Kept: String;
  P: Integer;
begin
  Paths := UserPath;
  if not PathHas(Paths, Dir) then Exit;
  Kept := '';
  while Paths <> '' do
  begin
    P := Pos(';', Paths);
    if P = 0 then
    begin
      Entry := Paths;
      Paths := '';
    end
    else
    begin
      Entry := Copy(Paths, 1, P - 1);
      Delete(Paths, 1, P);
    end;
    if (Entry <> '') and (CompareText(RemoveBackslashUnlessRoot(Entry), RemoveBackslashUnlessRoot(Dir)) <> 0) then
    begin
      if Kept <> '' then Kept := Kept + ';';
      Kept := Kept + Entry;
    end;
  end;
  RegWriteExpandStringValue(HKCU, EnvironmentKey, 'Path', Kept);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('addtopath') then
    AddToUserPath(ExpandConstant('{app}'));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RemoveFromUserPath(ExpandConstant('{app}'));
end;
