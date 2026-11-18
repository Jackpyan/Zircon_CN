#!/bin/sh
# Zircon 服务端 systemd 安装脚本
# 用法： sudo sh install.sh /opt/zircon            （安装 Server.Web 单元）
#        sudo sh install.sh /opt/zircon --start    （安装并立即启动）
set -eu

DIR="${1:-}"
START="${2:-}"

if [ -z "$DIR" ]; then
    echo "用法: sudo sh install.sh <程序目录> [--start]"
    echo "例:   sudo sh install.sh /opt/zircon --start"
    exit 1
fi

DIR=$(cd "$DIR" && pwd)

# ---------- 1. 确定启动命令 ----------
if [ -x "$DIR/Server.Web" ]; then
    EXEC="$DIR/Server.Web"
elif [ -f "$DIR/Server.Web.dll" ]; then
    EXEC="dotnet $DIR/Server.Web.dll"
else
    echo "错误：$DIR 下找不到 Server.Web 或 Server.Web.dll"
    exit 1
fi

# ---------- 2. 定位 dotnet 运行时根目录 ----------
# systemd 不继承 ~/.bashrc 里的 DOTNET_ROOT，找不到运行时会以 131 退出
DOTNET_ROOT_DIR=""

if [ -n "${DOTNET_ROOT:-}" ] && [ -d "$DOTNET_ROOT/shared" ]; then
    DOTNET_ROOT_DIR="$DOTNET_ROOT"
fi

if [ -z "$DOTNET_ROOT_DIR" ] && command -v dotnet >/dev/null 2>&1; then
    CANDIDATE=$(dirname "$(readlink -f "$(command -v dotnet)")")
    [ -d "$CANDIDATE/shared" ] && DOTNET_ROOT_DIR="$CANDIDATE"
fi

if [ -z "$DOTNET_ROOT_DIR" ]; then
    for CANDIDATE in /usr/share/dotnet /usr/lib/dotnet /root/.dotnet /opt/dotnet; do
        [ -d "$CANDIDATE/shared" ] && DOTNET_ROOT_DIR="$CANDIDATE" && break
    done
fi

if [ -z "$DOTNET_ROOT_DIR" ] && [ -f /etc/dotnet/install_location ]; then
    CANDIDATE=$(head -n1 /etc/dotnet/install_location)
    [ -d "$CANDIDATE/shared" ] && DOTNET_ROOT_DIR="$CANDIDATE"
fi

if [ -z "$DOTNET_ROOT_DIR" ]; then
    echo "警告：没找到 dotnet 运行时目录（DOTNET_ROOT）。"
    echo "      如果程序启动后立即退出（status=131），请手动执行："
    echo "        readlink -f \"\$(command -v dotnet)\""
    echo "      把其所在目录写进 unit 的 Environment=DOTNET_ROOT="
else
    echo "dotnet 运行时：$DOTNET_ROOT_DIR"
fi

# ---------- 3. 生成 unit ----------
UNIT=/etc/systemd/system/zircon-web.service
SRC="$(dirname "$0")/zircon-web.service"

sed -e "s|^WorkingDirectory=.*|WorkingDirectory=$DIR|" \
    -e "s|^ExecStart=.*|ExecStart=$EXEC|" \
    "$SRC" > "$UNIT"

if [ -n "$DOTNET_ROOT_DIR" ]; then
    sed -i "s|^Environment=DOTNET_ROOT=.*|Environment=DOTNET_ROOT=$DOTNET_ROOT_DIR|" "$UNIT"
else
    sed -i "s|^Environment=DOTNET_ROOT=.*|#Environment=DOTNET_ROOT=|" "$UNIT"
fi

# 去掉 Windows 换行（若通过 U 盘/共享目录拷贝过来的话）
sed -i 's/\r$//' "$UNIT" 2>/dev/null || true

systemctl daemon-reload
systemctl enable zircon-web.service

echo "已安装：$UNIT"
echo "  工作目录：$DIR"
echo "  启动命令：$EXEC"
echo
echo "常用命令："
echo "  systemctl start zircon-web      # 启动"
echo "  systemctl stop zircon-web       # 优雅停止（会先保存数据库）"
echo "  systemctl restart zircon-web    # 重启"
echo "  systemctl status zircon-web     # 状态"
echo "  journalctl -u zircon-web -f     # 实时日志"

if [ "$START" = "--start" ]; then
    systemctl restart zircon-web
    sleep 3
    systemctl status zircon-web --no-pager || true
    echo
    echo "---- 最近日志 ----"
    journalctl -u zircon-web -n 30 --no-pager -l || true
fi
