using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OpenCodeGoProxy;

public enum SessionSource
{
    /// <summary>The caller supplied x-opencode-session; it is preserved verbatim.</summary>
    ClientHeader,

    /// <summary>Derived deterministically from a client conversation identifier header.</summary>
    ConversationId,

    /// <summary>The stable fallback session loaded from (or created in) the local session store.</summary>
    PersistentFallback,

    /// <summary>Explicitly configured with OPENCODE_GO_SESSION_ID / Proxy:SessionId.</summary>
    Configured,
}

public readonly record struct SessionResolution(string SessionId, SessionSource Source)
{
    public string SourceDescription => Source switch
    {
        SessionSource.ClientHeader => "client header",
        SessionSource.ConversationId => "conversation id",
        SessionSource.Configured => "configured",
        _ => "persistent fallback",
    };
}

/// <summary>
/// Resolves the x-opencode-session value using the documented priority:
/// client header -> deterministic conversation ID -> persistent fallback.
/// </summary>
public sealed class SessionResolver
{
    public const string SessionHeader = "x-opencode-session";

    /// <summary>Optional client conversation identifier headers, checked in order.</summary>
    public static readonly string[] ConversationHeaders = ["x-conversation-id", "x-chat-id"];

    private const string StoreFileName = "session.json";

    private readonly ProxyOptions _options;
    private readonly ILogger<SessionResolver> _logger;
    private readonly string _storeFile;
    private readonly object _gate = new();
    private string? _fallback;

    public SessionResolver(ProxyOptions options, ILogger<SessionResolver> logger)
    {
        _options = options;
        _logger = logger;
        _storeFile = Path.Combine(options.SessionStorePath, StoreFileName);
    }

    public string StoreFile => _storeFile;

    public SessionResolution Resolve(IHeaderDictionary headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        if (headers.TryGetValue(SessionHeader, out var provided) && !string.IsNullOrWhiteSpace(provided.ToString()))
        {
            return new SessionResolution(provided.ToString().Trim(), SessionSource.ClientHeader);
        }

        foreach (var headerName in ConversationHeaders)
        {
            if (headers.TryGetValue(headerName, out var conversation) && !string.IsNullOrWhiteSpace(conversation.ToString()))
            {
                var conversationId = conversation.ToString().Trim();
                return new SessionResolution(
                    CreateSessionId($"conversation|{headerName}|{conversationId}"),
                    SessionSource.ConversationId);
            }
        }

        return GetFallback();
    }

    /// <summary>Deterministic, opaque session identifier (ses_ + 32 lowercase hex characters).</summary>
    public static string CreateSessionId(string seed)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return "ses_" + Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }

    private SessionResolution GetFallback()
    {
        if (_options.SessionId is { Length: > 0 } configured)
        {
            return new SessionResolution(configured, SessionSource.Configured);
        }

        lock (_gate)
        {
            if (_fallback is not null)
            {
                return new SessionResolution(_fallback, SessionSource.PersistentFallback);
            }

            var stored = TryReadStore();
            if (stored is not null)
            {
                _fallback = stored;
                return new SessionResolution(stored, SessionSource.PersistentFallback);
            }

            var created = CreateSessionId($"fallback|{Guid.NewGuid():N}");
            _fallback = created;
            TryWriteStore(created);
            _logger.LogInformation("Created persistent fallback session in {SessionStore}", _storeFile);
            return new SessionResolution(created, SessionSource.PersistentFallback);
        }
    }

    private string? TryReadStore()
    {
        try
        {
            if (!File.Exists(_storeFile))
            {
                return null;
            }

            using var stream = File.OpenRead(_storeFile);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.TryGetProperty("sessionId", out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                var sessionId = value.GetString();
                if (!string.IsNullOrWhiteSpace(sessionId))
                {
                    return sessionId.Trim();
                }
            }

            _logger.LogWarning("Session store {SessionStore} does not contain a usable sessionId; creating a new one.", _storeFile);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning("Could not read session store {SessionStore}: {Error}. A session will be generated for this run.", _storeFile, ex.Message);
            return null;
        }
    }

    private void TryWriteStore(string sessionId)
    {
        try
        {
            Directory.CreateDirectory(_options.SessionStorePath);
            var temporaryFile = _storeFile + ".tmp";
            var payload = JsonSerializer.Serialize(new SessionStoreDocument { SessionId = sessionId });
            File.WriteAllText(temporaryFile, payload, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryFile, _storeFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not persist session store {SessionStore}: {Error}. The session stays stable only for this process.", _storeFile, ex.Message);
        }
    }

    private sealed class SessionStoreDocument
    {
        [System.Text.Json.Serialization.JsonPropertyName("sessionId")]
        public string SessionId { get; init; } = string.Empty;
    }
}
