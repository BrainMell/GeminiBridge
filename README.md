# GeminiBridge

An OpenAI-compatible standalone HTTP bridge that enables coding agents (like **OpenCode**) to use the official Google Gemini Web UI (via a Playwright automated browser context) as a model backend.

## Features

- **OpenAI Compatibility**: Exposes standard `/v1/chat/completions` and `/v1/models` HTTP endpoints.
- **Dynamic Tool Translation**: Captures agent tool definitions (e.g. MCP tools schemas) and formats them into XML structures for Gemini, parsing back tool calls output by the model.
- **Robust Parsing Fallbacks**: Automatically supports both the `TOOLCALL:` JSON format and natural browser-mimicked `[tool_call:name {json}]` formats, with automatic correction for unquoted JSON keys.
- **Session Continuity**: Retains chat session URLs to maintain conversation history across subsequent API completions.
- **Sequential Locking**: Employs semaphore-based request serialization to prevent concurrent write collisions on the single automated Playwright browser tab.
- **Visual Debugging**: Diagnostic endpoints available to inspect the browser state:
  - `GET /v1/debug/history` – Scrapes and dumps the current active chat session messages.
  - `GET /v1/debug/screenshot` – Captures a PNG screenshot of the automated browser window.

## Setup & Running

1. **Restore and Build**:
   ```bash
   dotnet build
   ```

2. **First-Time Run (Authentication)**:
   On the first run, the automated browser will launch in **headed** (visible) mode. Scan the console or complete the Google account login/CAPTCHA verification in the opened window.
   ```bash
   dotnet run
   ```
   Once authentication is successful, cookies are persisted to `PlaywrightProfile/` and future runs will default to **headless** (invisible) mode.

3. **Integrate with OpenCode**:
   Add a custom provider pointing to your local GeminiBridge instance in `opencode.json`:
   ```json
   {
     "providers": {
       "gemini-bridge": {
         "type": "openai-compatible",
         "baseURL": "http://127.0.0.1:8787/v1",
         "apiKey": "noop"
       }
     },
     "models": {
       "gemini-web": {
         "providerID": "gemini-bridge"
       }
     }
   }
   ```
