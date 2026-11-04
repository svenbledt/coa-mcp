using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace CoaMcp.Server;

/// <summary>The one shared bearer token. It comes from the environment and is never logged, stored or echoed.</summary>
public sealed class TokenAuth
{
    public const string EnvVar = "COA_MCP_BEARER_TOKEN";
    public const string Scheme = "CoaBearer";
    public const int MinChars = 32;

    private readonly string? _token;
    private readonly byte[]? _hash;

    public TokenAuth(string? configured)
    {
        var token = configured?.Trim();
        if (token is not { Length: >= MinChars }) return;
        _token = token;
        _hash = Hash(token);
    }

    public bool Enabled => _hash is not null;

    private static byte[] Hash(string s) => SHA256.HashData(Encoding.UTF8.GetBytes(s));

    /// <summary>Constant time over hashes, so neither the length nor a matching prefix can be timed.</summary>
    public bool Matches(string presented)
    {
        var hash = Hash(presented);
        return _hash is not null && CryptographicOperations.FixedTimeEquals(hash, _hash);
    }

    /// <summary>Catches a token pasted into a write, which would otherwise end up in the database and in public answers.</summary>
    public bool AppearsIn(string? text) => _token is not null && text is not null && text.Contains(_token, StringComparison.Ordinal);

    public void LogState(ILogger log)
    {
        if (!Enabled) log.LogWarning("Writes are disabled: {Variable} is unset or under {Min} characters. Reads are unaffected.", EnvVar, MinChars);
    }
}

/// <summary>Authenticates a valid bearer token. Anything else gives NoResult, never a failure, so the request carries on as an anonymous read.</summary>
public sealed class TokenAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, TokenAuth token)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private const string Prefix = "Bearer ";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization;
        if (header.Count != 1 || header[0] is not { } value || !value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) || !token.Matches(value[Prefix.Length..]))
            return Task.FromResult(AuthenticateResult.NoResult());

        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "token-holder") }, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
