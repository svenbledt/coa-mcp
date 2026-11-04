using System.Globalization;
using CoaMcp.Core;

namespace CoaMcp.Pipeline;

/// <summary>
/// A layer's version names its file on the server, and a published version never changes. Each build therefore gets
/// the UTC build time (yyyyMMddHHmmss) unless <c>--version</c> says otherwise, so a rebuild can always be published.
/// </summary>
public static class LayerVersion
{
    public static string Resolve(string? requested)
    {
        var version = requested ?? DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        if (!Manifest.IsSafeVersion(version))
            throw new InvalidOperationException($"'{version}' is not a usable version: use letters, digits, '.', '_' and '-', starting with a letter or digit.");
        return version;
    }
}
