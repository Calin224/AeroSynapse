using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AeroSynapse.Infrastructure.Security;

namespace AeroSynapse.API.Security;

internal static class ErrorResponseRedaction
{
    public static void UseErrorResponseRedaction(this WebApplication app) => app.Use(async (context, next) =>
    {
        var original = context.Response.Body;
        using var stream = new ErrorStream(original, context);
        context.Response.Body = stream;
        try
        {
            try { await next(context); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { return; }
            catch (BadHttpRequestException error) when (!context.Response.HasStarted)
            {
                await AeroSynapse.API.Identity.WorkspaceIdentity.Problem(error.StatusCode, "invalid_request", "Invalid request.").ExecuteAsync(context);
            }
            catch (Exception error) when (!context.Response.HasStarted)
            {
                app.Logger.LogError("Request failed errorType={ErrorType} correlationId={CorrelationId}", error.GetType().Name, context.Items["CorrelationId"]);
                await AeroSynapse.API.Identity.WorkspaceIdentity.Problem(500, "request_failed", "Request failed. Use the correlation ID to inspect the server diagnostics.").ExecuteAsync(context);
            }
            if (stream.Captured)
            {
                JsonNode? value;
                try { value = stream.Truncated ? null : JsonNode.Parse(stream.Content); }
                catch (JsonException) { value = null; }
                value = value is null ? new JsonObject { ["status"] = context.Response.StatusCode, ["code"] = "request_rejected", ["title"] = "Request rejected. Inspect the correlation ID." }
                    : Clean(value, context.RequestServices.GetRequiredService<SecretRedactor>());
                var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
                context.Response.ContentType = "application/problem+json";
                context.Response.ContentLength = bytes.Length;
                await original.WriteAsync(bytes, context.RequestAborted);
            }
        }
        finally { context.Response.Body = original; }
    });

    private static JsonNode? Clean(JsonNode? node, SecretRedactor redactor)
    {
        if (node is JsonObject map)
        {
            var result = new JsonObject();
            foreach (var item in map)
                result[redactor.Redact(item.Key)] = SecretRedactor.Sensitive(item.Key)
                    || item.Key is "input" or "ctx" or "requestBody" or "responseBody"
                    ? JsonValue.Create("[REDACTED]") : Clean(item.Value, redactor);
            return result;
        }
        if (node is JsonArray array) return new JsonArray(array.Select(item => Clean(item, redactor)).ToArray());
        return node is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? JsonValue.Create(redactor.Redact(text)) : node?.DeepClone();
    }

    // Successful exports/metrics/replays pass straight through without buffering.
    private sealed class ErrorStream(Stream output, HttpContext context) : Stream
    {
        private readonly MemoryStream capture = new();
        public bool Captured { get; private set; }
        public bool Truncated { get; private set; }
        public string Content => Encoding.UTF8.GetString(capture.ToArray());
        private bool Store(ReadOnlySpan<byte> bytes)
        {
            if (context.Response.StatusCode < 400) return false;
            Captured = true;
            if (capture.Length + bytes.Length > 65536) Truncated = true;
            else if (!Truncated) capture.Write(bytes);
            return true;
        }
        public override void Write(byte[] buffer, int offset, int count) { if (!Store(buffer.AsSpan(offset, count))) output.Write(buffer, offset, count); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) { if (!Store(buffer.Span)) await output.WriteAsync(buffer, token); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) => WriteAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override void Flush() { if (context.Response.StatusCode < 400) output.Flush(); }
        public override Task FlushAsync(CancellationToken token) => context.Response.StatusCode < 400 ? output.FlushAsync(token) : Task.CompletedTask;
        protected override void Dispose(bool disposing) { if (disposing) capture.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
    }
}
