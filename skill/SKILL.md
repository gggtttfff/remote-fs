---
name: remote-fs
description: 通过 remote-fs MCP 工具直接操作远程服务器文件与命令，无需手工 SSH。当需要查看远程服务器目录、读取/修改远程配置文件、上传下载文件、查看远程日志或执行远程命令时使用。触发词：远程服务器、远程文件、remote、SSH 上传、远程配置、服务器日志、部署配置。
---

# 远程服务器文件操作

通过 `remote-fs` MCP 服务直接操作远程服务器。所有工具的 `host` 参数是主机别名，省略时使用配置中的默认主机。

## 工具清单

### 读取类

| 工具 | 关键参数 | 用途 |
| --- | --- | --- |
| `remote_ls` | `path`, `showHidden` | 列出目录内容 |
| `remote_read_file` | `path`, `offset`, `length`, `maxLines` | 读取文件，带行号 |
| `remote_stat` | `path` | 查看权限、属主、修改时间 |
| `remote_search` | `path`, `pattern`, `filePattern`, `recursive` | 搜索文件内容 |
| `remote_tree` | `path`, `depth` | 目录树结构 |

### 写入类

| 工具 | 关键参数 | 用途 |
| --- | --- | --- |
| `remote_write_file` | `path`, `content`, `append` | 写入/追加文件 |
| `remote_edit_file` | `path`, `oldText`, `newText`, `replaceAll` | 精确替换文本 |
| `remote_mkdir` | `path` | 创建目录 |
| `remote_remove` | `path`, `recursive` | 删除文件/目录 |
| `remote_move` | `source`, `destination` | 移动/重命名 |
| `remote_copy` | `source`, `destination` | 复制 |
| `remote_chmod` | `path`, `permissions` | 改权限 |
| `remote_symlink` | `target`, `linkPath` | 软链接 |

### 命令与传输

| 工具 | 关键参数 | 用途 |
| --- | --- | --- |
| `remote_exec` | `command`, `timeout` | 执行远程命令 |
| `remote_test_connection` | — | 测试连接 |
| `remote_list_hosts` | `includeOffline` | 列出主机 |
| `remote_download` | `remotePath`, `localPath`, `overwrite` | 下载文件 |
| `remote_upload` | `localPath`, `remotePath`, `overwrite` | 上传文件 |
| `remote_download_dir` | `remotePath`, `localPath` | 递归下载目录 |

## 决策流程

### 1. 不知道主机别名

先调 `remote_list_hosts` 查看可用主机。若返回空，说明未配置，需要提示用户执行 `remote-fs hosts add <别名>`。

### 2. 不知道远端有什么文件

用 `remote_ls` 或 `remote_tree` 浏览，**不要凭猜测拼路径**。目录结构未知时先 `remote_ls` 根目录或已知上级目录。

### 3. 定位配置项在哪

优先用 `remote_search` 搜内容，比逐个读文件快：

```
remote_search(path="/etc/nginx", pattern="proxy_pass", filePattern="*.conf")
```

### 4. 修改文件

**优先用 `remote_edit_file` 而非 `remote_write_file`**：
- `remote_edit_file` 只替换指定文本，不会误删没读到的内容，且是原子写入
- `remote_write_file` 覆盖整个文件，只有确认要整体重写时用

`oldText` 必须精确匹配（含缩进和换行）。若报错返回文件片段，读一下再调整 `oldText`。

多个匹配点时，工具会报错并提示：要么补充上下文让 `oldText` 更精确，要么显式传 `replaceAll=true`。

### 5. 大文件

`remote_read_file` 超过 `maxReadBytes` 会截断并提示继续的 `offset`。按提示带 `offset` 续读，或用 `remote_search` 直接定位需要的行。

二进制文件无法用 `remote_read_file` 读，会报错提示改用 `remote_download`。

### 6. 传输文件

- 单个文件：`remote_download` / `remote_upload`
- 整个目录：`remote_download_dir`
- 默认不覆盖已存在文件，需要覆盖传 `overwrite=true`

### 7. 执行命令

`remote_exec` 用于非纯文件操作：重启服务、看日志、查进程状态。

注意退出码非 0 时工具返回的是错误结果，此时 stderr 里是真正的失败原因，要读。

## 注意事项

- **路径白名单**：主机配置了 `allowedRoots` 时，文件类工具访问越界路径会被拒绝。这是用户主动设置的安全边界，被拒绝时**不要用 `remote_exec` 变相绕过**，应提示用户调整 `allowedRoots` 配置
- **审计日志**：所有操作已记入 `~/.remote-fs/remote-fs.log`，排查问题时先看这里
- **删除操作**：`remote_remove` 删非空目录必须显式传 `recursive=true`，这是防止误删的保险
- **主机密钥**：指纹不匹配会拒绝连接，通常是服务器重装导致，需要重新执行 `remote-fs hosts fingerprint <别名>` 更新

## 排查连接问题

| 现象 | 处理 |
| --- | --- |
| 提示未找到主机 | `remote_list_hosts` 确认别名 |
| 认证失败 | 检查私钥路径、passphrase、密码环境变量 |
| 指纹不匹配 | 服务器重装，执行 `remote-fs hosts fingerprint <别名>` 更新配置 |
| 权限不足 | 用 `remote_exec` 确认当前用户，或提示用户改用高权限账号 |
| 路径被拒绝 | 超出 `allowedRoots`，需用户调整白名单配置后重试 |
