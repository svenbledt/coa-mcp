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
    private static byte[] Error(string message) => Encoding.UTF8.GetBytes($"{{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{{\"code\":-32600,\"message\":\"{message}\"}}}}");

    private static readonly byte[] Unreadable = Error("Send one JSON-RPC message as the body, with Content-Type: application/json.");
    private static readonly byte[] TooLarge = Error("The request body is too large.");
    private static readonly byte[] BadBody = Error("The request body could not be read.");

    private static async Task Refuse(HttpContext ctx, int status, byte[] body)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength = body.Length;
        await ctx.Response.Body.WriteAsync(body, CancellationToken.None);
    }

    public static async Task Run(HttpContext ctx, Func<Task> next)
    {
        if (HttpMethods.IsPost(ctx.Request.Method) && ctx.Request.Path.StartsWithSegments("/mcp") && !Readable(ctx.Request))
        {
            await Refuse(ctx, StatusCodes.Status400BadRequest, Unreadable);
            return;
        }

        try
        {
            await next();
        }
        catch (BadHttpRequestException ex) when (!ctx.Response.HasStarted)
        {
            ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Requests").LogInformation("A request body was refused with status {Status}.", ex.StatusCode);
            await Refuse(ctx, ex.StatusCode, ex.StatusCode == StatusCodes.Status413PayloadTooLarge ? TooLarge : BadBody);
        }
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
