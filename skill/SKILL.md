---
name: remote-fs
description: 通过 remote-fs MCP 直接操作远程服务器文件与命令，替代手工 SSH。当需要浏览远程目录、读写远程配置文件、上传下载文件、查看服务器日志、执行远程命令，或在远程服务器上编译构建、跑测试这类长任务时使用。触发词：远程服务器、远程文件、SSH、远程配置、服务器日志、部署配置、上传下载、远程编译、远程构建、长任务、remote。
---

# 远程服务器操作

22 个工具。`host` 参数省略时用配置里的默认主机；不确定别名先调 `remote_list_hosts`。

## 工具速查

| 场景 | 工具 | 要点 |
| --- | --- | --- |
| 看目录 | `remote_ls` / `remote_tree` | 条目超上限会提示截断 |
| 读文件 | `remote_read_file` | 带行号，`offset`/`length` 分页，`maxLines` 限行 |
| 查属性 | `remote_stat` | 类型、大小、权限、属主、时间 |
| 搜内容 | `remote_search` | 比逐个读文件快，受 `maxResults` 限制 |
| 改文件 | `remote_edit_file` | **改配置首选**，精确替换 + 原子写入 |
| 重写文件 | `remote_write_file` | 整体覆盖；`append=true` 追加；`permissions` 先校验后写 |
| 增删移 | `remote_mkdir` / `remote_remove` / `remote_move` / `remote_copy` | 见下方限制 |
| 权限链接 | `remote_chmod` / `remote_symlink` | 八进制如 644 |
| 传输 | `remote_download` / `remote_upload` / `remote_download_dir` | 默认不覆盖 |
| 跑命令 | `remote_exec` | 退出码非 0 即失败，原因在 stderr 段 |
| 长任务 | `remote_exec(background)` → `remote_job_status` | 立即拿任务号，增量读输出 |
| 管任务 | `remote_job_list` / `remote_job_kill` | 列任务、按进程组终止 |

## 决策流程

### 先看再改

不要凭猜测拼路径。结构未知时先 `remote_ls` 上级目录，或 `remote_tree` 看两层。

### 改配置用 edit，不用 write

- `remote_edit_file` 只替换目标文本，不会误删没读到的内容，失败时回传文件片段
- `oldText` 必须精确匹配，含缩进与换行
- 多处匹配会报错：补充上下文，或显式 `replaceAll=true`
- **文件超过读取上限（默认 1MB）会直接拒绝**，此时改走 `remote_download` → 本地修改 → `remote_upload`

### 定位配置项直接搜

`remote_search(path="/etc/nginx", pattern="proxy_pass", filePattern="*.conf")`
比逐个读文件快得多。

### 大文件分页

`remote_read_file` 截断时会给出下一次的 `offset`，按提示续读。二进制内容会报错，改用 `remote_download`。

### 传输

- 单文件 `remote_download` / `remote_upload`，整目录 `remote_download_dir`
- `remotePath` 以 `/` 结尾表示按目录处理，保留本地文件名
- 默认不覆盖，要覆盖必须传 `overwrite=true`

### 编码

服务器上的文件不一定是 UTF-8。GBK/GB18030 文件在没指定编码时会报「无法按 utf-8 解码」，**这不是二进制文件**。

- 中文服务器建议**在主机配置里把「默认编码」设成 `gb18030`**，之后读写都不用再传参数
- 单次需要时传 `encoding=`：`remote_read_file`、`remote_write_file`、`remote_edit_file`、`remote_search`、`remote_exec` 都支持
- 带 BOM 的文件以 BOM 为准，与配置无关
- `remote_exec` 的 `encoding` **只影响输出解码**；命令本身永远以 UTF-8 下发到服务器
- **编辑非 UTF-8 文件必须显式传 `encoding`**，否则会解码失败——工具不会猜
- 写文件时目标编码表示不了的字符会直接报错，不会静默变成问号

### 执行命令

`remote_exec` 用于非文件操作：重启服务、查进程、看日志。退出码非 0 时工具返回错误结果，真实原因要读 stderr 段。

**编译、构建、跑测试这类超过几十秒的命令，一律加 `background=true`**：

```
remote_exec(command="npm run build", background=true, ttlMinutes=20)
  → 任务已提交: job-20261111-153012-a3f2
remote_job_status(jobId="job-20261111-153012-a3f2")
  → 已运行 42 秒 + 新增日志 + nextStdoutOffset: 8192
remote_job_status(jobId="...", stdoutOffset=8192)
  → 只返回新增部分，不会重复
```

要点：

- **把上次返回的 `nextStdoutOffset` / `nextStderrOffset` 传回去**，否则会重复读到旧内容
- `ttlMinutes` 不传就用全局配置（默认 30 分钟）。构建时间可能有波动，**按预估时长往上留一倍**
- 长任务不需要了用 `remote_job_kill`，信号下发到整个进程组，不会留下子进程
- `ttlMinutes` 是硬上限，到期由服务器上的看护进程强制终止——**即使 MCP 服务已经挂掉也会生效**
- 前台模式超时不会再丢输出：工具会返回终止前已产生的内容

任务状态只有 `运行中` 和 `已结束（退出码 0）` 算正常，另外三种都是失败：

| 状态 | 含义 | 该做什么 |
| --- | --- | --- |
| 已超时终止 | 超过 `ttlMinutes` 被看护进程杀掉 | 先看已有输出定位卡在哪，再决定加长 `ttlMinutes` 重试 |
| 已手动终止 | 被 `remote_job_kill` 终止 | 无需处理 |
| 异常终止 | 进程消失但没留退出码，通常是 OOM 或 KILL | 让用户查 `dmesg` / 内存 |

服务重启后遗留的任务用 `remote_job_list` 找，能看到所有还挂在服务器上的任务。

## 必须知道的限制

- **路径白名单**：主机配置了 `allowedRoots` 时越界路径直接拒绝，`remote_exec` 同样受限。这是用户主动设定的安全边界，**不要尝试绕过**，被拒就说明情况请用户调整
- **软链接**：白名单生效时软链接会被拒绝，防止借道跳到范围外，改用链接指向的真实路径
- **`remote_move` 默认不覆盖**：目标已存在时会失败，确认要覆盖才传 `overwrite=true`
- **删除目录必须 `recursive=true`**，非空目录否则报错；软链接只删链接本身，不会跟进目标
- **`remote_exec` 的路径防护是尽力而为**：能拦显式绝对路径和相对 `..`，但变量拼接、命令替换拦不住。全局 `remoteExecPolicy` 设为 `Deny` 时该工具直接不可用
- **任务目录不要手动删**：任务文件在 `~/.remote-fs/jobs/<jobId>/`，删掉正在运行任务的目录会让它的状态查询失效
- **审计日志**写在 **exe 同级的 `log/`**（JSONL），含完整出入参，排查问题先看这里

## 报错对照

| 报错 | 处理 |
| --- | --- |
| 未找到主机 | `remote_list_hosts` 确认别名，或提示用户 `remote-fs hosts add` |
| 指纹不匹配 | 服务器重装导致，让用户执行 `remote-fs hosts fingerprint <别名>` |
| 私钥文件不存在 / 加载失败 | 私钥路径或口令配置有误，提醒用户检查 |
| 路径不在允许范围 | 超出 `allowedRoots`，请用户调整白名单，不要绕 |
| 是符号链接 | 改用链接指向的真实路径 |
| 文件超过读取上限 | 走 download → 本地改 → upload |
| 命令执行失败 | 读 stderr 段与退出码，不要只看退出码 |
| 无法按 utf-8 解码 | 文件不是 UTF-8，用 `encoding=gb18030` 重试，或把主机默认编码设为 gb18030 |
| 无法用 x 编码写入 | 目标编码表示不了这些字符，改用 `encoding=gb18030` 或 `utf-8` |
| 找不到任务 jobId | `remote_job_list` 确认编号；可能已被保留策略清理 |
| pid 已被其他进程复用 | 工具拒绝终止以免误杀，把情况说明给用户，由用户人工确认 |
| 任务已提交但没有写出 pid | 远端可能缺 `sh` 或任务目录不可写，让用户看该任务目录下的 `err` |