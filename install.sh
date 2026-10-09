#!/usr/bin/env bash
set -euo pipefail

# Usage:
#   curl -fsSL .../install.sh | sudo bash                              # CLI + ASP.NET Core 9 runtime
#   curl -fsSL .../install.sh | sudo bash -s -- --no-dotnet            # CLI only
#   curl -fsSL .../install.sh | sudo bash -s -- --dotnet-channel 8.0   # CLI + another runtime version

REPO_RAW="https://raw.githubusercontent.com/dkdghar/dotnet-on-cyberpanel/main"
TARGET="/usr/local/bin/cyberpanel-dotnet"
SOURCE="${REPO_RAW}/cli/cyberpanel-dotnet"
DOTNET_INSTALL_URL="https://dot.net/v1/dotnet-install.sh"

INSTALL_DOTNET="yes"
DOTNET_CHANNEL="9.0"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --no-dotnet) INSTALL_DOTNET="no"; shift;;
    --dotnet-channel)
      [[ -n "${2:-}" ]] || { echo "[X] --dotnet-channel requires a value (e.g. 9.0)" >&2; exit 1; }
      DOTNET_CHANNEL="$2"; shift 2;;
    *) echo "[X] Unknown arg: $1" >&2; exit 1;;
  esac
done

[[ $EUID -eq 0 ]] || { echo "[X] Run as root (sudo)." >&2; exit 1; }
command -v curl >/dev/null 2>&1 || { echo "[X] Missing dependency: curl" >&2; exit 1; }

TMPDIR_INSTALL="$(mktemp -d)"
trap 'rm -rf "${TMPDIR_INSTALL}"' EXIT

# ---------------- cyberpanel-dotnet CLI ----------------

echo "[i] Installing cyberpanel-dotnet CLI to ${TARGET}"

# Download to a temp file first so a failed/partial download never replaces a working install
TMP="${TMPDIR_INSTALL}/cyberpanel-dotnet"
if ! curl -fsSL "${SOURCE}" -o "${TMP}"; then
  echo "[X] Failed to download ${SOURCE}" >&2
  exit 1
fi

# Normalize line endings (fix if uploaded with CRLF from Windows)
sed -i 's/\r$//' "${TMP}"

# Quick syntax validation
if ! bash -n "${TMP}"; then
  echo "[X] Syntax error detected in downloaded script" >&2
  exit 1
fi

install -m 755 "${TMP}" "${TARGET}"
echo "[✓] Installed cyberpanel-dotnet v$("${TARGET}" --version 2>/dev/null || echo '?')"

# ---------------- ASP.NET Core runtime ----------------

has_aspnet_runtime(){
  command -v dotnet >/dev/null 2>&1 &&
    dotnet --list-runtimes 2>/dev/null | grep -q "^Microsoft\.AspNetCore\.App ${DOTNET_CHANNEL//./\\.}\."
}

install_runtime_deps(){
  # Native libraries the .NET runtime needs (ICU for globalization, OpenSSL, zlib, ...)
  if command -v apt-get >/dev/null 2>&1; then
    local icu
    icu="$(apt-cache pkgnames libicu 2>/dev/null | grep -E '^libicu[0-9]+$' | sort -V | tail -n1 || true)"
    DEBIAN_FRONTEND=noninteractive apt-get update -qq || true
    DEBIAN_FRONTEND=noninteractive apt-get install -y -qq ca-certificates tzdata zlib1g ${icu:-libicu-dev} \
      || echo "[!] Could not install some runtime dependencies; continuing" >&2
  elif command -v dnf >/dev/null 2>&1; then
    dnf install -y -q ca-certificates tzdata zlib libicu openssl-libs krb5-libs libstdc++ \
      || echo "[!] Could not install some runtime dependencies; continuing" >&2
  elif command -v yum >/dev/null 2>&1; then
    yum install -y -q ca-certificates tzdata zlib libicu openssl-libs krb5-libs libstdc++ \
      || echo "[!] Could not install some runtime dependencies; continuing" >&2
  fi
}

install_aspnet_runtime(){
  if has_aspnet_runtime; then
    echo "[✓] ASP.NET Core ${DOTNET_CHANNEL} runtime already installed"
    return
  fi

  echo "[i] Installing ASP.NET Core ${DOTNET_CHANNEL} runtime"
  install_runtime_deps

  # Install next to an existing dotnet (side-by-side) so one `dotnet` host sees every runtime
  local root="/usr/share/dotnet"
  if command -v dotnet >/dev/null 2>&1; then
    root="$(dirname "$(readlink -f "$(command -v dotnet)")")"
  fi

  local script="${TMPDIR_INSTALL}/dotnet-install.sh"
  curl -fsSL "${DOTNET_INSTALL_URL}" -o "${script}" \
    || { echo "[X] Failed to download ${DOTNET_INSTALL_URL}" >&2; exit 1; }
  bash "${script}" --channel "${DOTNET_CHANNEL}" --runtime aspnetcore --install-dir "${root}" --no-path \
    || { echo "[X] ASP.NET Core ${DOTNET_CHANNEL} runtime install failed" >&2; exit 1; }

  # Generated systemd units use the dotnet found on PATH; make sure it's there
  if ! command -v dotnet >/dev/null 2>&1; then
    ln -sfn "${root}/dotnet" /usr/bin/dotnet
  fi

  if has_aspnet_runtime; then
    echo "[✓] Installed ASP.NET Core ${DOTNET_CHANNEL} runtime ($(command -v dotnet))"
  else
    echo "[X] ASP.NET Core ${DOTNET_CHANNEL} runtime not detected after install" >&2
    exit 1
  fi
}

if [[ "${INSTALL_DOTNET}" == "yes" ]]; then
  install_aspnet_runtime
else
  echo "[i] Skipping ASP.NET Core runtime install (--no-dotnet)"
fi

echo "    Test with: cyberpanel-dotnet --help"
