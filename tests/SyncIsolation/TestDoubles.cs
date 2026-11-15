using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using RefertoPallamano_Blazor.Models;

sealed class TestNavigationManager : NavigationManager
{
    public TestNavigationManager() { Initialize("https://prod.test/", "https://prod.test/"); }
    protected override void NavigateToCore(string uri, bool forceLoad) { }
}

sealed class TestJsRuntime : IJSRuntime
{
    private readonly Dictionary<string, string> _storage = new(StringComparer.Ordinal);
    public string? Read(string key) => _storage.TryGetValue(key, out var value) ? value : null;

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
        InvokeAsync<TValue>(identifier, CancellationToken.None, args);

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        cancellationToken.ThrowIfCancellationRequested();
        object? result = null;
        var key = args is { Length: > 0 } ? args[0]?.ToString() ?? "" : "";
        if (identifier == "localStorage.getItem")
            result = _storage.TryGetValue(key, out var value) ? value : null;
        else if (identifier == "localStorage.setItem" && args is { Length: > 1 })
            _storage[key] = args[1]?.ToString() ?? "";
        else if (identifier == "localStorage.removeItem")
            _storage.Remove(key);
        else
            throw new NotSupportedException("Chiamata JS non prevista: " + identifier);
        return ValueTask.FromResult((TValue)result!);
    }
}

sealed class MockArchiveServer
{
    private readonly object _gate = new();
    public ArchiveDatabase Data { get; private set; }
    public string Sha { get; private set; }
    public bool Exists { get; private set; } = true;
    public int PutCount { get; private set; }
    public int ConflictCount { get; private set; }

    public MockArchiveServer(ArchiveDatabase initial, string sha)
    {
        Data = Clone(initial);
        Sha = sha;
    }

    public HttpResponseMessage Handle(HttpRequestMessage request)
    {
        lock (_gate)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (request.Method == HttpMethod.Get && path == "/api/me")
                return Json(HttpStatusCode.OK, """{"role":"admin","operatorName":"Admin","deviceId":"test-device"}""");

            if (request.Method == HttpMethod.Get && path == "/api/archive")
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { exists = Exists, sha = Sha, data = Data }));

            if (request.Method == HttpMethod.Put && path == "/api/archive")
            {
                PutCount++;
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                var expectedSha = root.TryGetProperty("sha", out var shaElement) ? shaElement.GetString() : null;
                if (!Exists || !string.Equals(expectedSha, Sha, StringComparison.Ordinal))
                {
                    ConflictCount++;
                    return Json(HttpStatusCode.Conflict, """{"error":"conflict"}""");
                }
                var incoming = JsonSerializer.Deserialize<ArchiveDatabase>(root.GetProperty("data").GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                Data = Clone(incoming);
                Sha = "sha-" + (int.Parse(Sha.AsSpan(4)) + 1).ToString();
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { sha = Sha }));
            }

            return Json(HttpStatusCode.NotFound, """{"error":"not found"}""");
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string content) =>
        new(status) { Content = new StringContent(content, Encoding.UTF8, "application/json") };

    public static ArchiveDatabase Clone(ArchiveDatabase source) =>
        JsonSerializer.Deserialize<ArchiveDatabase>(JsonSerializer.Serialize(source))!;
}

sealed class MockArchiveHandler(MockArchiveServer server) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(server.Handle(request));
    }
}
