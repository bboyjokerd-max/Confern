; ============================================================================================
; confern.iss -- the Windows installer for Confern.
;
; WHY INNO SETUP AND NOT THE ALTERNATIVES (measured on this machine 2026-10-23):
;   MSI/WiX      : the "enterprise standard", but a .NET desktop prerequisite in MSI needs a launch
;                  condition plus custom actions, and it is not a single .exe.
;   NSIS         : same capability class as Inno, but every page has to be hand-built and the script
;                  is far harder to read for the next person.
;   IExpress     : built into Windows, produces a .exe, but has no shortcuts, no uninstall entry and
;                  no prerequisite check -- it fails the "what Windows users expect" bar.
;   MSIX         : needs a code-signing certificate and sideloading policy; not universal.
;
; WHAT THIS INSTALLER GUARANTEES (each one is a decision, not an accident):
;   * Installs PER USER by default (%LOCALAPPDATA%\Programs\Confern) so there is no UAC prompt for
;     the normal case. An admin can still choose "install for all users" in the dialog.
;   * Checks for the .NET 8 DESKTOP runtime before touching anything, and offers to download and
;     install it right there. If the user says no, the installer EXITS -- a half-installed app that
;     cannot start is worse than no install.
;     Detection reads C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App\8.* (and the
;     per-user location). It does NOT read the registry: measured on this machine, the documented
;     HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx key does not exist at all, while
;     8.0.6 and 8.0.31 are installed and working.
;   * Does NOT add an autostart entry. The product has its own setting for that; two owners of the
;     same registry value is how you end up with a disabled app that still starts.
;   * Uninstaller removes the app; it ASKS before deleting %LOCALAPPDATA%\Confern (settings and
;     logs), because those are the user's data, not ours.
;   * Same AppId across versions, so a newer setup upgrades in place instead of installing twice.
;
; SILENT: Confern-Setup-x.y.z.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART [/DIR="..."]
; ============================================================================================

#define AppName "Confern"
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef StagingDir
  #error Pass /DStagingDir=<publish output>
#endif
#ifndef OutputDir
  #define OutputDir "dist"
#endif


[Setup]
; A FIXED AppId is what makes an upgrade an upgrade. Never change this value.
AppId={{7C1B4E62-9A3D-4F58-8B2E-5D0C6A1F93E4}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Confern
AppPublisherURL=https://example.invalid/confern
DefaultDirName={autopf}\Confern
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayName={#AppName} {#AppVersion}
UninstallDisplayIcon={app}\Confern.exe
OutputDir={#OutputDir}
OutputBaseFilename=Confern-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Per-user by default; an admin gets the choice in the dialog. Nothing else here needs elevation.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
; CloseApplications: Confern has a single-instance guard, so an upgrade while it runs would leave
; the old build alive and the new files locked. Let Windows close it (and only it) first.
CloseApplications=yes
RestartApplications=no
UsedUserAreasWarning=no
VersionInfoVersion={#AppVersion}.0
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}
VersionInfoDescription={#AppName} installer
DisableWelcomePage=no

[Languages]
; Traditional Chinese first so it is the default; English kept because it is the fallback every
; Inno Setup build has without extra files.
Name: "cht"; MessagesFile: "{#SourcePath}\langs\ChineseTraditional.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startmenuicon"; Description: "在開始功能表建立捷徑"; GroupDescription: "捷徑:"
Name: "desktopicon"; Description: "在桌面建立捷徑"; GroupDescription: "捷徑:"

[Files]
; The published application, exactly as dotnet produced it (0 warnings, 0 errors asserted by the
; build script). recursesubdirs brings models\ along -- and models\ is the reason remove-bg works
; out of the box, so the build script asserts it is in the staging folder before compiling.
;
; The documents are staged under ASCII names (usage.txt / version.txt) and renamed here. Reason: the
; build script that drives this file is PURE ASCII (Windows PowerShell 5.1 reads a BOM-less .ps1 as
; ANSI, so a CJK literal in it is silently mangled -- measured). This file is UTF-8 with BOM, so the
; Chinese names belong here, where they are read correctly.
Source: "{#StagingDir}\*"; DestDir: "{app}"; Excludes: "usage.txt,version.txt"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StagingDir}\usage.txt"; DestDir: "{app}"; DestName: "使用說明.txt"; Flags: ignoreversion
Source: "{#StagingDir}\version.txt"; DestDir: "{app}"; DestName: "版本資訊.txt"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\Confern.exe"; Tasks: startmenuicon
Name: "{group}\使用說明"; Filename: "{app}\使用說明.txt"; Tasks: startmenuicon
Name: "{group}\解除安裝 {#AppName}"; Filename: "{uninstallexe}"; Tasks: startmenuicon
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\Confern.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Confern.exe"; Description: "啟動 {#AppName}"; Flags: nowait postinstall skipifsilent
Filename: "{app}\使用說明.txt"; Description: "開啟使用說明"; Flags: shellexec nowait postinstall unchecked skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
const
  RuntimeUrl = 'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe';
  RuntimePage = 'https://dotnet.microsoft.com/download/dotnet/8.0';

{ TOnDownloadProgress is a FUNCTION returning Boolean (measured against the 6.7 help page after the
  compiler rejected a procedure with "Type mismatch" on line 166): returning False would abort the
  download, so it returns True and only logs. }
function DownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  if ProgressMax <> 0 then
    Log(Format('  .NET runtime: %d of %d bytes', [Progress, ProgressMax]));
  Result := True;
end;

function BoolText(B: Boolean): String;
begin
  if B then Result := 'True' else Result := 'False';
end;
{ Looks for an installed Microsoft.WindowsDesktop.App 8.x. Two roots, because .NET can be installed
  machine-wide or per user, and the product's own runtime check does the same. }
function DesktopRuntime8Present(): Boolean;
var
  Roots: array[0..1] of String;
  I: Integer;
  FindRec: TFindRec;
  Hit: Boolean;
begin
  Result := False;
  Roots[0] := ExpandConstant('{commonpf}\dotnet\shared\Microsoft.WindowsDesktop.App');
  Roots[1] := ExpandConstant('{localappdata}\Microsoft\dotnet\shared\Microsoft.WindowsDesktop.App');
  for I := 0 to 1 do
  begin
    if FindFirst(Roots[I] + '\8.*', FindRec) then
    begin
      try
        Hit := False;
        repeat
          if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
            Hit := True;
        until Hit or (not FindNext(FindRec));
      finally
        FindClose(FindRec);
      end;
      if Hit then
      begin
        Result := True;
        Exit;
      end;
    end;
  end;
end;

function DownloadAndInstallRuntime(): Boolean;
var
  BaseName: String;
  TempFile: String;
  Downloaded: Int64;
  ResultCode: Integer;
begin
  Result := False;
  // DownloadTemporaryFile writes into the temp folder using BaseName and returns the BYTE COUNT
  // (0 when the file was already downloaded), not a path.
  BaseName := 'windowsdesktop-runtime-8-x64.exe';
  TempFile := ExpandConstant('{tmp}') + '\' + BaseName;
  try
    Downloaded := DownloadTemporaryFile(RuntimeUrl, BaseName, '', @DownloadProgress);
  except
    Log('DownloadTemporaryFile failed: ' + GetExceptionMessage);
    Exit;
  end;
  if not FileExists(TempFile) then
  begin
    Log('the download reported ' + IntToStr(Downloaded) + ' bytes but nothing is at ' + TempFile);
    Exit;
  end;
  // 1638 = a newer version is already installed, 3010 = installed and a reboot is pending: both are
  // successes for our purposes.
  if Exec(TempFile, '/install /quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
    Result := (ResultCode = 0) or (ResultCode = 1638) or (ResultCode = 3010);
end;

function InitializeSetup(): Boolean;
var
  Answer: Integer;
  ResultCode: Integer;
  Present: Boolean;
begin
  Result := True;
  Present := DesktopRuntime8Present();
  // Logged on purpose: the drill asserts this line, so "the installer detected the runtime" is
  // evidence in a log file instead of something the author believes.
  Log('dotnet8: present=' + BoolText(Present));
  Log('dotnet8: looked in ' + ExpandConstant('{commonpf}') + '\dotnet\shared\Microsoft.WindowsDesktop.App and ' +
      ExpandConstant('{localappdata}') + '\Microsoft\dotnet\shared\Microsoft.WindowsDesktop.App');
  if Present then
    Exit;

  Answer := MsgBox(
    'Confern 需要 .NET 8 桌面執行階段（約 55 MB）。' + #13#10 + #13#10 +
    '要現在自動下載並安裝嗎？（會需要一次管理員授權）' + #13#10 +
    '選「否」會開啟官方下載頁面，並結束這次安裝。',
    mbConfirmation, MB_YESNO);

  if Answer = IDYES then
  begin
    MsgBox('開始下載 .NET 8 桌面執行階段，完成後安裝程式會繼續。', mbInformation, MB_OK);
    DownloadAndInstallRuntime();
    if DesktopRuntime8Present() then
      Exit;
    MsgBox('沒有偵測到安裝完成的 .NET 8 桌面執行階段。安裝程式會結束，' + #13#10 +
           '請手動安裝後再重新執行本安裝程式。', mbError, MB_OK);
  end;

  ShellExec('open', RuntimePage, '', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
  Result := False;
end;

{ The uninstaller removes the application. Settings and logs are the user's, so they are only
  removed when the user says so -- and never silently. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;
  DataDir := ExpandConstant('{localappdata}\Confern');
  if not DirExists(DataDir) then
    Exit;
  if UninstallSilent then
    Exit;
  if MsgBox('要一併刪除 Confern 的設定與日誌嗎？' + #13#10 + #13#10 + DataDir,
            mbConfirmation, MB_YESNO) = IDYES then
    DelTree(DataDir, True, True, True);
end;