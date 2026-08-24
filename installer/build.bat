@echo off
setlocal enabledelayedexpansion

REM ============================================================================
REM  MaxChemical 安装包一键打包
REM
REM  1. 定位 Inno Setup 的 ISCC.exe(ISCC_PATH -> PATH -> 注册表 -> 常见路径)
REM  2. 校验 redist\ 里三个前置文件(.NET / MySQL / VC++)
REM  3. dotnet publish 主程序 + 网关代理到 stage\app\
REM  4. 逐个编译设备驱动到 stage\app\Plugins\
REM  5. dotnet publish MCP 统一入口到 stage\mcp\
REM  6. 调 ISCC 编译出 output\MaxChemical-Setup-x.x.x.exe
REM
REM  在「仓库根目录\installer」下运行(脚本用 %~dp0.. 定位仓库根)。
REM  首次使用请先看 README.md 准备 redist\ 下的三个前置文件。
REM
REM  -- 改这个文件前先看这三条,都是踩过的坑 --
REM
REM  其一:本文件必须存成 GBK(ANSI/936),不能存 UTF-8,也不要加 chcp 65001。
REM        cmd 执行 goto/call :标签 时按字节偏移重新定位文件,UTF-8 下中文是
REM        多字节,偏移量会落在行中间,把 not/ISCC/call 切成碎片,报出
REM        "'ot' 不是内部或外部命令" 这种莫名其妙的错。
REM
REM  其二:%ProgramFiles(x86)% 展开后带的 "(x86)" 会把 for ... in ( ... ) 的
REM        括号块提前闭合,后面命令被当成独立语句执行。所以带括号的环境变量
REM        一律先转存成普通变量,路径探测用顺序 if 加子例程,不用列表。
REM
REM  其三:MCP 工程位置不写死,第 4 步会自己找。机器上可能存在多份 Bridge
REM        (比如 MaxChemical.Mcp.History_Step0\ 里那份 Step 0 阶段的旧副本),
REM        判定标准不是路径而是内容:合并版会引用 MaxChemical.Mcp.History 工程,
REM        旧版没有。所以放哪儿都行,但旧版顶不了新版。
REM ============================================================================

cd /d "%~dp0"
set "ROOT=%~dp0.."
set "STAGE=%~dp0stage"
set "REDIST=%~dp0redist"

REM 先把带括号的环境变量转存成普通变量(见其二)
set "PF64=%ProgramFiles%"
set "PF32=%ProgramFiles(x86)%"
set "LAD=%LOCALAPPDATA%"

echo ============================================
echo   MaxChemical 安装包打包
echo ============================================
echo.

REM ---- 0. 环境检查:dotnet ----------------------------------------------
where dotnet >nul 2>&1
if errorlevel 1 (
    echo [错误] 找不到 dotnet SDK。请先安装 .NET 8 SDK。
    echo        https://dotnet.microsoft.com/download/dotnet/8.0
    goto :fail
)

REM ---- 0b. 定位 ISCC.exe -------------------------------------------------
set "ISCC="

REM (1) 手工指定。装在别处时:  set ISCC_PATH=D:\Tools\Inno Setup 6\ISCC.exe
REM     注意等号后不要带引号,否则引号会被一起吃进去
if defined ISCC_PATH if exist "%ISCC_PATH%" set "ISCC=%ISCC_PATH%"

REM (2) PATH
if not defined ISCC for /f "delims=" %%I in ('where ISCC.exe 2^>nul') do if not defined ISCC set "ISCC=%%I"

REM (3) 卸载注册表里的 InstallLocation,能查到自定义安装路径。
REM     用户级安装(装时选"仅为我安装")写 HKCU,机器级写 HKLM,都查一遍。
if not defined ISCC call :FromReg "HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1"
if not defined ISCC call :FromReg "HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1"
if not defined ISCC call :FromReg "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1"

REM (4) 常见默认路径。用户级安装落在 AppData 下,是没有管理员权限的开发机上
REM     最常见的,所以放在最前。
if not defined ISCC call :TryPath "%LAD%\Programs\Inno Setup 6\ISCC.exe"
if not defined ISCC call :TryPath "%PF32%\Inno Setup 6\ISCC.exe"
if not defined ISCC call :TryPath "%PF64%\Inno Setup 6\ISCC.exe"
if not defined ISCC call :TryPath "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if not defined ISCC call :TryPath "C:\Program Files\Inno Setup 6\ISCC.exe"
if not defined ISCC call :TryPath "D:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if not defined ISCC call :TryPath "D:\Program Files\Inno Setup 6\ISCC.exe"

if not defined ISCC (
    echo [错误] 找不到 Inno Setup 的 ISCC.exe。
    echo.
    echo   没装的话:  https://jrsoftware.org/isdl.php
    echo              装 innosetup-6.x.x.exe,安装时勾上 Inno Setup Preprocessor
    echo              （脚本用了 #define,必要）
    echo.
    echo   装在别处:  设环境变量后重跑,等号后不要带引号
    echo              set ISCC_PATH=D:\Tools\Inno Setup 6\ISCC.exe
    echo.
    goto :fail
)
echo [0/5] Inno Setup: !ISCC!

REM ---- 1. 校验前置文件(先查再编译,免得白等十几分钟)---------------------
echo.
echo [1/5] 检查前置安装包...
REM  用通配符取实际文件名,不写死版本号。下载到的小版本和文档不一定一致,
REM  写死的话每换一次版本要改两个地方,所以扫到啥就用 /D 传给 ISCC。
set "RT_NAME="
set "MY_NAME="
set "VC_NAME="
for %%F in ("%REDIST%\windowsdesktop-runtime-*-win-x64.exe") do set "RT_NAME=%%~nxF"
for %%F in ("%REDIST%\mysql-*-winx64.zip") do set "MY_NAME=%%~nxF"
for %%F in ("%REDIST%\vc_redist.x64.exe") do set "VC_NAME=%%~nxF"

set "MISSING=0"
if defined RT_NAME (echo       [就绪] !RT_NAME!) else (echo       [缺失] redist\windowsdesktop-runtime-*-win-x64.exe & set "MISSING=1")
if defined MY_NAME (echo       [就绪] !MY_NAME!) else (echo       [缺失] redist\mysql-*-winx64.zip & set "MISSING=1")
if defined VC_NAME (echo       [就绪] !VC_NAME!) else (echo       [缺失] redist\VC_redist.x64.exe & set "MISSING=1")

if "!MISSING!"=="1" (
    echo.
    echo [错误] 前置文件缺失。下载地址见 README.md 第二节。
    echo        .NET 要 Desktop Runtime x64 安装包；MySQL 要 ZIP Archive 不是 MSI。
    echo        VC++ 运行库是 MySQL 的硬依赖 —— 少了它 mysqld 起不来,
    echo        表现成"装完没有 MaxChemicalMySQL 服务",而且安装过程一切正常。
    echo        https://aka.ms/vs/17/release/vc_redist.x64.exe
    goto :fail
)

REM ---- 2. 发布主程序 -----------------------------------------------------
REM  框架依赖发布(不加 --self-contained):运行时由安装程序统一装一份,
REM  几个程序共用,比每个都自包含省下好几份运行时的体积。
echo.
echo [2/5] 发布主程序...
if exist "%STAGE%" rmdir /s /q "%STAGE%"
mkdir "%STAGE%"

dotnet publish "%ROOT%\MaxChemical.Shell\MaxChemical.Shell.csproj" -c Release -r win-x64 --self-contained false -o "%STAGE%\app" -p:DebugType=none -p:GenerateDocumentationFile=false
if errorlevel 1 goto :fail

REM  网关配置代理。它是 32 位独立 exe(要调厂商的 32 位 ZLDevManage.dll),
REM  主程序运行时按 <程序目录>\GatewayConfigProxy\ 这个固定路径去找它
REM  (见 GatewayConfigService.cs)。
REM
REM  Shell 的 csproj 里有个 AfterTargets="Build" 的拷贝步骤,但它拷去的是
REM  bin\ 而不是 publish 输出目录 —— publish 不会把 build 之后往 bin 里
REM  加的文件带过来。所以那条 target 对打包没用,必须在这里单独发布一次。
set "GWPROXY=%ROOT%\MaxChemical.GatewayConfig.Proxy\MaxChemical.GatewayConfig.Proxy.csproj"
if exist "%GWPROXY%" (
    echo       网关配置代理...
    dotnet publish "%GWPROXY%" -c Release -o "%STAGE%\app\GatewayConfigProxy" -p:DebugType=none --nologo -v q
    if errorlevel 1 goto :fail
) else (
    echo       [警告] 没有 MaxChemical.GatewayConfig.Proxy 工程,网关配置功能装上去会用不了。
)

REM ---- 3. 设备驱动 -------------------------------------------------------
REM  主程序运行时扫 <程序目录>\Plugins\*.dll 用 MEF 装配设备(见
REM  DeviceManager.GetPluginPath)。这些驱动工程反过来引用 DevicePlugins,
REM  Shell 并不引用它们 —— 所以 dotnet publish 主程序时它们一个都不会出现。
REM  少了这一步,装到客户机上软件能起来、界面正常,就是设备库空的,
REM  日志里只有一句 "插件目录不存在" 然后自动建个空目录,很难发现。
echo.
echo [3/5] 编译设备驱动...
mkdir "%STAGE%\app\Plugins" 2>nul

REM  认工程看内容不看名字。判据是源码里有没有 [Export(typeof(IDevice))] ——
REM  这正是 MEF 在 Plugins\ 下要找的东西,跟运行时的实际机制对齐。
REM
REM  为什么不用"引用了 DevicePlugins.csproj":那条太宽。FlowEngine.Core、
REM  MaxChemical.Infrastructure、Modules.Data 都引用它,但都不是驱动。
REM  其中 FlowEngine.Core 尤其能骗过人 —— 它没被任何工程引用,所以主目录里
REM  也没有它的 DLL,靠"主目录有没有同名 DLL"根本挡不住,会被当驱动装进去。
REM
REM  (目录名更不可靠 —— SiphonInfusionPump_ModbusTcp\ 下面是
REM   SiphonInfusionPump_ModbusRTU.csproj,TemperatureController_ModbusTCP\
REM   下面是 MicroReactor_ModbusTCP.csproj,都对不上。)
set /a PLUG_OK=0
set "PLUG_SKIP="
set "PLUG_FAIL="
for /f "delims=" %%P in ('dir /s /b "%ROOT%\*.csproj" 2^>nul') do call :BuildPlugin "%%P"

if defined PLUG_FAIL (
    echo.
    echo [错误] 有驱动没编译成功:!PLUG_FAIL!
    echo        少一个驱动就是少一类设备,不能当没看见。上面有具体报错。
    goto :fail
)
if "!PLUG_OK!"=="0" (
    echo.
    echo [错误] 一个驱动都没编出来。正常应该有二三十个。
    echo        检查一下驱动工程是否都在仓库里。
    goto :fail
)
echo       驱动 !PLUG_OK! 个
REM  下面那行 echo 的括号必须用全角。半角的 ) 会把 if 块提前闭合,后半截
REM  变成一条独立命令,报出「此时不应有 ...」—— 跟文件头「其二」同一个坑。
REM  这两行注释也刻意放在 if 块外面:块内的 REM 照样会走变量展开,
REM  写了叹号就有被延迟展开吃掉的风险。
if defined PLUG_SKIP (
    echo       跳过（没有 Export IDevice,不是驱动）:!PLUG_SKIP!
    echo       上面这行扫一眼。真驱动被误跳的话,装完那类设备就没了。
)

REM ---- 4. 发布 MCP 统一入口 ---------------------------------------------
echo.
echo [4/5] 发布 MCP 统一入口...

REM  Bridge 工程放哪儿都行,这里自己找:先看仓库根,再看 History_Step0\,
REM  最后在仓库里递归搜一遍。
REM
REM  认的不是路径而是内容 —— 合并版 Bridge 引用了 MaxChemical.Mcp.History 工程,
REM  Step 0 那版没有。这条判据要紧:旧版 Bridge 照样能编译能跑,只是少了 8 个
REM  历史查询工具,不数工具数发现不了。所以宁可找不到报错,也不按路径顺序凑合。
set "BRIDGE="
set "STALE="
set "NOHIST="

call :TryBridge "%ROOT%\MaxChemical.Mcp.Bridge\MaxChemical.Mcp.Bridge.csproj"
if not defined BRIDGE call :TryBridge "%ROOT%\MaxChemical.Mcp.History_Step0\MaxChemical.Mcp.Bridge\MaxChemical.Mcp.Bridge.csproj"
if not defined BRIDGE for /f "delims=" %%I in ('dir /s /b "%ROOT%\MaxChemical.Mcp.Bridge.csproj" 2^>nul') do if not defined BRIDGE call :TryBridge "%%I"

if not defined BRIDGE (
    echo [错误] 没找到可用的 MCP 统一入口工程。
    echo.
    if defined NOHIST (
        echo        在这里找到了合并版 Bridge:
        echo          !NOHIST!
        echo        但同一层缺少 MaxChemical.Mcp.History\ 。Bridge 是用
        echo        ..\MaxChemical.Mcp.History\ 这个相对路径引用它的,
        echo        两个目录必须挨着放,少一个编译不过。
        echo.
    )
    if defined STALE (
        echo        找到的 Bridge 是合并之前的 Step 0 旧版本（没有引用 History 工程）:
        echo          !STALE!
        echo        它只转发实时工具,不带 8 个历史查询工具,打出来的安装包
        echo        照样能装能跑,只是少一半功能 —— 所以这里不拿它顶数。
        echo        请换成合并后的版本。
        echo.
    )
    if not defined NOHIST if not defined STALE (
        echo        仓库里没搜到 MaxChemical.Mcp.Bridge.csproj。
        echo        请把 MaxChemical.Mcp.Bridge\ 与 MaxChemical.Mcp.History\
        echo        两个目录放进仓库,放哪一层都行,只要它们俩挨着。
        echo.
    )
    goto :fail
)
echo       工程: !BRIDGE!

dotnet publish "!BRIDGE!" -c Release -r win-x64 --self-contained false -o "%STAGE%\mcp" -p:DebugType=none
if errorlevel 1 goto :fail

REM 建库脚本随主程序走,安装时按此结构要求
if exist "%ROOT%\MaxChemical.Data\Maxchemical.sql" copy /y "%ROOT%\MaxChemical.Data\Maxchemical.sql" "%STAGE%\app\" >nul

REM 清掉 .pdb,减小体积
del /s /q "%STAGE%\*.pdb" >nul 2>&1

REM ---- 5. 编译安装包 -----------------------------------------------------
echo.
echo [5/5] 编译安装包(压缩较慢,约 3-5 分钟)...
if not exist "%~dp0output" mkdir "%~dp0output"

REM  先确认旧安装包删得掉。删不掉说明有东西正占着它 —— 杀毒软件在扫、
REM  资源管理器的预览窗格开着、虚拟机还挂着它、或者旧安装程序没退干净。
REM  不先查的话,ISCC 会一路压缩完(几分钟),到最后写图标资源那一步才失败,
REM  报一句 "EndUpdateResource failed ... (110)",白等。
del /f /q "%~dp0output\*.exe" >nul 2>&1
if exist "%~dp0output\*.exe" (
    echo.
    echo [错误] output 目录里的旧安装包删不掉,有程序正占着它。
    echo        ISCC 最后写图标资源时会失败,先处理掉再编译。
    echo.
    echo        常见的几个:
    echo          · 杀毒软件正在扫描 —— 把 installer\output\ 和 installer\stage\
    echo            加进排除列表,这也是 Inno 自己给的建议
    echo          · 资源管理器开着 output 目录,预览窗格占着文件 —— 关掉窗口
    echo          · 虚拟机还挂着上一版安装包 —— 在虚拟机里删掉或断开共享
    echo          · 上一版安装程序还在运行 —— 任务管理器里结束掉
    echo.
    goto :fail
)
"!ISCC!" /DDotnetRuntimeExe="!RT_NAME!" /DMySqlZipName="!MY_NAME!" /DVcRedistExe="!VC_NAME!" "%~dp0MaxChemical.iss"
if errorlevel 1 goto :fail

echo.
echo ============================================
echo   打包成功
echo ============================================
echo   输出目录: %~dp0output
REM  带上时间和大小。图标/资源改完发现没生效时,第一件事是确认手里这个
REM  exe 到底是不是刚才编出来的 —— 看一眼时间戳比什么都快。
for %%F in ("%~dp0output\*.exe") do echo   %%~tF  %%~zF 字节  %%~nxF
echo.
echo   图标没跟着变的话:上面 [图标] 那几行写明了编进去的是哪个文件。
echo   若那里已经是对的,就是资源管理器的图标缓存没刷 —— 把 exe 改个名
echo   或换个文件夹看,图标就会更新。
echo.
exit /b 0

:fail
echo.
echo ============================================
echo   打包失败
echo ============================================
exit /b 1

REM ------------------------------------------------------------------------
REM  子过程。放在 exit /b 之后,正常流程走不到这里
REM ------------------------------------------------------------------------

:TryPath
if exist "%~1" set "ISCC=%~1"
goto :eof

REM  编译一个设备驱动工程,把输出 DLL 放进 stage\app\Plugins\。
REM  不是驱动的直接跳过。
:BuildPlugin
REM  先用 DevicePlugins 引用粗筛一遍,把绝大多数工程挡在外面,
REM  免得对每个工程都去递归搜源码。
findstr /i /c:"DevicePlugins.csproj" "%~1" >nul
if errorlevel 1 goto :eof

REM  真正的判据:源码里有没有导出 IDevice。用宽松正则,连
REM  Export(typeof(DevicePlugins.Devices.IDevice)) 这种全限定写法一起认。
findstr /s /i /r /c:"Export(typeof(.*IDevice))" "%~dp1*.cs" >nul 2>&1
if errorlevel 1 (
    set "PLUG_SKIP=!PLUG_SKIP! %~n1"
    goto :eof
)

REM  没有驱动工程自定义 AssemblyName,输出 DLL 名就是 csproj 文件名。
set "PDLL=%~n1.dll"

REM  第二道:主目录里已经有同名 DLL 就不再放一份。
REM  Plugins\ 下每个 dll 都会被 MEF 建成一个 AssemblyCatalog,同一个程序集
REM  出现两份会扫出重复的 Export,组合时直接冲突 —— 比漏掉更难查。
REM  眼下没有驱动同时被 Shell 引用,这条是给以后留的。
if exist "%STAGE%\app\!PDLL!" (
    set "PLUG_SKIP=!PLUG_SKIP! %~n1-主目录已有"
    goto :eof
)

dotnet build "%~1" -c Release -p:DebugType=none --nologo -v q
if errorlevel 1 (
    echo       [失败] %~n1
    set "PLUG_FAIL=!PLUG_FAIL! %~n1"
    goto :eof
)

REM  驱动统一是 net8.0-windows,先按这个猜;万一哪个工程换了目标框架,
REM  再去 bin\Release\ 底下捞一遍,不至于因为路径猜错就漏掉一个驱动。
set "POUT=%~dp1bin\Release\net8.0-windows\!PDLL!"
if not exist "!POUT!" for /f "delims=" %%O in ('dir /s /b "%~dp1bin\Release\!PDLL!" 2^>nul') do set "POUT=%%O"
if not exist "!POUT!" (
    echo       [失败] %~n1 编译过了但找不到输出 DLL
    set "PLUG_FAIL=!PLUG_FAIL! %~n1"
    goto :eof
)

copy /y "!POUT!" "%STAGE%\app\Plugins\" >nul
if errorlevel 1 (
    echo       [失败] %~n1 拷贝失败
    set "PLUG_FAIL=!PLUG_FAIL! %~n1"
    goto :eof
)
set /a PLUG_OK+=1
echo       [%~n1]
goto :eof

REM  验一份 Bridge 工程能不能用。三道:文件在不在、是不是合并版、
REM  同级有没有 History。没过的记下原因,好让上面报错时说得具体些。
:TryBridge
if not exist "%~1" goto :eof
findstr /i /c:"MaxChemical.Mcp.History.csproj" "%~1" >nul
if errorlevel 1 (
    set "STALE=%~1"
    goto :eof
)
if not exist "%~dp1..\MaxChemical.Mcp.History\MaxChemical.Mcp.History.csproj" (
    set "NOHIST=%~1"
    goto :eof
)
set "BRIDGE=%~1"
goto :eof

:FromReg
REM  取到的 InstallLocation 自带尾随反斜杠,直接拼会得到 "...Inno Setup 6\\ISCC.exe"。
REM  Windows 容忍双反斜杠,但打印出来难看,也容易让人以为路径错了。
REM  值里可能含 "(x86)",所以只能 call 出去处理,不能写成 for 块(见文件头「其二」)。
for /f "tokens=2,*" %%A in ('reg query %1 /v InstallLocation 2^>nul ^| findstr /i "InstallLocation"') do call :FromRegPath "%%~B"
goto :eof

:FromRegPath
set "RP=%~1"
if "!RP:~-1!"=="\" set "RP=!RP:~0,-1!"
call :TryPath "!RP!\ISCC.exe"
goto :eof
