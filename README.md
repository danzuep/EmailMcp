# Email MCP Server

A lightweight [Model Context Protocol](https://modelcontextprotocol.io/) server that lets AI assistants read, search, and (optionally) send email through your own IMAP/SMTP account.

Built with **.NET**, **MailKitSimplified**, and the official **Model Context Protocol** C# SDK.

---

## Features

- [x] Read newest messages or a single message by UniqueId
- [x] Search by subject, from, body, unread, or read
- [x] List IMAP folders
- [x] Connection status check
- [x] Open a pre-filled draft in your default mail app (never sends)
- [x] Optional SMTP send, off by default and gated by an allow-list
- [x] OAuth2 (XOAUTH2) or username/password authentication
- [x] Friendly JSON errors instead of stack traces
- [x] Single-file runnable script — no project scaffolding required

---

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (or newer)
- An email account with IMAP access (and SMTP if you enable sending)
- For Gmail / Microsoft 365: an [app password](https://support.google.com/accounts/answer/185833) or OAuth2 access token

---

## Quick start

### 1. Save the server

Copy `EmailMcp.cs` to a folder of your choice, for example:

```text
~/tools/EmailMcp.cs
```

### 2. Set credentials

```bash
export IMAP_HOST=imap.example.com
export IMAP_PORT=993
export IMAP_USER=you@example.com
export IMAP_PASSWORD='your-app-password'
```

Or put the same keys in a file and point to it:

```bash
export IMAP_SETTINGS_FILE=~/.config/email-mcp.env
```

Example file contents:

```ini
IMAP_HOST=imap.example.com
IMAP_PORT=993
IMAP_USER=you@example.com
IMAP_PASSWORD=your-app-password
```

### 3. Run it

```bash
dotnet run --file ~/tools/EmailMcp.cs
```

The process speaks MCP over stdio and waits for a client to connect. Logs go to stderr so they never corrupt the protocol stream.

---

## Connect to VS Code / GitHub Copilot

Create or edit `.vscode/mcp.json` in your workspace:

```json
{
  "servers": {
    "email": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["run", "--file", "/absolute/path/to/EmailMcp.cs"],
      "env": {
        "IMAP_HOST": "imap.example.com",
        "IMAP_PORT": "993",
        "IMAP_USER": "you@example.com",
        "IMAP_PASSWORD": "${input:imap-password}"
      }
    }
  },
  "inputs": [
    {
      "type": "promptString",
      "id": "imap-password",
      "description": "IMAP password or app password",
      "password": true
    }
  ]
}
```

Then:

1. Open the Command Palette → **MCP: List Servers**
2. Start the `email` server if it is not already running
3. Switch Copilot Chat to **Agent** mode
4. Ask things like:
   - “Show my last 5 emails”
   - “Search for messages from alice@example.com”
   - “Read the email with uniqueId 42”
   - “Draft a reply saying I’ll review this tomorrow”

---

## Tools

| Tool | Description |
|------|-------------|
| `read_email` | Newest messages, or one message by `uniqueId`. Returns UniqueId, headers, Markdown body, attachment metadata. |
| `search_emails` | Search by `all`, `subject`, `from`, `unread`, or `read`. |
| `list_folders` | List IMAP folders on the server. |
| `get_status` | Check connectivity and report whether sending is enabled. |
| `draft_email` | Open a mailto: draft in your default mail app. **Never sends.** |
| `send_email` | Send via SMTP. **Disabled unless explicitly enabled** (see below). |

All tools return JSON shaped like:

```json
{ "ok": true, "data": { ... } }
```

or on failure:

```json
{ "ok": false, "error": "Clear human-readable message" }
```

---

## Optional: enable sending

Sending is **off by default**. To turn it on:

```bash
export SMTP_HOST=smtp.example.com
export SMTP_PORT=587
export SMTP_USER=you@example.com
export SMTP_PASSWORD='your-app-password'
export SMTP_FROM=you@example.com          # optional, defaults to SMTP_USER

export SEND_EMAIL_ENABLED=true
export SEND_ALLOW_LIST='alice@example.com,*@yourcompany.com'
```

- `SEND_ALLOW_LIST` accepts exact addresses or domain patterns (`*@example.com`).
- An empty allow-list blocks every recipient (safe default).
- SMTP settings fall back to `IMAP_*` values when the corresponding `SMTP_*` variable is missing.

---

## Configuration reference

| Variable | Required | Default | Description |
|----------|----------|---------|-------------|
| `IMAP_HOST` | yes | — | IMAP server hostname |
| `IMAP_PORT` | no | `993` | IMAP port |
| `IMAP_USER` | yes | — | Login username |
| `IMAP_PASSWORD` | yes\* | — | Password or app password |
| `IMAP_ACCESS_TOKEN` | yes\* | — | OAuth2 access token (XOAUTH2) |
| `IMAP_SETTINGS_FILE` | no | — | Path to a key=value settings file |
| `SMTP_HOST` | for send | — | SMTP server hostname |
| `SMTP_PORT` | no | `587` | SMTP port |
| `SMTP_USER` | for send | — | SMTP username |
| `SMTP_PASSWORD` | for send | — | SMTP password |
| `SMTP_FROM` | no | `SMTP_USER` | From address |
| `SEND_EMAIL_ENABLED` | no | `false` | Set to `true` to allow `send_email` |
| `SEND_ALLOW_LIST` | no | _(empty)_ | Comma-separated allowed recipients |

\* Provide either `IMAP_PASSWORD` or `IMAP_ACCESS_TOKEN`.

---

## Security notes

- Prefer **app passwords** or short-lived OAuth tokens over your main account password.
- Keep `SEND_EMAIL_ENABLED` unset (or `false`) unless you need outbound mail.
- Always set a tight `SEND_ALLOW_LIST` when sending is enabled.
- Never commit passwords or tokens to source control; use environment variables or the VS Code `${input:…}` prompt.
- The server runs locally over stdio — your mail credentials stay on your machine.

---

## Example prompts

Once the server is connected in Agent mode:

- “What’s in my inbox?”
- “Find unread mail about the quarterly report”
- “Read uniqueId 128 and summarize it”
- “List my mail folders”
- “Draft an email to bob@example.com about rescheduling our meeting”
- “Send a short confirmation to alice@example.com” *(only if send is enabled and Alice is on the allow-list)*

---

## License

Use and modify freely for your own projects.
