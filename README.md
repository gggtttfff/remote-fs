# remote-fs

给 Claude Code 等 AI 客户端用的远程服务器 MCP。一个常驻进程接住连续的工具调用，通过内置 SSH/SFTP 完成目录浏览、文件改写、上传下载和远程命令，调用方拿到的是带边界的结构化结果。

原生单文件可执行程序，基于 .NET 9 + SSH.NET。程序自己完成 SSH，目标机器上要有 shell 与 SFTP。发布包自带运行时，解压即可运行。

## 为什么做成 remote-fs

AI 改远程配置、看日志、在服务器上编译，一次对话里会连续调用十几次。把这些调用收成一个 MCP 进程，是为了让下面四件事同时成立。

**每次调用都有边界。** 文件操作走 SFTP 工具，路径在连上服务器之前就按该主机的 `allowedRoots` 校验。改配置用精确替换和原子写入：只动匹配到的文本，写到一半中断不会留下半份文件；文件超过单次读取上限时直接拒绝编辑，避免只拿到开头一段就把整份文件覆盖掉。命令是单独的工具，全局策略可以设成拦截越界路径，或直接禁用。

**长命令能活过这一次工具调用。** 编译和测试经常超过一次 MCP 调用能等待的时间。每条命令都在远端落成一个任务目录，用 `setsid`（没有则用 `nohup`）脱离当前 SSH 会话，通道关掉任务还在跑。输出按字节偏移增量读取。时限由服务器上的看护进程执行，本机 MCP 进程退出后到期仍会终止整个进程组。服务重启后按远端目录接管遗留任务，任务目录是唯一事实来源。

**连接可以复用。** MCP 工具调用是串行的，同一主机在空闲期内复用同一条 SFTP 连接，目录列举还有短时缓存。连续读配置、改一行、再看目录，不必每次重新握手。

**部署和追溯都落在程序自己身上。** 单文件发布，配置、缓存、日志都在可执行文件旁边，拷走整个目录就带走环境。每次工具调用的参数、结果、耗时写入 JSONL 日志，出问题能对上是哪一次调用动了哪个路径。Windows 上双击打开配置界面，不改任何外部客户端配置文件，只生成可复制的 MCP 片段。

## 特性

### 文件

23 个 MCP 工具，`host` 省略时用全局默认主机。

- 浏览：`remote_ls`、`remote_tree`、`remote_stat`、`remote_search`
- 读取：`remote_read_file` 带行号，按 `offset` 分页；二进制内容拒绝当文本读，改走下载
- 改写：`remote_edit_file` 精确替换；`remote_write_file` 覆盖或追加
- 整理：创建、删除、移动、复制、改权限、建软链接。移动默认不覆盖；删目录必须显式递归；软链接只删链接本身
- 传输：单文件上传、下载，以及整目录递归下载。默认不覆盖已有文件

目录列举缓存在程序目录的 `cache/listings/`，默认 30 秒。会改远程内容的工具调用结束后，按主机使缓存失效。

### 命令与长任务

`remote_exec` 一律先在远端建任务再执行。

- 前台：等到结束或超时，一次返回退出码和输出。超时会终止整个进程组，并带回终止前已经产生的输出
- 后台：`background=true` 立即返回任务号。用 `remote_job_status` 按偏移拉新增日志，用 `remote_job_list` 查看遗留任务，用 `remote_job_kill` 终止
- 时限：`ttlMinutes` 不传则用全局配置，默认 30 分钟，范围 1 到 1440 分钟。到期由远端看护进程先 TERM 再 KILL
- 终止范围是整个进程组，命令拉起的子进程一起结束。终止前核对进程启动时间，pid 已被复用时拒绝动手
- 任务文件在远端登录用户的 `~/.remote-fs/jobs/<任务号>/`。正在跑的任务不要手删这个目录，否则状态查询会失效。已结束任务按保留天数和每台主机的数量上限清理

任务状态里，`运行中` 和 `已结束（退出码 0）` 是正常结果。另外三种都是失败：超过时限被看护进程杀掉、被手动终止、进程消失但没有退出码（常见于 OOM 或被 KILL）。

### 安全

- 每台主机一份路径白名单。全局可再强制「没配白名单的主机拒绝一切路径」
- 白名单生效时拒绝操作软链接，路径本身或任一上级目录是软链接都会被拦下，避免路径看起来合法、实际指向范围外
- 主机密钥按 SHA256 指纹校验。开了强校验却没填指纹时直接拒绝连接，避免「以为校验了其实没校验」
- `remote_exec` 的路径检查是尽力而为：能拦住命令里写明的越界绝对路径和相对 `..`，拦不住变量拼接、命令替换。需要严格隔离时把 `remoteExecPolicy` 设为 `Deny`
- 单次输出超过上限会截断并提示，避免一次结果撑满模型上下文
- 密码优先从环境变量读取。日志默认记录完整出入参，服务器上的文件内容会明文落盘

### 运行形态

- 单文件，自带运行时
- 配置、缓存、日志都在可执行文件旁边，可用 `REMOTE_FS_HOME` 把配置目录指到别处
- 能从 `~/.ssh/config` 导入主机
- Windows 提供图形界面：主机、日志、日志设置、全局配置。可最小化到托盘，也可写入当前用户的开机启动项（不需要管理员）
- Linux / macOS 为命令行

## 安装

从 [Releases](https://github.com/gggtttfff/remote-fs/releases) 下载对应平台的压缩包，解压后把 `remote-fs`（Windows 为 `remote-fs.exe`）放进 `PATH`。

## 快速开始

### 1. 添加主机

```bash
remote-fs hosts add web1
```

交互式填写地址、用户名、端口、认证方式和允许访问的路径范围。

已有 `~/.ssh/config` 时可以直接导入：

```bash
remote-fs hosts import          # 导入全部
remote-fs hosts import web1     # 只导入指定别名
```

自动带入 `HostName`、`Port`、`User`、`IdentityFile`，别名沿用 `Host` 名。图形界面里也可以勾选后导入。

导入的主机还没有指纹，指纹校验默认关着。探测到指纹并写入配置后，把 `strictHostKeyChecking` 改为 `true`。

### 2. 记录主机密钥指纹

```bash
remote-fs hosts fingerprint web1
```

把输出的指纹填进该主机的 `knownHostKeyFingerprint`。程序启动时会打印配置目录的真实路径，主机列表在其中的 `hosts.json`。

### 3. 接入客户端

`remote-fs serve` 以 stdio 方式提供 MCP。Claude Code 的配置示例：

```json
{
  "mcpServers": {
    "remote-fs": {
      "command": "remote-fs",
      "args": ["serve"]
    }
  }
}
```

重启客户端后即可调用。图形界面的「生成 MCP 配置」会按当前程序的真实路径给出同样的片段，复制后自行合并。程序不会改客户端的配置文件。

## 数据位置

程序目录下三个子目录，拷贝整个目录即可带走：

```
<程序目录>/
  config/    配置：hosts.json（主机列表）、config.json（全局配置）
  cache/     缓存：tmp（下载临时文件）、listings（远程目录列举缓存）
  log/       日志：remote-fs.log 与轮转归档
```

配置或日志目录写不进去时，对应功能禁用并在界面提示。首次运行若发现 `config/` 还没有文件、而用户主目录的 `.remote-fs/` 里有旧配置，会把旧文件复制过来，原件保留。`REMOTE_FS_HOME` 只重定向配置目录。

远端任务目录不在本机，而在 SSH 登录用户的家目录：`~/.remote-fs/jobs/<任务号>/`。

## 命令

```
remote-fs                          打开图形配置界面（仅 Windows，等同于 gui）
remote-fs serve                    启动 MCP stdio 服务
remote-fs listen [--port 43336]              在前台监听备用端口
remote-fs listen --install-service [--port 43336]  注册成开机自启的系统服务
remote-fs gui                      打开图形界面（仅 Windows）
remote-fs hosts list               列出已配置主机
remote-fs hosts add <别名>         交互式添加主机
remote-fs hosts remove <别名>      删除主机
remote-fs hosts fingerprint <别名> 查看主机密钥指纹
remote-fs hosts import [别名...]   从 ~/.ssh/config 导入
remote-fs config show              查看当前全局配置
remote-fs version                  显示版本号
```

## 图形界面（仅 Windows）

```bash
remote-fs gui
```

不带参数直接双击 `remote-fs.exe` 也会打开这个界面。

改动在点击保存后写入程序目录下的 `config/hosts.json`。配置文件解析失败时界面进入只读，避免覆盖损坏的内容。

| 标签页 | 内容 |
| --- | --- |
| 主机配置 | 主机增删改、从 ssh_config 导入、指纹探测、连接测试、生成 MCP 配置片段 |
| 日志 | 实时日志，等级/关键词/失败过滤、底部跟随、详情复制 |
| 日志设置 | 日志等级、是否记录出入参、轮转与保留、清空日志 |
| 配置 | 默认主机、白名单、本地允许目录、读写上限、超时、命令策略、软链接、目录缓存、后台任务时限与保留、退出时终止任务、关闭窗口时驻留托盘、开机启动 |

「关闭窗口时驻留后台」开启后，点关闭只收进托盘，从托盘菜单选退出才结束进程。开机启动写入当前用户的 Run 注册表项，以注册表为准。

Linux / macOS 没有图形界面，用上面的 `hosts` 与 `config` 子命令。

## SSH 不可用时的备用端口

文件读写默认走 SSH。主机把 `agentFirst` 设为 `true` 时，直接走备用端口，不再尝试 SSH。没开这个开关时，SSH 在连接阶段失败后的 10 分钟内也不再尝试 SSH，时间过了会再探一次。认证失败和指纹不符不会切换，也不会记入这 10 分钟。

备用端口跑在 **Linux 服务器本机**，直接改本机文件，不经过 SFTP。同一条 TCP 连接会连续承接多次调用。目前承接全部文件工具（读写、按行替换、列举、搜索、目录树、创建、删除、移动、复制、改权限、建链接），以及 `remote_exec` 和任务查询/终止。上传下载仍然只走 SSH。

服务器上：

服务器上注册成系统服务，只需做一次。之后开机、进程退出、替换程序文件都会自己起来，不用再手工执行 listen：

```bash
remote-fs listen --install-service --port 43336
```

服务名是 `remote-fs-agent`。允许目录仍放在程序旁边的 `config/config.json`。验签用运行用户的 `~/.ssh/authorized_keys`。

备用通道沿用主机配置里的 SSH 私钥签名，不再另配一套密钥。服务器用运行用户的 `~/.ssh/authorized_keys` 验签。界面上只保留原来的「私钥路径」。

```json
"agentPort": 43336,
"agentFirst": true
```

`privateKeyPath` 仍指向原来的 SSH 私钥。文件内容仍然是明文，这条通道没有 TLS。

## 日志

日志在可执行文件同级的 `log/`，JSONL，一行一条。内容包括工具名、主机、参数、结果、耗时、成败和异常堆栈。

- 目录不可写时日志功能禁用，并在界面用红条提示
- 超过 `logMaxFileSizeBytes` 后切成 `remote-fs-<时间戳>.log`
- 按 `logRetentionDays` 与 `logMaxFiles` 清理
- 目录权限收紧为当前用户可访问（Windows 上同时保留 Administrators）
- 多个进程可以同时追加写

`logRecordPayloads` 默认开启。AI 读写的服务器文件内容会明文落盘，其中可能有密码和密钥。不需要时在「日志设置」里关掉，或把 `logLevel` 调到 `Warn`。

`logTraceToolTiming` 默认关闭。打开后，每次工具调用在原有日志之外再写一条 `tool_timing`，只保留工具名、主机、耗时和成败，方便对照文件编辑慢在哪一次调用。这条日志同样受 `logLevel` 过滤。

日志视图里，工具调用的摘要优先显示路径、命令、搜索词，而不是整段 JSON。失败和警告用颜色标出来。详情会把 JSON 格式化，错误放在出入参前面。向上翻时停止跟随，工具栏显示新增条数，滚回底部后恢复。可用 Ctrl+F 搜索。日志设置保存在 `config/config.json`，改动后需要重启 MCP 服务端才会作用于 `serve` 进程。

## 配置

### 主机 `config/hosts.json`

```json
[
  {
    "name": "web1",
    "host": "192.168.1.10",
    "port": 22,
    "username": "root",
    "auth": "key",
    "privateKeyPath": "~/.ssh/id_rsa",
    "privateKeyPassphrase": "",
    "password": "",
    "passwordEnvironmentVariable": "",
    "strictHostKeyChecking": true,
    "knownHostKeyFingerprint": "SHA256:xxxxxxxxxxxxxxxxxxxx=",
    "allowedRoots": ["/etc/nginx", "/var/www"],
    "commandTimeoutMilliseconds": 60000,
    "agentPort": 43336,
    "agentFirst": false,
    "description": "生产环境 web 服务器"
  }
]
```

| 字段 | 说明 |
| --- | --- |
| `auth` | `key` / `password` / `auto`（先试密钥再试密码） |
| `privateKeyPath` | 私钥路径，支持 `~` |
| `privateKeyPassphrase` | 私钥口令，没有则留空 |
| `password` | 登录密码。会明文进配置文件 |
| `passwordEnvironmentVariable` | 从该环境变量读密码，优先于 `password` |
| `strictHostKeyChecking` | 是否强制校验主机密钥指纹，默认 `true` |
| `knownHostKeyFingerprint` | 期望的 SHA256 指纹，带 `SHA256:` 前缀 |
| `allowedRoots` | 允许访问的路径根。空数组表示不限制，访问时会在日志里告警 |
| `commandTimeoutMilliseconds` | 前台命令的等待时间，默认 60 秒。超时后终止进程组并返回已有输出 |
| `agentPort` | SSH 连不上时改用的备用端口。0 或不写表示不启用 |
| `privateKeyPath` | SSH 私钥。备用通道也用它签名 |
| `agentFirst` | 为 `true` 时文件操作不尝试 SSH，直接走备用端口 |

`strictHostKeyChecking` 为 `true` 且没有 `knownHostKeyFingerprint` 时，连接会被拒绝。先执行 `remote-fs hosts fingerprint <别名>` 写入指纹，或把该主机的强校验关掉。

### 全局 `config/config.json`

```json
{
  "defaultHost": "web1",
  "requireAllowedRoots": true,
  "maxReadBytes": 1048576,
  "maxOutputCharacters": 200000,
  "maxListEntries": 2000,
  "maxTreeDepth": 4,
  "maxSearchResults": 200,
  "auditLogEnabled": true,
  "logLevel": "Info",
  "logRecordPayloads": true,
  "logTraceToolTiming": false,
  "logMaxFileSizeBytes": 10485760,
  "logRetentionDays": 7,
  "logMaxFiles": 20,
  "connectionTimeoutMilliseconds": 30000,
  "sftpOperationTimeoutMilliseconds": 60000,
  "connectionIdleSeconds": 300,
  "remoteExecPolicy": "Guard",
  "followSymbolicLinks": false,
  "localRoots": [],
  "cacheEnabled": true,
  "cacheTtlSeconds": 30,
  "backgroundJobTtlMinutes": 30,
  "jobRetentionDays": 7,
  "jobMaxCount": 50,
  "killJobsOnExit": true,
  "stayInBackgroundOnExit": false,
  "agentListenPort": 43336,
  "agentAllowedRoots": ["/var/www"]
}
```

| 字段 | 说明 |
| --- | --- |
| `requireAllowedRoots` | 为 `true` 时，没配 `allowedRoots` 的主机无法访问任何路径 |
| `maxReadBytes` | 单文件读取上限，默认 1MB。超过后 `remote_edit_file` 拒绝编辑 |
| `maxOutputCharacters` | 单次工具输出的字符上限，超出截断 |
| `remoteExecPolicy` | `Guard`（默认，拦截命令中写明的越界路径）/ `Allow` / `Deny`（禁用 `remote_exec`） |
| `followSymbolicLinks` | 默认 `false`。白名单生效时，路径本身或其任一上级目录是软链接都会被拒绝 |
| `localRoots` | 上传下载能触碰的本机目录。为空表示不限制，此时可以上传任意本地文件、覆盖任意本地路径。建议至少配置成工作目录 |
| `connectionIdleSeconds` | SFTP 连接空闲多久后回收，期间复用同一条连接 |
| `cacheEnabled` / `cacheTtlSeconds` | 目录列举缓存。默认开启，30 秒过期 |
| `backgroundJobTtlMinutes` | 后台任务默认时限，默认 30 分钟。单次调用可用 `ttlMinutes` 覆盖 |
| `jobRetentionDays` / `jobMaxCount` | 远端已结束任务的保留天数，以及每台主机最多保留多少条 |
| `killJobsOnExit` | 默认 `true`，MCP 退出时终止它启动的远程任务。设为 `false` 后任务在服务器上继续跑，下次启动可用 `remote_job_list` 接管 |
| `stayInBackgroundOnExit` | 仅 Windows 图形界面：关闭主窗口时收进托盘 |
| `agentListenPort` | 服务器上 `remote-fs listen` 未指定 `--port` 时使用的端口。0 表示用默认 43336 |
| `agentAuthorizedKeysPath` | 验签用的公钥文件。空则使用运行用户的 `~/.ssh/authorized_keys` |
| `agentAllowedRoots` | 备用通道允许修改的本机目录。为空时拒绝监听 |
| `logLevel` | `Debug` / `Info` / `Warn` / `Error` |
| `logRecordPayloads` | 是否记录工具调用的完整出入参 |
| `logTraceToolTiming` | 为 `true` 时，每次工具调用额外写一条 `tool_timing`，只含工具名、主机、耗时和成败，不含文件内容。默认关闭 |

## MCP 工具

所有工具都接受 `host`。省略时使用 `defaultHost`。

### 文件读取

| 工具 | 用途 |
| --- | --- |
| `remote_ls` | 列出目录，含类型、大小、修改时间、权限。条目超过上限会截断 |
| `remote_read_file` | 带行号读取文本，支持 `offset` / `length` / `maxLines` |
| `remote_stat` | 类型、大小、权限、属主、修改时间、链接目标 |
| `remote_search` | 在目录中搜索文本，可按文件名过滤 |
| `remote_tree` | 树形展示目录，深度受 `maxTreeDepth` 限制 |

### 文件写入

| 工具 | 用途 |
| --- | --- |
| `remote_write_file` | 覆盖写入；`append=true` 追加。可带权限位，先校验再写 |
| `remote_edit_file` | 精确替换。多处匹配会报错，除非 `replaceAll=true` |
| `remote_mkdir` | 创建目录，可递归。已存在不报错 |
| `remote_remove` | 删除文件或目录。目录必须 `recursive=true` |
| `remote_move` | 移动或重命名。目标已存在时默认失败 |
| `remote_copy` | 在远程主机内部复制，不经过本地磁盘 |
| `remote_chmod` | 八进制权限，如 `644` |
| `remote_symlink` | 创建软链接。目标与链接路径都要落在白名单内 |

### 命令、任务与传输

| 工具 | 用途 |
| --- | --- |
| `remote_exec` | 执行 shell 命令。`background=true` 时立即返回任务号 |
| `remote_job_status` | 查看任务状态，按上次的偏移只返回新增输出 |
| `remote_job_list` | 列出该主机上的任务，含服务重启后仍留在服务器上的 |
| `remote_job_kill` | 终止任务。默认先 TERM 再 KILL，`force=true` 直接 KILL |
| `remote_test_connection` | 测试连接，返回延迟、指纹和系统信息 |
| `remote_list_hosts` | 列出已配置主机 |
| `remote_download` | 下载远程文件到本地 |
| `remote_upload` | 上传本地文件到远程 |
| `remote_download_dir` | 递归下载远程目录 |

`remotePath` 以 `/` 结尾时按目录处理，保留本地文件名。下载和上传默认不覆盖，要覆盖必须传 `overwrite=true`。

后台任务把上次返回的 `nextStdoutOffset` / `nextStderrOffset` 传回去，否则会重复读到已经看过的内容。构建时间有波动时，把 `ttlMinutes` 按预估时长往上留一倍。

## 从源码构建

```bash
git clone https://github.com/gggtttfff/remote-fs.git
cd remote-fs
dotnet publish -c Release -r win-x64 --self-contained true -o ./out
```

## License

MIT
