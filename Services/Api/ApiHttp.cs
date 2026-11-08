using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace AiPulse.Services.Api;

/// <summary>How a response may be cached. Public data is cacheable by anyone; per-user data only by the caller.</summary>
public sealed record ApiCache(string CacheControl, bool UseETag)
{
    public static readonly ApiCache News = new("public, max-age=60", true);
    public static readonly ApiCache Reference = new("public, max-age=300", true);
    public static readonly ApiCache Document = new("public, max-age=3600", true);
    public static readonly ApiCache Private = new("private, no-cache", true);
    public static readonly ApiCache NoStore = new("no-store", false);
}

/// <summary>An RFC 7807 problem. <see cref="Code"/> becomes the fragment of the "type" URI on /developers.</summary>
public sealed record ApiProblem(int Status, string Code, string Title, string Detail, IReadOnlyDictionary<string, string[]>? Errors = null)
{
    public static ApiProblem InvalidParameters(ApiQueryErrors errors) =>
        new(400, "invalid-parameter", "Invalid query parameter", "One or more query parameters are not valid. See \"errors\".", errors.ToDictionary());
    public static ApiProblem BadRequest(string parameter, string detail) =>
        new(400, "invalid-parameter", "Invalid query parameter", detail, new Dictionary<string, string[]> { [parameter] = new[] { detail } });
    public static ApiProblem NotFound(string detail) => new(404, "not-found", "Not found", detail);
}

/// <summary>A handler's outcome: a body to send as JSON, or a problem.</summary>
public sealed record ApiResult(object? Body, ApiCache Cache, ApiProblem? Problem = null, int Status = 200)
{
    public static ApiResult Ok(object body, ApiCache cache) => new(body, cache);
    public static ApiResult Fail(ApiProblem problem) => new(null, ApiCache.NoStore, problem, problem.Status);
    public static ApiResult Unavailable(object body) => new(body, ApiCache.NoStore, null, 503);
}

/// <summary>Serialization, conditional requests (ETag / If-None-Match), compression and problem+json for /api/v1.</summary>
public static class ApiHttp
{
    public const string JsonContentType = "application/json; charset=utf-8";
    public const string ProblemContentType = "application/problem+json; charset=utf-8";
    /// <summary>Bodies smaller than this are sent uncompressed; the gzip header would cost more than it saves.</summary>
    public const int CompressionThreshold = 1024;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null,
        // Relaxed: bodies are served as application/json, never inlined into HTML, so quotes and non-ASCII need no escaping.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false
    };

    /// <summary>A weak validator over the uncompressed JSON, so the same data gives the same ETag whatever the encoding.</summary>
    public static string ComputeETag(ReadOnlySpan<byte> body)
    {
        var hash = SHA256.HashData(body);
        return "W/\"" + Convert.ToBase64String(hash, 0, 16).TrimEnd('=').Replace('+', '-').Replace('/', '_') + "\"";
    }

    /// <summary>RFC 9110 weak comparison: "*" matches anything, otherwise any listed tag equal to ours once W/ is ignored.</summary>
    public static bool IfNoneMatchHits(StringValues ifNoneMatch, string etag)
    {
        if (StringValues.IsNullOrEmpty(ifNoneMatch)) return false;
        var ours = StripWeak(etag);
        foreach (var header in ifNoneMatch)
        {
            if (header is null) continue;
            foreach (var part in header.Split(','))
            {
                var tag = part.Trim();
                if (tag == "*") return true;
                if (tag.Length > 0 && StripWeak(tag) == ours) return true;
            }
        }
        return false;
    }

    private static string StripWeak(string tag) => tag.StartsWith("W/", StringComparison.Ordinal) ? tag[2..] : tag;

    /// <summary>"br" or "gzip" when the client accepts it (q &gt; 0), preferring br; null for identity.</summary>
    public static string? ChooseEncoding(StringValues acceptEncoding)
    {
        if (StringValues.IsNullOrEmpty(acceptEncoding)) return null;
        double br = -1, gzip = -1, star = -1;
        foreach (var header in acceptEncoding)
        {
            if (header is null) continue;
            foreach (var part in header.Split(','))
            {
                var bits = part.Split(';');
                var name = bits[0].Trim().ToLowerInvariant();
                double q = 1;
                foreach (var p in bits.Skip(1))
                {
                    var kv = p.Trim();
                    if (kv.StartsWith("q=", StringComparison.OrdinalIgnoreCase) &&
                        double.TryParse(kv[2..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                        q = parsed;
                }
                if (name == "br") br = q; else if (name == "gzip") gzip = q; else if (name == "*") star = q;
            }
        }
        if (br < 0 && star > 0) br = star;
        if (gzip < 0 && star > 0) gzip = star;
        if (br > 0 && br >= gzip) return "br";
        if (gzip > 0) return "gzip";
        return null;
    }

    public static byte[] Compress(byte[] body, string encoding)
    {
        using var output = new MemoryStream();
        using (Stream stream = encoding == "br"
                   ? new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true)
                   : new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            stream.Write(body);
        return output.ToArray();
    }

    public static string Origin(HttpRequest request) => $"{request.Scheme}://{request.Host}{request.PathBase}";

    public static Task WriteAsync(HttpContext ctx, ApiResult result)
    {
        if (result.Problem is { } problem) return WriteProblemAsync(ctx, problem);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result.Body, Json);
        return WriteBytesAsync(ctx, bytes, result.Status, JsonContentType, result.Cache);
    }

    public static Task WriteProblemAsync(HttpContext ctx, ApiProblem problem)
    {
        var body = new Dictionary<string, object?>
        {
            ["type"] = $"{Origin(ctx.Request)}/developers#error-{problem.Code}",
            ["title"] = problem.Title,
            ["status"] = problem.Status,
            ["detail"] = problem.Detail,
            ["instance"] = ctx.Request.Path.Value + ctx.Request.QueryString.Value,
            ["traceId"] = ctx.TraceIdentifier
        };
        if (problem.Errors is { Count: > 0 }) body["errors"] = problem.Errors;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, Json);
        return WriteBytesAsync(ctx, bytes, problem.Status, ProblemContentType, ApiCache.NoStore);
    }

    private static async Task WriteBytesAsync(HttpContext ctx, byte[] bytes, int status, string contentType, ApiCache cache)
    {
        var response = ctx.Response;
        response.Headers.CacheControl = cache.CacheControl;
        response.Headers.Append(HeaderNames.Vary, "Accept-Encoding");

        if (cache.UseETag && status == 200)
        {
            var etag = ComputeETag(bytes);
            response.Headers.ETag = etag;
            if (IfNoneMatchHits(ctx.Request.Headers.IfNoneMatch, etag))
            {
                response.StatusCode = StatusCodes.Status304NotModified;
                return;
            }
        }

        response.StatusCode = status;
        response.ContentType = contentType;
        if (bytes.Length >= CompressionThreshold && ChooseEncoding(ctx.Request.Headers.AcceptEncoding) is { } encoding)
        {
            bytes = Compress(bytes, encoding);
            response.Headers.ContentEncoding = encoding;
        }
        response.ContentLength = bytes.Length;
        if (HttpMethods.IsHead(ctx.Request.Method)) return;
        await response.Body.WriteAsync(bytes, ctx.RequestAborted);
    }
}
