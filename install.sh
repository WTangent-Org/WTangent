#!/usr/bin/env bash
# wtangent 一键安装（Linux/macOS）：curl -fsSL <RAW>/install.sh | bash [channel]
# 参数：channel（stable=正式版[默认] / beta=预发布）
# 环境变量：
#   AGENT_DEST          安装路径（缺省 /usr/local/bin/wtangent）
# 注意：本脚本常驻仓库根（raw 分发），不随 release 发布——引导器版本无关
set -euo pipefail

CHANNEL="${1:-stable}"
DEST="${AGENT_DEST:-/usr/local/bin/wtangent}"

case "$(uname -s)" in
  Linux)
    case "$(uname -m)" in
      x86_64|amd64)  OS="linux"; ARCH="x86_64" ;;
      aarch64|arm64) OS="linux"; ARCH="aarch64" ;;
      armv7l|armv6l|arm) OS="linux"; ARCH="arm" ;;
      *) echo "[install] 不支持的架构: $(uname -m)"; exit 1 ;;
    esac
    ;;
  Darwin)
    case "$(uname -m)" in
      x86_64)  OS="osx"; ARCH="x64" ;;
      arm64)   OS="osx"; ARCH="arm64" ;;
      *) echo "[install] 不支持的架构: $(uname -m)"; exit 1 ;;
    esac
    ;;
  *) echo "[install] 不支持的系统: $(uname -s)"; exit 1 ;;
esac

ASSET="wtangent-$OS-$ARCH"

case "$CHANNEL" in
  stable)
    URL="https://github.com/WTangent-Org/WTangent/releases/latest/download/$ASSET"
    echo "[install] 频道 stable（正式版）"
    ;;
  beta)
    TAG=$(curl -fsSL -H "User-Agent: wtangent-install" "https://api.github.com/repos/WTangent-Org/WTangent/releases?per_page=20" \
      | grep -m1 '"tag_name":' | sed 's/.*"tag_name": *"\([^"]*\)".*/\1/')
    [ -n "$TAG" ] || { echo "[install] 无 beta 版本"; exit 1; }
    URL="https://github.com/WTangent-Org/WTangent/releases/download/$TAG/$ASSET"
    echo "[install] 频道 beta（预发布 $TAG）"
    ;;
  nightly|debug)
    echo "[install] 频道 $CHANNEL 已不再提供（仅 stable / beta）"
    exit 1
    ;;
  *)
    echo "[install] 未知频道: $CHANNEL（支持 stable / beta）"
    exit 1
    ;;
esac

echo "[install] 下载 $URL"
if ! curl -fsSL -o /tmp/wtwtangent-install "$URL"; then
  echo "[install] 下载失败：可用 AGENT_RELEASE_BASE 指定源，或本地拷贝 $DEST"
  exit 1
fi
chmod +x /tmp/wtwtangent-install
if [ -w "$(dirname "$DEST")" ]; then mv /tmp/wtwtangent-install "$DEST"; else sudo mv /tmp/wtwtangent-install "$DEST"; fi
echo "[install] 已安装：$DEST"

# 永久加入 PATH（~/.profile 追加，已有则跳过；当前会话用 export PATH 手动加）
BINDIR="$(dirname "$DEST")"
PROFILE="$HOME/.profile"
if ! grep -qsF "$BINDIR" "$PROFILE" 2>/dev/null; then
  {
    echo ""
    echo "# wtangent"
    echo "export PATH=\"$BINDIR:\$PATH\""
  } >> "$PROFILE"
  echo "[install] 已永久加入 PATH（$PROFILE）；重新登录或 source ~/.profile 生效"
else
  echo "[install] 已在 PATH：$BINDIR"
fi

"$DEST" --help 2>&1 | head -3

# 自动安装官方组件（serve/tui/client/git；失败不阻断，可手动 wtangent install）
echo "[install] 自动安装官方组件（serve/tui/client/git）…"
"$DEST" install >/dev/null 2>&1 || echo "[install] 官方组件安装失败（可手动 wtangent install）"

echo "[install] 完成：$DEST（当前会话：export PATH=\"$BINDIR:\$PATH\"）"
