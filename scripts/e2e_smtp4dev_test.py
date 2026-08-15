import json
import os
import smtplib
import subprocess
import sys
import time
from email.message import EmailMessage

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
SMTP_PORT = 2525
IMAP_PORT = 2143


def send_frame(proc, payload):
    body = json.dumps(payload, separators=(",", ":")).encode("utf-8")
    frame = b"Content-Length: " + str(len(body)).encode("ascii") + b"\r\n\r\n" + body
    proc.stdin.write(frame)
    proc.stdin.flush()


def read_frame(proc):
    headers = {}
    while True:
        line = proc.stdout.readline()
        if not line:
            raise RuntimeError("process closed while reading MCP response")
        if line in (b"\r\n", b"\n"):
            break
        if b":" not in line:
            raise RuntimeError(f"malformed MCP header: {line!r}")
        key, value = line.decode("utf-8", errors="replace").split(":", 1)
        headers[key.strip().lower()] = value.strip()
    length = int(headers.get("content-length", "0"))
    body = proc.stdout.read(length)
    if len(body) != length:
        raise RuntimeError(f"expected {length} body bytes, got {len(body)}")
    return json.loads(body.decode("utf-8"))


def wait_for_server(proc):
    for _ in range(40):
        if proc.poll() is not None:
            raise RuntimeError(f"server exited early with code {proc.returncode}")
        try:
            send_frame(proc, {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2024-11-05", "capabilities": {}, "clientInfo": {"name": "e2e-test", "version": "1.0"}}})
            response = read_frame(proc)
            if "result" in response:
                return response
        except Exception:
            time.sleep(0.25)
    raise RuntimeError("timed out waiting for MCP initialize response")


def main():
    docker_cmd = ["docker", "compose", "-f", os.path.join(ROOT, "docker-compose.test.yml"), "up", "-d"]
    print("[1/5] Starting smtp4dev container...")
    subprocess.run(docker_cmd, cwd=ROOT, check=True)
    time.sleep(8)

    print("[2/5] Starting EmailMcp server...")
    proc = subprocess.Popen(
        ["dotnet", "run", "--project", "src/EmailMcp.csproj"],
        cwd=ROOT,
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=False,
        env={**os.environ,
             "EmailSender__SmtpHost": "localhost",
             "EmailSender__SmtpPort": str(SMTP_PORT),
             "EmailSender__SmtpCredential__UserName": "",
             "EmailSender__SmtpCredential__Password": "",
             "EmailReceiver__ImapHost": "localhost",
             "EmailReceiver__ImapPort": str(IMAP_PORT),
             "EmailReceiver__ImapCredential__UserName": "",
             "EmailReceiver__ImapCredential__Password": "",
             "EmailReceiver__MailFolderName": "INBOX"},
    )

    try:
        init = wait_for_server(proc)
        print("Initialize response:", json.dumps(init, indent=2))

        send_frame(proc, {"jsonrpc": "2.0", "method": "notifications/initialized", "params": {}})
        send_frame(proc, {"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}})
        tools = read_frame(proc)
        print("Tools response:", json.dumps(tools, indent=2))

        send_frame(proc, {"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": {"name": "get_status", "arguments": {}}})
        status = read_frame(proc)
        print("Status response:", json.dumps(status, indent=2))

        print("[3/5] Sending a test email to smtp4dev via SMTP...")
        msg = EmailMessage()
        msg["From"] = "sender@example.test"
        msg["To"] = "recipient@example.test"
        msg["Subject"] = "E2E smtp4dev smoke test"
        msg.set_content("This email was sent by the MCP end-to-end test for smtp4dev.")
        with smtplib.SMTP("localhost", SMTP_PORT, timeout=10) as smtp:
            smtp.send_message(msg)

        print("[4/5] Polling inbox for the new message via the MCP read_email tool...")
        deadline = time.time() + 20
        found = False
        while time.time() < deadline:
            send_frame(proc, {"jsonrpc": "2.0", "id": 4, "method": "tools/call", "params": {"name": "read_email", "arguments": {"maxResults": 10, "folder": "INBOX"}}})
            payload = read_frame(proc)
            result = payload.get("result", {})
            content = result.get("content") if isinstance(result, dict) else None
            if isinstance(content, list):
                text = "".join(item.get("text", "") for item in content if isinstance(item, dict))
                if "E2E smtp4dev smoke test" in text:
                    found = True
                    print("Found the sent message in the inbox: ", text[:500])
                    break
            time.sleep(1)

        if not found:
            raise RuntimeError("the test message was not visible in INBOX through the MCP server")

        print("[5/5] End-to-end test passed.")
    finally:
        try:
            proc.stdin.close()
        except Exception:
            pass
        try:
            proc.terminate()
        except Exception:
            pass
        try:
            proc.wait(timeout=10)
        except Exception:
            pass

        print("Stopping smtp4dev container...")
        subprocess.run(["docker", "compose", "-f", os.path.join(ROOT, "docker-compose.test.yml"), "down"], cwd=ROOT, check=False)


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        print(f"E2E test failed: {exc}", file=sys.stderr)
        raise
