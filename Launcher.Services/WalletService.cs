using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Launcher.Core.Abstractions;
using Launcher.Core.Models;
using Launcher.Shared;
using Microsoft.Extensions.Options;

namespace Launcher.Services;

public sealed class WalletService : IWalletService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly LauncherOptions _options;

    public WalletService(HttpClient http, IOptions<LauncherOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public async Task<WalletSnapshot> GetSnapshotAsync(AuthSession session, CancellationToken ct = default)
    {
        EnsureSignedIn(session);

        using var walletDocument = await ReadNodeAsync($"wallets/{session.FirebaseUid}", session, ct);
        using var ownedDocument = await ReadNodeAsync($"owned/{session.FirebaseUid}", session, ct);
        using var casesDocument = await ReadNodeAsync($"case_inventory/{session.FirebaseUid}", session, ct);
        using var equippedDocument = await ReadNodeAsync($"equipped/{session.FirebaseUid}", session, ct);
        using var ledgerDocument = await ReadNodeAsync($"ledger/{session.FirebaseUid}", session, ct);

        var wallet = walletDocument.RootElement.ValueKind == JsonValueKind.Object
            ? walletDocument.RootElement
            : default;
        var ownedNode = ownedDocument.RootElement;
        var shopItems = ownedNode.ValueKind == JsonValueKind.Object && ownedNode.EnumerateObject().Any()
            ? await ReadShopItemDetailsAsync(session, ct)
            : new Dictionary<string, (string Name, string Type)>(StringComparer.Ordinal);
        var owned = new List<OwnedShopItem>();
        if (ownedNode.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in ownedNode.EnumerateObject())
            {
                var value = property.Value;
                var itemId = ReadString(value, "itemId") ?? property.Name;
                shopItems.TryGetValue(itemId, out var itemDetails);
                var name = itemDetails.Name ?? ReadString(value, "name") ?? ReadString(value, "itemName") ?? itemId;
                var type = itemDetails.Type ?? ReadString(value, "type") ?? ReadString(value, "itemType") ?? "item";
                owned.Add(new OwnedShopItem(itemId, name, type,
                    ReadInt64(value, "acquiredAt"), ReadNullableInt64(value, "expiresAt")));
            }
        }

        var cases = new List<CaseInventoryCount>();
        var casesNode = casesDocument.RootElement;
        if (casesNode.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in casesNode.EnumerateObject())
            {
                var count = ReadCount(property.Value);
                if (count > 0) cases.Add(new CaseInventoryCount(property.Name, count));
            }
        }

        var equipped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var equippedNode = equippedDocument.RootElement;
        if (equippedNode.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in equippedNode.EnumerateObject())
            {
                var itemId = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()
                    : ReadString(property.Value, "itemId");
                if (!string.IsNullOrWhiteSpace(itemId)) equipped[property.Name] = itemId;
            }
        }

        var ledger = new List<WalletLedgerEntry>();
        var ledgerNode = ledgerDocument.RootElement;
        if (ledgerNode.ValueKind == JsonValueKind.Object)
        {
            ledger.AddRange(ledgerNode.EnumerateObject().Select(property => new WalletLedgerEntry(
                property.Name,
                ReadInt32(property.Value, "delta"),
                ReadString(property.Value, "reason") ?? "",
                ReadString(property.Value, "ref"),
                ReadInt32(property.Value, "balanceAfter"),
                ReadInt64(property.Value, "ts"))));
        }

        return new WalletSnapshot(Math.Max(0, ReadInt32(wallet, "coins")), ReadInt64(wallet, "updatedAt"),
            owned, cases, equipped, ledger.OrderByDescending(entry => entry.Timestamp).Take(100).ToArray());
    }

    public Task<WalletPurchaseResult> PurchaseWithCoinsAsync(AuthSession session, string itemId, string requestId, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "Покупка за FL Coins требует серверной проверки (Cloud Functions), которая сейчас отключена. Используйте оплату картой или криптой.");

    public Task<CaseOpenResult> OpenCaseAsync(AuthSession session, string caseId, string requestId, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "Открытие кейсов требует серверной проверки (Cloud Functions), которая сейчас отключена.");

    public async Task WatchBalanceAsync(AuthSession session, Action<int> onChanged, CancellationToken ct = default)
    {
        EnsureSignedIn(session);
        using var request = new HttpRequestMessage(HttpMethod.Get, RtdbUrl($"wallets/{session.FirebaseUid}/coins", session));
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Не удалось подписаться на баланс в Realtime Database (HTTP {(int)response.StatusCode}).",
                null, response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var eventName = "";
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
            {
                if (eventName is "put" or "patch")
                    onChanged(await GetCoinsAsync(session, ct));
                else if (eventName is "cancel" or "auth_revoked")
                    throw new HttpRequestException("Подписка на баланс закрыта; требуется переподключение.");
                eventName = "";
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal)) eventName = line[6..].Trim();
        }
    }

    private async Task<Dictionary<string, (string Name, string Type)>> ReadShopItemDetailsAsync(AuthSession session, CancellationToken ct)
    {
        using var document = await ReadNodeAsync("shop_items", session, ct);
        var result = new Dictionary<string, (string Name, string Type)>(StringComparer.Ordinal);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in root.EnumerateObject())
                AddShopItemDetails(result, property.Name, property.Value);
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in root.EnumerateArray()) AddShopItemDetails(result, (index++).ToString(), item);
        }
        return result;
    }

    private static void AddShopItemDetails(Dictionary<string, (string Name, string Type)> result, string id, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return;
        result[ReadString(value, "id") ?? id] =
            (ReadString(value, "name") ?? id, ReadString(value, "type") ?? "item");
    }

    private async Task<int> GetCoinsAsync(AuthSession session, CancellationToken ct)
    {
        using var document = await ReadNodeAsync($"wallets/{session.FirebaseUid}/coins", session, ct);
        return Math.Max(0, ReadNumericInt32(document.RootElement));
    }

    private async Task<JsonDocument> ReadNodeAsync(string path, AuthSession session, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, RtdbUrl(path, session));
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Не удалось прочитать /{path} из Realtime Database (HTTP {(int)response.StatusCode}). Проверьте правила доступа.",
                null, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(ct);
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "null" : body);
    }

    private static int ReadCount(JsonElement value) => value.ValueKind == JsonValueKind.Number
        ? ReadNumericInt32(value)
        : ReadInt32(value, "count");

    private static int ReadNumericInt32(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;

    private static JsonElement GetObject(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) ? value : default;
    private static string? ReadString(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;
    private static int ReadInt32(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value) ? value : 0;
    private static long ReadInt64(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var value) ? value : 0;
    private static int? ReadNullableInt32(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value) ? value : null;
    private static long? ReadNullableInt64(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var value) ? value : null;

    /// <summary>Firebase ID-токен для RTDB REST передаётся только как ?auth= (Bearer-заголовок даёт 401).</summary>
    private string RtdbUrl(string path, AuthSession session)
    {
        var safePath = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        return $"{GetDatabaseUrl()}/{safePath}.json?auth={Uri.EscapeDataString(session.FirebaseIdToken ?? "")}";
    }

    private string GetDatabaseUrl()
    {
        if (string.IsNullOrWhiteSpace(_options.FirebaseDatabaseUrl) ||
            !Uri.TryCreate(_options.FirebaseDatabaseUrl, UriKind.Absolute, out var databaseUri) || databaseUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Не задан HTTPS FirebaseDatabaseUrl.");
        return _options.FirebaseDatabaseUrl.TrimEnd('/');
    }

    private static void EnsureSignedIn(AuthSession session)
    {
        if (string.IsNullOrWhiteSpace(session.FirebaseUid) || string.IsNullOrWhiteSpace(session.FirebaseIdToken))
            throw new InvalidOperationException("Для кошелька нужен вход через Microsoft (нет Firebase-сессии).");
    }
}
