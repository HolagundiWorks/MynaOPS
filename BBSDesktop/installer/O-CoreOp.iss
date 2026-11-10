; MynaOps — Inno Setup script (per-user, no admin)
; Build via: powershell -File build-installer.ps1
; With /DWithCad /DCadStage=<folder holding app\ODraft.WinUi.exe> it builds the combined Setup, MynaOpsCAD-Setup, which installs MynaOps and MynaCAD
; together (they depend on each other and share the project file). build-all.ps1 in the MynaHub repository does that.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#define MyAppName "MynaOps"
#ifdef WithCad
  #ifndef CadStage
    #error WithCad needs /DCadStage=<folder that holds app\ODraft.WinUi.exe>
  #endif
  #define MyBundleName "MynaOps and MynaCAD"
  #define MyOutName "MynaOpsCAD-Setup-" + AppVersion
#else
  #define MyBundleName "MynaOps"
  #define MyOutName "O-CoreOp-Setup-" + AppVersion
#endif
#define MyAppPublisher "Human Centric Works"
#define MyAppPublisherUrl "https://humancentic.works"
#define MyAppExeName "O-CoreOp.exe"
#define MyAppId "{{A8F3C2E1-9B4D-4E6A-8C1F-2D7E5A9B0C3D}"
; The assistant model: Olmo-3-7B-Instruct Q4_K_M from unsloth/Olmo-3-7B-Instruct-GGUF (Apache-2.0), pinned revision, shared with MynaCAD.
#define ModelFile "Olmo-3-7B-Instruct-Q4_K_M.gguf"
#define ModelSha256 "0de0bc63fae8a73a48e167be48f149c9544bd4af61d223cd3f331c6ea664c5c4"
#define ModelUrl "https://huggingface.co/unsloth/Olmo-3-7B-Instruct-GGUF/resolve/a2a9ae53f5a95087616b3f45ca96e9d2631344ee/" + ModelFile

[Setup]
AppId={#MyAppId}
AppName={#MyBundleName}
AppVersion={#AppVersion}
AppVerName={#MyBundleName} {#AppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppPublisherUrl}
AppSupportURL={#MyAppPublisherUrl}
DefaultDirName={localappdata}\Programs\O-CoreOp
DefaultGroupName={#MyBundleName}
UsePreviousAppDir=no
UsePreviousGroup=no
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts\installer
OutputBaseFilename={#MyOutName}
SetupIconFile=..\BBSApp\Assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoProductName={#MyBundleName}
VersionInfoCopyright=Copyright © {#MyAppPublisher}, Hospet
LicenseFile=..\..\LICENSE
ChangesAssociations=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "model"; Description: "Download the assistant model (Olmo 3 7B, 4.5 GB, runs on this computer only; MynaCAD uses it too)"; GroupDescription: "Assistant:"

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
#ifdef WithCad
; MynaCAD: the portable layout. The folder keeps the old name O-Draft on purpose (an identifier), so an earlier install is replaced in place.
Source: "{#CadStage}\app\*"; DestDir: "{localappdata}\Programs\O-Draft"; Flags: ignoreversion recursesubdirs createallsubdirs
#endif

[Registry]
; The Myna Suite project file (.osp): a right-click "Open in MynaOps". Whichever app registered first stays the default (MynaCAD, when installed with the suite).
Root: HKCU; Subkey: "Software\Classes\.osp"; ValueType: string; ValueName: ""; ValueData: "OSAS.Project"; Flags: createvalueifdoesntexist
Root: HKCU; Subkey: "Software\Classes\OSAS.Project"; ValueType: string; ValueName: ""; ValueData: "Myna Suite project"; Flags: createvalueifdoesntexist
Root: HKCU; Subkey: "Software\Classes\OSAS.Project\shell\ocoreop"; ValueType: string; ValueName: ""; ValueData: "Open in MynaOps"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\OSAS.Project\shell\ocoreop\command"; ValueType: string; ValueName: ""; ValueData: """{localappdata}\Programs\O-CoreOp\{#MyAppExeName}"" ""%1"""
#ifdef WithCad
; Double-click opens the project in MynaCAD; MynaOps keeps the right-click verb. Neither starts Myna.
Root: HKCU; Subkey: "Software\Classes\OSAS.Project\shell\open"; ValueType: string; ValueName: ""; ValueData: "Open in MynaCAD"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\OSAS.Project\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{localappdata}\Programs\O-Draft\ODraft.WinUi.exe"" ""%1"""
#endif

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon
#ifdef WithCad
Name: "{group}\MynaCAD"; Filename: "{localappdata}\Programs\O-Draft\ODraft.WinUi.exe"; WorkingDir: "{localappdata}\Programs\O-Draft"
Name: "{autodesktop}\MynaCAD"; Filename: "{localappdata}\Programs\O-Draft\ODraft.WinUi.exe"; WorkingDir: "{localappdata}\Programs\O-Draft"; Tasks: desktopicon
#endif

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[InstallDelete]
; Earlier versions installed as AQC-Core / AQCCore.exe. Remove that program so an upgrade leaves one MynaOps behind.
; Settings in %LocalAppData%\AQCCore are not touched: the app copies them to MynaOps on first run.
Type: filesandordirs; Name: "{localappdata}\Programs\AQC-Core"
; Earlier Setups used the old name for the Start menu folder.
Type: filesandordirs; Name: "{userprograms}\O-CoreOp"
Type: files; Name: "{userdesktop}\O-CoreOp.lnk"
; Setups of 1.6.x used the name Falcon for the Start menu folder and the desktop shortcut.
Type: filesandordirs; Name: "{userprograms}\Falcon"
Type: files; Name: "{userdesktop}\Falcon.lnk"
; MynaOps no longer reads the company file, so versions up to 1.6.1 left an office.dll behind that nothing uses.
Type: files; Name: "{app}\office.dll"
Type: files; Name: "{app}\AQCCore.*"
#ifdef WithCad
; The two apps used to be installed separately: remove their old Start menu folders and shortcuts (names MynaOps, MynaCAD, Tren, O-Draft).
Type: filesandordirs; Name: "{userprograms}\MynaOps"
Type: filesandordirs; Name: "{userprograms}\MynaCAD"
Type: files; Name: "{userprograms}\MynaCAD.lnk"
Type: files; Name: "{userprograms}\Tren.lnk"
Type: files; Name: "{userprograms}\O-Draft.lnk"
Type: files; Name: "{userdesktop}\Tren.lnk"
#endif

#ifdef WithCad
[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\Programs\O-Draft"
#endif

[Code]
var
  ModelPage: TDownloadWizardPage;

function ModelPath: String;
begin
  Result := ExpandConstant('{localappdata}\HumanCentricWorks\models\{#ModelFile}');
end;

procedure InitializeWizard;
begin
  ModelPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), nil);
end;

{ The model is fetched before files are copied, checked against its SHA-256, and moved into the folder both apps read. An existing file is kept. }
function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpReady) and WizardIsTaskSelected('model') and (not FileExists(ModelPath)) then
  begin
    ModelPage.Clear;
    ModelPage.Add('{#ModelUrl}', '{#ModelFile}', '{#ModelSha256}');
    ModelPage.Show;
    try
      try
        ModelPage.Download;
        ForceDirectories(ExtractFileDir(ModelPath));
        if not RenameFile(ExpandConstant('{tmp}\{#ModelFile}'), ModelPath) then
        begin
          CopyFile(ExpandConstant('{tmp}\{#ModelFile}'), ModelPath, False);
          DeleteFile(ExpandConstant('{tmp}\{#ModelFile}'));
        end;
      except
        if ModelPage.AbortedByUser then
          Log('Model download aborted by the user.')
        else
          SuppressibleMsgBox(AddPeriod(GetExceptionMessage) + #13#10 + 'The assistant will not work until {#ModelFile} is in ' + ExtractFileDir(ModelPath) + '.', mbError, MB_OK, IDOK);
        { not fatal: the app installs, and the Ask bar says where the file goes }
      end;
    finally
      ModelPage.Hide;
    end;
  end;
end;
