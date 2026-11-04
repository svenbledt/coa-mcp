using System.Text;
using Microsoft.Net.Http.Headers;

namespace CoaMcp.Server;

/// <summary>
/// Answers POST /mcp requests the SDK would throw on while reading the body. Its JSON reader throws for any content type it can't take: a missing one, one it can't parse, or an unknown charset.
/// That would mean a 500 and an error in the log for every anonymous request. Nothing here reads the body, so the size limits still apply to whatever goes on.
/// </summary>
public static class McpRequestGuard
{
    private static readonly byte[] Refusal = Encoding.UTF8.GetBytes(
        "{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{\"code\":-32600,\"message\":\"Send one JSON-RPC message as the body, with Content-Type: application/json.\"}}");

    public static async Task Run(HttpContext ctx, Func<Task> next)
    {
        if (HttpMethods.IsPost(ctx.Request.Method) && ctx.Request.Path.StartsWithSegments("/mcp") && !Readable(ctx.Request))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.Body.WriteAsync(Refusal, ctx.RequestAborted);
            return;
        }
        await next();
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
