using System.Globalization;

namespace PCL.Modpack;

/// <summary>Keeps one reusable GET redirect for the lifetime of an archive download.</summary>
internal sealed class PackArchiveLink {
    private static readonly TimeSpan RenewalMargin = TimeSpan.FromSeconds(30);
    private string? address;
    private DateTimeOffset expires;

    internal string? Get(DateTimeOffset now) => now + RenewalMargin < expires ? address : null;

    internal void Clear() => address = null;

    internal void Remember(Uri redirect, DateTimeOffset now) {
        Clear();
        if (redirect.Scheme != Uri.UriSchemeHttps || redirect.UserInfo.Length != 0 || redirect.Fragment.Length != 0) return;
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string field in redirect.Query.TrimStart('?').Split('&')) {
            var parts = field.Split(new[] { '=' }, 2);
            if (parts.Length != 2) return;
            string name = Uri.UnescapeDataString(parts[0]);
            if (query.ContainsKey(name)) return;
            query.Add(name, Uri.UnescapeDataString(parts[1]));
        }
        if (!query.TryGetValue("X-Amz-Algorithm", out var algorithm) || algorithm != "AWS4-HMAC-SHA256" ||
            !query.TryGetValue("X-Amz-SignedHeaders", out var headers) || headers != "host" ||
            !query.TryGetValue("X-Amz-Signature", out var signature) || !Regex.IsMatch(signature, @"\A[a-f0-9]{64}\z") ||
            !query.TryGetValue("X-Amz-Date", out var date) ||
            !DateTimeOffset.TryParseExact(date, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var signedAt) ||
            !query.TryGetValue("X-Amz-Expires", out var seconds) ||
            !int.TryParse(seconds, NumberStyles.None, CultureInfo.InvariantCulture, out int lifetime) || lifetime < 1 || lifetime > 604800) return;

        // Reject implausible dates and renew ahead of expiry to allow for clock skew and network delay.
        if (signedAt > now + RenewalMargin || signedAt < now - TimeSpan.FromDays(7)) return;
        expires = signedAt.AddSeconds(lifetime);
        if (now + RenewalMargin < expires) address = redirect.AbsoluteUri;
    }
}
