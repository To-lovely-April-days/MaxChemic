# MaxChemical 安装包

一个安装包搞定:主程序 + MCP 接口 + .NET 8 运行时 + MySQL + WorkBuddy 连接器配置。
客户机双击即用,不需要预装任何东西,不需要联网。

---

## 一、为什么这么设计(先看这段,能省很多来回)

### 不用自包含发布,而是内置运行时安装器

三个 exe(主程序 + Bridge + History)如果都用 `--self-contained`,等于把 .NET 运行时
打进去三份,体积翻三倍。改成**框架依赖发布 + 安装时静默装一次 .NET 8 Desktop Runtime**,
一份运行时供三个程序共用。已装过的机器会自动跳过。

### 会先检测机器上有没有 MySQL,并让你选

安装向导第一页会**扫描 Windows 服务注册表**,列出机器上已有的 MySQL / MariaDB 服务
(比查安装目录可靠——免安装版没有卸载注册表项,但注册成服务就一定在这里),然后让你选:

| 模式 | 做什么 | 适用 |
|---|---|---|
| **内置(推荐)** | 部署一个独立实例:独立端口、独立服务名 | 客户机上没有数据库,或有但不想让我们碰 |
| **使用现有** | 只在客户已有的实例上建库建账号,不装第二个 | 客户有 DBA 统一管理数据库 |

即便检测到已有 MySQL,默认仍是**内置**——互不干扰,风险最小。

选内置时,填完端口会**立刻检测该端口是否被占用**,占了就当场拦住让你换。
不做这一步的话,要等安装到最后 `net start` 失败才暴露,那时文件都铺完了,只能卸载重来。

> 端口检测搜的是 `":端口 "`(带尾随空格)。这个细节要紧:否则查 3306 会误命中
> 33060——那是 MySQL X Protocol 的默认端口,并不代表 3306 被占。

选"使用现有"时会多问主机、端口、管理员账号口令。**管理员口令只在安装过程中使用,
不写进任何配置文件**;而且这条路径**绝不会改动客户 root 的口令**——
设置 root 口令的语句单独放在 `init-db-embedded.sql`,只在内置模式执行,
由代码路径保证,而不是靠注释提醒。

### MySQL 用免安装 zip 版,不用 MSI

MSI 装的 MySQL 会占用 3306、注册全局服务、与客户机上已有的 MySQL 打架,卸载还容易留残留。
改成解压 zip 版到安装目录、**用独立端口(默认 3307)注册成独立服务** `MaxChemicalMySQL`,
好处:

- 客户机上原来有 MySQL 也不冲突,各跑各的
- 卸载时连服务带数据目录一起干净移除
- 数据目录在安装目录下,备份就是拷一个文件夹

### 安装时生成数据库账号,不再用 root/123456

代码里 `DatabaseConfigService` 的兜底连接串是硬编码的 `root/123456`(整库最高权限)。
安装包会在装的时候建两个账号并把连接串写进配置文件:

| 账号 | 权限 | 用途 |
|---|---|---|
| `maxchemical` | 该库全部读写 | 主程序 |
| `mcp_ro` | 该库只读(SELECT) | MCP 历史查询接口 |

口令在安装界面上输入(有默认值可改)。**这样 root 弱口令这个老问题在新装的机器上就不存在了。**

两种数据库模式都会建这两个账号;只建 `@'localhost'` 与 `@'127.0.0.1'`,不建 `@'%'`——
后者等于允许从任意 IP 登录,本机部署完全用不上。

### WorkBuddy 连接器自动配好

安装完会在安装目录生成 `workbuddy-mcp.json`,里面的 exe 路径、只读连接串都是
按实际安装路径填好的,直接复制粘贴进 WorkBuddy 即可。安装向导最后一页会提示位置。

---

## 二、打包前要准备的东西(三个前置文件,仓库里不放)

在 `installer\redist\` 目录下放这三个文件:

| 文件 | 从哪下 | 大小 |
|---|---|---|
| `windowsdesktop-runtime-*-win-x64.exe` | https://dotnet.microsoft.com/download/dotnet/8.0 → **.NET Desktop Runtime 8.0.x x64** | 约 58 MB |
| `mysql-*-winx64.zip` | https://dev.mysql.com/downloads/mysql/ → **Windows (x86, 64-bit), ZIP Archive**(不是 MSI) | 约 220 MB |
| `VC_redist.x64.exe` | https://aka.ms/vs/17/release/vc_redist.x64.exe | 约 25 MB |

> **VC++ 那个别漏。** MySQL 的免安装 zip 版是 MSVC 编译的,`mysqld.exe` 硬依赖
> `vcruntime140_1.dll`,而 Windows 不预装它——干净系统上一定缺。缺了的后果很隐蔽:
> `mysqld` 连启动都启动不了,数据目录初始化失败,**服务永远注册不上**,
> 但安装程序从头到尾一切正常,最后显示"安装成功"。
> 症状就是「装完在服务列表里找不到 MaxChemicalMySQL」。

> **版本号不用管。** `build.bat` 会按通配符扫描 `redist\` 取实际文件名,
> 再用 `/D` 传给 ISCC。下多少版本号都行,不必改任何脚本——
> 唯一的要求是每类文件各只放一个。三个缺任何一个,`build.bat` 第 1 步就会停下。

还需要装 **Inno Setup 6**(免费,6.5 以上):https://jrsoftware.org/isdl.php
装的时候语言列表里勾不勾简体中文都行——`installer\lang\ChineseSimplified.isl`
仓库里自带了一份,编译器自己没有就用这份。(Inno 默认只装英文,不自带这个文件。)
`build.bat` 会自己去找 `ISCC.exe`(PATH → 卸载注册表 → 常见路径,含用户级安装的
`%LOCALAPPDATA%\Programs\Inno Setup 6\`)。装在别处就设个环境变量,等号后别加引号:

```bat
set ISCC_PATH=D:\Tools\Inno Setup 6\ISCC.exe
```

### MCP 两个工程放哪儿都行,但得挨着

`MaxChemical.Shell\` 必须在仓库根下(`build.bat` 按 `installer\..` 找它)。
MCP 那两个工程则不挑位置,第 3 步会自己搜:先看仓库根,再看
`MaxChemical.Mcp.History_Step0\`,还没有就在仓库里递归找一遍。

唯一的硬要求是**这两个目录必须在同一层**:

```
随便哪一层\
  ├─ MaxChemical.Mcp.Bridge\      MCP 统一入口
  └─ MaxChemical.Mcp.History\     Bridge 用 ..\MaxChemical.Mcp.History\ 引用它
```

> **认的是内容,不是路径。** 机器上可能存着不止一份 Bridge——比如
> `MaxChemical.Mcp.History_Step0\` 里那份 Step 0 阶段的旧版,它还没合并历史工具。
> 脚本用「csproj 里有没有引用 `MaxChemical.Mcp.History.csproj`」来区分:
> 有就是合并版,没有就是旧版,直接报错不用。
>
> 这道判据省不掉:旧版 Bridge 照样能编译、能装、能跑,只是 30 个工具变 22 个,
> 少的正是 8 个历史查询工具。不数工具数根本发现不了,等发到客户手上才知道就晚了。

---

## 三、打包(一条命令)

```bat
cd installer
build.bat
```

它按顺序做这些:

1. 校验 `redist\` 里三个前置文件在不在
2. `dotnet publish` 主程序 + 32 位网关代理到 `stage\app\`
3. **逐个编译设备驱动**到 `stage\app\Plugins\`
4. `dotnet publish` MCP 统一入口到 `stage\mcp\`
5. 调 `ISCC.exe` 编译出 `installer\output\MaxChemical-Setup-1.0.0.exe`

### 第 3、4 步为什么不能省

`dotnet publish` 主程序**不会带上设备驱动**。驱动工程反过来引用 `DevicePlugins`,
Shell 并不引用它们,publish 的依赖图里根本看不到——而主程序是运行时扫
`<程序目录>\Plugins\*.dll` 用 MEF 装配设备的(`DeviceManager.GetPluginPath`)。

漏了这步的后果特别不显眼:**软件能装能启动,界面一切正常,只是设备库是空的**,
日志里仅有一句"插件目录不存在",然后它自己建个空目录接着跑。

网关代理同理。Shell 的 csproj 里确实有个拷贝它的步骤,但那是 `AfterTargets="Build"`
拷到 `bin\`,而 publish 不会把 build 之后才写进 `bin\` 的文件带走,所以对打包无效。

两处都加了防呆:驱动一个都没编出来时 `build.bat` 直接报错退出;
绕过 `build.bat` 直接跑 ISCC 的话,`MaxChemical.iss` 会因为找不到
`stage\app\Plugins` 而编译失败,不会让你打出一个空设备库的包。

成品大约 **300~350 MB**(主要是 MySQL 和运行时)。

---

## 四、客户机上安装

双击安装包,向导会依次:

1. 选安装目录(默认 `C:\Program Files\MaxChemical`)
2. **设置数据库端口与口令**(端口默认 3307;两个账号口令有默认值,建议改)
3. 检测 .NET 8 Desktop Runtime,没有就静默安装
4. 检测 VC++ 2015-2022 x64 运行库,没有就静默安装(MySQL 要用)
5. 解压 MySQL、初始化数据目录、注册服务 `MaxChemicalMySQL` 并启动
6. 建库 `maxchemical`、建两个账号、导入表结构
7. **验收检查**:读部署日志确认成功标记,内置模式还要确认服务真的注册上了;
   不通过就弹窗说明原因和日志位置
8. 把连接串写进 `MaxChemical.Shell.dll.config` 与 `mcp\appsettings.json`
9. 生成 `workbuddy-mcp.json`、创建快捷方式

装完直接能跑,**不需要重启**。

> **改 `[Run]` 里那两条 `cmd /C` 时务必留意最外层的那对引号。**
> `cmd /?` 写明 `/C` 的引号处理只有两条规则:规则一要求「整行恰好两个引号」才原样保留,
> 我们那行有 22 个引号,必然落到规则二——「首字符是引号就去掉它,并去掉整行最后一个引号」。
> 没有外层引号的话,被去掉的就是 bat 路径的开引号和日志路径的闭引号,
> 整行引号奇偶性翻转:参数值跑到引号外,参数间的空格反倒进了引号里。
> cmd 只在引号外的空白处断命令名,于是去找一个叫 `C:\Program` 的程序,**脚本一行都没跑**。
> 连带 `>` 也落进引号内不再是重定向,所以 `mysql-setup.log` 根本不生成——
> 没服务、没日志、不报错,这个组合极难查。卸载那条只有 4 个引号,同样中招,
> 后果是卸载时服务没删掉,留个指向已删目录的僵尸服务。

> 第 7 步是补上的一个窟窿。`[Run]` 段不看退出码,所以在此之前,数据库部署失败时
> 安装程序照样一路走完显示"安装成功"——用户要等到打开软件连不上库、或者发现
> 服务列表里没有 `MaxChemicalMySQL` 才察觉,那时早不知道是哪一步的问题了。
> 现在 `mysql-setup.bat` 成功时会输出一行纯 ASCII 的 `MAXCHEMICAL_DB_SETUP_OK`,
> 安装程序在 `ssPostInstall` 里读日志找它。用纯 ASCII 是因为日志是 UTF-8,
> 而 Inno 的 `LoadStringFromFile` 按字节读,中文串匹配不上。

### 装完验证四件事

```bat
sc query MaxChemicalMySQL
```
应显示 `RUNNING`。

**设备库不是空的。** 打开设备库页面,应该能看到各类设备。空的话先去安装目录看
`Plugins\` 下有没有 DLL——这一项容易被忽略,因为软件本身照常启动,不报任何错。

启动主程序,日志里应有:
```
外部 MCP 管道服务已启动:管道 MaxChemical.Mcp.v1,模式 readonly,放行 22/44 项工具
```

把安装目录下 `workbuddy-mcp.json` 的内容贴进 WorkBuddy 的 MCP 配置,新开会话应看到
**30 个工具**(22 实时 + 8 历史)。

---

## 五、卸载

控制面板卸载会:停止并删除 `MaxChemicalMySQL` 服务 → 删除程序文件。

**数据目录 `data\` 默认保留**(实验数据是客户资产,不能因为卸载就没了)。
需要彻底清除的,卸载后手工删除安装目录即可。

---

## 六、目录结构(装完之后)

```
C:\Program Files\MaxChemical\
├─ MaxChemical.Shell.exe          主程序
├─ Plugins\                       设备驱动 DLL(build.bat 第 3 步铺进来的)
├─ GatewayConfigProxy\            32 位网关代理,主程序按固定路径找它
├─ mcp\
│   ├─ MaxChemical.Mcp.Bridge.exe 统一 MCP 入口(实时+历史)
│   ├─ MaxChemical.Mcp.History.exe 历史工具也可单独挂,平时用不到
│   └─ appsettings.json           管道名 + 只读连接串
├─ mysql\
│   ├─ bin\ lib\ share\           MySQL 程序本体
│   ├─ my.ini                     配置(端口、数据目录)
│   └─ data\                      数据目录(卸载保留)
├─ workbuddy-mcp.json             连接器配置,复制粘贴用
└─ unins000.exe
```

---

## 七、已知边界

- **只支持 x64**。32 位机器装不了(现在也基本不存在了)。
- **MySQL 授权**:MySQL Community 是 GPL。随闭源商业软件分发存在合规讨论空间,
  对外销售前建议确认;若需规避,把 `redist` 里换成 **MariaDB** 的 zip 版即可——
  命令行参数与 MySQL 兼容,`mysql-setup.bat` 基本不用改(变量名已抽出)。
- 安装需要**管理员权限**(要注册 Windows 服务)。
- 若客户机 3307 端口被占,安装向导里改成别的即可,配置会跟着走。
- **系统要求 Windows 10 1607(Build 14393)以上**,安装向导第一步就会拦下不合格的机器。
  这是 .NET 8 的硬性要求,不是我们加的限制。

  1507(Build 10240)这种老系统上,安装过程本身不会报错,**主程序一启动就崩**,
  事件查看器里只留一个 `0xe0434352`——所有托管异常都是这个码,看不出跟系统版本
  有关系,不知道的话能查很久。所以宁可在装之前就拦住。

  查版本:`winver` 看内部版本号;已经崩了的话看报告里 `KERNELBASE.dll` 的版本,
  `10.0.10240.x` 就是 1507。顺带一提,非 LTSB 的 1507 早在 2017 年 5 月就停止支持了。

### 程序目录的写权限

这个软件把运行数据写在自己的安装目录下,而 `Program Files` 对普通用户是只读的。
安装包已经把 `Logs\` 与 `ControlLogs\` 预先建好并授予 Users 写权限——
**少了这一步主程序根本起不来**:`App.OnStartup` 第一句就去建 `Logs\`,没有 try,
权限不足直接抛异常,窗口都不出来,事件查看器里只留一个 `0xe0434352`
(所有托管异常都是这个码),看不出跟权限有关。

只授这两个目录是有意的。给整个 `{app}` 开写权限等于让任何本地用户都能替换
`MaxChemical.Shell.exe`,是标准的本地提权路径。代价是还有两处写不了,
都不影响主流程:

| 写不了的 | 后果 |
|---|---|
| `{app}\RecentFiles.json` | 最近打开的文件列表存不下来(代码里有 try,不会崩) |
| `{app}\GatewayConfigProxy\zlsdk.log` | 网关 SDK 的诊断日志没有 |

要根治,得改代码把可写数据挪到 `%ProgramData%\MaxChemical\` 下——那才是
Windows 上放这类数据的正确位置。眼下先靠安装时授权顶着。
