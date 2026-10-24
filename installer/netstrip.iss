; NetStrip per-user installer (Inno Setup 6).
; Build from the repo root:  iscc installer/netstrip.iss /DAppVersion=<ver>
; Expects the framework-dependent publish output in out/fd-x64 (see .github/workflows/release.yml).

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#define AppName "NetStrip"
#define AppExe "NetStrip.exe"

[Setup]
AppId={{BC0539C1-F886-4D8E-8326-63F5D7EB106D}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Tarek Alnaggar
DefaultDirName={localappdata}\Programs\NetStrip
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
CloseApplications=yes
AppMutex=Local\NetStrip
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile={#SourcePath}\..\src\NetStrip\Assets\NetStrip.ico
UninstallDisplayName={#AppName}
LicenseFile={#SourcePath}\..\LICENSE
OutputDir={#SourcePath}\Output
OutputBaseFilename=NetStrip-{#AppVersion}-setup-x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Tasks]
Name: "autostart"; Description: "Start with Windows"; Flags: checkedonce

[Files]
Source: "{#SourcePath}\..\out\fd-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"

[Registry]
; Run value is removed on uninstall; [Code] also removes it in case the app wrote it later.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "NetStrip"; ValueData: """{app}\{#AppExe}"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch NetStrip"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  RuntimeUrl = 'https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe';

// True when any 10.x Microsoft.WindowsDesktop.App runtime folder exists.
function HasDesktopRuntime10(): Boolean;
var
  FindRec: TFindRec;
  SearchPath: String;
begin
  Result := False;
  SearchPath := ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*');
  if FindFirst(SearchPath, FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and $10) <> 0 then  // $10 = FILE_ATTRIBUTE_DIRECTORY
          Result := True;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

// Runs before files are copied. Downloads and installs the .NET 10 Desktop Runtime only when it is missing.
// The runtime installer is machine-wide, so Windows asks for elevation for that step only.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Installer: String;
  ResultCode: Integer;
begin
  Result := '';
  if HasDesktopRuntime10() then
    Exit;

  Installer := ExpandConstant('{tmp}\windowsdesktop-runtime-win-x64.exe');
  if (not Exec(ExpandConstant('{sysnative}\curl.exe'),
               '-L --fail --silent --show-error -o "' + Installer + '" ' + RuntimeUrl,
               '', SW_HIDE, ewWaitUntilTerminated, ResultCode)) or (ResultCode <> 0) then
  begin
    Result := 'Could not download the .NET 10 Desktop Runtime. Install it from ' +
              'https://dotnet.microsoft.com/download/dotnet/10.0 and run this setup again.';
    Exit;
  end;

  if not ShellExec('runas', Installer, '/install /quiet /norestart', '', SW_SHOW,
                   ewWaitUntilTerminated, ResultCode) then
    Result := 'The .NET 10 Desktop Runtime installer could not be started.'
  else if (ResultCode <> 0) and (ResultCode <> 3010) then
    Result := 'The .NET 10 Desktop Runtime installation did not complete (exit code ' +
              IntToStr(ResultCode) + ').';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RegDeleteValue(HKEY_CURRENT_USER, RunKey, 'NetStrip');
end;
