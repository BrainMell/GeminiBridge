# GeminiBridge

An OpenAI-compatible HTTP bridge that lets any AI coding agent use **Google Gemini Web** (via a Playwright-automated browser) as a model backend. No API keys, no billing — just a Google account and a browser session.

## How it works

```
Coding Agent (OpenCode, Claude Code, etc.)
  → OpenAI SDK-compatible HTTP request
  → GeminiBridge (localhost:8787)
  → Playwright Chromium browser
  → gemini.google.com web UI
  → scraped response → back to agent
```

## Setup

```bash
dotnet build
dotnet run
```

First run opens a visible browser — sign into your Google account. After that it runs headless.

## Configuring agents

All of these support custom OpenAI-compatible providers. Point them at `http://127.0.0.1:8787/v1` with any dummy API key.

### OpenCode

`~/.config/opencode/opencode.json`:
```json
{
  "provider": {
    "gemini-bridge": {
      "npm": "@ai-sdk/openai-compatible",
      "name": "Gemini (browser)",
      "options": {
        "baseURL": "http://127.0.0.1:8787/v1",
        "apiKey": "noop"
      }
    }
  },
  "model": "gemini-bridge/gemini-web"
}
```

### Claude Code

`~/.claude.json` or `claude.json` in project:
```json
{
  "apiKey": "noop",
  "model": "gemini-web",
  "provider": {
    "name": "gemini-bridge",
    "apiBase": "http://127.0.0.1:8787/v1"
  }
}
```

### Continue (VS Code / JetBrains)

`~/.continue/config.json`:
```json
{
  "models": [{
    "title": "Gemini (Browser)",
    "provider": "openai",
    "model": "gemini-web",
    "apiBase": "http://127.0.0.1:8787/v1",
    "apiKey": "noop"
  }]
}
```

### Cline / Roo Code (VS Code)

In extension settings, add an OpenAI-compatible provider:
- **Base URL**: `http://127.0.0.1:8787/v1`
- **API Key**: any value
- **Model ID**: `gemini-web`

### Claude Desktop (MCP)

`~/.claude/servers.json` or Claude Desktop settings → Developer → Edit Config:
```json
{
  "mcpServers": {
    "gemini-bridge": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/GeminiBridge"]
    }
  }
}
```

### Aider

`~/.aider.conf.yml` or `aider --openai-api-base http://127.0.0.1:8787/v1 --model gemini-web`

### Cursor

Settings → Models → Add Model:
- **Provider**: OpenAI
- **Base URL**: `http://127.0.0.1:8787/v1`
- **Model**: `gemini-web`
- **API Key**: any value

### Windsurf

`~/.codeium/windsurf.json` or Settings → Models → Custom Provider:
- **Base URL**: `http://127.0.0.1:8787/v1`
- **Model**: `gemini-web`

### Any OpenAI-compatible client

```
OPENAI_BASE_URL=http://127.0.0.1:8787/v1
OPENAI_API_KEY=noop
OPENAI_MODEL=gemini-web
```

## API endpoints

| Endpoint | Description |
|---|---|
| `GET /v1/models` | Lists available models |
| `POST /v1/chat/completions` | Chat completion (stream + non-stream) |
| `GET /healthz` | Health check |
| `GET /v1/debug/screenshot` | Browser screenshot |
| `GET /v1/debug/history` | Dump chat history |

## Auto-starting GeminiBridge when the agent starts

Each agent has its own way to run a command at startup.

### OpenCode

`~/.config/opencode/opencode.json`:
```json
{
  "hooks": {
    "startup": "dotnet run --project /path/to/GeminiBridge"
  }
}
```

### Claude Code

`~/.claude.json`:
```json
{
  "hooks": {
    "postStartup": "dotnet run --project /path/to/GeminiBridge"
  }
}
```

Or via a `claude_prestart.sh` wrapper:
```bash
#!/usr/bin/env bash
dotnet run --project /path/to/GeminiBridge -- --headless &
claude "$@"
```

### Continue

`~/.continue/config.json`:
```json
{
  "experimental": {
    "startupCommands": ["dotnet run --project /path/to/GeminiBridge"]
  }
}
```

### Cline / Roo Code

Use a VS Code task in `.vscode/tasks.json`:
```json
{
  "version": "2.0.0",
  "tasks": [{
    "label": "gemini-bridge",
    "type": "shell",
    "command": "dotnet run --project /path/to/GeminiBridge",
    "isBackground": true,
    "runOptions": { "runOn": "folderOpen" }
  }]
}
```

Or define GeminiBridge as an MCP server in Cline's `mcp_settings.json` — Cline auto-starts MCP servers on launch:
```json
{
  "mcpServers": {
    "gemini-bridge": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/GeminiBridge"]
    }
  }
}
```

### Cursor

Use a Cursor launch task in `.cursor/tasks.json`:
```json
{
  "tasks": [{
    "label": "gemini-bridge",
    "command": "dotnet run --project /path/to/GeminiBridge",
    "runAt": "startup"
  }]
}
```

### Windsurf

Add a pre-start script via `~/.codeium/windsurf.json`:
```json
{
  "preStartCommands": ["dotnet run --project /path/to/GeminiBridge &"]
}
```

### Aider

Use a wrapper script `aider-gemini`:
```bash
#!/usr/bin/env bash
dotnet run --project /path/to/GeminiBridge &
sleep 3
aider --openai-api-base http://127.0.0.1:8787/v1 --model gemini-web "$@"
```

### Generic (any agent)

**systemd (Linux)** — survives agent restarts:
```ini
# ~/.config/systemd/user/gemini-bridge.service
[Unit]
Description=GeminiBridge

[Service]
ExecStart=dotnet run --project /path/to/GeminiBridge
Restart=on-failure

[Install]
WantedBy=default.target
```
```bash
systemctl --user enable --now gemini-bridge
```

**launchd (macOS)**:
```xml
<!-- ~/Library/LaunchAgents/com.user.geminibridge.plist -->
<plist>
<dict>
  <key>Label</key>  <string>com.user.geminibridge</string>
  <key>ProgramArguments</key>
  <array>
    <string>/usr/bin/dotnet</string>
    <string>run</string>
    <string>--project</string>
    <string>/path/to/GeminiBridge</string>
  </array>
  <key>RunAtLoad</key> <true/>
  <key>KeepAlive</key>  <true/>
</dict>
</plist>
```
```bash
launchctl load ~/Library/LaunchAgents/com.user.geminibridge.plist
```

**tmux** — start in a detached session from your shell rc:
```bash
# ~/.bashrc / ~/.zshrc
tmux new-session -d -s gemini-bridge 'dotnet run --project /path/to/GeminiBridge' 2>/dev/null
```
