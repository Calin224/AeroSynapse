using AeroSynapse.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace AeroSynapse.API.Security;

// Redact before provider dispatch, including test/third-party providers and scopes.
public sealed class RedactingLoggerFactory : ILoggerFactory
{
    private readonly LoggerFactory inner;
    private readonly SecretRedactor redactor;
    public RedactingLoggerFactory(IEnumerable<ILoggerProvider> providers, IOptionsMonitor<LoggerFilterOptions> filters,
        IOptions<LoggerFactoryOptions> options, SecretRedactor redactor)
    {
        inner = new(providers, filters, options);
        this.redactor = redactor;
    }
    public ILogger CreateLogger(string categoryName) => new RedactingLogger(inner.CreateLogger(categoryName), redactor);
    public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);
    public void Dispose() => inner.Dispose();

    private sealed class RedactingLogger(ILogger logger, SecretRedactor redactor) : ILogger
    {
        public bool IsEnabled(LogLevel level) => logger.IsEnabled(level);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => logger.BeginScope(Safe(state));
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            // Exception messages/Data often contain raw upstream bodies or connection
            // strings. Preserve the type and stack frames, never those raw fields.
            var safeError = error is null ? null : new Exception(redactor.Redact(error.GetType().Name + "\n" + error.StackTrace));
            var safeState = Safe(state);
            var message = safeState is KeyValuePair<string, object?>[] fields
                ? Render(fields) : redactor.Redact(safeState.ToString());
            logger.Log(level, eventId, safeState, safeError, (_, _) => message);
        }
        private object Safe<TState>(TState state) => state is IEnumerable<KeyValuePair<string, object?>> fields
            ? fields.Select(field => new KeyValuePair<string, object?>(field.Key,
                SecretRedactor.Sensitive(field.Key) || new[] { "error", "exception", "message", "detail", "body", "payload", "requestbody", "responsebody", "headers" }.Contains(field.Key.ToLowerInvariant())
                    ? "[REDACTED]" : field.Value is Exception error
                    ? redactor.Redact(error.GetType().Name + "\n" + error.StackTrace) : redactor.Redact(field.Value?.ToString()))).ToArray()
            : state is string text ? redactor.Redact(text) : state?.GetType().Name ?? "";
        private string Render(KeyValuePair<string, object?>[] fields)
        {
            var template = fields.FirstOrDefault(field => field.Key == "{OriginalFormat}").Value?.ToString();
            if (template is null) return string.Join(" ", fields.Select(field => field.Key + "=" + field.Value));
            foreach (var field in fields.Where(field => field.Key != "{OriginalFormat}"))
                template = System.Text.RegularExpressions.Regex.Replace(template, "\\{" + System.Text.RegularExpressions.Regex.Escape(field.Key) + "(?::[^{}]*)?\\}",
                    _ => field.Value?.ToString() ?? "", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100));
            return redactor.Redact(template);
        }
    }
}
