---
name: email
description: >-
  Read, search, list folders, check status, draft, or send email via the Email MCP
  server. Use when the user asks about inbox, messages, mail, SMTP/IMAP, or drafting
  replies. Prefer MCP tools over inventing message content.
---

# Email skill

## When to use

- User asks to read, search, or summarize email
- User asks which folders exist or whether mail is connected
- User wants a draft opened in their mail app
- User wants to send mail (only if send is enabled and recipient is allowed)

## Tools (MCP server `email`)

| Tool | Purpose |
|------|---------|
| `get_status` | Connectivity + send allow-list / enabled flag |
| `list_folders` | IMAP folder names |
| `search_emails` | Lightweight hits (UniqueId, Date, From, To, Subject) |
| `read_email` | Full message(s) or one by UniqueId |
| `draft_email` | Open mailto: draft (never sends) |
| `send_email` | SMTP send (gated by SEND_EMAIL_ENABLED + SEND_ALLOW_LIST) |

## Rules

1. Never invent message bodies, subjects, or UniqueIds — always call a tool.
2. Prefer `search_emails` then `read_email` with `uniqueId` for targeted reads.
3. Before `send_email`, call `get_status`. If send is disabled or recipient is not allowed, use `draft_email` instead and tell the user.
4. Default folder is INBOX; pass another folder only when the user names one.
5. Keep `maxResults` small (≤ 10 unless the user asks for more; hard max 25).
