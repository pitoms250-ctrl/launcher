using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.Core.Abstractions;
using Launcher.Core.Models;
using Microsoft.Extensions.Logging;

namespace Launcher.UI.ViewModels;

public sealed record ShopCategoryOption(string Id, string Label);

/// <summary>Магазин, кошелёк FL Coins и инвентарь. Данные идут из Firebase (RTDB + Cloud Functions) через IShopService / IWalletService.</summary>
public partial class MainViewModel
{
    private IShopService _shop = null!;
    private IWalletService _wallet = null!;

    private List<ShopItem> _shopItems = new();
    private CancellationTokenSource? _shopWatchCts;
    private CancellationTokenSource? _walletWatchCts;
    private bool _shopWatchStarted;
    private static readonly Regex TagRegex = new("^[A-Za-z0-9_]{3,16}$", RegexOptions.Compiled);

    public string[] ShopCurrencies { get; } = { "UAH", "USDT", "FL" };
    public ObservableCollection<ShopCategoryOption> ShopCategories { get; } = new();
    public ObservableCollection<ShopItem> VisibleShopItems { get; } = new();
    public string ShopEmptyMessage => _shopItems.Count == 0
        ? ShopStatus
        : VisibleShopItems.Count == 0 ? "По выбранным фильтрам товары не найдены." : "";

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShopEmptyMessage))]
    private string _shopStatus = "Открой магазин, чтобы загрузить каталог.";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanPurchaseShopItems))] private bool _isShopBusy;
    [ObservableProperty] private string _shopSearch = "";
    [ObservableProperty] private int _shopSortIndex;
    [ObservableProperty] private string _selectedShopCurrency = "UAH";
    [ObservableProperty] private string _selectedShopCategory = "all";
    [ObservableProperty] private string _requestedTag = "";

    // ---------- кошелёк ----------
    [ObservableProperty] private int _walletCoins;
    [ObservableProperty] private string _walletStatus = "Войдите через Microsoft, чтобы видеть баланс FL Coins.";
    [ObservableProperty] private bool _hasWalletInventory;
    public ObservableCollection<OwnedShopItem> OwnedShopItems { get; } = new();
    public ObservableCollection<CaseInventoryCount> CaseInventory { get; } = new();

    /// <summary>Покупать можно только после входа Microsoft (нужен Firebase uid), а гость только смотрит каталог.</summary>
    public bool CanPurchaseShopItems => !IsShopBusy && _session?.FirebaseUid is not null;

    private static bool HasFirebaseSession(AuthSession? session) =>
        !string.IsNullOrWhiteSpace(session?.FirebaseUid) && !string.IsNullOrWhiteSpace(session.FirebaseIdToken);

    partial void OnShopSearchChanged(string value) => RefreshVisibleShopItems();
    partial void OnShopSortIndexChanged(int value) => RefreshVisibleShopItems();
    partial void OnSelectedShopCurrencyChanged(string value) => RefreshVisibleShopItems();
    partial void OnSelectedShopCategoryChanged(string value) => RefreshVisibleShopItems();

    // ---------- каталог ----------
    private async Task<AuthSession> GetShopSessionAsync()
    {
        if (_session?.FirebaseUid is not null)
        {
            _session = await _auth.RefreshFirebaseTokenAsync(_session);
            return _session;
        }
        return await _auth.GetOrCreateFirebaseGuestSessionAsync();
    }

    [RelayCommand]
    private async Task LoadShopAsync()
    {
        if (IsShopBusy) return;
        IsShopBusy = true;
        ShopStatus = "Загружаем каталог…";
        try
        {
            var session = await GetShopSessionAsync();
            var items = await _shop.GetItemsAsync(session);
            ApplyShopItems(items);
            StartShopWatch(session);
            OnPropertyChanged(nameof(CanPurchaseShopItems));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Не удалось загрузить магазин");
            ShopStatus = "Каталог недоступен: " + ex.Message;
        }
        finally { IsShopBusy = false; }
    }

    private void StartShopWatch(AuthSession session)
    {
        if (_shopWatchStarted) return;
        _shopWatchStarted = true;
        _shopWatchCts = new CancellationTokenSource();
        var ct = _shopWatchCts.Token;
        // Живое обновление цен/остатков: каталог меняется в CRM — лаунчер подхватывает сразу.
        _ = Task.Run(async () =>
        {
            try
            {
                await _shop.WatchItemsAsync(session, items =>
                    Application.Current.Dispatcher.Invoke(() => ApplyShopItems(items)), ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log.LogDebug(ex, "Подписка на каталог остановлена"); }
            finally { _shopWatchStarted = false; }
        }, ct);
    }

    private void ApplyShopItems(IReadOnlyList<ShopItem> items)
    {
        _shopItems = items.Where(i => i.IsAvailableNow).ToList();

        var keep = SelectedShopCategory;
        ShopCategories.Clear();
        ShopCategories.Add(new ShopCategoryOption("all", "Все"));
        foreach (var c in _shopItems.Select(i => i.Category).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().OrderBy(CategoryOrder))
            ShopCategories.Add(new ShopCategoryOption(c, CategoryLabel(c)));
        SelectedShopCategory = ShopCategories.Any(c => c.Id == keep) ? keep : "all";

        ShopStatus = _shopItems.Count == 0
            ? "В каталоге пока пусто."
            : $"Товаров: {_shopItems.Count}. " + (CanPurchaseShopItems ? "Выбери способ оплаты." : "Войди через Microsoft, чтобы покупать.");
        RefreshVisibleShopItems();
        OnPropertyChanged(nameof(ShopEmptyMessage));
    }

    private static int CategoryOrder(string id) => id switch
    {
        "currency" => 0, "cases" => 1, "premium" => 2, "bundles" => 3, "capes" => 4, "frames" => 5, "badges" => 6,
        "banners" => 7, "themes" => 8, "tags" => 9, "nick" => 10, "emoji" => 11, "gifts" => 12, _ => 99
    };

    private static string CategoryLabel(string id) => id switch
    {
        "currency" => "FL Coins", "cases" => "Кейсы", "premium" => "Премиум", "bundles" => "Наборы", "capes" => "Плащи",
        "frames" => "Рамки", "badges" => "Значки", "banners" => "Баннеры", "themes" => "Темы", "tags" => "Теги",
        "nick" => "Ник", "emoji" => "Эмодзи", "gifts" => "Подарки",
        _ => id.Length == 0 ? "Другое" : char.ToUpperInvariant(id[0]) + id[1..]
    };

    private static int RarityRank(string r) => r switch { "legendary" => 3, "epic" => 2, "rare" => 1, _ => 0 };

    private decimal PriceFor(ShopItem i) => SelectedShopCurrency switch
    {
        "USDT" => i.PriceUsdt,
        "FL" => i.PriceCoins ?? decimal.MaxValue,
        _ => i.PriceUah
    };

    private void RefreshVisibleShopItems()
    {
        IEnumerable<ShopItem> q = _shopItems;
        if (SelectedShopCategory is { Length: > 0 } cat && cat != "all") q = q.Where(i => i.Category == cat);
        var s = ShopSearch?.Trim();
        if (!string.IsNullOrEmpty(s))
            q = q.Where(i => i.Name.Contains(s, StringComparison.CurrentCultureIgnoreCase) ||
                             i.Description.Contains(s, StringComparison.CurrentCultureIgnoreCase));
        q = ShopSortIndex switch
        {
            1 => q.OrderBy(PriceFor),
            2 => q.OrderByDescending(i => PriceFor(i) == decimal.MaxValue ? -1 : PriceFor(i)),
            3 => q.OrderByDescending(i => RarityRank(i.Rarity)).ThenBy(i => i.SortOrder),
            4 => q.OrderByDescending(i => i.SalesCount),
            _ => q.OrderBy(i => i.SortOrder).ThenBy(i => i.Name)
        };
        VisibleShopItems.Clear();
        foreach (var i in q) VisibleShopItems.Add(i);
    }

    // ---------- покупки ----------
    private bool EnsureCanBuy(ShopItem? item, out AuthSession session)
    {
        session = _session!;
        if (item is null) return false;
        if (_session?.FirebaseUid is null)
        {
            ShopStatus = "Для покупки войдите через Microsoft (кнопка вверху).";
            return false;
        }
        if (item.Type == "tag" && !TagRegex.IsMatch(RequestedTag ?? ""))
        {
            ShopStatus = "Введите ник для тега: 3–16 символов, латиница, цифры и «_».";
            return false;
        }
        return true;
    }

    [RelayCommand]
    private Task BuyShopItemCardAsync(ShopItem? item) => BuyViaOrderAsync(item, "card");

    [RelayCommand]
    private Task BuyShopItemCryptoAsync(ShopItem? item) => BuyViaOrderAsync(item, "crypto");

    private async Task BuyViaOrderAsync(ShopItem? item, string method)
    {
        if (IsShopBusy || !EnsureCanBuy(item, out var session)) return;
        IsShopBusy = true;
        ShopStatus = $"Создаём заказ на «{item!.Name}»…";
        try
        {
            session = await _auth.RefreshFirebaseTokenAsync(session);
            _session = session;
            var order = await _shop.CreateOrderAsync(session, item.Id, method, item.Type == "tag" ? RequestedTag.Trim() : null);
            ShopStatus = $"Заказ создан. Код чека: {order.ReceiptCode}. Оплатите в Telegram-боте — после оплаты предмет выдадут автоматически.";
            if (!string.IsNullOrWhiteSpace(order.BotLink))
                Process.Start(new ProcessStartInfo(order.BotLink) { UseShellExecute = true });
            _ = WatchOrderAsync(session, order.OrderId, item.Name);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Не удалось создать заказ");
            ShopStatus = "Не удалось создать заказ: " + ex.Message;
        }
        finally { IsShopBusy = false; }
    }

    /// <summary>Опрашиваем статус заказа в RTDB (до ~15 мин) и обновляем кошелёк, когда он оплачен.</summary>
    private async Task WatchOrderAsync(AuthSession session, string orderId, string itemName)
    {
        try
        {
            for (var i = 0; i < 180; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                var fresh = _session?.FirebaseUid is not null ? await _auth.RefreshFirebaseTokenAsync(_session) : session;
                var status = (await _shop.GetOrderStatusAsync(fresh, orderId)).ToLowerInvariant();
                if (status is "paid" or "completed" or "fulfilled" or "done" or "delivered")
                {
                    ShopStatus = $"Оплата получена. «{itemName}» добавлен в профиль.";
                    await RefreshWalletAsync();
                    return;
                }
                if (status is "cancelled" or "canceled" or "failed" or "expired" or "rejected")
                {
                    ShopStatus = $"Заказ «{itemName}» не завершён ({status}).";
                    return;
                }
            }
        }
        catch (Exception ex) { _log.LogDebug(ex, "Опрос статуса заказа остановлен"); }
    }

    [RelayCommand]
    private async Task BuyShopItemCoinsAsync(ShopItem? item)
    {
        if (IsShopBusy || !EnsureCanBuy(item, out var session)) return;
        if (item!.PriceCoins is null) { ShopStatus = "Этот товар нельзя купить за FL Coins."; return; }
        var ok = MessageBox.Show($"Купить «{item.Name}» за {item.PriceCoins:0} FL?", "Покупка за FL Coins",
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        if (!ok) return;

        IsShopBusy = true;
        ShopStatus = "Покупаем…";
        try
        {
            session = await _auth.RefreshFirebaseTokenAsync(session);
            _session = session;
            // requestId делает покупку идемпотентной: повторный клик/обрыв сети не спишет монеты дважды.
            var res = await _wallet.PurchaseWithCoinsAsync(session, item.Id, Guid.NewGuid().ToString("N"));
            WalletCoins = res.Balance;
            ShopStatus = $"Готово! «{item.Name}» куплен. Баланс: {res.Balance} FL.";
            await RefreshWalletAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Покупка за монеты не удалась");
            ShopStatus = "Покупка не удалась: " + ex.Message;
        }
        finally { IsShopBusy = false; }
    }

    [RelayCommand]
    private async Task OpenShopCaseAsync(ShopItem? item)
    {
        if (IsShopBusy || !EnsureCanBuy(item, out var session)) return;
        IsShopBusy = true;
        ShopStatus = $"Открываем «{item!.Name}»…";
        try
        {
            session = await _auth.RefreshFirebaseTokenAsync(session);
            _session = session;
            // Результат выбирает сервер (Cloud Function), клиент ничего не подкручивает.
            var r = await _wallet.OpenCaseAsync(session, item.Id, Guid.NewGuid().ToString("N"));
            if (r.Balance is { } b) WalletCoins = b;
            ShopStatus = r.Kind == "coins" && r.Coins is { } c
                ? $"Выпало {c} FL Coins!"
                : r.Duplicate
                    ? $"Дубликат — возвращено {r.CoinsRefunded} FL."
                    : $"Выпал предмет: {r.ItemId}. Он уже в профиле.";
            await RefreshWalletAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Не удалось открыть кейс");
            ShopStatus = "Не удалось открыть кейс (нужен кейс в инвентаре): " + ex.Message;
        }
        finally { IsShopBusy = false; }
    }

    // ---------- кошелёк / инвентарь ----------
    [RelayCommand]
    private void OpenCoinTopUp()
    {
        if (IsZbtLocked) return;
        CurrentPage = "Shop";
        SelectedShopCategory = "currency";
        _ = LoadShopAsync();
    }

    private async Task RefreshWalletAsync()
    {
        if (_session?.FirebaseUid is null)
        {
            WalletCoins = 0;
            OwnedShopItems.Clear(); CaseInventory.Clear();
            HasWalletInventory = false;
            WalletStatus = "Войдите через Microsoft, чтобы видеть баланс FL Coins.";
            return;
        }
        try
        {
            _session = await _auth.RefreshFirebaseTokenAsync(_session);
            var snap = await _wallet.GetSnapshotAsync(_session);
            WalletCoins = snap.Coins;
            OwnedShopItems.Clear(); foreach (var o in snap.Owned) OwnedShopItems.Add(o);
            CaseInventory.Clear(); foreach (var c in snap.Cases.Where(c => c.Count > 0)) CaseInventory.Add(c);
            HasWalletInventory = OwnedShopItems.Count > 0 || CaseInventory.Count > 0;
            WalletStatus = $"Обновлено {DateTime.Now:HH:mm}. История операций: {snap.Ledger.Count}.";
            StartWalletWatch(_session);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Не удалось загрузить кошелёк");
            WalletStatus = "Кошелёк недоступен: " + ex.Message;
        }
    }

    private void StartWalletWatch(AuthSession session)
    {
        if (_walletWatchCts is not null) return;
        _walletWatchCts = new CancellationTokenSource();
        var ct = _walletWatchCts.Token;
        _ = Task.Run(async () =>
        {
            try { await _wallet.WatchBalanceAsync(session, c => Application.Current.Dispatcher.Invoke(() => WalletCoins = c), ct); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log.LogDebug(ex, "Подписка на баланс остановлена"); }
            finally { _walletWatchCts = null; }
        }, ct);
    }

    private void StopWalletWatch()
    {
        _walletWatchCts?.Cancel();
        _walletWatchCts = null;
    }

    private void StopShopWatch()
    {
        _shopWatchCts?.Cancel();
        _shopWatchCts = null;
        _shopWatchStarted = false;
    }
}
