# OpenCode Go Local API Proxy

A small local HTTP reverse proxy that lets third-party OpenAI-compatible applications
(Msty Go, AnythingLLM, custom scripts) talk to **OpenCode Go** at
`https://opencode.ai/inference/go`.

The proxy exists because some clients do not send `x-opencode-session`, and OpenCode Go
rejects those requests with:

```text
HTTP 400
{"type":"error","error":{"type":"MissingSessionID","message":"Request is missing x-opencode-session and cannot be routed efficiently. ..."}}
```

The proxy supplies that header automatically, forwards everything else unchanged, and
streams responses back as they arrive (including SSE).

```text
Msty Go / AnythingLLM / any OpenAI-compatible client
                 |
                 | http://127.0.0.1:8765/v1
                 v
        OpenCodeGoProxy (this app)
                 |   adds x-opencode-session
                 |   forwards headers + body byte-for-byte
                 |   streams the response
                 | https://opencode.ai/inference/go
                 v
            OpenCode Go
```

The proxy does not modify prompts, model parameters, tool definitions, or response
content, does not translate protocols, does not retry requests, and does not pretend to be
the official OpenCode client.

---

## 1. Prerequisites

| Purpose | Requirement |
| --- | --- |
| Build and run from source | .NET 10 SDK (ASP.NET Core 10 runtime) on Windows 11 |
| Run the published build | Nothing else (self-contained executable, no Docker/WSL/IIS/IIS Express/YARP) |
| Actual inference | An OpenCode Go API key (`oc_sk_...`) with an active Go allowance |
| Install as a Windows service (optional) | Administrator rights, one time (see §16) |

Check the SDK:

```powershell
dotnet --list-sdks
```

## 2. Project layout

```text
OpenCodeGoProxy/
  appsettings.json                     proxy configuration (no secrets)
  OpenCodeGoProxy.sln
  README.md
  src/
    OpenCodeGoProxy/
      Program.cs                       host entry point
      ProxyApp.cs                      endpoints, middleware, HttpClient setup
      ProxyForwarder.cs                forwarding, streaming, error handling
      SessionResolver.cs               x-opencode-session resolution + persistent store
      ProxyOptions.cs                  configuration parsing and validation
      Logging/FileLoggerProvider.cs    rolling file log used when running headless
  tests/
    OpenCodeGoProxy.Tests/              xUnit tests with a mocked upstream
  deploy/
    install-service.ps1                installs the published build as a Windows service
    uninstall-service.ps1              stops and removes the service (data optional)
  specs/
    001-initial/initial.md             specification
```

`appsettings.json` is **not** inside the project folder. It lives at the workspace root and
is copied next to the executable at build/publish time; the app uses the executable
directory as its content root. Edit the root file and rebuild, or edit the copy next to the
executable when using a published build.

## 3. Build

```powershell
cd C:\dev\opencodegoproxy
dotnet build OpenCodeGoProxy.sln
```

## 4. Run

```powershell
$env:OPENCODE_GO_API_KEY = "oc_sk_your_key_here"
dotnet run --project src\OpenCodeGoProxy
```

Expected startup output:

```text
info: OpenCodeGoProxy[0]
      Proxy started on http://127.0.0.1:8765 -> https://opencode.ai/inference/go
      (session mode: Auto, session store: C:\Users\<you>\AppData\Local\OpenCodeGoProxy,
      log directory: (console only))
```

The proxy refuses to start if `Proxy:Host` is not a loopback address.

To run it permanently (at boot, without a console window or a logged-on user), install it as
a Windows service - see §16. Clients keep using the same base URL.

## 5. Configuration

`appsettings.json`:

```json
{
  "Proxy": {
    "Host": "127.0.0.1",
    "Port": 8765,
    "UpstreamBaseUrl": "https://opencode.ai/inference/go",
    "SessionMode": "Auto",
    "Logging": "Minimal"
  },
  "Logging": {
    "LogLevel": { "Default": "Information" }
  }
}
```

| Key | Default | Meaning |
| --- | --- | --- |
| `Proxy:Host` | `127.0.0.1` | Bind address. Must be loopback (`127.0.0.1`, `::1` or `localhost`); anything else is rejected at startup. |
| `Proxy:Port` | `8765` | Local TCP port (`0` = ephemeral, useful for tests). |
| `Proxy:UpstreamBaseUrl` | `https://opencode.ai/inference/go` | Fixed upstream base. `http://` is accepted only for loopback (tests). No query/fragment allowed. |
| `Proxy:SessionMode` | `Auto` | Only `Auto` is implemented in the MVP; other values log a warning and behave as `Auto`. |
| `Proxy:Logging` | `Minimal` | `Minimal` → Information, `Verbose`/`Debug` → Debug, `Quiet`/`Warning` → Warning, `Error` → Error (applied to the proxy's own log category). |
| `Proxy:SessionStorePath` | `%LOCALAPPDATA%\OpenCodeGoProxy` (`%ProgramData%\OpenCodeGoProxy` when running as a service) | Directory for `session.json`. |
| `Proxy:SessionId` | *(none)* | Explicit fallback session ID. Prefer `OPENCODE_GO_SESSION_ID`. |
| `Proxy:ApiKey` | *(none)* | Static API key. `OPENCODE_GO_API_KEY` wins. Lets a service keep its key in `appsettings.Production.json` instead of an environment variable. |
| `Proxy:LogDirectory` | *(none)*; `%ProgramData%\OpenCodeGoProxy\logs` when running as a service | Enables rolling file logging in this directory. Unset in console mode = console only. |

Environment variables (they override `appsettings.json`; ASP.NET Core `__` maps nesting):

| Variable | Meaning |
| --- | --- |
| `OPENCODE_GO_API_KEY` | Upstream API key used for every request (Mode B). Never written to disk or logs. |
| `OPENCODE_GO_SESSION_ID` | Explicit fallback session ID (overrides `Proxy:SessionId`). |
| `Proxy__Host`, `Proxy__Port`, `Proxy__UpstreamBaseUrl`, `Proxy__Logging`, `Proxy__SessionStorePath`, `Proxy__ApiKey`, `Proxy__LogDirectory` | Any `appsettings.json` value. |

Example session override:

```powershell
$env:OPENCODE_GO_SESSION_ID = "ses_0123456789abcdef0123456789abcdef"
```

**Never put an API key in `appsettings.json`.** The proxy reads it from the environment (or
the client's own `Authorization` / `x-api-key` header) only.

## 6. Endpoints

| Local endpoint | Upstream endpoint | Notes |
| --- | --- | --- |
| `POST /v1/chat/completions` | `/inference/go/openai/v1/chat/completions` | Chat Completions, streaming and non-streaming. |
| `POST /v1/responses` | `/inference/go/openai/v1/responses` | Native Responses protocol, forwarded without conversion. |
| `GET /v1/models` | `/inference/go/v1/models` | Model list. The upstream endpoint is public, so no local key is required. |
| `GET /healthz` | local | `200 {"status":"ok"}`. |

Everything else returns `404` with a JSON error and is never sent upstream.

Methods, query strings, request bodies, status codes, error bodies and relevant response
headers are preserved. Hop-by-hop headers (`Host`, `Connection`, `Keep-Alive`, `TE`,
`Trailer`, `Transfer-Encoding`, `Upgrade`, `Proxy-*`) are not forwarded. The proxy sets its
own `User-Agent: OpenCodeGoProxy/1.0` and does not forward the caller's.

## 7. Session management

`x-opencode-session` is resolved in this order:

1. **Client header** — if the caller sends `x-opencode-session`, it is preserved verbatim.
2. **Conversation identifier** — if the caller sends `x-conversation-id` or `x-chat-id`, the
   proxy derives a deterministic ID: `ses_` + the first 32 hex characters of
   `SHA256("conversation|<header>|<value>")`. The same conversation value always maps to the
   same session, including after restarts, and different values map to different sessions.
3. **Persistent fallback** — otherwise one ID is generated once, stored in
   `%LOCALAPPDATA%\OpenCodeGoProxy\session.json`, and reused for every request and across
   restarts:

   ```json
   {"sessionId":"ses_94edc9b391409ceaaf15a81baa465c6b"}
   ```

All IDs have the form `ses_<32 lowercase hex characters>`. The upstream does not validate
the value (it only requires the header to be present), so this format is safe; the `ses_`
prefix matches the documented style.

Log lines report which rule was used (`Session source: client header`, `conversation id`,
`persistent fallback`, or `configured`). Session values themselves are not logged.

### Limitations of fallback session management

- A client that sends **no** conversation identifier shares **one** session for all its
  conversations. This can reduce upstream routing isolation and prompt-cache efficiency; it
  never changes the response content.
- The MVP does not fingerprint conversation identity from message text, does not guess, and
  does not store chat history. If the client sends no reliable identifier, the proxy cannot
  distinguish its conversations — by design.
- To separate conversations, either configure clients that support extra headers to send
  `x-conversation-id`/`x-chat-id`, or run separate proxy instances with different
  `OPENCODE_GO_SESSION_ID` values (for example one per client).
- Delete `session.json` to force a new fallback ID; it is recreated on the next request.

## 8. Authentication

Two modes, chosen automatically:

- **Mode A – forward:** no `OPENCODE_GO_API_KEY` configured. The caller's
  `Authorization: Bearer ...` (or `x-api-key`) header is forwarded unchanged. If the caller
  sends neither, the proxy returns `401` with a clear local error instead of sending a
  request that is guaranteed to fail.
- **Mode B – configured key:** `OPENCODE_GO_API_KEY` is set. Every upstream request uses
  that key; the caller's credential headers are dropped, so a local placeholder is enough.
  `x-opencode-session`-style local security still applies: the key never appears in
  responses or logs.

Upstream auth, entitlement, quota and policy errors (`401`, `403`, `429`, ...) are returned
to the client unchanged, with the original status and body.

## 9. Client configuration

Base URL for every client: `http://127.0.0.1:8765/v1`, model `deepseek-v4.1-flash`.
The API key field can be any non-empty placeholder when Mode B is used
(`OPENCODE_GO_API_KEY` set on the proxy).

### Msty Go

1. Settings → Providers → add a **Generic OpenAI** (OpenAI-compatible) provider.
2. Base URL: `http://127.0.0.1:8765/v1`.
3. API key: your real key, or a placeholder such as `local` with Mode B.
4. Model: `deepseek-v4.1-flash`.
5. Send a normal chat message; the proxy log should show
   `POST /v1/chat/completions` and `Upstream status: 200`.

Msty Go must support a custom base URL for this to work — verify that in the provider
settings before reporting a problem; if the field is missing, the app cannot be redirected
at the HTTP level.

### AnythingLLM

1. Settings → LLM Preference → provider **Generic OpenAI**.
2. Base URL: `http://127.0.0.1:8765/v1`.
3. Chat Model Name: `deepseek-v4.1-flash`.
4. API Key: your real key, or a placeholder with Mode B.
5. Save and start a new workspace chat.

AnythingLLM may call `GET /v1/models` to populate its model list; that endpoint is
proxied and works without a local key.

## 10. Testing with PowerShell

Health:

```powershell
Invoke-RestMethod http://127.0.0.1:8765/healthz
```

Model list:

```powershell
(Invoke-RestMethod http://127.0.0.1:8765/v1/models).data | Select-Object id
```

Non-streaming chat:

```powershell
$body = @{
  model    = "deepseek-v4.1-flash"
  messages = @(@{ role = "user"; content = "Say hello in five words." })
} | ConvertTo-Json -Depth 5

Invoke-RestMethod -Uri http://127.0.0.1:8765/v1/chat/completions `
  -Method Post -ContentType "application/json" -Body $body |
  Select-Object -ExpandProperty choices |
  Select-Object -ExpandProperty message
```

Streaming chat (SSE chunks appear as they arrive; `-N` disables curl buffering):

```powershell
curl.exe -N http://127.0.0.1:8765/v1/chat/completions `
  -H "Content-Type: application/json" `
  --data-binary '{\"model\":\"deepseek-v4.1-flash\",\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":\"Count to five.\"}]}'
```

Prove that the session header is what fixes the upstream call — direct request first:

```powershell
curl.exe -s -o - -w "`nHTTP %{http_code}`n" `
  https://opencode.ai/inference/go/openai/v1/chat/completions `
  -H "Content-Type: application/json" `
  -H "Authorization: Bearer $env:OPENCODE_GO_API_KEY" `
  --data-binary '{\"model\":\"deepseek-v4.1-flash\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}'
```

Without `x-opencode-session` this returns `400 MissingSessionID`. The same payload through
the proxy returns a normal completion.

## 11. Automated tests

```powershell
dotnet test OpenCodeGoProxy.sln
```

The suite (`tests/OpenCodeGoProxy.Tests`, xUnit) starts the real proxy on an ephemeral
loopback port in front of a mocked OpenAI-compatible upstream and covers:

- missing session header is added; an existing one is preserved;
- stable conversation IDs produce stable sessions, different IDs produce different ones;
- the fallback session survives a proxy restart (same store directory);
- explicit `OPENCODE_GO_SESSION_ID` is honoured;
- the caller's `Authorization` / `x-api-key` is forwarded, and a configured key overrides it;
- a missing credential produces a local `401` without contacting the upstream;
- request JSON is forwarded byte-for-byte (including whitespace and non-ASCII text);
- large (2 MB) payloads are forwarded completely; query strings are preserved;
- `chat/completions`, `responses` and `models` map to the correct upstream paths;
- upstream `400/401/403/429/500` statuses and bodies are preserved unchanged;
- SSE responses are streamed incrementally (the test blocks the upstream mid-stream and
  asserts the first chunk arrives before the rest is released), and `[DONE]` passes through;
- client cancellation aborts the upstream request;
- unsupported paths and wrong methods return `404` without touching the upstream;
- the server binds only to loopback, and non-loopback configuration is rejected;
- the session store never contains the API key.

Test logs can be enabled with `$env:PROXY_TEST_LOGS = "1"`.

## 12. Real integration test (requires a valid key)

This was **not** executed as part of this implementation because no valid OpenCode Go API
key was available in the environment. What *was* verified live on 2026-11-22:

| Check | Result |
| --- | --- |
| Direct chat completions, no session header | `400 MissingSessionID` |
| Direct chat completions, `x-opencode-session` + invalid key | `401 authentication_error` (`Invalid credential`) |
| Through the proxy with a placeholder key | `401 Invalid credential` — i.e. the session gate was passed and only the key was rejected |
| `GET /inference/go/v1/models` directly | `200`, 30 models, includes `deepseek-v4.1-flash` |
| Persistent session file | created at `%LOCALAPPDATA%\OpenCodeGoProxy\session.json` |
| API key in logs | not present |

To complete acceptance with a real key:

```powershell
$env:OPENCODE_GO_API_KEY = "oc_sk_..."
dotnet run --project src\OpenCodeGoProxy
```

1. Non-streaming chat through the proxy returns a normal completion (no `MissingSessionID`).
2. The same request sent directly still fails with `MissingSessionID` (if the upstream still
   enforces it).
3. Streaming chat yields incremental `data: {...}` chunks and a final `data: [DONE]`.
4. Two requests in a row reuse the same session ID (`Session source: persistent fallback` in
   both logs, and the same value in `session.json`).
5. The same works from Msty Go / AnythingLLM with base URL `http://127.0.0.1:8765/v1`.

Do not claim successful real-API integration until step 1–4 have actually passed.

## 13. Security notes

- Binds exclusively to `127.0.0.1`; non-loopback hosts are rejected at startup.
- Loopback is machine-wide: every local process of every user can reach the port, and the
  Windows firewall does not filter loopback traffic. In Mode B (a configured key) that means
  other local users could spend your OpenCode Go allowance. On a shared machine, prefer a
  per-user setup (Task Scheduler at logon) or add a local authentication token.
- No CORS headers are added; browsers on other origins cannot use it by default.
- TLS certificate validation for the upstream is left at .NET defaults (never disabled).
- `Authorization`, `x-api-key` and bodies are never logged; prompts and completions are
  never logged or persisted.
- Errors from the upstream (including entitlement and rate-limit errors) are passed through
  unchanged; the proxy does not bypass quotas or authorization.
- Sessions and IDs are opaque and local; no chat history is stored.
- No administrative endpoints exist.

## 14. Troubleshooting

| Symptom | Cause / fix |
| --- | --- |
| Upstream `400 MissingSessionID` | The client bypassed the proxy. The base URL must be `http://127.0.0.1:8765/v1`. Check the proxy log for the request; if absent, the client is not using it. |
| Local `401` with “No API key available” | No `OPENCODE_GO_API_KEY` and the client sent no `Authorization`/`x-api-key`. Set one of them. |
| Upstream `401 Invalid credential` | Wrong or expired API key. |
| Upstream `403 EntitlementError` | The key has no OpenCode Go subscription/allowance for that model. Use a paid/Go model such as `deepseek-v4.1-flash`. |
| Upstream `429` | Go allowance exhausted for the current 5-hour/weekly/monthly window. Check `GET https://opencode.ai/inference/go/v1/usage` with your key. |
| `502` “Cannot reach the upstream” | Network/DNS/TLS or a system proxy between you and `opencode.ai`. The proxy does not retry. |
| Streaming looks buffered | The client is buffering. With curl use `-N`; in client apps disable response buffering. The proxy flushes every chunk. |
| `Address already in use` | Another instance is running on the port. Change `Proxy:Port`. |
| `Configuration error: Proxy:Host ... is not a loopback address` | Deliberate: the proxy must stay on localhost. |
| Session store warning on startup | `%LOCALAPPDATA%\OpenCodeGoProxy` is not writable. The proxy falls back to an in-memory session that changes on restart. |
| Session file looks wrong/corrupt | Delete `%LOCALAPPDATA%\OpenCodeGoProxy\session.json`; a new ID is generated on the next request. |
| No log output | Set `"Logging": "Verbose"` in `Proxy:Logging` (or `Proxy__Logging=Verbose`). A service writes to `%ProgramData%\OpenCodeGoProxy\logs` instead of the console; check `Proxy:LogDirectory`. |
| Service stops immediately after start | Configuration error. Check `%ProgramData%\OpenCodeGoProxy\logs\startup-error.log` and `Get-EventLog -LogName Application -Source OpenCodeGoProxy -Newest 10`. |
| Service is running but the key is rejected (`401`) | User environment variables do not reach services. Set the key via `.\deploy\install-service.ps1 -ApiKey ...`, in the service `Environment` registry value, or in `appsettings.Production.json` in the install folder. |
| `Error 1053`: service did not respond in a timely fashion | The exe was started as a service without SCM support - use `deploy\install-service.ps1` and a build that includes `Microsoft.Extensions.Hosting.WindowsServices`. |

## 15. Publishing a self-contained Windows executable

```powershell
dotnet publish src\OpenCodeGoProxy -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true
```

Run it:

```powershell
$env:OPENCODE_GO_API_KEY = "oc_sk_..."
.\src\OpenCodeGoProxy\bin\Release\net10.0\win-x64\publish\OpenCodeGoProxy.exe
```

The publish folder contains `appsettings.json`; edit that copy (or rebuild after editing the
root file). No WSL, Docker, IIS or extra services are required, and no administrator rights
are needed. Do not enable trimming for this app.

To install the published build as a Windows service, continue with §16.

## 16. Installing as a Windows service

The proxy can run as a native Windows service: available at boot, no console window, no
logged-on user required. Clients keep using `http://127.0.0.1:8765/v1`; nothing changes on
their side.

### 16.1 Publish

```powershell
dotnet publish .\src\OpenCodeGoProxy\OpenCodeGoProxy.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true
```

### 16.2 Install

From an **elevated** PowerShell session:

```powershell
cd C:\dev\opencodegoproxy
.\deploy\install-service.ps1 -ApiKey 'oc_sk_...'
```

The script:

- copies the publish output to `C:\Program Files\OpenCodeGoProxy`;
- creates `C:\ProgramData\OpenCodeGoProxy` (session store) and `...\logs`;
- registers the `OpenCodeGoProxy` Application event log source;
- creates the service with **Automatic (Delayed Start)** and restart-on-failure actions
  (5 s, 30 s, 60 s; failure counter reset daily);
- writes service-scoped environment variables (`OPENCODE_GO_API_KEY`,
  `Proxy__SessionStorePath`, `Proxy__LogDirectory`) under the service's registry key - user
  environment variables are *not* inherited by services;
- starts the service and probes `/healthz`.

A key you set as a **user** environment variable (for the console build) is therefore not
enough for the service. Either pass `-ApiKey 'oc_sk_...'` (or `-ApiKey $env:OPENCODE_GO_API_KEY`
when the elevated shell inherited your user environment), or put the key in
`appsettings.Production.json` in the install folder. Symptom when it is missing: clients that
send no credential of their own get a local `401` "No API key available".

Useful parameters: `-ServiceAccount LocalService` (least privilege; the script grants the
required ACLs), `-InstallPath`, `-DataPath`, `-SkipHealthCheck`, `-Force` (replace an
existing service), `-WhatIf` (print the plan, change nothing).

The health probe targets `http://127.0.0.1:8765/healthz`; if you changed `Proxy:Port`, pass
`-SkipHealthCheck` (the service itself is installed either way).

Prefer not to pass the key on the command line? Put it in
`C:\Program Files\OpenCodeGoProxy\appsettings.Production.json`; that file is loaded
automatically and preserved by reinstalls:

```json
{ "Proxy": { "ApiKey": "oc_sk_..." } }
```

Restart the service after editing it. The key is never written to the log files.

### 16.3 Logs

A service has no console, so the proxy writes rolling files to
`C:\ProgramData\OpenCodeGoProxy\logs`:

| File | Content |
| --- | --- |
| `proxy-yyyyMMdd.log` | One file per UTC day: proxy requests, session source, upstream status, errors with stack traces, host lifecycle. |
| `proxy-yyyyMMdd.N.log` | The same day rolled over after 10 MB (`.1`, `.2`, ...). |
| `startup-error.log` | Configuration errors that prevented the service from starting. |

Files older than 7 days are removed at startup. Request bodies, `Authorization`, `x-api-key`
and prompts are never logged. The Windows event log receives only Warning and worse, so it
is not flooded by the four informational lines per proxied request.

Override the directory with `Proxy:LogDirectory` (`Proxy__LogDirectory`). In console mode no
file log is written unless that setting is present.

### 16.4 Verify, upgrade, remove

```powershell
Get-Service OpenCodeGoProxy
Invoke-RestMethod http://127.0.0.1:8765/healthz
Get-ChildItem C:\ProgramData\OpenCodeGoProxy\logs\proxy-*.log |
  Sort-Object LastWriteTime | Select-Object -Last 1 | Get-Content -Tail 50
```

- **Upgrade**: re-run `.\deploy\install-service.ps1 -Force` (add `-ApiKey` unless the key
  lives in `appsettings.Production.json`; the service `Environment` value is rewritten as a
  whole). `appsettings.Production.json` in the install folder is preserved.
- **Remove**: `.\deploy\uninstall-service.ps1` (`-KeepData` keeps `session.json` and the
  logs, `-KeepEventSource` keeps the event log source). Safe to re-run.

### 16.5 Service-mode security notes

- The service listens on loopback only, exactly like the console build; no firewall rule is
  needed and none would apply.
- Loopback is machine-wide: any local process of any user can reach the port. In Mode B (a
  configured key) that means other local users could spend your allowance - see §13.
- A service-scoped key sits in
  `HKLM\SYSTEM\CurrentControlSet\Services\OpenCodeGoProxy\Environment` (administrators only)
  or in `appsettings.Production.json` (restrict its ACL to the service account).
- Prefer `-ServiceAccount LocalService` over `LocalSystem` unless something requires SYSTEM.

## 17. Assumptions and deviations

These decisions were made where the specification left room; each is cheap to change.

1. **`appsettings.json` location.** The single config file lives at the workspace root and is
   copied next to the executable; the app uses the executable directory as content root.
   Environment variables still win over `appsettings.json`.
2. **No `Model:` log line.** Printing the model would require buffering and parsing the
   request body, which conflicts with “preserve bodies without JSON reserialization” and
   “support large request bodies”. Request bodies are never read by the proxy logic.
3. **Local `401` when no credential exists at all.** Instead of forwarding a request that
   cannot succeed, the proxy returns a concise JSON error (`proxy_error`).
4. **Own `User-Agent`.** The proxy identifies itself as `OpenCodeGoProxy/1.0`; it does not
   impersonate the official CLI and sends no `x-opencode-client` header.
5. **Session format.** `ses_` + 32 lowercase hex characters. Upstream accepts any opaque
   value (verified: a plain string also passes the gate), so this is a safe, stable choice.
6. **`SessionMode`.** Only `Auto` is implemented; other values warn and behave as `Auto`.
7. **Models endpoint.** It is public upstream, so the local endpoint does not require a key.
   Credentials are still forwarded when present/configured.
8. **Conversation identity.** Only `x-conversation-id` and `x-chat-id` are recognised; there
   is no fingerprinting and no history storage.
9. **Header precedence.** If both `Authorization` and `x-api-key` are supplied, only
   `Authorization` is forwarded (unless a configured key replaces both).
10. **`x-opencode-session` on models.** The header is also attached to `GET /v1/models`;
    the upstream ignores it, and it keeps the session logic uniform.
11. **No retries.** Upstream failures are returned immediately to avoid duplicate model
    generations.
12. **No response decompression.** Compressed bodies pass through byte-exact; the client
    decodes them as before.
13. **Request body limit.** Kestrel's default 30 MB cap is raised to 256 MB for large
    prompts; bodies are still streamed and never buffered by the proxy.
14. **`Proxy:SessionId`** is accepted as a config-key alias for
    `OPENCODE_GO_SESSION_ID` (convenient for tests); the environment variable wins.
15. **Windows service support.** `AddWindowsService` is registered unconditionally but only
    activates when the process was started by the SCM, so `dotnet run` and the tests are
    unaffected. Service mode defaults the session store and log directory to
    `%ProgramData%\OpenCodeGoProxy` because a service account has no usable `LOCALAPPDATA`.
16. **File logging** is a small in-repo provider (`Logging/FileLoggerProvider.cs`) instead of
    a third-party sink, keeping the dependency surface at one package. It is inert in
    console mode unless `Proxy:LogDirectory` is configured, and it never throws into the
    request path.
