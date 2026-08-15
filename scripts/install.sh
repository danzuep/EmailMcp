#!/usr/bin/env bash
# ./install.sh — in-repo setup for Email MCP
# Run from the repo root (directory that contains EmailMcp.cs).
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "${REPO_ROOT}"

if [[ ! -f "${REPO_ROOT}/EmailMcp.cs" ]]; then
  echo "Error: EmailMcp.cs not found in ${REPO_ROOT}"
  exit 1
fi

chmod +x "${REPO_ROOT}/EmailMcp.cs" 2>/dev/null || true

# ── .env (create only if missing; never overwrite secrets) ──────────────────
ENV_FILE="${REPO_ROOT}/.env"
if [[ -f "${ENV_FILE}" ]]; then
  echo "==> .env already exists — leaving it unchanged"
else
  cat > "${ENV_FILE}" << 'EOF'
# Required for reading mail
IMAP_HOST=imap.example.com:993
IMAP_USER=you@example.com
IMAP_PASSWORD=your-app-password
# Or use OAuth2 instead of password:
# IMAP_ACCESS_TOKEN=ya29...

# Optional SMTP (for send_email)
SMTP_HOST=smtp.example.com:587
SMTP_USER=you@example.com
SMTP_PASSWORD=your-app-password
SMTP_FROM=you@example.com

# Safety gates for sending
SEND_EMAIL_ENABLED=false
SEND_ALLOW_LIST=you@example.com,*@yourdomain.com
EOF
  echo "==> Created ${ENV_FILE} — edit with your real credentials"
fi

# Ensure .env is gitignored
if [[ -f "${REPO_ROOT}/.gitignore" ]]; then
  if ! grep -qxF '.env' "${REPO_ROOT}/.gitignore" 2>/dev/null; then
    echo '.env' >> "${REPO_ROOT}/.gitignore"
    echo "==> Added .env to .gitignore"
  fi
else
  echo '.env' > "${REPO_ROOT}/.gitignore"
  echo "==> Created .gitignore with .env"
fi

# Absolute path for configs that need one
ABS_RUN="${REPO_ROOT}/run-email-mcp"

# ── .vscode/mcp.json (workspace MCP for VS Code Copilot) ────────────────────
mkdir -p "${REPO_ROOT}/.vscode"
MCP_JSON="${REPO_ROOT}/.vscode/mcp.json"
if [[ -f "${MCP_JSON}" ]]; then
  echo "==> .vscode/mcp.json already exists — not overwriting"
  echo "    Add an 'email' server entry manually if missing (see printed snippet below)."
else
  cat > "${MCP_JSON}" << EOF
{
  "servers": {
    "email": {
      "type": "stdio",
      "command": "${ABS_RUN}"
    }
  }
}
EOF
  echo "==> Created .vscode/mcp.json → ${ABS_RUN}"
fi

echo
echo "========================================"
echo "  Email MCP — in-repo setup complete"
echo "========================================"
echo
echo "1. Edit credentials:"
echo "     ${ENV_FILE}"
echo
echo "2. Smoke-test (Ctrl+C to quit):"
echo "     ${ABS_RUN}"
echo
echo "3. VS Code Copilot"
echo "   - Open this folder as the workspace"
echo "   - MCP config: .vscode/mcp.json (already written)"
echo "   - Reload window, then MCP: List Servers → start 'email' if needed"
echo "   - Copilot Chat (agent mode) should expose email tools"
echo
echo "Requires: .NET 10+ SDK with 'dotnet' on PATH."
echo "Done."