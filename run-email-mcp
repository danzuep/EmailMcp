#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "${ROOT}"

if [[ -f .env ]]; then
  set -a
  # shellcheck disable=SC1091
  source .env
  set +a
fi

resolve_dotnet() {
  if [[ -n "${DOTNET_ROOT:-}" ]]; then
    if [[ -x "${DOTNET_ROOT}/dotnet" ]]; then
      echo "${DOTNET_ROOT}/dotnet"
      return 0
    fi
    if [[ -x "${DOTNET_ROOT}/dotnet.exe" ]]; then
      echo "${DOTNET_ROOT}/dotnet.exe"
      return 0
    fi
  fi

  if command -v dotnet >/dev/null 2>&1; then
    command -v dotnet
    return 0
  fi

  if command -v pwsh >/dev/null 2>&1; then
    local pwsh_path
    pwsh_path="$(command -v pwsh)"
    local resolved
    resolved="$("${pwsh_path}" -NoLogo -NoProfile -Command "& { $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue; if ($dotnet) { $dotnet.Source } }" 2>/dev/null || true)"
    if [[ -n "${resolved}" ]]; then
      echo "${resolved}"
      return 0
    fi
  fi

  for candidate in \
    "/c/Program Files/dotnet/dotnet" \
    "/c/Program Files/dotnet/dotnet.exe" \
    "/usr/bin/dotnet" \
    "/usr/local/bin/dotnet"; do
    if [[ -x "${candidate}" ]]; then
      echo "${candidate}"
      return 0
    fi
  done

  return 1
}

DOTNET_BIN="$(resolve_dotnet)" || {
  echo "dotnet not found. Install the .NET SDK or set DOTNET_ROOT." >&2
  exit 127
}

exec "${DOTNET_BIN}" "${ROOT}/EmailMcp.cs" "$@"
