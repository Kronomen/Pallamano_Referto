using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.JSInterop;
using RefertoPallamano_Blazor.Models;

namespace RefertoPallamano_Blazor.Services;

public sealed class CloudArchiveService(HttpClient http, IJSRuntime js)
{
    private const string ApiBase = "https://pallamano-referto-api.kronomen.workers.dev";
    private const string TokenKey = "pallamanoCloudApiToken";
    private string? _token;
    private string? _sha;
    private bool _autoSyncEnabled;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(_token);
    public bool HasRemoteArchive { get; private set; }
    public string Status { get; private set; } = "Non collegato al cloud.";
    public string? RemoteSha => _sha;

    public async Task LoginAsync(string password)
    {
        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Inserisci la password di accesso al cloud.");
        using var response = await http.PostAsJsonAsync(ApiBase + "/api/login", new { password });
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ReadError(body, "Accesso cloud non riuscito."));
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        _token = ReadString(root, "token") ?? ReadString(root, "apiToken") ??
                 ReadString(root, "accessToken") ?? ReadString(root, "sessionToken");
        if (string.IsNullOrWhiteSpace(_token))
            throw new InvalidOperationException("Il server ha risposto, ma non ha restituito il token di sessione atteso.");
        await js.InvokeVoidAsync("sessionStorage.setItem", TokenKey, _token);
        _autoSyncEnabled = false;
        Status = "Accesso effettuato. Verifico l'archivio remoto…";
        await RefreshRemoteAsync();
    }

    public async Task RefreshRemoteAsync()
    {
        await EnsureTokenAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiBase + "/api/archive");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        using var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ReadError(body, "Impossibile leggere l'archivio cloud."));
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        HasRemoteArchive = root.TryGetProperty("exists", out var exists) && exists.ValueKind == JsonValueKind.True;
        _sha = ReadString(root, "sha");
        Status = HasRemoteArchive ? "Archivio cloud trovato. Scegli se caricarlo su questo dispositivo." : "Archivio cloud vuoto. Puoi caricare la copia locale iniziale.";
    }

    public async Task<ArchiveDatabase?> DownloadRemoteAsync()
    {
        await RefreshRemoteAsync();
        if (!HasRemoteArchive) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiBase + "/api/archive");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        using var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ReadError(body, "Download dell'archivio cloud non riuscito."));
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind == JsonValueKind.Null)
            throw new InvalidOperationException("Il server segnala un archivio esistente ma non restituisce i dati.");
        var archive = JsonSerializer.Deserialize<ArchiveDatabase>(data.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (archive is null) throw new InvalidOperationException("I dati ricevuti dal cloud non sono un archivio valido.");
        _autoSyncEnabled = true;
        Status = "Archivio cloud caricato. Sincronizzazione automatica attiva.";
        return archive;
    }

    public async Task UploadInitialAsync(ArchiveDatabase archive)
    {
        await EnsureTokenAsync();
        if (HasRemoteArchive)
            throw new InvalidOperationException("Nel cloud esiste già un archivio. Per evitare sovrascritture, il caricamento iniziale è bloccato.");
        await PutAsync(archive, null);
        _autoSyncEnabled = true;
        Status = "Archivio locale caricato nel cloud. Sincronizzazione automatica attiva.";
    }

    public async Task TrySyncAsync(ArchiveDatabase archive)
    {
        if (!_autoSyncEnabled || !IsAuthenticated) return;
        await _syncLock.WaitAsync();
        try
        {
            await PutAsync(archive, _sha);
            Status = "Archivio salvato anche nel cloud.";
        }
        catch (Exception ex)
        {
            Status = "Salvataggio locale eseguito; cloud non aggiornato: " + ex.Message;
        }
        finally { _syncLock.Release(); }
    }

    private async Task PutAsync(ArchiveDatabase archive, string? expectedSha)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, ApiBase + "/api/archive");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        var payload = new Dictionary<string, object?> { ["data"] = archive };
        if (!string.IsNullOrWhiteSpace(expectedSha)) payload["sha"] = expectedSha;
        request.Content = JsonContent.Create(payload);
        using var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            if ((int)response.StatusCode == 409)
            {
                _autoSyncEnabled = false;
                Status = "Conflitto: l'archivio cloud è cambiato da un altro dispositivo. Sincronizzazione sospesa.";
            }
            throw new InvalidOperationException(ReadError(body, "Salvataggio cloud non riuscito."));
        }
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                _sha = ReadString(document.RootElement, "sha") ?? ReadString(document.RootElement, "contentSha") ?? _sha;
            }
            catch (JsonException) { /* Alcune risposte di successo possono non avere un corpo JSON. */ }
        }
        HasRemoteArchive = true;
        if (string.IsNullOrWhiteSpace(_sha))
        {
            await RefreshRemoteAsync();
            if (string.IsNullOrWhiteSpace(_sha))
                throw new InvalidOperationException("Il salvataggio è riuscito, ma il server non ha restituito la versione dell'archivio necessaria per proteggere le modifiche successive.");
        }
    }

    private async Task EnsureTokenAsync()
    {
        if (string.IsNullOrWhiteSpace(_token))
            _token = await js.InvokeAsync<string?>("sessionStorage.getItem", new object?[] { TokenKey });
        if (string.IsNullOrWhiteSpace(_token))
            throw new InvalidOperationException("La sessione cloud è scaduta. Effettua nuovamente l'accesso.");
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static string ReadError(string body, string fallback)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return ReadString(document.RootElement, "error") ?? fallback;
        }
        catch { return fallback; }
    }
}
