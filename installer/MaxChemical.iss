; ============================================================================
;  MaxChemical 安装包 (Inno Setup 6)
;
;  一个包搞定:主程序 + MCP 接口 + .NET 8 运行时 + MySQL + WorkBuddy 连接器配置。
;  客户机不需要预装任何东西,不需要联网。
;
;  数据库支持两种模式(安装时选):
;    · 内置   —— 部署独立 MySQL 服务,独立端口,与机器上已有的 MySQL 互不干扰
;    · 使用现有 —— 连客户已有的 MySQL/MariaDB,只建库建账号,不装第二个实例
;
;  编译: build.bat  详见: README.md
; ============================================================================

#define AppName        "MaxChemical"
#define AppNameFull    "MaxChemical 智能连续流实验平台"
#define AppVersion     "1.0.0"
#define AppPublisher   "MaxChemic"
#define AppExeName     "MaxChemical.Shell.exe"

; ── 前置安装包文件名 ──
;  build.bat 会扫描 redist\ 目录,把实际文件名用 /D 传进来,所以换版本
;  不必改这里。下面只是单独跑 ISCC(不经 build.bat)时的兜底默认值。
#ifndef DotnetRuntimeExe
  #define DotnetRuntimeExe "windowsdesktop-runtime-8.0.30-win-x64.exe"
#endif
#ifndef MySqlZipName
  #define MySqlZipName     "mysql-8.0.39-winx64.zip"
#endif
#ifndef VcRedistExe
  #define VcRedistExe      "VC_redist.x64.exe"
#endif

; MySQL 服务名。用产品专属名,避免与客户机上已有的 MySQL 服务重名
#define MySqlServiceName "MaxChemicalMySQL"

[Setup]
AppId={{8F3A2C71-5D64-4E9B-A013-7C25E8B4F906}
AppName={#AppNameFull}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
OutputDir=output
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExeName}
SetupLogging=yes

; ── 安装包外观(全部可选,缺了就用 Inno 默认外观,不影响编译)──────────
;  找图标的顺序:installer\assets\setup.ico(想让安装包用单独的图标就放这个)
;  -> 主程序自己的 app.ico(默认走这条,安装包和程序图标一致)-> 都没有就默认。
;
;  刻意不写死路径:app.ico 一直被 .gitignore 挡在仓库外(现已开例外),
;  别人克隆下来那个文件可能是缺的。写死会让编译直接失败——为了个图标
;  把整个打包卡住不值当,所以这里一律 FileExists 探测后再用。
;  编译时会把选中的那条打印出来(下面的 #pragma message)。图标这东西
;  改完看不出变化时,先分不清是"没编译进去"还是"资源管理器缓存没刷",
;  有这一行就不用猜了。
#define SetupIco  AddBackslash(SourcePath) + "assets\setup.ico"
#define AppIco    AddBackslash(SourcePath) + "..\MaxChemical.Shell\Resources\Icon\app.ico"
#if FileExists(SetupIco)
  #pragma message "[图标] 安装包 exe 用 assets\setup.ico"
SetupIconFile=assets\setup.ico
#elif FileExists(AppIco)
  #pragma message "[图标] 安装包 exe 用主程序的 app.ico(assets\setup.ico 不存在)"
SetupIconFile=..\MaxChemical.Shell\Resources\Icon\app.ico
#else
  #pragma message "[图标] 没找到自定义图标,安装包 exe 用 Inno 默认那个下载箭头"
#endif

;  向导配图。大图铺在欢迎页/结束页左侧,小图在其余页面右上角。
;  必须是 BMP(Inno 不吃 PNG)。推荐尺寸 164x314 与 55x55,
;  给高 DPI 屏可以按 @2x 命名多放几档,Inno 会自动挑。
#define WizBig   AddBackslash(SourcePath) + "assets\wizard.bmp"
#define WizSmall AddBackslash(SourcePath) + "assets\wizard-small.bmp"
#if FileExists(WizBig)
  #pragma message "[图标] 向导大图用 assets\wizard.bmp"
WizardImageFile=assets\wizard.bmp
#endif
#if FileExists(WizSmall)
  #pragma message "[图标] 向导小图用 assets\wizard-small.bmp"
WizardSmallImageFile=assets\wizard-small.bmp
#endif

[Languages]
; 简体中文语言文件不随 Inno Setup 默认安装 —— 安装 Inno 时要在语言列表里手工勾选,
; 没勾的机器上编译会报 "Couldn't open include file ... ChineseSimplified.isl"。
; 与其要求每台打包机都去补装,不如仓库里自带一份(lang\,取自 Inno Setup 官方仓库,
; 对应 6.5.0+),这样谁拉下来都能直接编译。
;
; 若编译器自己带了,优先用它的 —— 版本跟编译器严格配套,不会出现新版编译器
; 引用了旧语言文件里没有的消息项。
#define CompilerZh AddBackslash(CompilerPath) + "Languages\ChineseSimplified.isl"
#define LocalZh    AddBackslash(SourcePath) + "lang\ChineseSimplified.isl"
#if FileExists(CompilerZh)
Name: "chinese"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
#elif FileExists(LocalZh)
Name: "chinese"; MessagesFile: "lang\ChineseSimplified.isl"
#else
#error 找不到 ChineseSimplified.isl。仓库里 installer\lang\ 下应有一份;若已删除,请到 https://jrsoftware.org/files/istrans/ 重新下载放回。
#endif

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务:"

[Files]
; 下面两条带 recursesubdirs,所以 stage\app\ 里的 Plugins\(设备驱动)与
; GatewayConfigProxy\(32 位网关代理)会一并打包,不用单独列。
;
; 但这也意味着:build.bat 少铺一个子目录,这里不会有任何提示,照样能编译成功,
; 装到客户机上才发现设备库是空的。所以下面显式挡一道 —— 直接跑 ISCC 而不经
; build.bat 是最容易踩的情形。
#if !DirExists(AddBackslash(SourcePath) + "stage\app\Plugins")
  #error stage\app\Plugins 不存在。请用 build.bat 打包,不要直接跑 ISCC —— 设备驱动是 build.bat 第 3 步单独编译铺进去的,少了这一步装出来一个设备都认不出来。
#endif
#if !DirExists(AddBackslash(SourcePath) + "stage\app\GatewayConfigProxy")
  #pragma message "[警告] stage\app\GatewayConfigProxy 不存在,装上去网关配置功能用不了"
#endif

Source: "stage\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "stage\mcp\*"; DestDir: "{app}\mcp"; Flags: ignoreversion recursesubdirs createallsubdirs

Source: "scripts\mysql-setup.bat";   DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "scripts\mysql-remove.bat";  DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "scripts\my.ini.template";   DestDir: "{app}\scripts"; Flags: ignoreversion

; 三个 PowerShell 助手。原先这些逻辑是内联在 .bat 里的,靠多行 ^ 续行拼出来,
; 里面嵌着双引号、单引号和圆括号 —— cmd 要先把这锅汤解析一遍才轮到 PowerShell,
; 安装路径带个空格就散架,报一句「此时不应有 .」了事。抽成独立脚本后 cmd 只需
; 处理一层引号,PowerShell 的部分由它自己按具名参数接收。
Source: "scripts\unpack-mysql.ps1";  DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "scripts\write-myini.ps1";   DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "scripts\write-sql.ps1";     DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "scripts\init-db.sql";          DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "scripts\init-db-embedded.sql"; DestDir: "{app}\scripts"; Flags: ignoreversion

Source: "redist\{#MySqlZipName}"; DestDir: "{tmp}"; Flags: deleteafterinstall

; VC++ 2015-2022 x64 运行库。MySQL 的免安装 zip 版是拿 MSVC 编译的,
; mysqld.exe 硬依赖 vcruntime140_1.dll —— 干净的 Windows 不预装这个。
; 缺了它 mysqld 连启动都启动不了,数据目录初始化那步直接失败,服务永远注册不上,
; 而 runhidden 把报错藏进日志,安装程序照样显示"安装成功"。
Source: "redist\{#VcRedistExe}"; DestDir: "{tmp}"; Flags: deleteafterinstall; \
    Check: NeedsVcRedist

Source: "redist\{#DotnetRuntimeExe}"; DestDir: "{tmp}"; Flags: deleteafterinstall; \
    Check: NeedsDotnetRuntime

[Dirs]
; ── 让普通用户能写日志目录 ──────────────────────────────────────────
;  程序把运行数据写在自己的安装目录下,而 Program Files 对普通用户是只读的。
;  最要命的是 App.OnStartup 第一句就去建 Logs\ 目录(LogManager.Initialize),
;  而且没有 try —— 权限不足直接抛 UnauthorizedAccessException,
;  在窗口出现之前就崩,事件查看器里只留一个 0xe0434352(托管异常统一代码),
;  看不出跟权限有关。开发机上跑 bin\Debug\ 永远碰不到这个。
;
;  所以这两个目录必须预先建好并授权,而不是等程序自己建。
Name: "{app}\Logs";        Permissions: users-modify
Name: "{app}\ControlLogs"; Permissions: users-modify

;  刻意只授这两个,不图省事给整个 {app} 开写权限:那等于让任何本地用户
;  都能替换 MaxChemical.Shell.exe 和一堆 DLL,是标准的本地提权路径。
;  代价是还有两处写不了,都不影响主流程,见 README「已知边界」:
;    · {app}\RecentFiles.json      —— 最近打开的文件列表,存不下来
;    · {app}\GatewayConfigProxy\zlsdk.log —— 网关 SDK 日志
;  要根治得改代码,把可写数据挪到 %ProgramData%\MaxChemical\ 下。

[Icons]
Name: "{group}\{#AppNameFull}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppNameFull}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; ① .NET 8 Desktop Runtime —— 静默安装,已装则跳过
Filename: "{tmp}\{#DotnetRuntimeExe}"; Parameters: "/install /quiet /norestart"; \
    StatusMsg: "正在安装 .NET 8 桌面运行时(约 1 分钟)..."; \
    Check: NeedsDotnetRuntime; Flags: waituntilterminated

; ② VC++ 运行库 —— 必须排在数据库部署前面,MySQL 起不来就全白搭
Filename: "{tmp}\{#VcRedistExe}"; Parameters: "/install /quiet /norestart"; \
    StatusMsg: "正在安装 Visual C++ 运行库(MySQL 需要)..."; \
    Check: NeedsVcRedist; Flags: waituntilterminated

; ③ 数据库部署
;    参数: <模式> <安装目录> <zip> <主机> <端口> <管理员账号> <管理员口令> <应用口令> <只读口令>
;    注意 /C 后面那一整串外面还要再包一层引号(所以是四个连续引号开头、
;    三个连续引号结尾)。这不是多余的:
;
;    cmd /? 里写了 /C 的引号处理只有两条规则。规则一要求「整行恰好两个引号」
;    才保留原样;我们这行有 22 个引号,必然落到规则二 ——「若首字符是引号,
;    去掉它,并去掉整行最后一个引号」。少了外层这对引号的话,被去掉的就成了
;    bat 路径的开引号和日志路径的闭引号,整行引号的奇偶性被翻转:参数值跑到
;    引号外,参数之间的空格反倒跑进引号里。cmd 只在引号外的空白处断命令名,
;    于是它去找一个叫 C:\Program 的程序 —— 脚本一行都没跑。
;
;    连带的:那个 > 重定向符也落进了引号内,不再是重定向,所以
;    mysql-setup.log 根本不会生成。既没服务、也没日志、还什么都不报,
;    正是这个 bug 最难查的地方。
;
;    包上外层引号后,规则二剥掉的是这对外层引号,内部结构原封不动。
Filename: "{cmd}"; \
    Parameters: "/C """"{app}\scripts\mysql-setup.bat"" ""{code:GetDbMode}"" ""{app}"" ""{tmp}\{#MySqlZipName}"" ""{code:GetDbHost}"" ""{code:GetDbPort}"" ""{code:GetAdminUser}"" ""{code:GetAdminPwd}"" ""{code:GetAppPwd}"" ""{code:GetRoPwd}"" > ""{app}\scripts\mysql-setup.log"" 2>&1"""; \
    StatusMsg: "正在部署数据库(首次初始化约 1-2 分钟)..."; \
    Flags: waituntilterminated runhidden

Filename: "{app}\{#AppExeName}"; Description: "立即启动 {#AppName}"; \
    Flags: nowait postinstall skipifsilent

[UninstallRun]
; 只有内置模式才需要删服务。使用现有 MySQL 时脚本会自己判断并跳过。
; 同样要在 /C 后面那串外再包一层引号 —— 这条只有 4 个引号,一样不满足
; 规则一的「恰好两个」,一样会被规则二把命令名截成 C:\Program。
; 少了它的后果是卸载时服务根本没被删掉,留下一个指向已删目录的僵尸服务。
Filename: "{cmd}"; Parameters: "/C """"{app}\scripts\mysql-remove.bat"" ""{app}"""""; \
    RunOnceId: "RemoveMySqlService"; Flags: waituntilterminated runhidden

[UninstallDelete]
Type: filesandordirs; Name: "{app}\mysql\bin"
Type: filesandordirs; Name: "{app}\mysql\lib"
Type: filesandordirs; Name: "{app}\mysql\share"
Type: files;          Name: "{app}\workbuddy-mcp.json"
Type: files;          Name: "{app}\scripts\mysql-setup.log"
; 注意:{app}\mysql\data 与 my.ini 刻意不删(实验数据是客户资产)

[Code]
var
  ModePage:  TInputOptionWizardPage;   // 内置 / 使用现有
  EmbedPage: TInputQueryWizardPage;    // 端口 + 两个新账号口令
  ExistPage: TInputQueryWizardPage;    // 主机/端口/管理员账号口令 + 两个新账号口令
  DetectedMySql: String;               // 检测结果,显示在选择页上

// ═════════════════════════════════════════════════════════════════
//  环境检测
// ═════════════════════════════════════════════════════════════════

// ── 系统版本闸门 ──
// .NET 8 要求 Windows 10 1607(Build 14393)以上。1507(Build 10240,2015 年的
// 初版 RTM)不在支持列表里。
//
// 这一关必须放在最前面:1507 上装完是能装上的,主程序一启动就崩,事件查看器里
// 只有一个 0xe0434352 —— 所有托管异常都是这个码,看不出跟系统版本有关,
// 排查起来非常费劲。与其让人装完 340MB 再撞墙,不如开头就说清楚。
//
// 顺带一提:非 LTSB 的 1507 早在 2017 年 5 月就停止支持了,不再有任何安全更新。
function InitializeSetup(): Boolean;
var
  V: TWindowsVersion;
begin
  Result := True;
  GetWindowsVersionEx(V);

  if (V.Major < 10) or ((V.Major = 10) and (V.Build < 14393)) then
  begin
    MsgBox(
      '这台机器的 Windows 版本太低,装不了。' + #13#10 + #13#10 +
      '当前:Windows ' + IntToStr(V.Major) + '.' + IntToStr(V.Minor) +
      ' (内部版本 ' + IntToStr(V.Build) + ')' + #13#10 +
      '需要:Windows 10 版本 1607 (内部版本 14393) 或更高' + #13#10 + #13#10 +
      '原因是本软件基于 .NET 8,而 .NET 8 不支持 1607 以下的 Windows 10。' +
      '硬装上去的话,主程序会在启动时直接崩溃。' + #13#10 + #13#10 +
      '解决办法:把系统升级到 Windows 10 22H2(免费),或换一台机器。' + #13#10 +
      '用 winver 命令可以查看当前的内部版本号。',
      mbCriticalError, MB_OK);
    Result := False;
  end;
end;

// ── .NET 8 Desktop Runtime ──
// 查注册表的 sharedfx 版本键,比调 `dotnet --list-runtimes` 可靠——
// 后者依赖 dotnet 在 PATH 里,而只装了运行时的机器不一定有。
function IsDotnet8DesktopInstalled: Boolean;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if RegGetValueNames(HKLM, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', Names) then
  begin
    for I := 0 to GetArrayLength(Names) - 1 do
      if Copy(Names[I], 1, 2) = '8.' then
      begin
        Result := True;
        Exit;
      end;
  end;
end;

function NeedsDotnetRuntime: Boolean;
begin
  Result := not IsDotnet8DesktopInstalled;
end;

// ── Visual C++ 2015-2022 x64 运行库 ──
// MySQL 的免安装 zip 版是 MSVC 编译的,mysqld.exe 依赖 vcruntime140_1.dll。
// Windows 不预装这个,干净系统上一定缺 —— 缺了 mysqld 连启动都启动不了,
// 表现是「装完没有 MaxChemicalMySQL 服务」,而安装程序一切正常。
//
// 直接查 DLL 文件而不是查注册表版本号:mysqld 要的就是这个文件,
// 查它最贴近实际。注册表里的版本号还得处理 x64/WOW6432Node 两个视图,
// 以及"装过又被别的程序卸载了"这种注册表还在但文件没了的情况。
//
// {sys} 在 64 位安装模式下指向 System32(本脚本有 ArchitecturesInstallIn64BitMode),
// 不会被 WOW64 重定向到 SysWOW64。
function IsVcRedistInstalled: Boolean;
begin
  Result := FileExists(ExpandConstant('{sys}\vcruntime140_1.dll'))
        and FileExists(ExpandConstant('{sys}\msvcp140.dll'));
end;

function NeedsVcRedist: Boolean;
begin
  Result := not IsVcRedistInstalled;
end;

// ── 检测机器上已有的 MySQL / MariaDB ──
// 遍历服务注册表项,按服务名/显示名匹配。比查安装目录可靠:
// 免安装版没有卸载注册表项,但注册成服务就一定在这里。
function DetectExistingMySql: String;
var
  Names: TArrayOfString;
  I: Integer;
  Display, Found: String;
begin
  Found := '';
  if RegGetSubkeyNames(HKLM, 'SYSTEM\CurrentControlSet\Services', Names) then
  begin
    for I := 0 to GetArrayLength(Names) - 1 do
    begin
      // 跳过我们自己上次装的那个,它不算"客户已有的"
      if CompareText(Names[I], '{#MySqlServiceName}') = 0 then
        Continue;

      if (Pos('mysql', LowerCase(Names[I])) > 0) or (Pos('mariadb', LowerCase(Names[I])) > 0) then
      begin
        if not RegQueryStringValue(HKLM, 'SYSTEM\CurrentControlSet\Services\' + Names[I], 'DisplayName', Display) then
          Display := Names[I];
        if Found = '' then
          Found := Names[I] + ' (' + Display + ')'
        else
          Found := Found + '、' + Names[I];
      end;
    end;
  end;
  Result := Found;
end;

// ── 端口占用检测 ──
// 用 netstat 找监听行。搜 ":端口 "(带尾随空格)是关键:
// 否则查 3306 会误命中 33060(MySQL X Protocol 默认端口)。
function IsPortInUse(Port: String): Boolean;
var
  ResultCode: Integer;
  TempFile: String;
  Lines: TArrayOfString;
begin
  Result := False;
  TempFile := ExpandConstant('{tmp}\portcheck.txt');
  DeleteFile(TempFile);
  if Exec(ExpandConstant('{cmd}'),
          '/C netstat -an | findstr /C:":' + Port + ' " > "' + TempFile + '"',
          '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    if LoadStringsFromFile(TempFile, Lines) then
      Result := GetArrayLength(Lines) > 0;
  end;
  DeleteFile(TempFile);
end;

// ═════════════════════════════════════════════════════════════════
//  向导页
// ═════════════════════════════════════════════════════════════════

procedure InitializeWizard;
var
  Hint: String;
begin
  DetectedMySql := DetectExistingMySql;

  // ── 第一页:数据库模式 ──
  if DetectedMySql <> '' then
    Hint := '检测到本机已安装数据库服务:' + #13#10 + '    ' + DetectedMySql + #13#10 + #13#10 +
            '你可以让本产品使用它(只建库建账号),也可以另装一个独立实例——' + #13#10 +
            '独立实例用单独的端口与服务名,与已有的互不干扰。'
  else
    Hint := '未检测到本机已安装 MySQL / MariaDB 服务。' + #13#10 +
            '建议选择第一项,由安装程序部署一个独立实例。';

  ModePage := CreateInputOptionPage(wpSelectTasks,
    '数据库设置', '选择数据库的部署方式', Hint, True, False);
  ModePage.Add('安装内置数据库(推荐):部署独立实例,端口与服务名均独立');
  ModePage.Add('使用本机已有的 MySQL / MariaDB:仅建库与账号');

  if DetectedMySql <> '' then
    ModePage.SelectedValueIndex := 0   // 即便检测到,默认仍推荐内置——互不干扰,风险最小
  else
    ModePage.SelectedValueIndex := 0;

  // ── 第二页 A:内置模式参数 ──
  EmbedPage := CreateInputQueryPage(ModePage.ID,
    '内置数据库', '设置端口与账号口令',
    '安装程序会部署一个独立的数据库服务 {#MySqlServiceName}。' + #13#10 +
    '口令请妥善保管。');
  EmbedPage.Add('数据库端口:', False);
  EmbedPage.Add('应用账号 maxchemical 的口令:', True);
  EmbedPage.Add('只读账号 mcp_ro 的口令(供 AI 查询接口使用):', True);
  EmbedPage.Values[0] := '3307';
  EmbedPage.Values[1] := 'MaxChem@2026';
  EmbedPage.Values[2] := 'McpRead@2026';

  // ── 第二页 B:使用现有实例 ──
  ExistPage := CreateInputQueryPage(EmbedPage.ID,
    '使用现有数据库', '填写现有实例的连接信息',
    '安装程序会用管理员账号在该实例上创建 maxchemical 库与两个账号。' + #13#10 +
    '管理员口令只在安装过程中使用,不会写入任何配置文件。');
  ExistPage.Add('主机:', False);
  ExistPage.Add('端口:', False);
  ExistPage.Add('管理员账号(需有建库与建用户权限):', False);
  ExistPage.Add('管理员口令:', True);
  ExistPage.Add('新建应用账号 maxchemical 的口令:', True);
  ExistPage.Add('新建只读账号 mcp_ro 的口令:', True);
  ExistPage.Values[0] := 'localhost';
  ExistPage.Values[1] := '3306';
  ExistPage.Values[2] := 'root';
  ExistPage.Values[3] := '';
  ExistPage.Values[4] := 'MaxChem@2026';
  ExistPage.Values[5] := 'McpRead@2026';
end;

function UseEmbedded: Boolean;
begin
  Result := (ModePage <> nil) and (ModePage.SelectedValueIndex = 0);
end;

// 按模式跳过不相干的那一页
function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if PageID = EmbedPage.ID then Result := not UseEmbedded;
  if PageID = ExistPage.ID then Result := UseEmbedded;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Port: Integer;
begin
  Result := True;

  // ── 内置模式:端口范围 + 占用预检 + 口令非空 ──
  if CurPageID = EmbedPage.ID then
  begin
    Port := StrToIntDef(EmbedPage.Values[0], 0);
    if (Port < 1024) or (Port > 65535) then
    begin
      MsgBox('端口需为 1024-65535 之间的数字。', mbError, MB_OK);
      Result := False;
      Exit;
    end;

    // 提前拦住端口冲突。不做这一步的话,要等到安装末尾 net start 失败才暴露,
    // 那时候文件都铺完了,用户只能卸载重来。
    if IsPortInUse(EmbedPage.Values[0]) then
    begin
      MsgBox('端口 ' + EmbedPage.Values[0] + ' 已被占用,请换一个(例如 3308、3309)。' + #13#10 + #13#10 +
             '如果占用它的正是你想使用的数据库,请返回上一页选择' + #13#10 +
             '「使用本机已有的 MySQL / MariaDB」。', mbError, MB_OK);
      Result := False;
      Exit;
    end;

    if (Trim(EmbedPage.Values[1]) = '') or (Trim(EmbedPage.Values[2]) = '') then
    begin
      MsgBox('两个口令都不能为空。', mbError, MB_OK);
      Result := False;
      Exit;
    end;
  end;

  // ── 现有实例模式:必填项校验 ──
  if CurPageID = ExistPage.ID then
  begin
    if Trim(ExistPage.Values[0]) = '' then
    begin
      MsgBox('主机不能为空(本机填 localhost)。', mbError, MB_OK);
      Result := False;
      Exit;
    end;
    Port := StrToIntDef(ExistPage.Values[1], 0);
    if (Port < 1) or (Port > 65535) then
    begin
      MsgBox('端口需为 1-65535 之间的数字。', mbError, MB_OK);
      Result := False;
      Exit;
    end;
    if Trim(ExistPage.Values[2]) = '' then
    begin
      MsgBox('管理员账号不能为空。', mbError, MB_OK);
      Result := False;
      Exit;
    end;
    if (Trim(ExistPage.Values[4]) = '') or (Trim(ExistPage.Values[5]) = '') then
    begin
      MsgBox('两个新账号的口令都不能为空。', mbError, MB_OK);
      Result := False;
      Exit;
    end;
  end;
end;

// ═════════════════════════════════════════════════════════════════
//  取值(供 [Run] 的 {code:...} 使用,按模式返回对应页的值)
// ═════════════════════════════════════════════════════════════════

function GetDbMode(Param: String): String;
begin
  if UseEmbedded then Result := 'embedded' else Result := 'existing';
end;

function GetDbHost(Param: String): String;
begin
  if UseEmbedded then Result := '127.0.0.1' else Result := ExistPage.Values[0];
end;

function GetDbPort(Param: String): String;
begin
  if UseEmbedded then Result := EmbedPage.Values[0] else Result := ExistPage.Values[1];
end;

function GetAdminUser(Param: String): String;
begin
  // 内置模式:--initialize-insecure 建出的是无口令 root
  if UseEmbedded then Result := 'root' else Result := ExistPage.Values[2];
end;

function GetAdminPwd(Param: String): String;
begin
  if UseEmbedded then Result := '' else Result := ExistPage.Values[3];
end;

function GetAppPwd(Param: String): String;
begin
  if UseEmbedded then Result := EmbedPage.Values[1] else Result := ExistPage.Values[4];
end;

function GetRoPwd(Param: String): String;
begin
  if UseEmbedded then Result := EmbedPage.Values[2] else Result := ExistPage.Values[5];
end;

// ═════════════════════════════════════════════════════════════════
//  写配置文件
//
//  三处要写:
//    ① 主程序 App.config 的 MaxChemicalDB 连接串
//       —— DatabaseConfigService 优先读它,读不到才用硬编码的 root/123456 兜底。
//          写了它,就等于把那个弱口令兜底彻底绕开。
//    ② mcp\appsettings.json 的只读连接串与管道名
//    ③ workbuddy-mcp.json —— 给用户复制粘贴进 AI 客户端的连接器配置
// ═════════════════════════════════════════════════════════════════

// JSON 里路径的反斜杠要写成 \\。Pascal Script 要求先声明后使用。
function EscapeJsonPath(S: String): String;
begin
  StringChangeEx(S, '\', '\\', True);
  Result := S;
end;

procedure WriteAppConfig(AppDir: String);
var
  ConfigPath, Content, ConnStr: String;
begin
  ConfigPath := AppDir + '\MaxChemical.Shell.dll.config';
  ConnStr :=
    'Server=' + GetDbHost('') + ';Port=' + GetDbPort('') + ';Database=maxchemical;' +
    'Uid=maxchemical;Pwd=' + GetAppPwd('') + ';CharSet=utf8mb4;SslMode=None;' +
    'AllowUserVariables=True;AllowPublicKeyRetrieval=True;';

  Content :=
    '<?xml version="1.0" encoding="utf-8"?>' + #13#10 +
    '<configuration>' + #13#10 +
    '  <connectionStrings>' + #13#10 +
    '    <add name="MaxChemicalDB"' + #13#10 +
    '         connectionString="' + ConnStr + '"' + #13#10 +
    '         providerName="MySql.Data.MySqlClient" />' + #13#10 +
    '  </connectionStrings>' + #13#10 +
    '</configuration>' + #13#10;

  SaveStringToFile(ConfigPath, Content, False);
end;

procedure WriteMcpConfig(AppDir: String);
var
  JsonPath, Content, RoConn: String;
begin
  RoConn :=
    'Server=' + GetDbHost('') + ';Port=' + GetDbPort('') + ';Database=maxchemical;' +
    'Uid=mcp_ro;Pwd=' + GetRoPwd('') + ';CharSet=utf8mb4;SslMode=None;' +
    'AllowPublicKeyRetrieval=True;';

  // ① mcp\appsettings.json —— Bridge 与 History 共用这一份
  JsonPath := AppDir + '\mcp\appsettings.json';
  Content :=
    '{' + #13#10 +
    '  "_说明": "由安装程序生成。ConnectionString 为只读账号;PipeName 须与主程序 MaxChemicalMcp:PipeName 一致。",' + #13#10 +
    '  "PipeName": "MaxChemical.Mcp.v1",' + #13#10 +
    '  "ConnectTimeoutMs": 3000,' + #13#10 +
    '  "ConnectionString": "' + RoConn + '",' + #13#10 +
    '  "MaxRows": 200,' + #13#10 +
    '  "CommandTimeoutSeconds": 15,' + #13#10 +
    '  "EnableRawSql": true' + #13#10 +
    '}' + #13#10;
  SaveStringToFile(JsonPath, Content, False);

  // ② workbuddy-mcp.json —— 用户复制粘贴用,路径按实际安装目录填好
  JsonPath := AppDir + '\workbuddy-mcp.json';
  Content :=
    '{' + #13#10 +
    '  "mcpServers": {' + #13#10 +
    '    "maxchemical": {' + #13#10 +
    '      "command": "' + EscapeJsonPath(AppDir + '\mcp\MaxChemical.Mcp.Bridge.exe') + '",' + #13#10 +
    '      "args": [],' + #13#10 +
    '      "env": {' + #13#10 +
    '        "MAXCHEMICAL_MCP_CONNSTR": "' + RoConn + '"' + #13#10 +
    '      }' + #13#10 +
    '    }' + #13#10 +
    '  }' + #13#10 +
    '}' + #13#10;
  SaveStringToFile(JsonPath, Content, False);
end;

// ═════════════════════════════════════════════════════════════════
//  数据库部署的验收检查
//
//  [Run] 不看退出码。mysql-setup.bat 中途失败(缺 VC++ 运行库、端口被占、
//  管理员口令不对……)时,安装程序照样一路走完显示"安装成功",用户要等到
//  打开软件连不上库、或者发现服务列表里没有 MaxChemicalMySQL 才察觉,
//  那时早就不知道是哪一步的问题了。
//
//  ssPostInstall 在 [Run] 之后触发,正好在这里验收。查的是最终结果而不是
//  退出码 —— 用户关心的就是"服务在不在、库建没建成"这件事本身。
// ═════════════════════════════════════════════════════════════════
function DbSetupReportedSuccess(AppDir: String): Boolean;
var
  S: AnsiString;
begin
  Result := False;
  if LoadStringFromFile(AppDir + '\scripts\mysql-setup.log', S) then
    Result := Pos('MAXCHEMICAL_DB_SETUP_OK', S) > 0;
end;

procedure VerifyDbSetup(AppDir: String);
var
  LogOk, SvcOk: Boolean;
  Msg: String;
begin
  LogOk := DbSetupReportedSuccess(AppDir);

  // 内置模式必须留下服务;使用现有实例的模式本来就不注册服务,不能拿它当判据。
  if UseEmbedded then
    SvcOk := RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\{#MySqlServiceName}')
  else
    SvcOk := True;

  if LogOk and SvcOk then
    Exit;

  Msg := '数据库没有部署成功,软件装上了但连不上库。' + #13#10 + #13#10;

  if not LogOk then
    Msg := Msg + '· 部署脚本没有跑到最后一步' + #13#10
  else if not SvcOk then
    Msg := Msg + '· 脚本报告成功,但服务 {#MySqlServiceName} 不存在' + #13#10;

  Msg := Msg + #13#10 +
    '详细原因看这个日志:' + #13#10 +
    AppDir + '\scripts\mysql-setup.log' + #13#10 + #13#10 +
    '最常见的两个原因:' + #13#10 +
    '  · 端口被占用 —— 日志里会有 net start 失败' + #13#10 +
    '  · 使用现有实例时管理员账号口令不对 —— 日志里是连接超时' + #13#10 + #13#10 +
    '排除原因后,可以用管理员身份重跑这个脚本,不必重装:' + #13#10 +
    AppDir + '\scripts\mysql-setup.bat';

  MsgBox(Msg, mbError, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    WriteAppConfig(ExpandConstant('{app}'));
    WriteMcpConfig(ExpandConstant('{app}'));
    VerifyDbSetup(ExpandConstant('{app}'));
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpFinished then
    WizardForm.FinishedLabel.Caption :=
      WizardForm.FinishedLabel.Caption + #13#10#13#10 +
      '要让 WorkBuddy 等 AI 客户端连接本系统,请把安装目录下的' + #13#10 +
      'workbuddy-mcp.json 内容复制到 AI 客户端的 MCP 配置中。' + #13#10 + #13#10 +
      '数据库部署日志:scripts\mysql-setup.log';
end;
