# remote-fs

远程服务器文件管理 MCP 服务。让 Claude Code 等 AI 客户端直接对远程服务器做文件增删改查、命令执行和文件传输，不必每次手动 SSH。

原生单文件可执行程序，基于 .NET 9 + SSH.NET，**不依赖系统 OpenSSH 客户端，也不需要预装 .NET 运行时**。

## 特性

- **单文件部署**：解压即用，无运行时依赖
- **增删改查齐全**：目录浏览、文件读写、精确编辑、移动、复制、删除、改权限、软链接
- **双向传输**：本地 ↔ 远程上传下载，支持整目录递归
- **远程命令**：执行 shell 命令并返回标准输出/错误/退出码
- **路径白名单**：按主机限制可访问的目录范围，防止误操作与提示注入
- **主机密钥校验**：SHA256 指纹比对，防止连到伪造的中间人主机
- **审计日志**：所有远程操作留痕到 `~/.remote-fs/remote-fs.log`

## 安装

从 [Releases](https://github.com/gggtttfff/remote-fs/releases) 下载对应平台的压缩包，解压后把 `remote-fs`（Windows 为 `remote-fs.exe`）放进 `PATH`。

## 快速开始

### 1. 添加主机

```bash
remote-fs hosts add web1
```

交互式填写地址、用户名、端口、认证方式和允许访问的路径范围。

**已有 `~/.ssh/config` 的话更省事**，一条命令导入：

```bash
remote-fs hosts import          # 导入全部
remote-fs hosts import web1     # 只导入指定别名
```

自动带入 `HostName`、`Port`、`User`、`IdentityFile`，别名沿用 `Host` 名。
GUI 里点「从 ssh_config 导入」可以勾选后再导。

导入的主机指纹校验默认关闭（还没采集指纹），用「探测指纹」拿到指纹填进去后，
把 `strictHostKeyChecking` 改成 `true` 即可开启校验。

### 2. 记录主机密钥指纹

```bash
remote-fs hosts fingerprint web1
```

把输出的指纹填入 `~/.remote-fs/hosts.json` 对应主机的 `knownHostKeyFingerprint` 字段，后续连接会强制校验。

### 3. 接入 Claude Code

编辑 `~/.claude/config.json`：

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

重启 Claude Code 即可使用。

## 数据位置

程序目录下三个子目录，全部自包含，拷贝整个目录即可带走：

```
<程序目录>/
  config/    配置：hosts.json（主机列表）、config.json（全局配置）
  cache/     缓存：tmp（下载临时文件）、listings（远程目录列举缓存）
  log/       日志：remote-fs.log 与轮转归档
```

配置与日志目录写不进去时功能会禁用并在界面提示，不静默降级。
首次运行若发现 `config/` 为空而 `~/.remote-fs/` 里有旧配置，会自动复制过来（旧文件保留不删）。
`REMOTE_FS_HOME` 环境变量可整体重定向配置目录。

## 图形化配置界面（仅 Windows）

```bash
remote-fs gui
```

Windows 版**直接双击 `RemoteFs.exe` 也会打开这个界面**（不带参数即进入图形模式）。

打开主机编辑器，可视化增删改主机配置、探测主机密钥指纹、测试连接。
改动只有点击保存后才写入 `~/.remote-fs/hosts.json`；配置文件解析失败时自动进入只读模式，不会覆盖损坏的内容。

「生成 MCP 配置」按钮会按当前程序的真实路径给出 `mcpServers` 片段，复制后自行合并到客户端配置即可——
工具本身不会修改任何外部配置文件。

界面共四个标签页：

| 标签页 | 内容 |
| --- | --- |
| 主机配置 | 主机增删改、从 ssh_config 导入、指纹探测、连接测试、生成 MCP 配置片段 |
| 日志 | 实时日志视图，等级过滤、底部跟随、详情查看 |
| 日志设置 | 日志等级、出入参记录、轮转与保留参数、清空日志 |
| 配置 | 其余全部配置项：默认主机、强制白名单、读取与输出上限、连接超时、命令策略、软链接、缓存 |

Linux / macOS 版本为纯命令行，请使用 `remote-fs hosts` 子命令。

## 日志

日志写在**程序所在目录下的 `log/`**，JSONL 格式（一行一条），记录每次工具调用的完整出入参、耗时、成功与否和异常堆栈。

- 目录不可写时日志功能直接禁用并在界面红条提示，不静默降级
- 超过 `logMaxFileSizeBytes` 自动分割为 `remote-fs-<时间戳>.log`
- 按 `logRetentionDays` 与 `logMaxFiles` 双重上限自动清理
- 日志目录权限限制为仅当前用户可访问

> ⚠️ `logRecordPayloads` 开启时（默认开启），AI 读写的服务器文件内容会以**明文**落盘，可能包含密码与密钥。
> 不需要时在 GUI 的「日志设置」页关掉，或把 `logLevel` 调到 `Warn`。

GUI 的「日志」标签页实时跟踪日志文件，支持等级过滤与详情查看。向上翻看历史时视图冻结，
工具栏提示有多少条新日志，滚回底部自动恢复跟随。

日志设置保存在 `~/.remote-fs/config.json`，改动**需重启 Claude Code** 才会对 MCP 服务端生效。

## 配置

### 主机配置 `~/.remote-fs/hosts.json`

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
    "passwordEnvironmentVariable": "",
    "strictHostKeyChecking": true,
    "knownHostKeyFingerprint": "SHA256:xxxxxxxxxxxxxxxxxxxx=",
    "allowedRoots": ["/etc/nginx", "/var/www"],
    "commandTimeoutMilliseconds": 60000,
    "description": "生产环境 web 服务器"
  }
]
```

| 字段 | 说明 |
| --- | --- |
| `auth` | `key` / `password` / `auto`（先试密钥再试密码） |
| `privateKeyPath` | 私钥路径，支持 `~` 展开 |
| `privateKeyPassphrase` | 私钥 passphrase，没有则留空 |
| `passwordEnvironmentVariable` | 从该环境变量读密码，避免明文落盘 |
| `strictHostKeyChecking` | 是否强制校验主机密钥指纹 |
| `knownHostKeyFingerprint` | 期望的 SHA256 指纹，`SHA256:` 前缀 |
| `allowedRoots` | 允许访问的路径根目录，空数组表示不限制 |

### 全局配置 `~/.remote-fs/config.json`

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
  "logMaxFileSizeBytes": 10485760,
  "logRetentionDays": 7,
  "logMaxFiles": 20,
  "connectionTimeoutMilliseconds": 30000,
  "sftpOperationTimeoutMilliseconds": 60000,
  "connectionIdleSeconds": 300,
  "remoteExecPolicy": "Guard",
  "followSymbolicLinks": false
}
```

| 字段 | 说明 |
| --- | --- |
| `requireAllowedRoots` | 设为 `true` 后，未配置 `allowedRoots` 的主机将无法访问任何路径 |
| `remoteExecPolicy` | `Guard`（默认，尽力拦截命令中的越界绝对路径）/ `Allow`（不检查）/ `Deny`（禁用 remote_exec） |
| `followSymbolicLinks` | 默认 `false`。白名单生效时拒绝软链接，防止借道链接跳出范围 |
| `connectionIdleSeconds` | SFTP 连接空闲多久后回收，期间的工具调用复用同一条连接 |
| `logLevel` | `Debug` / `Info`（默认）/ `Warn` / `Error`，低于该等级的不写入 |
| `logRecordPayloads` | 是否记录工具调用的完整出入参，默认 `true` |
| `logMaxFileSizeBytes` | 单个日志文件上限，超过后自动分割 |
| `logRetentionDays` / `logMaxFiles` | 日志保留天数与最多文件数，双重上限 |

> `strictHostKeyChecking` 为 `true` 且未填 `knownHostKeyFingerprint` 时，连接会被直接拒绝。
> 这是刻意设计——避免出现「以为校验了其实没校验」。先执行 `remote-fs hosts fingerprint <别名>` 固化指纹，
> 或把该主机的 `strictHostKeyChecking` 设为 `false`。

## MCP 工具

### 文件读取

| 工具 | 用途 |
| --- | --- |
| `remote_ls` | 列出目录内容，含类型、大小、修改时间 |
| `remote_read_file` | 读取文件内容，带行号，支持 offset 分页 |
| `remote_stat` | 查看文件属性：权限、属主、修改时间、链接目标 |
| `remote_search` | 在目录中搜索文本，返回匹配文件与行号 |
| `remote_tree` | 树形展示目录结构 |

### 文件写入

| 工具 | 用途 |
| --- | --- |
| `remote_write_file` | 写入或追加文件内容 |
| `remote_edit_file` | 精确替换文件中指定文本，原子写入 |
| `remote_mkdir` | 创建目录，支持递归 |
| `remote_remove` | 删除文件或目录 |
| `remote_move` | 移动或重命名 |
| `remote_copy` | 复制文件或目录 |
| `remote_chmod` | 修改权限位 |
| `remote_symlink` | 创建符号链接 |

### 命令与传输

| 工具 | 用途 |
| --- | --- |
| `remote_exec` | 执行远程 shell 命令，返回输出与退出码 |
| `remote_test_connection` | 测试连接，返回延迟与主机指纹 |
| `remote_list_hosts` | 列出已配置主机 |
| `remote_download` | 下载远程文件到本地 |
| `remote_upload` | 上传本地文件到远程 |
| `remote_download_dir` | 递归下载远程目录 |

所有工具都接受 `host` 参数指定主机别名，省略时使用 `defaultHost`。

## 安全设计

- **路径白名单**：每个主机可限定允许访问的根目录，越界访问在建立连接之前就被拒绝
- **软链接拦截**：白名单生效时拒绝符号链接，防止「路径合规但指向范围外」
- **主机密钥校验**：指纹不匹配或「开了强校验却没固化指纹」时拒绝连接，防止中间人攻击
- **原子写入**：`remote_edit_file` 先写临时文件再重命名，写入中断不会损坏原文件
- **写前校验**：`remote_edit_file` 发现文件超过单次读取上限时直接拒绝，不会只写回读到的那一段
- **审计留痕**：所有操作写入日志，包含主机、路径与关键参数
- **输出限流**：单次输出超过阈值时截断并提示，避免撑爆模型上下文

`remote_exec` 的路径检查是**尽力而为**：能拦住命令里显式写出的越界绝对路径和相对 `..` 跳转，
但变量拼接、命令替换、编码解码等写法绕得过去。需要严格隔离时请把 `remoteExecPolicy` 设为 `Deny`。

## 从源码构建

```bash
git clone https://github.com/gggtttfff/remote-fs.git
cd remote-fs
dotnet publish -c Release -r win-x64 --self-contained true -o ./out
```

## License

MIT
