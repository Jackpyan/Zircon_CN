# Zircon 服务端 systemd 托管

用 `systemctl` 启停，取代 `pkill -f "^\./Server.Web"` 这种强制杀进程的方式。

## 文件

| 文件 | 用途 |
|---|---|
| `zircon-web.service` | **主用**。托管 `Server.Web`（Web 后台 + 游戏服同进程） |
| `zircon-server.service` | 备用。只单独跑游戏服 `Server` 时用；用 Server.Web 时**不要装**，会端口冲突 |
| `install.sh` | 一键安装 `zircon-web.service`（自动填工作目录与启动命令） |

> **本目录随代码版本管理**，路径 `deploy/systemd/`（2026-11-13 从仓库外挪进来）。
> 旁边有 `deploy/.gitattributes` 钉死 `*.sh` / `*.service` 为 LF ——
> 本机 `core.autocrlf=true`，不加这条的话在 Windows 检出会变 CRLF，
> 拷到 Linux 后报 `install.sh: $'\r': not found` 或 `Failed to parse service file`。
>
> Windows 上 Git 记不住执行位（`core.filemode=false`），提交前补一条：
> `git update-index --chmod=+x deploy/systemd/install.sh`

## 安装（推荐用脚本）

把本目录三个文件拷到 Linux 服务器，例如 `/root/systemd/`：

```bash
cd /root/systemd
chmod +x install.sh
sudo sh install.sh /opt/zircon --start
```

`/opt/zircon` 换成你实际的程序目录（里面要有 `Server.Web`、`Server.ini`、`Data/`）。

脚本做的事：
1. 自动判断是 `Server.Web`（apphost）还是 `Server.Web.dll`（dotnet 启动）
2. 生成 `/etc/systemd/system/zircon-web.service`（替换 `WorkingDirectory` / `ExecStart`）
3. `systemctl daemon-reload` + `enable`（开机自启）
4. `--start` 时立即启动并打印状态

## 手工安装（等价做法）

```bash
sudo cp zircon-web.service /etc/systemd/system/
sudo vi /etc/systemd/system/zircon-web.service     # 改 WorkingDirectory / ExecStart
sudo systemctl daemon-reload
sudo systemctl enable --now zircon-web
```

## 日常命令

```bash
systemctl start zircon-web        # 启动
systemctl stop zircon-web         # 优雅停止（先存库再退出）
systemctl restart zircon-web      # 重启
systemctl status zircon-web       # 状态
systemctl is-active zircon-web    # 只输出 active/inactive，适合脚本
journalctl -u zircon-web -f       # 实时日志（Ctrl+C 退出）
journalctl -u zircon-web -n 200   # 最近 200 行
journalctl -u zircon-web --since today
```

> `journalctl -f` 看到的是**控制台输出**。游戏内日志仍在程序目录的 `Logs.txt`、`Chat Logs.txt`、`Errors\`，后台 Web 有「日志」页。

## 为什么这样就「优雅」了

`systemctl stop` 发的是 **SIGTERM**（不是 SIGKILL），程序走完整停机流程：

```
SIGTERM
 → ApplicationStopping（把内存里的配置写回 Server.ini）
 → GameServerController.StopAsync
     → SEnvir.StopServerAsync：通知游戏线程退出
         → 关闭 Web 服务 / 停止监听 / 断开在线连接
         → Session.Save：全量序列化 + 写 Users.db、System.db
         → Commit：生成 gz 备份
 → ApplicationStopped → 进程退出
```

关键参数（已写在 unit 里）：

| 参数 | 值 | 作用 |
|---|---|---|
| `KillSignal` | `SIGTERM` | 触发优雅关闭，而不是直接杀 |
| `KillMode` | `mixed` | SIGTERM 只发主进程；超时后 SIGKILL 打整组 |
| `TimeoutStopSec` | `180` | 等 180 秒；程序内 `ShutdownTimeout=120s`，必须比它大 |
| `Restart` | `on-failure` | 崩溃自动拉起；`systemctl stop` 属于正常停止，不会自动重启 |
| `LimitNOFILE` | `65535` | 玩家多时避免 fd 耗尽 |

## 停机耗时诊断

停服后看两个文件，确认时间花在哪：

```bash
grep "\[停服\]" /opt/zircon/Logs.txt | tail -20
cat /opt/zircon/Audit/shutdown.log
```

| 日志 | 含义 |
|---|---|
| `网络已停止，耗时 X s` | 断连接耗时，正常 < 1s |
| `序列化并写入数据库文件，耗时 X s` | 全量保存耗时，库越大越慢 |
| `Commit（含 gz 备份）耗时 X s` | gz 备份整库耗时 |
| `停服完成，总耗时 X s` | 全程；< 5s 正常，> 30s 需优化 |
| 卡在「等待游戏服线程结束」无后续 | 异常，游戏线程卡住 |

`systemctl stop` 后若 `status` 显示 `Failed with result 'timeout'`，说明 180s 内没停完，日志会指明卡在哪一步。

## 常见问题

**1. 启动报 `Server.Web 已禁用`**
`Server.ini` 的 `[AdminWeb] AdminWebEnabled` 被设为 False，改成 True。

**2. 端口被占用**
先 `systemctl stop zircon-web`，再 `ss -lntp | grep -E ':(8080|7000)'` 确认没有残留进程。
从旧方式（nohup/screen）切过来时，务必先 `pkill -f Server.Web` 清干净再启用 systemd。

**3. 权限问题：数据库写不进去**
unit 里没写 `User=` 时以 root 运行。若改成专用用户，需先把整个程序目录 chown 给该用户：

```bash
sudo useradd -r -s /sbin/nologin zircon
sudo chown -R zircon:zircon /opt/zircon
# 然后取消 unit 里 User=/Group= 的注释
sudo systemctl daemon-reload && sudo systemctl restart zircon-web
```

**4. 从 Windows 拷过来的文件有 CRLF**
```bash
sed -i 's/\r$//' /etc/systemd/system/zircon-web.service
sudo systemctl daemon-reload
```

**5. 想改端口/配置**
改 `Server.ini` 后 `systemctl restart zircon-web`；或直接在后台「服务器配置」页改（多数项即时生效，`AdminWebHost`/`AdminWebPort` 需重启）。

**6. 崩溃自动重启但反复失败**
`journalctl -u zircon-web -n 200` 看异常；`Errors\` 目录下按日期有崩溃堆栈。避免无限重启可临时 `systemctl stop` 后手工前台跑一次：

```bash
cd /opt/zircon && ./Server.Web
```

**7. 启动后立刻退出，`status=131` / `activating (auto-restart)`**
最常见原因：**systemd 环境里找不到 .NET 运行时**。
systemd 不继承 `~/.bashrc`，若 dotnet 是用 `dotnet-install.sh` 装在 `~/.dotnet` 而不是 `/usr/share/dotnet`，
apphost（`Server.Web` 本体）会解析不到框架，进程秒退，退出码 131（= 0x83，FrameworkMissing）。

诊断：

```bash
journalctl -u zircon-web -n 50 --no-pager -l     # 看真实报错
readlink -f "$(command -v dotnet)"               # 例：/root/.dotnet/dotnet
ls /usr/share/dotnet/shared 2>/dev/null          # 不存在说明不在默认位置
```

修复（两种任选）：

```bash
# A. 让 unit 指向真实运行时（重跑脚本会自动探测并写入）
sudo sh install.sh /root/mir3/Build_results --start

# B. 手工改
sudo sed -i 's|^Environment=DOTNET_ROOT=.*|Environment=DOTNET_ROOT=/root/.dotnet|' \
     /etc/systemd/system/zircon-web.service
sudo systemctl daemon-reload && sudo systemctl restart zircon-web
```

若程序只有 `Server.Web.dll`、没有 apphost，`ExecStart` 改成绝对路径的 dotnet：

```
ExecStart=/root/.dotnet/dotnet /root/mir3/Build_results/Server.Web.dll
```

> 注意：`/root/...` 路径下运行要求 `User=root`（unit 默认就是 root）。若改成专用用户，该用户读不到 `/root`，必须把程序移到 `/opt` 并 chown。
