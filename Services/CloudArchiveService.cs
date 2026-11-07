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
    public bool IsAdmin { get; private set; }
    public bool IsOperator => IsAuthenticated && !IsAdmin;
    public string? OperatorName { get; private set; }
    public string? DeviceId { get; private set; }
    public bool HasRemoteArchive { get; private set; }
    public string Status { get; private set; } = "Non collegato al cloud.";
    public string? RemoteSha => _sha;

    public async Task LoginAsync(string password)
    {
        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Inserisci la password amministratore.");
        using var response = await http.PostAsJsonAsync(ApiBase + "/api/login", new { password });
        await AcceptTokenResponseAsync(response, "Accesso amministratore non riuscito.");
        IsAdmin = true;
        OperatorName = "Amministratore";
        await PersistTokenAsync();
        _autoSyncEnabled = false;
        Status = "Accesso amministratore effettuato. Verifico l'archivio remoto…";
        await RefreshRemoteAsync();
    }

    public async Task ActivateOperatorAsync(string code, string deviceName)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("Inserisci il codice personale.");
        using var response = await http.PostAsJsonAsync(ApiBase + "/api/operator/activate", new { code, deviceName });
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ReadError(body, "Attivazione non riuscita."));
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        _token = ReadString(root, "token") ?? throw new InvalidOperationException("Il server non ha restituito la credenziale del dispositivo.");
        DeviceId = ReadString(root, "deviceId");
        OperatorName = ReadString(root, "operatorName");
        IsAdmin = false;
        await PersistTokenAsync();
        _autoSyncEnabled = false;
        Status = $"Dispositivo attivato per {OperatorName}.";
        await RefreshRemoteAsync();
    }

    public async Task<List<OperatorInfo>> GetOperatorsAsync()
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Get, "/api/admin/operators");
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ReadError(body, "Impossibile caricare gli operatori."));
        using var document = JsonDocument.Parse(body);
        var list = new List<OperatorInfo>();
        if (document.RootElement.TryGetProperty("operators", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
                list.Add(new OperatorInfo(
                    ReadString(item, "id") ?? "",
                    ReadString(item, "name") ?? "",
                    item.TryGetProperty("active", out var active) && active.ValueKind == JsonValueKind.Number && active.GetInt32() == 1,
                    item.TryGetProperty("device_count", out var count) ? count.GetInt32() : 0,
                    item.TryGetProperty("active_device_count", out var activeCount) && activeCount.ValueKind == JsonValueKind.Number ? activeCount.GetInt32() : 0));
        }
        return list;
    }

    public async Task<List<OperatorDeviceInfo>> GetOperatorDevicesAsync(string operatorId)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Get, "/api/admin/operators/" + Uri.EscapeDataString(operatorId) + "/devices");
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ReadError(body, "Impossibile caricare i dispositivi."));
        using var document = JsonDocument.Parse(body);
        var list = new List<OperatorDeviceInfo>();
        if (document.RootElement.TryGetProperty("devices", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
                list.Add(new OperatorDeviceInfo(
                    ReadString(item, "id") ?? "",
                    ReadString(item, "device_name") ?? "Dispositivo",
                    ReadString(item, "created_at"),
                    ReadString(item, "last_seen_at"),
                    ReadString(item, "revoked_at")));
        }
        return list;
    }

    public async Task<CreatedOperator> CreateOperatorAsync(string name)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Post, "/api/admin/operators", new { name });
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ReadError(body, "Creazione operatore non riuscita."));
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        return new CreatedOperator(ReadString(root, "id") ?? "", ReadString(root, "name") ?? name, ReadString(root, "code") ?? "");
    }

    public async Task SetOperatorActiveAsync(string id, bool active)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Patch, "/api/admin/operators/" + Uri.EscapeDataString(id), new { active });
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ReadError(body, "Modifica operatore non riuscita."));
    }

    public async Task RevokeDeviceAsync(string id)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Post, "/api/admin/devices/" + Uri.EscapeDataString(id) + "/revoke", new { });
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ReadError(body, "Revoca dispositivo non riuscita."));
    }

    public async Task<bool> TryRestoreSessionAsync()
    {
        try
        {
            await EnsureTokenAsync();
            using var request = new HttpRequestMessage(HttpMethod.Get, ApiBase + "/api/me");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            using var response = await http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ReadError(body, "Sessione non valida."));
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var role = ReadString(root, "role");
            IsAdmin = string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase);
            OperatorName = ReadString(root, "operatorName") ?? (IsAdmin ? "Amministratore" : "Operatore");
            DeviceId = ReadString(root, "deviceId");
            await RefreshRemoteAsync();
            Status = IsAdmin
                ? "Sessione amministratore ripristinata. Archivio cloud disponibile."
                : $"Sessione ripristinata per {OperatorName}. Archivio cloud disponibile.";
            return true;
        }
        catch
        {
            _token = null;
            _autoSyncEnabled = false;
            IsAdmin = false;
            OperatorName = null;
            DeviceId = null;
            Status = "Sessione cloud non attiva. Effettua l'accesso o l'attivazione.";
            try { await js.InvokeVoidAsync("localStorage.removeItem", TokenKey); } catch { }
            return false;
        }
    }

    public async Task RefreshRemoteAsync()
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Get, "/api/archive");
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
        _autoSyncEnabled = false;
        using var response = await SendAuthorizedAsync(HttpMethod.Get, "/api/archive");
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ReadError(body, "Download dell'archivio cloud non riuscito."));
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        HasRemoteArchive = root.TryGetProperty("exists", out var exists) && exists.ValueKind == JsonValueKind.True;
        _sha = ReadString(root, "sha");
        if (!HasRemoteArchive) { Status = "Archivio cloud vuoto."; return null; }
        if (!root.TryGetProperty("data", out var data) || data.ValueKind == JsonValueKind.Null)
            throw new InvalidOperationException("Il server segnala un archivio esistente ma non restituisce i dati.");
        var archive = JsonSerializer.Deserialize<ArchiveDatabase>(data.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (archive is null) throw new InvalidOperationException("I dati ricevuti dal cloud non sono un archivio valido.");
        Status = "Archivio cloud scaricato. Conferma la sostituzione locale per attivare la sincronizzazione.";
        return archive;
    }

    public void EnableAutoSync() { _autoSyncEnabled = true; Status = "Archivio cloud caricato. Sincronizzazione automatica attiva."; }

    public async Task UploadInitialAsync(ArchiveDatabase archive)
    {
        if (HasRemoteArchive) throw new InvalidOperationException("Nel cloud esiste già un archivio. Per evitare sovrascritture, il caricamento iniziale è bloccato.");
        await PutAsync(archive, null);
        _autoSyncEnabled = true;
        Status = "Archivio locale caricato nel cloud. Sincronizzazione automatica attiva.";
    }

    public async Task TrySyncAsync(ArchiveDatabase archive)
    {
        if (!_autoSyncEnabled || !IsAuthenticated) return;
        await _syncLock.WaitAsync();
        try { await PutAsync(archive, _sha); Status = "Archivio salvato anche nel cloud."; }
        catch (Exception ex) { Status = "Salvataggio locale eseguito; cloud non aggiornato: " + ex.Message; }
        finally { _syncLock.Release(); }
    }

    private async Task PutAsync(ArchiveDatabase archive, string? expectedSha)
    {
        var payload = new Dictionary<string, object?> { ["data"] = archive };
        if (!string.IsNullOrWhiteSpace(expectedSha)) payload["sha"] = expectedSha;
        using var response = await SendAuthorizedAsync(HttpMethod.Put, "/api/archive", payload);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            if ((int)response.StatusCode == 409) { _autoSyncEnabled = false; Status = "Conflitto: archivio cloud modificato da un altro dispositivo. Sincronizzazione sospesa."; }
            throw new InvalidOperationException(ReadError(body, "Salvataggio cloud non riuscito."));
        }
        using var document = JsonDocument.Parse(body);
        _sha = ReadString(document.RootElement, "sha") ?? _sha;
        HasRemoteArchive = true;
        if (string.IsNullOrWhiteSpace(_sha)) { await RefreshRemoteAsync(); if (string.IsNullOrWhiteSpace(_sha)) throw new InvalidOperationException("Versione archivio non restituita dal server."); }
    }

    private async Task<HttpResponseMessage> SendAuthorizedAsync(HttpMethod method, string path, object? payload = null)
    {
        await EnsureTokenAsync();
        var request = new HttpRequestMessage(method, ApiBase + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await http.SendAsync(request);
    }

    private async Task AcceptTokenResponseAsync(HttpResponseMessage response, string fallback)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ReadError(body, fallback));
        using var document = JsonDocument.Parse(body);
        _token = ReadString(document.RootElement, "token") ?? throw new InvalidOperationException("Token di sessione non restituito.");
    }

    private async Task PersistTokenAsync() => await js.InvokeVoidAsync("localStorage.setItem", TokenKey, _token);

    private async Task EnsureTokenAsync()
    {
        if (string.IsNullOrWhiteSpace(_token)) _token = await js.InvokeAsync<string?>("localStorage.getItem", new object?[] { TokenKey });
        if (string.IsNullOrWhiteSpace(_token)) throw new InvalidOperationException("Sessione cloud assente. Effettua l'accesso o l'attivazione.");
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string ReadError(string body, string fallback)
    {
        try { using var document = JsonDocument.Parse(body); return ReadString(document.RootElement, "error") ?? fallback; }
        catch { return fallback; }
    }
}

public sealed record OperatorInfo(string Id, string Name, bool Active, int DeviceCount, int ActiveDeviceCount);
public sealed record CreatedOperator(string Id, string Name, string Code);
public sealed record OperatorDeviceInfo(string Id, string DeviceName, string? CreatedAt, string? LastSeenAt, string? RevokedAt)
{ public bool IsRevoked => !string.IsNullOrWhiteSpace(RevokedAt); }
