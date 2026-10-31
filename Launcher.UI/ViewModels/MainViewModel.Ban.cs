using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Launcher.Core.Models;
using Launcher.Services;
using Microsoft.Extensions.Logging;

namespace Launcher.UI.ViewModels;

/// <summary>Блокировка из CRM в реальном времени (RTDB bans/{uid}) и действия после входа в Microsoft.</summary>
public partial class MainViewModel
{
    private CancellationTokenSource? _banWatchCts;

    [ObservableProperty] private bool _isBanned;
    [ObservableProperty] private string _banMessage = "";

    partial void OnIsBannedChanged(bool value)
    {
        PlayCommand.NotifyCanExecuteChanged();
        InstallOnlyCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Вызывается сразу после успешного входа Microsoft: облачный профиль, бан-вотчер, кошелёк.</summary>
    private async Task OnSignedInAsync()
    {
        if (_session is null) return;
        if (!HasFirebaseSession(_session))
        {
            var reason = AuthService.LastFirebaseError;
            var text = "Вход Microsoft выполнен, но Firebase-сессия не создана — профиль в базу не пишется, баны и магазин недоступны."
                + (string.IsNullOrWhiteSpace(reason) ? "" : " Причина: " + reason);
            _log.LogWarning("Firebase session missing after Microsoft sign-in: {Reason}", reason);
            ProfileActionMessage = LastLogLine = text;
            return;
        }

        StartBanWatch();
        await StartCloudSessionAsync();
        _ = RefreshWalletAsync();
        if (CurrentPage == "Shop") _ = LoadShopAsync();
    }

    private void StartBanWatch()
    {
        StopBanWatch();
        if (!HasFirebaseSession(_session)) return;
        var cts = _banWatchCts = new CancellationTokenSource();
        _ = Task.Run(() => BanWatchLoopAsync(cts.Token));
    }

    private void StopBanWatch()
    {
        _banWatchCts?.Cancel();
        _banWatchCts = null;
    }

    /// <summary>SSE-подписка на bans/{uid}; при обрыве или истёкшем токене переподключается с нарастающей паузой.</summary>
    private async Task BanWatchLoopAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(3);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var current = _session;
                if (!HasFirebaseSession(current)) return;
                var fresh = await _auth.RefreshFirebaseTokenAsync(current!, ct);
                await _realtimeDatabase.WatchBanAsync(fresh, (banned, reason) =>
                {
                    delay = TimeSpan.FromSeconds(3); // соединение живое — сбрасываем паузу
                    Application.Current?.Dispatcher.Invoke(() => ApplyBan(banned, reason));
                }, ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _log.LogDebug(ex, "Подписка на bans прервалась, переподключение"); }

            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { return; }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
        }
    }

    private void ApplyBan(bool banned, string? reason)
    {
        if (banned)
        {
            BanMessage = string.IsNullOrWhiteSpace(reason)
                ? "Ваш аккаунт заблокирован администрацией."
                : $"Ваш аккаунт заблокирован.\nПричина: {reason}";
            if (!IsBanned) _log.LogWarning("Аккаунт заблокирован через CRM (bans/{{uid}})");
            IsBanned = true;
            try { _launch.Stop(); _cts?.Cancel(); }
            catch (Exception ex) { _log.LogDebug(ex, "Не удалось остановить игру при блокировке"); }
        }
        else if (IsBanned)
        {
            IsBanned = false;
            BanMessage = "";
        }
    }

    /// <summary>Разовая проверка прямо перед запуском игры. Нет сети — не блокируем (подписка всё равно догонит).</summary>
    private async Task<bool> IsBannedNowAsync()
    {
        if (IsBanned) return true;
        if (!HasFirebaseSession(_session)) return false;
        try
        {
            var fresh = await _auth.RefreshFirebaseTokenAsync(_session!);
            _session = fresh;
            if (await _realtimeDatabase.IsPlayerBannedAsync(fresh))
            {
                ApplyBan(true, null);
                return true;
            }
        }
        catch (Exception ex) { _log.LogDebug(ex, "Не удалось проверить блокировку перед запуском"); }
        return false;
    }
}
