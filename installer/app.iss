; 战网账号切换管理器 —— Inno Setup 安装脚本
; 编译:  "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" installer\app.iss
; 产物:  installer\out\战网账号切换_安装_v1.2.0.exe
;
; 说明:框架依赖版(小),安装时检测 .NET 8 桌面运行时,缺了自动下载官方安装器静默安装。

#define AppName "战网账号切换管理器"
#define AppVer  "2.1.0"
#define AppExe  "BnetSwitch.exe"
#define AppPublisher "qiyh99"

[Setup]
AppId={{8F3A2B10-9C4D-4E7F-A1B2-3C4D5E6F7A8B}
AppName={#AppName}
AppVersion={#AppVer}
AppVerName={#AppName} v{#AppVer}
AppPublisher={#AppPublisher}
AppPublisherURL=https://github.com/qiyh99
DefaultDirName={autopf}\BnetSwitch
DefaultGroupName=战网账号切换管理器
DisableProgramGroupPage=yes
DisableDirPage=no
AllowNoIcons=yes
OutputDir=out
OutputBaseFilename=BnetSwitch-Setup-v{#AppVer}
SetupIconFile=..\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
CloseApplications=yes
CloseApplicationsFilter=BnetSwitch.exe

; 官方简体中文语言包(installer\ChineseSimplified.isl,UTF-8+BOM),所有向导页面统一中文
[Languages]
Name: "chinese"; MessagesFile: "ChineseSimplified.isl"

[CustomMessages]
CreateDesktopIcon=创建桌面快捷方式
LaunchApp=立即运行 {#AppName}
NetRuntimeTitle=正在准备运行环境
NetRuntimeDesc=正在下载并安装 .NET 8 桌面运行时(仅首次需要)

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "附加快捷方式:"

[Files]
; 多文件发布(非单文件):整个 publish 目录装进去,排除调试符号
Source: "..\publish\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[Code]
var
  DownloadPage: TDownloadWizardPage;

// 检测 C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App\8.* 是否存在
function IsDotNet8DesktopInstalled: Boolean;
var
  FindRec: TFindRec;
  Base: String;
begin
  Result := False;
  Base := ExpandConstant('{commonpf}\dotnet\shared\Microsoft.WindowsDesktop.App');
  if FindFirst(Base + '\8.*', FindRec) then
  begin
    try
      Result := True;
    finally
      FindClose(FindRec);
    end;
  end;
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(
    ExpandConstant('{cm:NetRuntimeTitle}'),
    ExpandConstant('{cm:NetRuntimeDesc}'), nil);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  if (CurPageID = wpReady) and (not IsDotNet8DesktopInstalled) then
  begin
    DownloadPage.Clear;
    DownloadPage.Add(
      'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe',
      'windowsdesktop-runtime-8-x64.exe', '');
    DownloadPage.Show;
    try
      try
        DownloadPage.Download;
      except
        if DownloadPage.AbortedByUser then
          MsgBox('已取消下载 .NET 运行时。', mbInformation, MB_OK)
        else
          MsgBox('下载 .NET 8 桌面运行时失败(请检查网络后重试),' + #13#10 +
                 '也可手动到 https://dotnet.microsoft.com/download/dotnet/8.0 安装后再运行本程序。' + #13#10 + #13#10 +
                 GetExceptionMessage, mbError, MB_OK);
        Result := False;
        DownloadPage.Hide;
        Exit;
      end;
    finally
      DownloadPage.Hide;
    end;

    // 静默安装 .NET 桌面运行时
    if not Exec(ExpandConstant('{tmp}\windowsdesktop-runtime-8-x64.exe'),
                '/install /quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
      MsgBox('启动 .NET 运行时安装程序失败,请手动安装 .NET 8 桌面运行时后再运行本程序。', mbError, MB_OK);
  end;
end;
