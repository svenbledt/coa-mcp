using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CoaMcp.Server;

#pragma warning disable MCPEXP002   // The only filter family that wraps the SDK's authorization check. An upgrade that changes it fails the build, so it can't change unnoticed.

/// <summary>
/// Answers refused tool calls before the SDK logs them. The endpoint is public, so a call that is forbidden, invalid or badly shaped is the caller's mistake,
/// and the SDK would log a stack trace for each one. The caller still gets the refusal. Any other exception is a real fault, and carries on to the SDK's log.
/// </summary>
public static class RefusedCalls
{
    public const string BadArguments = "The arguments don't fit this tool. Check its parameters.";

    /// <summary>The ordinary pipeline, which runs inside the catch where the SDK logs what tools throw: refusals from tools themselves and from binding their arguments.</summary>
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> InsideTool(McpRequestHandler<CallToolRequestParams, CallToolResult> next) => async (context, cancellationToken) =>
    {
        try
        {
            return await next(context, cancellationToken);
        }
        catch (McpException ex)
        {
            return Refusal(ex.Message);
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException)
        {
            // Almost always the SDK binding arguments. One line and no stack trace, but kept, in case it is a bug in a tool. The message goes no further than the log.
            context.Services?.GetService<ILoggerFactory>()?.CreateLogger("Tools").LogInformation("A tool call had bad arguments: {Problem}", ex.Message);
            return Refusal(BadArguments);
        }
    };

    /// <summary>The alternate-result pipeline, the only one that wraps the SDK's authorization check, which refuses a call by throwing.</summary>
    public static async ValueTask<ResultOrAlternate<CallToolResult>> AroundAuthorization(
        RequestContext<CallToolRequestParams> context,
        McpRequestHandler<CallToolRequestParams, ResultOrAlternate<CallToolResult>> next,
        CancellationToken cancellationToken)
    {
        try
        {
            return await next(context, cancellationToken);
        }
        catch (McpException ex)
        {
            return new ResultOrAlternate<CallToolResult>(Refusal(ex.Message));
        }
    }

    private static CallToolResult Refusal(string message) =>
        new() { IsError = true, Content = new List<ContentBlock> { new TextContentBlock { Text = message } } };
}
