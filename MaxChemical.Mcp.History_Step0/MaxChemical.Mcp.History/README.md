# MaxChemical 实验历史 MCP Server（Step 0）

一个**独立的、只读的** MCP Server。它把 MaxChemical 的实验历史数据库暴露给 AI Agent
（腾讯 WorkBuddy / Claude Desktop / 任何支持 MCP 的客户端），让 Agent 能回答
「上周那批苯甲醛氧化跑了几次、成功率多少、失败的那次卡在哪一步」这类问题。

## 这一步为什么要单独做

- **完全不碰 MaxChemical 主程序。** 只连 MySQL，主程序开着关着都不影响，崩了也不会连累主程序。
- 先把整条链路（WorkBuddy 找不找得到 exe → 子进程起不起得来 → stdio 通不通 →
  工具描述模型看不看得懂）趟平。这些外围问题一个都躲不掉，但都不值得在实时控制面上试错。
- 只读没有任何执行风险，可以直接放到现场机器上跑，不用等安全评审。

真正的设备控制在 Step 1（主程序内命名管道 + `MaxChemical.Mcp.Bridge.exe`），
那一步开始才涉及写操作，需要先补齐评估报告里列的 5 个 P0 安全项。

---

## 一、编译与发布

```bat
cd MaxChemical.Mcp.History
dotnet publish -c Release -r win-x64 --self-contained false -o C:\MaxChemical\mcp
```

产物是 `C:\MaxChemical\mcp\MaxChemical.Mcp.History.exe` + `appsettings.json`。
目标框架是 `net8.0`（**不是** `net8.0-windows`），纯控制台程序，不引用任何 WPF/Windows API，
所以它可以单独拷到别的机器上，不必跟主程序放一起。

需要目标机装了 .NET 8 运行时。要免运行时就加 `--self-contained true`，体积约 70 MB。

## 二、建一个只读数据库账号（必做）

程序里那几层 SQL 校验是兜底，**账号权限才是真正的那道门**。
用 root（或任何有 GRANT 权限的账号）连上 MySQL，执行：

```sql
CREATE USER 'mcp_ro'@'localhost' IDENTIFIED BY '换成一个强口令';
GRANT SELECT ON maxchemical.* TO 'mcp_ro'@'localhost';
FLUSH PRIVILEGES;
```

只 `GRANT SELECT`。这样即使程序里的关键字过滤被绕过、即使将来有人改代码加了写入口，
数据库这一层照样挡得住。

### 主机名那一段要对得上

`'mcp_ro'@'localhost'` 里的 `localhost` 和 `'%'` 在 MySQL 里是**两条不同的账号记录**。
连接串写 `Server=localhost` 时，MySQL 客户端在 Windows 上走 TCP 解析成 `127.0.0.1`，
通常仍命中 `@'localhost'`；但如果连接串写的是 `Server=127.0.0.1` 或机器名，就可能匹配不上而报
`Access denied`。同机部署建议两条都建：

```sql
CREATE USER 'mcp_ro'@'127.0.0.1' IDENTIFIED BY '同一个口令';
GRANT SELECT ON maxchemical.* TO 'mcp_ro'@'127.0.0.1';
FLUSH PRIVILEGES;
```

**不要**建 `'mcp_ro'@'%'`——那等于允许从任意 IP 登录，同机部署完全用不上。

### 验证真的是只读的

换 `mcp_ro` 登录后执行这三条，第二条**必须报错**：

```sql
SELECT COUNT(*) FROM maxchemical.experiment_records;   -- 正常返回
DELETE FROM maxchemical.experiment_records WHERE 1=0;  -- 必须报 command denied
SHOW DATABASES;                                        -- 只应看到 maxchemical + information_schema
```

第二条如果成功了，说明 GRANT 没生效，或者你还连在 root 上。

### 连接串必须带 AllowPublicKeyRetrieval=True

MySQL 8.0 默认认证插件是 `caching_sha2_password`，客户端要先取 RSA 公钥才能加密口令。
少了这个参数会直接连不上，报的还是一句看不出原因的认证错误。
主程序 `DatabaseConfigService` 里也带了这一项（那里还有一行注释记着当初踩坑的经过）。

```
Server=localhost;Port=3306;Database=maxchemical;Uid=mcp_ro;Pwd=你的口令;CharSet=utf8mb4;SslMode=None;AllowPublicKeyRetrieval=True;
```

也可以在建账号时改用旧插件绕开（但不推荐，`caching_sha2_password` 更安全）：

```sql
ALTER USER 'mcp_ro'@'localhost' IDENTIFIED WITH mysql_native_password BY '口令';
```

> 注意：主程序 `DatabaseConfigService` 里有一个硬编码的 `root/123456` 兜底值。
> **不要**把它抄到这里来。本程序在没配连接串时用的是一个不带凭据的兜底值，
> 目的只是让进程能起来并给出一条看得懂的报错。
> 顺带一提，那个 root/123456 本身也该换掉——它是整个库的最高权限。

## 三、配置

三个来源，优先级 **命令行 > 环境变量 > appsettings.json**：

| 配置项 | appsettings.json | 环境变量 | 命令行 | 默认 |
|---|---|---|---|---|
| 连接串 | `ConnectionString` | `MAXCHEMICAL_MCP_CONNSTR` | `--conn` | 无凭据兜底值 |
| 单次最大行数 | `MaxRows` | `MAXCHEMICAL_MCP_MAXROWS` | `--max-rows` | 200（限 1–5000） |
| 查询超时（秒） | `CommandTimeoutSeconds` | — | `--timeout` | 15（限 1–300） |
| 是否开放 query_sql | `EnableRawSql` | `MAXCHEMICAL_MCP_RAWSQL` | `--raw-sql` | true |

支持环境变量的原因：WorkBuddy 的 MCP 配置里可以给子进程设 `env`，
这样连接串不用落在磁盘上的明文配置文件里。

## 四、在 WorkBuddy 里挂上

WorkBuddy 支持 STDIO 传输的 MCP Server。在它的 MCP 配置里加一项：

```json
{
  "mcpServers": {
    "maxchemical-history": {
      "command": "C:\\MaxChemical\\mcp\\MaxChemical.Mcp.History.exe",
      "args": [],
      "env": {
        "MAXCHEMICAL_MCP_CONNSTR": "Server=localhost;Port=3306;Database=maxchemical;Uid=mcp_ro;Pwd=你的只读口令;CharSet=utf8mb4;SslMode=None;"
      }
    }
  }
}
```

Claude Desktop / Cline / Cursor 的配置格式一样，改路径即可。

配完之后，问它「最近一周做了哪些实验」就能看到它调 `list_experiments`。

## 五、先手工验一遍（不依赖任何客户端）

MCP 的 stdio 传输就是「标准输入输出上的换行分隔 JSON-RPC 2.0」，
所以可以直接用管道喂它。新建 `probe.txt`：

```
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"manual","version":"0"}}}
{"jsonrpc":"2.0","method":"notifications/initialized"}
{"jsonrpc":"2.0","id":2,"method":"tools/list"}
{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"describe_schema","arguments":{}}}
```

然后：

```bat
type probe.txt | MaxChemical.Mcp.History.exe
```

**stdout 应该只有 4 行 JSON，一个多余字符都不该有。**
启动日志、数据库连通情况、每次工具调用的记录都走 stderr，在控制台里能看到但不会污染协议流。

> 这是 MCP 最容易踩的坑：只要往 stdout 写一句调试输出，客户端就解析失败并断开，
> 而它给的报错通常只是一句没有信息量的 "server disconnected"。
> 本程序里 stdout 只有 `Program.Send()` 一个出口，改代码时请守住这条。

## 六、提供的工具（7 或 8 个）

| 工具 | 用途 |
|---|---|
| `describe_schema` | 列出所有表；传 `table` 则列出该表字段。模型不确定数据在哪时的第一站。 |
| `list_experiments` | 实验清单，可按关键词 / 最近 N 天 / 成功与否 / 排除模拟模式筛选。 |
| `get_experiment` | 单次实验详情：主记录 + 逐步节点明细 + 变量记录。定位失败在哪一步用它。 |
| `get_experiment_data` | 设备采集数据。**默认给统计摘要**（点数/最小/最大/均值），`mode=raw` 才给原始点。 |
| `summarize_experiments` | 按天 / 流程 / 操作者聚合，出次数、成功率、平均耗时。适合生成周报。 |
| `list_doe_batches` | DOE 批次清单。 |
| `get_doe_batch` | DOE 批次详情：批次信息 + 因子 + 响应 + 各组运行结果。 |
| `query_sql` | 受限只读 SQL，覆盖前面工具够不着的问题。`EnableRawSql=false` 时不注册。 |

所有工具返回的都是 **Markdown 表格**，不是 JSON。两个原因：模型读表格比读嵌套 JSON 省 token；
人把结果转发到日报或微信里时不用再加工。

### `query_sql` 的限制

- 必须以 `SELECT` 或 `WITH` 开头
- 不允许分号拼接多条语句
- 不允许出现注释（`--`、`/*`、`#`）——注释是绕过关键字检查最常用的手法
- 不允许任何写类关键字（`insert/update/delete/drop/alter/create/truncate/grant/...`）
- 没写 `LIMIT` 会自动补一个

副作用：`WHERE description LIKE '%update%'` 这种把关键字写进字符串字面量的查询也会被拒。
这是有意的取舍——宁可误伤，也不做「解析 SQL 判断关键字在不在字符串里」这种容易写错的事。
真需要就改用固定工具，或者临时把 `EnableRawSql` 关掉走 DBA。

## 七、边界（明确说清楚，免得被误当成能干别的）

- **不能控制任何设备。** 没有任何写数据库、写文件、发串口的路径。
- **不碰主程序。** 不读它的内存、不发消息给它、不需要它在运行。
- 只能看到连接串里那个库。跨库查询会被账号权限挡掉。
- 大字段（`input_parameters` / `output_parameters` / `design_config_json` 这些 TEXT）
  在渲染时**统一截断到 200 字符**。要看全文请用 `query_sql` 单独查那一条。

## 八、常见问题

**客户端显示 "server disconnected"，但手工管道测试是好的**
多半是路径里有空格没转义，或者 WorkBuddy 的命令白名单没放行这个 exe。先看 WorkBuddy 侧的日志。

**工具返回「数据库查询失败：Access denied」**
只读账号没建好，或者连接串里的 host 段与 `CREATE USER ... @'localhost'` 对不上
（本机连接走的是 `localhost` 还是 `127.0.0.1` 会命中不同的账号记录）。

**启动时 stderr 打了「数据库暂时连不上」，但进程没退出**
这是刻意的。MCP 客户端遇到子进程立刻退出，只会显示一句没有信息量的 disconnected；
让它活着、在工具调用时返回一条说得清的错误，排障容易得多。

**返回的表格里最小/最大/平均是空的**
`parameter_value` 是 TEXT 字段，摘要模式会先 `CAST` 成数值再统计。
文本型参数（状态描述之类）转不动，这三列就是空，看采样点数那列即可。
