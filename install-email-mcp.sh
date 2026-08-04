#!/usr/bin/env bash
set -euo pipefail

# Install / setup Email MCP server for local agent use
# Usage: ./install-email-mcp.sh [install-dir]

INSTALL_DIR="${1:-$HOME/.local/share/email-mcp}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC="${SCRIPT_DIR}/EmailMcp.cs"

echo "==> Installing Email MCP to ${INSTALL_DIR}"

mkdir -p "${INSTALL_DIR}"
cp -f "${SRC}" "${INSTALL_DIR}/EmailMcp.cs"
chmod +x "${INSTALL_DIR}/EmailMcp.cs"

# Example env file (never commit real secrets)
cat > "${INSTALL_DIR}/.env.example" << 'EOF'
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

# Optional: load settings from a file instead of env
# IMAP_SETTINGS_FILE=/path/to/settings.env
EOF

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

echo
echo "Done."
echo
echo "Next steps:"
echo "  1. cp ${INSTALL_DIR}/.env.example ${INSTALL_DIR}/.env"
echo "  2. Edit ${INSTALL_DIR}/.env with your IMAP/SMTP credentials"
echo "  3. Point your MCP client at the server, e.g.:"
echo
echo '     {'
echo '       "mcpServers": {'
echo '         "email": {'
echo "           \"command\": \"${INSTALL_DIR}/run-email-mcp\""
echo '         }'
echo '       }'
echo '     }'
echo
echo "Or run directly:  ${INSTALL_DIR}/run-email-mcp"
echo
echo "Requires: .NET 10 SDK (or later) with 'dotnet' on PATH."