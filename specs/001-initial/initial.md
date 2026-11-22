\# Specification: OpenCode Go Local API Proxy



\## 1. Objective



Build a lightweight, reliable, local HTTP reverse proxy that allows third-party AI applications to communicate with OpenCode Go's OpenAI-compatible API.



The primary problem is that some third-party applications, including Msty Go, fail with:



```text

HTTP 400: MissingSessionID

Request is missing x-opencode-session

```



The proxy must resolve this interoperability problem without requiring modifications to the client applications.



\*\*Primary objective: Fix the missing session header with the smallest possible implementation.\*\*



Avoid unnecessary abstractions, frameworks, dependencies, or functionality.



\## 2. Technology



\- Language: C#

\- Framework: .NET 10 / ASP.NET Core Minimal API

\- Platform: Windows 11

\- HTTP client: `HttpClient` using `IHttpClientFactory`

\- Dependencies: Prefer built-in .NET functionality

\- Configuration: `appsettings.json` and environment variables

\- Logging: Microsoft.Extensions.Logging

\- Tests: xUnit

\- File encoding: UTF-8

\- Line endings: CRLF



Do not use Docker, WSL, IIS, YARP, LiteLLM, or any external reverse proxy.



The application must run directly on Windows using `dotnet run` or a published executable.



\## 3. Architecture



```text

Msty Go / AnythingLLM / Other OpenAI-compatible clients

&#x20;                      |

&#x20;                      | HTTP

&#x20;                      v

&#x20;            127.0.0.1:8765

&#x20;                      |

&#x20;            OpenCodeGoProxy

&#x20;                      |

&#x20;            Add session header

&#x20;            Forward HTTP request

&#x20;            Stream HTTP response

&#x20;                      |

&#x20;                      | HTTPS

&#x20;                      v

&#x20;  https://opencode.ai/inference/go/openai/v1

&#x20;                      |

&#x20;                      v

&#x20;              OpenCode Go

```



The proxy must not modify prompts, model parameters, tool definitions, or response content.



It must not pretend to be the official OpenCode client.



\## 4. API Compatibility



Expose a local OpenAI-compatible API at:



```text

http://127.0.0.1:8765/v1

```



Initial endpoints:



| Local endpoint | Upstream endpoint |

|---|---|

| POST `/v1/chat/completions` | `/inference/go/openai/v1/chat/completions` |

| POST `/v1/responses` | `/inference/go/openai/v1/responses` |

| GET `/v1/models` | `/inference/go/v1/models` |

| GET `/healthz` | Local health endpoint |



Upstream host:



```text

https://opencode.ai

```



The proxy must preserve the original JSON request and response bodies.



Do not implement unsupported protocol translation.



Support OpenAI Chat Completions first. The Responses endpoint must forward its native protocol without conversion.



Do not implement Anthropic or Gemini protocol conversion in the MVP.



\## 5. Session Management



This is the primary feature.



OpenCode Go expects:



```http

x-opencode-session: ses\_...

```



\### 5.1 Session resolution



Use the following priority:



1\. If the client already supplies `x-opencode-session`, preserve it.

2\. If the client supplies a stable conversation identifier through a supported header, generate a deterministic session identifier based on that value.

3\. Otherwise, use a generated, persistent fallback session identifier for that client.



Examples of possible client conversation headers:



```text

x-conversation-id

x-chat-id

```



These are optional inputs. Do not assume every application supplies them.



Use an opaque identifier such as:



```text

ses\_<32-character-hex-identifier>

```



Verify that the generated format is accepted by the real OpenCode Go endpoint.



\### 5.2 Persistent fallback sessions



Some applications do not provide conversation identifiers.



For those applications:



\- Generate a session ID once.

\- Persist it locally.

\- Reuse it across requests and application restarts.

\- Allow explicitly configured session IDs.

\- Avoid generating a new ID for every request.



A single fallback session shared across multiple conversations is an MVP compromise. It may reduce routing isolation or caching efficiency.



Do not claim to distinguish conversations when the client provides no reliable identifier.



Do not infer conversation identity from arbitrary message text in the MVP.



\### 5.3 Future improvement



Support per-conversation session tracking when clients expose reliable conversation identifiers.



Do not implement complex conversation fingerprinting or persistent chat history storage in the MVP.



\## 6. Authentication



Support two authentication modes.



\### Mode A: Forward authentication



Forward the caller's existing:



```http

Authorization: Bearer <API\_KEY>

```



\### Mode B: Configured API key



Allow an upstream API key to be configured using:



```text

OPENCODE\_GO\_API\_KEY

```



When configured, the proxy uses this key for upstream authentication.



This allows third-party clients that require an API key field to use a harmless placeholder locally.



The upstream API key must never appear in responses or logs.



Do not hardcode credentials.



Do not require administrator privileges.



\## 7. HTTP Forwarding



The proxy must:



\- Preserve request methods and supported paths.

\- Preserve request bodies without JSON reserialization.

\- Preserve query strings.

\- Forward relevant request headers.

\- Add the required OpenCode session header.

\- Preserve upstream status codes.

\- Preserve relevant response headers.

\- Return upstream error bodies unchanged.

\- Support large request bodies.

\- Respect client disconnects and cancellation.



Handle HTTP hop-by-hop headers correctly.



Do not blindly forward `Host`, `Connection`, `Transfer-Encoding`, or other connection-specific headers.



The upstream destination must be fixed and configurable only through trusted local configuration.



Prevent arbitrary URL forwarding or SSRF.



\## 8. Streaming



Streaming support is mandatory.



Support:



```json

{

&#x20; "stream": true

}

```



Requirements:



\- Use `HttpCompletionOption.ResponseHeadersRead`.

\- Forward SSE responses without buffering the complete response.

\- Preserve chunk order.

\- Flush data incrementally.

\- Preserve `text/event-stream`.

\- Correctly handle `\[DONE]` when present.

\- Do not rewrite streaming event data.

\- Propagate cancellation when the client disconnects.



Do not implement streaming by collecting the complete response and returning it afterward.



Test both streaming and non-streaming requests.



\## 9. Configuration



Example:



```json

{

&#x20; "Proxy": {

&#x20;   "Host": "127.0.0.1",

&#x20;   "Port": 8765,

&#x20;   "UpstreamBaseUrl": "https://opencode.ai/inference/go",

&#x20;   "SessionMode": "Auto",

&#x20;   "Logging": "Minimal"

&#x20; }

}

```



The actual API key must be provided through an environment variable or another secure configuration source.



Store persistent session identifiers in the Windows user profile, for example under:



```text

%LOCALAPPDATA%\\OpenCodeGoProxy\\

```



Do not store API keys in the session configuration file.



\## 10. Security



The proxy is intended for local, single-user use.



Requirements:



\- Bind exclusively to `127.0.0.1`.

\- Do not expose the proxy on the LAN.

\- Do not enable permissive CORS by default.

\- Do not disable HTTPS certificate validation.

\- Do not log API keys.

\- Do not log prompts or completion contents.

\- Do not persist chat conversations.

\- Do not expose administrative endpoints.

\- Do not implement quota bypasses, request fingerprint spoofing, or authentication circumvention.



The proxy must preserve normal OpenCode Go usage limits and upstream authorization rules.



If the upstream rejects a request for entitlement or policy reasons, return that error transparently.



\## 11. Logging



Use concise structured logging.



Example:



```text

INFO  Proxy started on 127.0.0.1:8765

INFO  POST /v1/chat/completions

INFO  Model: deepseek-v4.1-flash

INFO  Session source: persistent fallback

INFO  Upstream status: 200

INFO  Duration: 1842ms

```



Never log sensitive request contents.



Support configurable logging levels.



Provide useful error messages for:



\- Missing API key

\- Upstream authentication failures

\- Upstream rate limits

\- Upstream connection failures

\- Invalid upstream session header

\- Client cancellation



Do not silently retry requests that may produce duplicate model generations.



\## 12. MVP



Implement only:



1\. ASP.NET Core Minimal API server.

2\. Localhost binding.

3\. Chat Completions proxying.

4\. Responses proxying.

5\. Model listing.

6\. Automatic session header injection.

7\. Persistent fallback session ID.

8\. Environment-variable API key configuration.

9\. Transparent error forwarding.

10\. Streaming support.

11\. Minimal logging.

12\. Health endpoint.



Do not add a GUI, Windows Service, installer, database, or management dashboard.



\## 13. Testing



Provide automated tests for:



\- Missing session header is added.

\- Existing session header is preserved.

\- A stable conversation ID produces a stable session ID.

\- Different conversation IDs produce different session IDs.

\- Fallback session ID survives application restart.

\- Authentication is forwarded correctly.

\- Configured authentication overrides caller credentials.

\- JSON payload is unchanged.

\- Streaming is forwarded incrementally.

\- Upstream HTTP 400/401/403/429/500 responses are preserved.

\- Client cancellation terminates the upstream request.

\- Unsupported paths are rejected.

\- The server binds only to localhost.



Use a mocked upstream HTTP server for automated tests.



\### Real integration test



After automated tests pass, test against the actual OpenCode Go endpoint with a valid user-provided API key.



Use:



```text

Model: deepseek-v4.1-flash

```



Verify:



1\. A request without an explicit session header succeeds through the proxy.

2\. The same request fails directly with `MissingSessionID`, if the upstream still enforces that requirement.

3\. Non-streaming chat works.

4\. Streaming chat works.

5\. The session ID remains stable across multiple requests.



Do not claim successful integration until the real endpoint test has passed.



\## 14. Third-party Client Testing



Test with Msty Go using its Generic OpenAI provider if it supports a custom Base URL.



Configuration:



```text

Provider: Generic OpenAI

Base URL: http://127.0.0.1:8765/v1

Model: deepseek-v4.1-flash

API Key: configured local placeholder or actual key

```



The expected result is a successful normal chat response without `MissingSessionID`.



Also document how to configure AnythingLLM using its Generic OpenAI provider.



Do not assume a specific client supports custom Base URLs without verifying that capability.



\## 15. Deliverables



Produce:



```text

OpenCodeGoProxy/

&#x20; src/

&#x20;   OpenCodeGoProxy/

&#x20; tests/

&#x20;   OpenCodeGoProxy.Tests/

&#x20; appsettings.json

&#x20; README.md

&#x20; OpenCodeGoProxy.sln

```



The README must document:



\- Prerequisites

\- Build instructions

\- Run instructions

\- API key configuration

\- Client configuration

\- Testing with PowerShell

\- Troubleshooting

\- Limitations of fallback session management

\- Publishing a self-contained Windows executable



The executable should run without requiring WSL, Docker, or additional services.



\## 16. Acceptance Criteria



The implementation is complete when:



\- The proxy starts on Windows 11.

\- An OpenAI-compatible client can connect to `localhost:8765`.

\- The proxy automatically supplies `x-opencode-session`.

\- Requests successfully reach OpenCode Go.

\- DeepSeek Flash returns normal responses.

\- SSE streaming works correctly.

\- Sessions are stable across requests.

\- Credentials remain protected.

\- The implementation passes its automated tests.



\## 17. Implementation Instructions



Implement the complete MVP in the current workspace, using a standalone `OpenCodeGoProxy` directory if the workspace already contains unrelated projects.



Follow these rules:



\- Prefer the simplest working implementation.

\- Do not modify unrelated projects or configuration.

\- Do not introduce unnecessary NuGet dependencies.

\- Do not implement speculative functionality.

\- Do not rewrite JSON unnecessarily.

\- Do not sacrifice streaming support.

\- Document every assumption.

\- Verify OpenCode's current API requirements against its official documentation.

\- Build and run the automated tests.

\- Report any tests that could not be executed.

\- Do not claim successful real API integration without performing that test.



\*\*Start implementation immediately. Do not stop after generating a plan.\*\*

