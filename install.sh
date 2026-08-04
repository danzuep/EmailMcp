#!/usr/bin/env bash
# install.sh — set up Email MCP server for local agent use
set -euo pipefail

INSTALL_DIR="${1:-$HOME/.local/share/email-mcp}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC="${SCRIPT_DIR}/EmailMcp.cs"

if [[ ! -f "${SRC}" ]]; then
  echo "Error: EmailMcp.cs not found next to this script (${SCRIPT_DIR})"
  exit 1
fi

echo "==> Installing Email MCP to ${INSTALL_DIR}"
mkdir -p "${INSTALL_DIR}"
cp -f "${SRC}" "${INSTALL_DIR}/EmailMcp.cs"
chmod +x "${INSTALL_DIR}/EmailMcp.cs"

# Create .env only if it does not already exist (never overwrite secrets)
ENV_FILE="${INSTALL_DIR}/.env"
if [[ -f "${ENV_FILE}" ]]; then
  echo "==> .env already exists — leaving it unchanged"
else
  cat > "${ENV_FILE}" << 'EOF'
# Required for reading mail
IMAP_HOST=imap.example.com
IMAP_PORT=993
IMAP_USER=you@example.com
IMAP_PASSWORD=your-app-password
# Or use OAuth2 instead of password:
# IMAP_ACCESS_TOKEN=ya29...

# Optional SMTP (for send_email)
SMTP_HOST=smtp.example.com
SMTP_PORT=587
SMTP_USER=you@example.com
SMTP_PASSWORD=your-app-password
SMTP_FROM=you@example.com

# Safety gates for sending
SEND_EMAIL_ENABLED=false
SEND_ALLOW_LIST=you@example.com,*@yourdomain.com

# Optional: load settings from a file instead of (or in addition to) env
# IMAP_SETTINGS_FILE=/path/to/settings.env
EOF
  echo "==> Created ${ENV_FILE} — edit with your real credentials"
fi

# Wrapper that loads .env (if present) and runs the single-file app
cat > "${INSTALL_DIR}/run-email-mcp" << EOF
#!/usr/bin/env bash
set -euo pipefail
cd "${INSTALL_DIR}"
if [[ -f .env ]]; then
  set -a
  # shellcheck disable=SC1091
  source .env
  set +a
fi
exec dotnet "${INSTALL_DIR}/EmailMcp.cs" "\$@"
EOF
chmod +x "${INSTALL_DIR}/run-email-mcp"

# Resolve absolute path for config snippets
ABS_RUN="$(cd "${INSTALL_DIR}" && pwd)/run-email-mcp"

echo
echo "========================================"
echo "  Email MCP installed"
echo "========================================"
echo
echo "1. Edit credentials (required once):"
echo "     ${ENV_FILE}"
echo
echo "2. Smoke-test (Ctrl+C to quit):"
echo "     ${ABS_RUN}"
echo
echo "3. Point a local agent at it (stdio MCP):"
echo
echo "── Claude Desktop ──"
echo "   macOS:  ~/Library/Application Support/Claude/claude_desktop_config.json"
echo "   Linux:  ~/.config/Claude/claude_desktop_config.json"
echo "   Windows: %APPDATA%\\Claude\\claude_desktop_config.json"
echo
echo '   {
     "mcpServers": {
       "email": {
         "command": "'"${ABS_RUN}"'"
       }
     }
   }'
echo "   Restart Claude Desktop after saving."
echo
echo "── Cursor ──"
echo "   Settings → MCP, or edit ~/.cursor/mcp.json (or project .cursor/mcp.json):"
echo
echo '   {
     "mcpServers": {
       "email": {
         "command": "'"${ABS_RUN}"'"
       }
     }
   }'
echo
echo "── VS Code Copilot (GitHub Copilot Chat) ──"
echo "   Requires the GitHub Copilot Chat extension with MCP support."
echo "   Create or edit:  .vscode/mcp.json  (workspace)  or user MCP settings"
echo
echo '   {
     "servers": {
       "email": {
         "type": "stdio",
         "command": "'"${ABS_RUN}"'"
       }
     }
   }'
echo
echo "   Alternative (user-level): Command Palette → 'MCP: Open User Configuration'"
echo "   and add the same 'email' entry under servers."
echo "   Reload the window, then open Copilot Chat and confirm tools appear"
echo "   (read_email, search_emails, list_folders, get_status, send_email, draft_email)."
echo
echo "── Claude Code (CLI) ──"
echo "   claude mcp add email -- ${ABS_RUN}"
echo
echo "── Generic / custom agent ──"
echo "   Any MCP client that launches a stdio process:"
echo '   { "command": "'"${ABS_RUN}"'" }'
echo
echo "Requires: .NET 10+ SDK with 'dotnet' on PATH for the agent process."
echo
echo "Done."
