using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CoaMcp.Server;

#pragma warning disable MCPEXP002   // The only filter family that wraps the SDK's authorization check. An upgrade that changes it fails the build, so it can't change unnoticed.

/// <summary>
/// Answers refused tool calls before the SDK logs them. The endpoint is public, so a call that is forbidden, invalid or badly shaped is the caller's mistake,
/// and the SDK would log a stack trace for each one. The caller still gets the refusal. Every other exception is a real fault, and carries on to the SDK's log and its generic text.
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
        catch (Exception ex) when (ex is ArgumentException or JsonException && IsFromBinding(ex))
        {
            return Refusal(BadArguments);
        }
    };

    /// <summary>
    /// The SDK binding arguments never reaches this server's code. The same exception types from anywhere in here are a real fault:
    /// damaged data or a bug, which must go on to the SDK's error log and not be blamed on the caller.
    /// </summary>
    private static bool IsFromBinding(Exception ex) =>
        !new StackTrace(ex).GetFrames().Any(frame => frame.GetMethod()?.DeclaringType is { } type && type.Assembly == typeof(RefusedCalls).Assembly && !IsThisFilter(type));

    private static bool IsThisFilter(Type type)
    {
        while (type.DeclaringType is { } outer) type = outer;
        return type == typeof(RefusedCalls);
    }

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
