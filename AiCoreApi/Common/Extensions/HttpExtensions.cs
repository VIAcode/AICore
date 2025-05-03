using System.IO.Compression;
using System.Net;
using System.Text;
using ICSharpCode.SharpZipLib.Zip.Compression.Streams;

namespace AiCoreApi.Common.Extensions;

public static class HttpExtensions
{
    public static async Task<string> GetCompressedStringAsync(this HttpClient httpClient, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        return await GetCompressedStringAsync(httpClient, request);
    }

    private static async Task<string> ReadDecompressedContentAsync(HttpResponseMessage response)
    {
        var encoding = response.Content.Headers.ContentEncoding.FirstOrDefault()?.ToLowerInvariant();
        await using var rawStream = await response.Content.ReadAsStreamAsync();

        Stream decompressedStream = encoding switch
        {
            "gzip" => new GZipStream(rawStream, CompressionMode.Decompress),
            "deflate" => new InflaterInputStream(rawStream),
            "br" => new BrotliStream(rawStream, CompressionMode.Decompress),
            _ => rawStream
        };

        using var reader = new StreamReader(decompressedStream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    public static async Task<string> GetCompressedStringAsync(this HttpClient httpClient, HttpRequestMessage originalRequest)
    {
        const int maxRedirects = 10;
        int redirectCount = 0;
        HttpRequestMessage currentRequest = CloneRequest(originalRequest);

        while (true)
        {
            using var response = await httpClient.SendAsync(currentRequest, HttpCompletionOption.ResponseHeadersRead);

            if (IsRedirect(response.StatusCode) && response.Headers.Location != null)
            {
                redirectCount++;
                if (redirectCount > maxRedirects)
                    throw new HttpRequestException($"Too many redirects. Limit is {maxRedirects}.");

                var redirectUri = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(currentRequest.RequestUri!, response.Headers.Location);

                response.Dispose(); // Dispose before creating new request
                currentRequest = CloneRequest(originalRequest, redirectUri); // Clone original with new URL
                continue;
            }

            response.EnsureSuccessStatusCode();
            return await ReadDecompressedContentAsync(response);
        }
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage request, Uri? newUri = null)
    {
        var clone = new HttpRequestMessage(request.Method, newUri ?? request.RequestUri);

        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        return clone;
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.Moved or  // 301
            HttpStatusCode.Found or       // 302
            HttpStatusCode.TemporaryRedirect or // 307
            HttpStatusCode.PermanentRedirect;   // 308
}