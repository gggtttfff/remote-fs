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

从 [Releases](https://github.com/你的用户名/remote-fs/releases) 下载对应平台的压缩包，解压后把 `remote-fs`（Windows 为 `remote-fs.exe`）放进 `PATH`。

## 快速开始

### 1. 添加主机

```bash
remote-fs hosts add web1
```

交互式填写地址、用户名、端口、认证方式和允许访问的路径范围。

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
  "auditLogEnabled": true
}
```

`requireAllowedRoots` 设为 `true` 后，未配置 `allowedRoots` 的主机将无法访问任何路径。

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

- **路径白名单**：每个主机可限定允许访问的根目录，越界访问直接拒绝
- **主机密钥校验**：指纹不匹配时拒绝连接，防止中间人攻击
- **原子写入**：`remote_edit_file` 先写临时文件再重命名，写入中断不会损坏原文件
- **审计留痕**：所有操作写入日志，包含主机、路径与关键参数
- **输出限流**：单次输出超过阈值时截断并提示，避免撑爆模型上下文

## 从源码构建

```bash
git clone https://github.com/你的用户名/remote-fs.git
cd remote-fs
dotnet publish -c Release -r win-x64 --self-contained true -o ./out
```

## License

MIT
