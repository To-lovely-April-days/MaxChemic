# MaxChemical 实时接口桥（MCP Server · Step 1）

把 MaxChemical **正在运行的主程序**暴露给外部 AI 客户端——腾讯 WorkBuddy、
OpenAI Codex、Google Gemini CLI、Claude Desktop / Claude Code，凡是支持 MCP 的都能接。

```
AI 客户端 ──stdio/JSON-RPC──▶ MaxChemical.Mcp.Bridge.exe ──命名管道──▶ MaxChemical 主程序
```

## 与 Mcp.History 的分工

两个 Server 各管一段，建议**都挂上**：

| | `maxchemical-history` | `maxchemical-bridge`（本程序） |
|---|---|---|
| 数据来源 | MySQL 只读账号 | 正在运行的主程序 |
| 回答什么 | 上周那批跑了几次、失败卡在哪一步 | 现在几度、流程跑到第几组、这台泵连上没有 |
| 主程序没开 | 照常可用 | 工具会回一句"主程序没运行" |
| 写风险 | 零（账号只有 SELECT） | 只读白名单（见下） |

## 当前只放只读

放行哪些工具由**主程序侧**的 `McpToolPolicy` 决定，本程序不做权限判断——
策略必须放在被保护的那一侧，这是唯一说得通的位置。

已放行 21 项，都是查询与计算：设备清单与实时参数、流程状态、DOE 进度与统计分析、
实验历史检索、模型预测与反向寻优、动力学拟合与浓度仿真、DOE 预览生成。

**明确不放行**，调用会拿到一句说明：

- 全部写操作：设参数、执行设备命令、流程启停、批次入库与执行、结果回填
- `connect_devices`：会真的开串口/建 TCP，对硬件有物理副作用
- `pause_flow`：暂停正在跑的流程属于控制动作（尽管它在本地不需要确认）
- `ask_user_choice`：会在现场机器上弹卡，外部客户端不该打断操作员
- `assign_*` 四个派工工具：**这是重点**。它们自身不需要确认，但会启动专员子循环，
  而执行专员持有 `execute_device_command` 等全部写操作工具。放出任意一个
  等于把写权限全放出去，而且绕过确认卡——确认事件的订阅方是聊天窗 ViewModel，
  外部客户端根本不在那条链路上。

白名单是**默认拒绝**的：不在名单里的一律拒掉。将来新增工具不会被自动暴露，
必须有人主动往名单里加。

## 一、编译与发布

```bat
cd MaxChemical.Mcp.Bridge
dotnet publish -c Release -r win-x64 --self-contained false -o C:\MaxChemical\mcp
```

产物 `MaxChemical.Mcp.Bridge.exe` + `appsettings.json`。可以和 `Mcp.History` 发到同一个目录。

目标框架是 `net8.0`（不是 `net8.0-windows`），纯控制台程序，不引用主程序任何工程——
主程序改了工具**不需要重新发布本程序**，工具清单是运行时从管道拿的。

## 二、主程序侧开关

主程序 `appsettings.json`：

```json
"MaxChemicalMcp": {
  "Mode": "readonly",
  "PipeName": "MaxChemical.Mcp.v1"
}
```

- `Mode`：`readonly`（默认）| `disabled`（整体关闭，不建管道）
- 写错或留空一律**降级为 readonly**——配置出错时应该少放权限，不是多放

改完重启主程序生效。启动日志里会有一行：

```
外部 MCP 管道服务已启动:管道 MaxChemical.Mcp.v1,模式 readonly,放行 21/43 项工具
```

## 三、在各家客户端里挂上

四家的配置格式几乎一样，都是 `mcpServers`，改路径即可。

**腾讯 WorkBuddy**（界面上有可视化 MCP 配置，也可直接编辑配置文件）：

```json
{
  "mcpServers": {
    "maxchemical-bridge": {
      "command": "C:\\MaxChemical\\mcp\\MaxChemical.Mcp.Bridge.exe",
      "args": []
    },
    "maxchemical-history": {
      "command": "C:\\MaxChemical\\mcp\\MaxChemical.Mcp.History.exe",
      "args": [],
      "env": {
        "MAXCHEMICAL_MCP_CONNSTR": "Server=localhost;Port=3306;Database=maxchemical;Uid=mcp_ro;Pwd=你的只读口令;CharSet=utf8mb4;SslMode=None;AllowPublicKeyRetrieval=True;"
      }
    }
  }
}
```

**Claude Desktop / Cline / Cursor**：同样的 `mcpServers` 结构。

**Gemini CLI**：写在 `~/.gemini/settings.json` 的 `mcpServers` 下，结构相同。
注意它默认会**净化子进程的环境变量**，需要传给 Server 的变量必须显式写在该 Server 的
`env` 里——这条对 History 的连接串有影响，对 Bridge 没有（Bridge 不需要任何机密）。

**Codex**：写在 `%USERPROFILE%\.codex\config.toml`，或用 `codex mcp add`。
ChatGPT 桌面版、CLI、IDE 扩展共用这一份配置。

> 提醒：Codex 的本行是编码代理。真要用它，价值在帮我们写设备驱动，
> 而不是当实验室操作台。控设备建议走 WorkBuddy 那条链路。

## 四、先手工验一遍（不依赖任何客户端）

MCP 的 stdio 传输就是「标准输入输出上的换行分隔 JSON-RPC 2.0」，可以直接用管道喂。
新建 `probe.txt`：

```
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"manual","version":"0"}}}
{"jsonrpc":"2.0","method":"notifications/initialized"}
{"jsonrpc":"2.0","id":2,"method":"tools/list"}
{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"list_devices","arguments":{}}}
```

```bat
type probe.txt | MaxChemical.Mcp.Bridge.exe
```

**stdout 应该只有 4 行 JSON，一个多余字符都不该有。** 启动日志、管道连接情况、
每次工具调用的记录都走 stderr，控制台里看得到但不会污染协议流。

> 这是 MCP 最容易踩的坑：只要往 stdout 写一句调试输出，客户端就解析失败并断开，
> 而它给的报错通常只是一句没有信息量的 "server disconnected"。
> 本程序里 stdout 只有 `Program.Send()` 一个出口，改代码时请守住这条。

验证第 4 行：主程序开着并且画布上有设备，应该看到设备清单；主程序没开，
应该看到一句说得清的"连不上主程序"，而**不是**进程崩掉或超时挂死。

## 五、配置项

| 配置项 | appsettings.json | 环境变量 | 命令行 | 默认 |
|---|---|---|---|---|
| 管道名 | `PipeName` | `MAXCHEMICAL_MCP_PIPE` | `--pipe` | `MaxChemical.Mcp.v1` |
| 连接超时(ms) | `ConnectTimeoutMs` | `MAXCHEMICAL_MCP_TIMEOUT_MS` | `--timeout-ms` | 3000（限 200–60000） |

优先级 **命令行 > 环境变量 > appsettings.json**，与 `Mcp.History` 一致。

## 六、边界

- **只监听本机命名管道，不开任何网络端口。** 远程机器连不上，这是有意的。
- 客户端与主程序必须在**同一台机器、同一个 Windows 账户**下——管道的默认
  访问控制授予创建者所属的登录会话。
- 目前**不能控制任何设备**，也不能写库、写盘。
- 主程序没运行时，本程序仍能启动，工具调用返回一句可读的说明。
  先开 AI 客户端、后开主程序不需要重启客户端，下一次调用会自动接上。

## 七、常见问题

**客户端显示 "server disconnected"，但手工管道测试是好的**
多半是路径里有空格没转义，或客户端的命令白名单没放行这个 exe。先看客户端侧的日志。

**工具返回「连不上 MaxChemical 主程序」**
按顺序查三件事：① 主程序在跑吗；② 主程序 `MaxChemicalMcp:Mode` 是不是被配成
`disabled` 了；③ 两个进程是不是同一个 Windows 账户（用「以管理员身份运行」启动
其中一个就会不是）。

**tools/list 返回空**
主程序没起来且本进程还没成功列过一次工具。等主程序起来后让客户端重新列一次。
本程序会缓存上一次成功的清单用于兜底。

**stderr 里有「管道协议版本不一致」**
Bridge 与主程序不是同一次发布的产物，重新发布两侧。

## 八、下一步（Step 2，尚未实现）

开放写操作需要先补齐评估报告里的 P0 安全项，并且要解决一个当前无解的问题：
**外部调用走不到确认卡**——确认事件 `XiaoTongAgent.ConfirmationRequested` 的唯一
订阅方是聊天窗 ViewModel。要开放写操作，必须先给外部调用做一条独立的
确认与审计通道，而不是简单地把白名单放开。
