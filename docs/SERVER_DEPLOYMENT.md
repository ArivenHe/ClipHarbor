# GitHub Actions 部署同步服务

部署采用 **SSH 用户名 + 密码**。Actions 测试 macOS、Windows 和服务端后，在 runner 构建生产镜像及 PostgreSQL / Caddy 镜像，生成 SHA256 校验包，通过 `sshpass` 上传到服务器。服务器只执行镜像加载与 `docker compose up --no-build --pull never`，不依赖现场下载 Docker Hub 镜像。

## 服务器准备

需要 Linux、Docker Engine、Compose v2、Python 3 和 curl。SSH 账号必须可以操作 Docker，并能写入部署目录，默认 `/opt/clipharbor`。域名 A / AAAA 记录指向服务器。独立入口模式使用 Caddy 的 80 / 443；已有宝塔 / Nginx 入口时，使用 `external` 模式，只在本机 `127.0.0.1:5890` 启动后台，再配置原有入口反向代理。

第一次部署前创建目录并授予 SSH 账号权限。不要在正在使用的其他应用目录执行部署。

## 仓库配置

在仓库 Settings → Secrets and variables → Actions 添加下面的 Secrets。密码不要提交到 Git，也无需发到聊天里。

| Secret | 内容 |
| --- | --- |
| `SERVER_SSH_HOST` | SSH IP 或主机名 |
| `SERVER_SSH_USER` | SSH 用户名 |
| `SERVER_SSH_PASSWORD` | SSH 登录密码 |
| `SERVER_SSH_KNOWN_HOSTS` | 已核实的 SSH 主机公钥，使用 OpenSSH known_hosts 格式 |
| `SYNC_ADMIN_PASSWORD` | 初始同步账号密码，独立于 SSH 密码 |
| `SYNC_DATABASE_PASSWORD` | PostgreSQL 初始密码，独立于上面两个密码 |

| Variable | 默认值 / 内容 |
| --- | --- |
| `DEPLOY_ENABLED` | 设置为 `true` 后启用生产部署 |
| `SYNC_DOMAIN` | 必填，例如 `sync.example.com`，不带协议、端口和路径 |
| `SYNC_ADMIN_USERNAME` | 默认 `ariven`，客户端登录使用这个账号 |
| `SERVER_SSH_PORT` | 默认 `22` |
| `SERVER_DEPLOY_DIR` | 默认 `/opt/clipharbor` |
| `SERVER_PLATFORM` | 默认 `linux/amd64`；ARM 服务器填 `linux/arm64` |
| `SYNC_PORT` | 默认 `5890`；后台 HTTP 仅绑定 `127.0.0.1` |
| `SYNC_PROXY_MODE` | 默认 `standalone`；宝塔 / 已有 Nginx 使用 `external`，关闭独立 Caddy 入口 |

`SERVER_SSH_KNOWN_HOSTS` 必须来自可信渠道：可以复用已经验证的本机 `~/.ssh/known_hosts` 条目；或从服务器控制台读取 `/etc/ssh/ssh_host_ed25519_key.pub`，核实指纹后构造条目。默认端口格式为 `host ssh-ed25519 公钥`，非默认端口为 `[host]:port ssh-ed25519 公钥`。流程强制校验主机身份，不会关闭 `StrictHostKeyChecking`。

生产任务使用 `production` environment。可以在该 environment 保存 Secrets；已有保护规则会由 GitHub 执行。默认代码未增加人工审批规则。

## 触发及验证

推送 `main` 或手动运行 **Verify and Deploy Sync**：

1. macOS 测试、通用应用打包；Windows 核心测试和 x64 / ARM64 原生打包。
2. PostgreSQL 上的真实 API、WebSocket、附件续传和两客户端同步集成测试。
3. 所有测试和客户端构建通过后生成 `ClipHarbor-server-bundle`。
4. 启用部署时，密码登录 SSH，检查提交号及包校验，加载镜像并启动。
5. 独立入口验证 HTTPS `/healthz`；`external` 模式验证本机后台 `/healthz`。必须返回镜像构建提交号才更新 `current` 指针。失败会启动上一套镜像和配置，保留数据卷。

只部署服务端或重试部署时，运行 **Deploy Sync Server**，可填写已经验证的构建 run ID，留空则选择最近保留的合格镜像包。它检查 macOS、Windows 两个架构、服务端和镜像打包均已通过，复用镜像，应用当前部署配置，不重建客户端。部署目录包含配置摘要，同一份镜像改变端口时也保留上一套配置以便回滚。

### 宝塔反向代理

仓库 Variables 设置 `SYNC_PROXY_MODE=external`、`SYNC_PORT=5890`。在宝塔为同步域名添加反向代理，目标填 **`http://127.0.0.1:5890`**，开启 WebSocket 支持，并为该域名配置有效的 HTTPS 证书。允许至少 16 MiB 请求体，以便上传分块及带格式文本；反向代理超时建议 300 秒。

客户端仍填写 `https://同步域名`。5890 是本机 HTTP 后台端口，无需开放给公网。`external` 部署完成只证明后台可用，域名、证书和 WebSocket 转发应在配置宝塔后单独验收。

尚未填写服务器信息时，构建和服务端包仍会生成，生产部署跳过。客户端安装包包含所需运行时；Mac 的同步组件位于应用包 `Contents/Helpers/Sync`，不要单独移动或删除。

Mac 的同步组件将托管程序集打包进单个可执行文件，只在旁边保留原生库，避免把普通 DLL / JSON 放进系统要求存放签名代码的 `Helpers` 目录。打包会分别签名两种架构的组件及原生库，为组件保留 JIT 权限，再签名外层应用；流程参考 [.NET macOS 发布说明](https://learn.microsoft.com/en-us/dotnet/core/deploying/macos) 和 [Apple 签名目录说明](https://developer.apple.com/library/archive/technotes/tn2206/)。

成功后，两端打开设置 → 同步，服务器填 `https://你的同步域名`，使用初始同步账号登录，保留“跨设备直接粘贴”开启。Mac 复制 → Windows `Ctrl+V`；Windows 复制 → Mac `⌘V`。文件要在完整下载后粘贴，正常 Finder / 资源管理器接收文件副本。

## 目录及运维

```text
/opt/clipharbor/
  current -> releases/<提交号>-<配置摘要>
  releases/<提交号>-<配置摘要>/
    compose.yml, Caddyfile, runtime.env, COMMIT, SHA256SUMS.txt
    images.tar.gz, deploy-server.sh
  secrets/
    database_password
    admin_password
```

`secrets` 目录为 0700，文件供容器只读挂载；目录外无法直接访问。正式数据在 `clipharbor_database`、`clipharbor_server`、`clipharbor_caddy` 等 Docker 卷中。升级保留卷，部署脚本不执行 `down -v`。密码文件仅在首次部署创建；修改 Actions 的密码 Secret 不会自动重置已有数据库或账号密码。

查看日志和状态：

```bash
cd /opt/clipharbor/current
export SECRETS_DIR=/opt/clipharbor/secrets
docker compose --env-file runtime.env -f compose.yml ps
docker compose --env-file runtime.env -f compose.yml logs --tail 100 server
```

创建或重置同步账号时，将新密码写入部署目录外的 0600 临时文件，复制进服务器容器，以文件传参，操作后移除。命令模板：

```bash
# 把已准备好的 0600 密码文件复制进去。
docker compose --env-file runtime.env -f compose.yml cp /安全位置/password.txt server:/tmp/account-password
docker compose --env-file runtime.env -f compose.yml exec server \
  dotnet ClipHarbor.Server.dll --create-account 用户名 --password-file /tmp/account-password
# 重置已有账号时将 --create-account 换成 --reset-password。
docker compose --env-file runtime.env -f compose.yml exec server rm /tmp/account-password
```

重置密码会撤销该账号已有会话。`--disable-account 用户名` 禁用账号，`--reset-epoch 用户名` 更新恢复标记。服务器默认普通记录保留 30 天、收藏免于时间清理，每账号最多 10,000 条活动记录和 2 GiB 正文 / 附件额度；附件失去活动引用后禁止下载，24 小时后可回收。历史日志保留 90 天，过期客户端改用快照。

## 备份与恢复

备份应同时覆盖 PostgreSQL、附件和 Data Protection keys。维护窗口先停止 `server`，保留 `database` 运行，使用 `pg_dump` 备份数据库，导出 `clipharbor_server` 卷，再启动 `server`。附件和数据库必须来自同一次维护窗口；HTTPS 证书卷可另外备份。

恢复后，在应用服务停止时为每个账号执行 `--reset-epoch 用户名`，再启动服务。客户端会要求重新登录并重新建立快照，旧队列仍保留在本机旧 epoch 目录，不会自动回放到恢复后的服务器。不要复制不匹配的数据库与附件备份。

## 开发验证

安装 .NET 10 SDK，连接独立测试 PostgreSQL，设置 `CLIPHARBOR_DATABASE` 和 `CLIPHARBOR_DATA_DIR`，运行 `bash scripts/test-sync.sh`。脚本创建 `alice` / `bob` 测试账号，仅用于独立测试库，不能在生产数据库运行。

自动化验证不能替代实际 Windows / Mac 的 Finder、资源管理器、富文本和目标应用粘贴验收。上线后需要在两台真实设备进行双向验收。
