using System.Text;
using Microsoft.Net.Http.Headers;

namespace CoaMcp.Server;

/// <summary>
/// Answers POST /mcp requests the SDK would throw on while reading the body. Its JSON reader throws for any content type it can't take: a missing one, one it can't parse, or an unknown charset.
/// That would mean a 500 and an error in the log for every anonymous request. The first check here doesn't read the body. Reading it comes later, in the SDK, where Kestrel enforces the size limits
/// and throws for an oversized or malformed body: the status it chose goes back to the caller with a short error, and the log gets one short line, because a caller can send these at will.
/// </summary>
public static class McpRequestGuard
{
    private static byte[] Error(string message, int code = -32600) => Encoding.UTF8.GetBytes($"{{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{{\"code\":{code},\"message\":\"{message}\"}}}}");

    private static readonly byte[] Unreadable = Error("Send one JSON-RPC message as the body, with Content-Type: application/json.");
    private static readonly byte[] TooLarge = Error("The request body is too large.");
    private static readonly byte[] BadBody = Error("The request body could not be read.");
    private static readonly byte[] Busy = Error("The server is busy. Retry in a few seconds.", -32000);
    private static readonly byte[] TooMany = Error("Too many requests from this client. Retry shortly.", -32000);
    private static readonly byte[] TooSlow = Error("The request body took too long to arrive.");

    /// <summary>How long a whole /mcp request body may take to arrive. MCP clients send a small body at once. Config Request:BodySeconds overrides it.</summary>
    public const int DefaultBodySeconds = 10;

    /// <summary>The answer when a per-client limit refuses a request.</summary>
    public static ValueTask RefuseTooMany(HttpContext ctx) => new(Refuse(ctx, StatusCodes.Status429TooManyRequests, TooMany));

    /// <summary>The answer when the concurrency limit on /mcp refuses a request.</summary>
    public static ValueTask RefuseBusy(HttpContext ctx) => new(Refuse(ctx, StatusCodes.Status503ServiceUnavailable, Busy));

    private static async Task Refuse(HttpContext ctx, int status, byte[] body)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength = body.Length;
        await ctx.Response.Body.WriteAsync(body, CancellationToken.None);
    }

    public static async Task Run(HttpContext ctx, TimeSpan bodyLimit, Func<Task> next)
    {
        var isCall = HttpMethods.IsPost(ctx.Request.Method) && ctx.Request.Path.StartsWithSegments("/mcp");
        if (isCall && !Readable(ctx.Request))
        {
            await Refuse(ctx, StatusCodes.Status400BadRequest, Unreadable);
            return;
        }

        try
        {
            if (isCall && !await ReadBody(ctx, bodyLimit)) return;
            await next();
        }
        catch (BadHttpRequestException ex) when (!ctx.Response.HasStarted)
        {
            ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Requests").LogInformation("A request body was refused with status {Status}.", ex.StatusCode);
            await Refuse(ctx, ex.StatusCode, ex.StatusCode == StatusCodes.Status413PayloadTooLarge ? TooLarge : BadBody);
        }
    }

    /// <summary>
    /// Reads the body here, under a time limit, because a request holds its concurrency permits while the body arrives. Kestrel's minimum data rate alone would let a slow sender keep one for minutes.
    /// Returns false when the request has already been answered, or its client is gone.
    /// </summary>
    private static async Task<bool> ReadBody(HttpContext ctx, TimeSpan limit)
    {
        var reader = ctx.Request.BodyReader;
        using var deadline = new CancellationTokenSource(limit);
        // Not a cancellation token on the read: Kestrel then fails to drain the body afterwards, and logs that as an error.
        using var stop = deadline.Token.Register(reader.CancelPendingRead);
        var body = new MemoryStream();
        ctx.Response.RegisterForDispose(body);
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(ctx.RequestAborted);
                foreach (var part in read.Buffer) body.Write(part.Span);
                reader.AdvanceTo(read.Buffer.End);
                if (read.IsCanceled)
                {
                    ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Requests").LogInformation("A request body did not arrive within {Seconds} s.", limit.TotalSeconds);
                    ctx.Response.Headers.Connection = "close";
                    await Refuse(ctx, StatusCodes.Status408RequestTimeout, TooSlow);
                    return false;
                }
                if (read.IsCompleted) break;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException && ctx.RequestAborted.IsCancellationRequested)
        {
            return false;   // the client left; there is nobody to answer
        }
        body.Position = 0;
        ctx.Request.Body = body;
        return true;
    }

    private static bool Readable(HttpRequest request)
    {
        // The same two tests the reader makes, so what passes here can't make it throw.
        return request.HasJsonContentType() && MediaTypeHeaderValue.TryParse(request.ContentType, out var type) && KnownCharset(type.Charset.Value);
    }

    private static bool KnownCharset(string? charset)
    {
        if (string.IsNullOrEmpty(charset)) return true;
        try
        {
            Encoding.GetEncoding(charset);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
