using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace AeroSynapse.Infrastructure.Security;

public sealed class SecretRedactor(IConfiguration configuration)
{
    private static readonly Regex Assignment = new("(?i)(\\b(?:password|passwd|pwd|secret|token|access_token|refresh_token|api[_-]?key|credential|authorization|cookie)\\b[\\\"']?\\s*[:=]\\s*)(?:[\\\"'][^\\\"']*[\\\"']|[^\\s;,}]+)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Bearer = new("(?i)\\bBearer\\s+[A-Za-z0-9._~+/=-]+", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Tokens = new("\\b(?:sk-(?:proj-)?[A-Za-z0-9_-]{12,}|eyJ[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+)\\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    public string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? "";
        try
        {
            foreach (var item in configuration.AsEnumerable().Where(item => item.Value?.Length >= 4 && Sensitive(item.Key)))
                value = value.Replace(item.Value!, "[REDACTED]", StringComparison.Ordinal);
            return RedactPatterns(value);
        }
        catch (RegexMatchTimeoutException) { return "[REDACTED]"; }
    }
    public static string RedactPatterns(string value)
    {
        try
        {
            value = Bearer.Replace(value, "Bearer [REDACTED]");
            value = Assignment.Replace(value, "$1[REDACTED]");
            return Tokens.Replace(value, "[REDACTED]");
        }
        catch (RegexMatchTimeoutException) { return "[REDACTED]"; }
    }
    public static bool Sensitive(string key) => Regex.IsMatch(key, "password|passwd|pwd|secret|token|credential|authorization|cookie|api.?key|signing.?key|private.?key|connectionstring", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
}
