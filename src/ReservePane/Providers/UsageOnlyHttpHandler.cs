using System.Globalization;
using System.Net.Http;

namespace ReservePane.Providers;

internal sealed class UsageOnlyHttpHandler(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
{
    private const string MemberBudgetPrefix = "/console/api/budgets/users/";

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        return base.SendAsync(request, cancellationToken);
    }

    protected override HttpResponseMessage Send(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        return base.Send(request, cancellationToken);
    }

    private static void ValidateRequest(HttpRequestMessage request)
    {
        if (request.Content is not null ||
            request.RequestUri is not { IsAbsoluteUri: true } uri ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 ||
            uri.Fragment.Length != 0 ||
            !IsAllowedEndpoint(request.Method, uri))
        {
            throw new InvalidOperationException("Only approved provider usage and account metadata requests are allowed.");
        }
    }

    private static bool IsAllowedEndpoint(HttpMethod method, Uri uri)
    {
        if (method == HttpMethod.Post)
        {
            return uri.Host == "ollama.com" &&
                uri.AbsolutePath == "/api/me" && IsTimestampQuery(uri.Query);
        }

        return method == HttpMethod.Get && IsAllowedGetEndpoint(uri);
    }

    private static bool IsAllowedGetEndpoint(Uri uri) => uri.Host switch
    {
        "api.anthropic.com" => uri.Query.Length == 0 &&
            uri.AbsolutePath is "/api/oauth/usage" or "/api/oauth/profile",
        "chatgpt.com" => uri.Query.Length == 0 &&
            uri.AbsolutePath == "/backend-api/wham/usage",
        "cli-chat-proxy.grok.com" => uri.AbsolutePath == "/v1/billing" &&
            uri.Query == "?format=credits",
        "opencode.ai" => uri.Query.Length == 0 &&
            (uri.AbsolutePath is "/console/api/orgs/current" or
                "/console/api/go/status" or "/zen/go/v1/usage" ||
                IsMemberBudgetPath(uri.AbsolutePath)),
        "ollama.com" => uri.AbsolutePath == "/api/usage" && IsTimestampQuery(uri.Query),
        _ => false,
    };

    private static bool IsMemberBudgetPath(string path)
    {
        if (!path.StartsWith(MemberBudgetPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        string member = Uri.UnescapeDataString(path[MemberBudgetPrefix.Length..]);
        return !string.IsNullOrWhiteSpace(member) &&
            member is not "." and not ".." &&
            !member.Any(character => character is '/' or '\\' or '%' || char.IsControl(character));
    }

    private static bool IsTimestampQuery(string query) =>
        query.StartsWith("?ts=", StringComparison.Ordinal) &&
        long.TryParse(query.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out long timestamp) &&
        timestamp > 0;
}
