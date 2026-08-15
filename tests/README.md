# Email MCP Server

A lightweight [Model Context Protocol](https://modelcontextprotocol.io/) server that lets AI assistants read, search, and (optionally) send email through your own IMAP/SMTP account.

Built with **.NET**, **MailKitSimplified**, and the official **Model Context Protocol** C# SDK.

---

## Quick start

### Run tests

```sh
dotnet run --file ./tests/e2e-tests.cs -- --compose
```

### Build and run server

```sh
dotnet build ./src/EmailMcp.csproj
docker compose -f tests/docker-compose.e2e.yml up -d
timeout 30s dotnet run --project ./src/EmailMcp.csproj
```

---

## Connect to VS Code / GitHub Copilot

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
