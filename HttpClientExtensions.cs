// HttpClientExtensions.cs
// A comprehensive set of extension methods and helpers for System.Net.Http.HttpClient
// Target Framework: .NET 8.0+
// License: MIT (see repo LICENSE)
// Author: Kshitij Jhangra attribution ready

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace HttpClientExtensionsPack
{
    /// <summary>
    /// Options that control JSON serialization for extension helpers.
    /// </summary>
    public sealed class JsonClientOptions
    {
        public JsonSerializerOptions SerializerOptions { get; init; } = DefaultJsonOptions();
        public static JsonSerializerOptions DefaultJsonOptions()
        {
            var opts = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                WriteIndented = false,
                AllowTrailingCommas = true,
                PropertyNameCaseInsensitive = true
            };
            opts.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
            return opts;
        }

        public static readonly JsonClientOptions Default = new();
    }

    /// <summary>
    /// Rich HTTP exception capturing request/response context.
    /// </summary>
    public sealed class HttpProblemException : HttpRequestException
    {
        public HttpStatusCode? StatusCode { get; }
        public string? ReasonPhrase { get; }
        public string? ResponseBody { get; }
        public string? ContentType { get; }
        public Uri? RequestUri { get; }
        public string Method { get; }

        public HttpProblemException(HttpResponseMessage response, string? body)
            : base($"HTTP {(int)response.StatusCode} {response.ReasonPhrase} for {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}")
        {
            StatusCode = response.StatusCode;
            ReasonPhrase = response.ReasonPhrase;
            ResponseBody = body;
            ContentType = response.Content.Headers.ContentType?.MediaType;
            RequestUri = response.RequestMessage?.RequestUri;
            Method = response.RequestMessage?.Method.Method ?? "UNKNOWN";
        }
    }

    /// <summary>
    /// A simple retry/backoff policy without external deps. Honors Retry-After.
    /// </summary>
    public sealed class SimpleRetryPolicy
    {
        public int MaxRetries { get; init; } = 3;
        public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(200);
        public Func<int, TimeSpan, TimeSpan>? Jitter { get; init; } = (attempt, delay) =>
        {
            // decorrelated jitter
            var rand = Random.Shared.NextDouble();
            var ms = delay.TotalMilliseconds * (1 + rand);
            return TimeSpan.FromMilliseconds(ms);
        };

        public bool ShouldRetry(HttpResponseMessage r)
            => (int)r.StatusCode == 429 || ((int)r.StatusCode >= 500 && (int)r.StatusCode <= 599);

        public TimeSpan GetDelay(int attempt, HttpResponseMessage? r)
        {
            // Use Retry-After when present
            if (r != null)
            {
                if (r.Headers.RetryAfter?.Delta is TimeSpan delta)
                    return delta;
                if (r.Headers.RetryAfter?.Date is DateTimeOffset when)
                {
                    var delay = when - DateTimeOffset.UtcNow;
                    if (delay > TimeSpan.Zero) return delay;
                }
            }
            var backoff = TimeSpan.FromMilliseconds(BaseDelay.TotalMilliseconds * Math.Pow(2, attempt));
            return Jitter?.Invoke(attempt, backoff) ?? backoff;
        }

        public static readonly SimpleRetryPolicy Default = new();
    }

    public static class HttpClientExtensions
    {
        // ----------------------------
        // Client configuration helpers
        // ----------------------------

        public static HttpClient WithBaseAddress(this HttpClient client, string baseAddress)
        {
            client.BaseAddress = new Uri(baseAddress, UriKind.Absolute);
            return client;
        }

        public static HttpClient WithDefaultHeader(this HttpClient client, string name, string value)
        {
            client.DefaultRequestHeaders.Remove(name);
            client.DefaultRequestHeaders.Add(name, value);
            return client;
        }

        public static HttpClient SetBearerToken(this HttpClient client, string token)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client;
        }

        public static HttpClient AcceptJson(this HttpClient client)
        {
            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return client;
        }

        public static HttpClient Accept(this HttpClient client, string mediaType)
        {
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(mediaType));
            return client;
        }

        public static HttpClient EnableAutomaticDecompression(this HttpClient client)
        {
            // Only effective if underlying handler supports it.
            // Recommended: new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
            client.DefaultRequestHeaders.AcceptEncoding.Clear();
            client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));
            client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
            client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));
            return client;
        }

        // ----------------------------
        // URI and query helpers
        // ----------------------------

        public static Uri BuildUri(this HttpClient client, string relativeOrAbsolute, IDictionary<string, string?>? query = null)
        {
            var baseUri = client.BaseAddress;
            var uri = Uri.TryCreate(relativeOrAbsolute, UriKind.Absolute, out var abs)
                ? abs
                : new Uri(baseUri ?? throw new InvalidOperationException("BaseAddress must be set or use absolute URL"), relativeOrAbsolute);

            if (query == null || query.Count == 0) return uri;

            var qb = new StringBuilder();
            qb.Append(uri.GetLeftPart(UriPartial.Path));
            qb.Append('?');
            bool first = true;
            foreach (var kv in query)
            {
                if (!first) qb.Append('&');
                first = false;
                qb.Append(Uri.EscapeDataString(kv.Key));
                qb.Append('=');
                qb.Append(Uri.EscapeDataString(kv.Value ?? string.Empty));
            }
            if (!string.IsNullOrEmpty(uri.Fragment)) qb.Append(uri.Fragment);
            return new Uri(qb.ToString(), UriKind.Absolute);
        }

        // ----------------------------
        // Core send + ensure helpers
        // ----------------------------

        public static async Task<HttpResponseMessage> SendWithRetryAsync(
            this HttpClient client,
            HttpRequestMessage request,
            SimpleRetryPolicy? policy = null,
            CancellationToken cancellationToken = default)
        {
            policy ??= SimpleRetryPolicy.Default;
            HttpResponseMessage? last = null;
            for (int attempt = 0; attempt <= policy.MaxRetries; attempt++)
            {
                last?.Dispose();
                last = await client.SendAsync(request.Clone(), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!policy.ShouldRetry(last)) return last; // return on success or non-retryable
                var delay = policy.GetDelay(attempt, last);
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            return last!;
        }

        public static async Task EnsureSuccessWithDetailsAsync(this HttpResponseMessage response, CancellationToken ct = default)
        {
            if (response.IsSuccessStatusCode) return;
            string? body = null;
            try
            {
                body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch { /* ignore */ }
            throw new HttpProblemException(response, body);
        }

        // ----------------------------
        // JSON helpers
        // ----------------------------

        public static Task<T?> GetFromJsonSafeAsync<T>(this HttpClient client, string url,
            IDictionary<string, string?>? query = null,
            JsonClientOptions? jsonOptions = null,
            CancellationToken cancellationToken = default) =>
            client.SendJsonAsync<object, T>(HttpMethod.Get, url, body: null, query, jsonOptions, cancellationToken);

        public static Task<TResponse?> PostAsJsonSafeAsync<TRequest, TResponse>(this HttpClient client, string url, TRequest body,
            IDictionary<string, string?>? query = null,
            JsonClientOptions? jsonOptions = null,
            CancellationToken cancellationToken = default) =>
            client.SendJsonAsync<TRequest, TResponse>(HttpMethod.Post, url, body, query, jsonOptions, cancellationToken);

        public static Task<TResponse?> PutAsJsonSafeAsync<TRequest, TResponse>(this HttpClient client, string url, TRequest body,
            IDictionary<string, string?>? query = null,
            JsonClientOptions? jsonOptions = null,
            CancellationToken cancellationToken = default) =>
            client.SendJsonAsync<TRequest, TResponse>(HttpMethod.Put, url, body, query, jsonOptions, cancellationToken);

        public static Task<TResponse?> PatchAsJsonSafeAsync<TRequest, TResponse>(this HttpClient client, string url, TRequest body,
            IDictionary<string, string?>? query = null,
            JsonClientOptions? jsonOptions = null,
            CancellationToken cancellationToken = default) =>
            client.SendJsonAsync<TRequest, TResponse>(HttpMethod.Patch, url, body, query, jsonOptions, cancellationToken);

        public static Task DeleteSafeAsync(this HttpClient client, string url,
            IDictionary<string, string?>? query = null,
            CancellationToken cancellationToken = default) =>
            client.SendJsonAsync<object, object>(HttpMethod.Delete, url, null, query, null, cancellationToken);

        public static async Task<TResponse?> SendJsonAsync<TRequest, TResponse>(
            this HttpClient client,
            HttpMethod method,
            string url,
            TRequest? body = default,
            IDictionary<string, string?>? query = null,
            JsonClientOptions? jsonOptions = null,
            CancellationToken cancellationToken = default)
        {
            jsonOptions ??= JsonClientOptions.Default;
            var uri = client.BuildUri(url, query);
            using var req = new HttpRequestMessage(method, uri);

            if (body is not null && method != HttpMethod.Get && method != HttpMethod.Head)
            {
                var json = JsonSerializer.Serialize(body, jsonOptions.SerializerOptions);
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            using var res = await client.SendWithRetryAsync(req, cancellationToken: cancellationToken).ConfigureAwait(false);
            await res.EnsureSuccessWithDetailsAsync(cancellationToken).ConfigureAwait(false);

            if (res.Content.Headers.ContentLength == 0) return default;
            if (res.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
            {
                await using var stream = await res.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                if (stream == Stream.Null) return default;
                return await JsonSerializer.DeserializeAsync<TResponse>(stream, jsonOptions.SerializerOptions, cancellationToken).ConfigureAwait(false);
            }
            // Fallback to string -> T when server lies about content-type
            var txt = await res.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (typeof(TResponse) == typeof(string))
                return (TResponse?)(object?)txt;
            return JsonSerializer.Deserialize<TResponse>(txt, jsonOptions.SerializerOptions);
        }

        // ----------------------------
        // Streaming and file helpers
        // ----------------------------

        public static async Task DownloadToFileAsync(this HttpClient client, string url, string filePath, CancellationToken cancellationToken = default)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, client.BuildUri(url));
            using var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            await res.EnsureSuccessWithDetailsAsync(cancellationToken).ConfigureAwait(false);
            await using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await using var stream = await res.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await stream.CopyToAsync(fs, 81920, cancellationToken).ConfigureAwait(false);
        }

        public static async Task<HttpResponseMessage> UploadFileAsync(this HttpClient client,
            string url,
            Stream content,
            string fileName,
            string fieldName = "file",
            IDictionary<string, string?>? additionalFields = null,
            string? contentType = null,
            CancellationToken cancellationToken = default)
        {
            var uri = client.BuildUri(url);
            using var form = new MultipartFormDataContent();
            var fileContent = new StreamContent(content);
            if (!string.IsNullOrWhiteSpace(contentType))
                fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            form.Add(fileContent, fieldName, fileName);
            if (additionalFields != null)
            {
                foreach (var kv in additionalFields)
                    form.Add(new StringContent(kv.Value ?? string.Empty), kv.Key);
            }
            using var req = new HttpRequestMessage(HttpMethod.Post, uri) { Content = form };
            var res = await client.SendWithRetryAsync(req, cancellationToken: cancellationToken).ConfigureAwait(false);
            await res.EnsureSuccessWithDetailsAsync(cancellationToken).ConfigureAwait(false);
            return res;
        }

        public static async IAsyncEnumerable<byte[]> StreamDownloadAsync(this HttpClient client, string url, int chunkSize = 32 * 1024, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize));
            using var req = new HttpRequestMessage(HttpMethod.Get, client.BuildUri(url));
            using var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            await res.EnsureSuccessWithDetailsAsync(cancellationToken).ConfigureAwait(false);
            await using var stream = await res.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = ArrayPool<byte>.Shared.Rent(chunkSize);
            try
            {
                int read;
                while ((read = await stream.ReadAsync(buffer.AsMemory(0, chunkSize), cancellationToken)) > 0)
                {
                    yield return buffer[..read];
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }

        // ----------------------------
        // Conditional requests and caching
        // ----------------------------

        public sealed record ConditionalGetResult<T>(bool NotModified, T? Value, EntityTagHeaderValue? ETag, DateTimeOffset? LastModified);

        public static async Task<ConditionalGetResult<T?>> GetIfNoneMatchAsync<T>(this HttpClient client,
            string url,
            EntityTagHeaderValue? currentETag,
            JsonClientOptions? jsonOptions = null,
            CancellationToken cancellationToken = default)
        {
            jsonOptions ??= JsonClientOptions.Default;
            using var req = new HttpRequestMessage(HttpMethod.Get, client.BuildUri(url));
            if (currentETag is not null) req.Headers.IfNoneMatch.Add(currentETag);
            using var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (res.StatusCode == HttpStatusCode.NotModified)
            {
                return new(true, default, res.Headers.ETag, res.Content.Headers.LastModified ?? res.Headers.LastModified);
            }

            await res.EnsureSuccessWithDetailsAsync(cancellationToken).ConfigureAwait(false);
            await using var stream = await res.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var value = await JsonSerializer.DeserializeAsync<T>(stream, jsonOptions.SerializerOptions, cancellationToken).ConfigureAwait(false);
            return new(false, value, res.Headers.ETag, res.Content.Headers.LastModified ?? res.Headers.LastModified);
        }

        // ----------------------------
        // Pagination helper
        // ----------------------------

        /// <summary>
        /// Iterates paginated endpoints where the server returns a "next" URL.
        /// You provide a function that, given a page URL, returns (items, nextUrl).
        /// </summary>
        public static async IAsyncEnumerable<T> PaginateAsync<T>(this HttpClient client,
            string firstPageUrl,
            Func<HttpClient, string, CancellationToken, Task<(IReadOnlyList<T> items, string? nextUrl)>> fetch,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            string? next = firstPageUrl;
            while (!string.IsNullOrEmpty(next))
            {
                var (items, nextUrl) = await fetch(client, next!, cancellationToken).ConfigureAwait(false);
                foreach (var item in items)
                    yield return item;
                next = nextUrl;
                if (cancellationToken.IsCancellationRequested) yield break;
            }
        }

        // ----------------------------
        // HEAD and OPTIONS convenience
        // ----------------------------

        public static Task<HttpResponseMessage> HeadAsync(this HttpClient client, string url, CancellationToken cancellationToken = default)
            => client.SendAsync(new HttpRequestMessage(HttpMethod.Head, client.BuildUri(url)), cancellationToken);

        public static Task<HttpResponseMessage> OptionsAsync(this HttpClient client, string url, CancellationToken cancellationToken = default)
            => client.SendAsync(new HttpRequestMessage(HttpMethod.Options, client.BuildUri(url)), cancellationToken);

        // ----------------------------
        // Timeout wrapper
        // ----------------------------

        public static async Task<T> WithTimeout<T>(this Task<T> task, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delayTask = Task.Delay(timeout, cts.Token);
            var finished = await Task.WhenAny(task, delayTask).ConfigureAwait(false);
            if (finished == delayTask)
                throw new TimeoutException($"Operation timed out after {timeout}.");
            cts.Cancel();
            return await task.ConfigureAwait(false);
        }

        // ----------------------------
        // Request cloning to support retries
        // ----------------------------

        private static HttpRequestMessage Clone(this HttpRequestMessage request)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Version = request.Version,
                VersionPolicy = request.VersionPolicy
            };
            // Headers
            foreach (var header in request.Headers)
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            // Content
            if (request.Content != null)
            {
                var ms = new MemoryStream();
                request.Content.CopyToAsync(ms).GetAwaiter().GetResult();
                ms.Position = 0;
                var contentClone = new StreamContent(ms);
                foreach (var header in request.Content.Headers)
                    contentClone.Headers.TryAddWithoutValidation(header.Key, header.Value);
                clone.Content = contentClone;
            }
            return clone;
        }

        // ----------------------------
        // Minimal Problem+JSON helper (RFC 7807)
        // ----------------------------

        public sealed record ProblemDetails(
            string? Type,
            string? Title,
            int? Status,
            string? Detail,
            string? Instance,
            Dictionary<string, object?>? Extensions);

        public static bool TryParseProblemDetails(string? json, [NotNullWhen(true)] out ProblemDetails? problem)
        {
            problem = null;
            if (string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var ext = new Dictionary<string, object?>();
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Name is "type" or "title" or "status" or "detail" or "instance") continue;
                    ext[prop.Name] = prop.Value.ToString();
                }
                problem = new ProblemDetails(
                    root.TryGetProperty("type", out var type) ? type.GetString() : null,
                    root.TryGetProperty("title", out var title) ? title.GetString() : null,
                    root.TryGetProperty("status", out var status) && status.TryGetInt32(out var s) ? s : null,
                    root.TryGetProperty("detail", out var detail) ? detail.GetString() : null,
                    root.TryGetProperty("instance", out var instance) ? instance.GetString() : null,
                    ext);
                return true;
            }
            catch { return false; }
        }

        // ----------------------------
        // Factory helper to create a tuned HttpClient
        // ----------------------------

        public static HttpClient CreateDefaultClient(
            string? baseAddress = null,
            TimeSpan? timeout = null,
            bool enableDecompression = true,
            Version? httpVersion = null)
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = enableDecompression ? (DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli) : DecompressionMethods.None,
                AllowAutoRedirect = true,
                UseCookies = true,
            };
            var client = new HttpClient(handler)
            {
                Timeout = timeout ?? TimeSpan.FromSeconds(100),
                DefaultRequestVersion = httpVersion ?? HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
            };
            if (!string.IsNullOrWhiteSpace(baseAddress)) client.WithBaseAddress(baseAddress!);
            client.AcceptJson();
            if (enableDecompression) client.EnableAutomaticDecompression();
            return client;
        }
    }
}
