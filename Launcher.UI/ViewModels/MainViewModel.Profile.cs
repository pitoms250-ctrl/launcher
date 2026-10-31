using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Serilog;

namespace Launcher.UI.ViewModels;

/// <summary>Профиль игрока: аккаунт Microsoft, лицензия Minecraft, статистика, фото, согласие на email, скин для главного экрана.</summary>
public partial class MainViewModel
{
    private const string MinecraftStoreUrl = "https://www.minecraft.net/store/minecraft-java-bedrock-edition-pc";

    [ObservableProperty] private string _profileDisplayName = "Player";
    [ObservableProperty] private string _profileGamertag = "—";
    [ObservableProperty] private string _profileUuid = "—";
    [ObservableProperty] private string _profileEmail = "Не указан";
    [ObservableProperty] private string _profileAccountStatus = "Оффлайн-профиль";
    [ObservableProperty] private string _profileActionLabel = "Войти Microsoft";
    [ObservableProperty] private string _profileActionMessage = "";
    [ObservableProperty] private string _profileAvatarLetter = "P";
    [ObservableProperty] private string _minecraftLicenseStatus = "Не проверена";
    [ObservableProperty] private string _profileLicenseCheckMessage = "Войдите через Microsoft, чтобы проверить лицензию.";
    [ObservableProperty] private string _profileLaunchCount = "0";
    [ObservableProperty] private string _profilePlaytime = "0 ч 0 мин";
    [ObservableProperty] private string _profileLastVersion = "—";

    [ObservableProperty] private BitmapSource? _profilePhoto;
    [ObservableProperty] private bool _hasProfilePhoto;
    [ObservableProperty] private BitmapSource? _playerSkin;

    [ObservableProperty] private string _contactEmail = "";
    [ObservableProperty] private bool _contactEmailConsent;
    [ObservableProperty] private string _contactEmailMessage = "";

    /// <summary>Пересобирает все поля профиля из текущей сессии и сохранённой статистики.</summary>
    private void RefreshProfileState()
    {
        var s = _session;
        if (PlayerSkin is null)
        {
            try { PlayerSkin = LoadDefaultSkin(); }
            catch (Exception ex) { _log.LogError(ex, "Bundled lobby skin could not be loaded"); }
        }
        IsAuthenticated = s is not null;
        AccountLabel = s is null ? "Оффлайн-профиль" : $"Microsoft: {s.Email ?? s.Username}";
        AccountActionLabel = s is null ? "Войти Microsoft" : "Выйти";
        ProfileDisplayName = s?.Username ?? Nickname;
        ProfileAvatarLetter = string.IsNullOrWhiteSpace(ProfileDisplayName) ? "?" : ProfileDisplayName[..1].ToUpperInvariant();
        ProfileGamertag = string.IsNullOrWhiteSpace(s?.XboxGamertag) ? "—" : s!.XboxGamertag!;
        ProfileUuid = s is null || string.IsNullOrWhiteSpace(s.Uuid) ? "—" : s.Uuid;
        ProfileEmail = string.IsNullOrWhiteSpace(s?.Email) ? "Не указан" : s!.Email!;
        ProfileAccountStatus = s is null ? "Оффлайн-профиль" : "Microsoft подключён";
        ProfileActionLabel = s is null ? "Войти Microsoft" : "Выйти";
        MinecraftLicenseStatus = s is null ? "Не проверена" : s.HasMinecraftLicense ? "Куплена" : "Не куплена";
        ProfileLicenseCheckMessage = s is null
            ? "Войдите через Microsoft, чтобы проверить лицензию."
            : s.HasMinecraftLicense ? "Лицензия подтверждена Microsoft." : "На этом аккаунте не найдена Minecraft: Java Edition.";

        var h = (int)(_settings.TotalPlayMinutes / 60);
        var m = (int)(_settings.TotalPlayMinutes % 60);
        ProfileLaunchCount = _settings.LaunchCount.ToString();
        ProfilePlaytime = $"{h} ч {m} мин";
        ProfileLastVersion = string.IsNullOrWhiteSpace(_settings.LastVersion) ? "—" : _settings.LastVersion!;

        ContactEmail = _settings.ContactEmail;
        ContactEmailConsent = _settings.ContactEmailConsent;
        OnPropertyChanged(nameof(ProfileUuid));
        OnPropertyChanged(nameof(CanPurchaseShopItems));
    }

    private void OnAccountChanged()
    {
        RefreshProfileState();
        _ = LoadPlayerSkinAsync();
    }

    // ---------- скин для главного экрана ----------
    private async Task LoadPlayerSkinAsync()
    {
        try
        {
            var name = _session?.Username ?? Nickname;
            var png = string.IsNullOrWhiteSpace(name) ? null : await _skins.DownloadByNickAsync(name.Trim());
            if (png is { Length: > 0 }) PlayerSkin = ToBitmap(png);
            var skin = PlayerSkin;
            Log.Information("LobbySkin: texture loaded. Source={Source}, Width={Width}, Height={Height}",
                png is { Length: > 0 } ? "account" : "default", skin?.PixelWidth ?? 0, skin?.PixelHeight ?? 0);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Player skin download failed; bundled skin remains visible");
        }
    }

    private static BitmapSource ToBitmap(byte[] png)
    {
        var bmp = new BitmapImage();
        using var ms = new MemoryStream(png);
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private static BitmapSource LoadDefaultSkin()
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri("pack://application:,,,/Assets/default_skin.png", UriKind.Absolute);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    // ---------- фото профиля ----------
    private void LoadProfilePhotoFromSettings()
    {
        try
        {
            var p = _settings.ProfilePhotoPath;
            if (string.IsNullOrWhiteSpace(p) || !File.Exists(p)) { HasProfilePhoto = false; ProfilePhoto = null; return; }
            ProfilePhoto = ToBitmap(File.ReadAllBytes(p));
            HasProfilePhoto = true;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Фото профиля не прочиталось");
            HasProfilePhoto = false; ProfilePhoto = null;
        }
    }

    [RelayCommand]
    private void SelectProfilePhoto()
    {
        var dlg = new OpenFileDialog { Filter = "Изображения|*.png;*.jpg;*.jpeg;*.bmp", Title = "Фото профиля" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            // копируем в папку настроек — фото не пропадёт, если исходный файл переместят
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".fluncher");
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, "profile" + Path.GetExtension(dlg.FileName).ToLowerInvariant());
            File.Copy(dlg.FileName, target, true);
            _settings.ProfilePhotoPath = target;
            _store.Save(_settings);
            LoadProfilePhotoFromSettings();
            ProfileActionMessage = "Фото профиля обновлено.";
        }
        catch (Exception ex) { ProfileActionMessage = "Не удалось сохранить фото: " + ex.Message; }
    }

    // ---------- действия профиля ----------
    [RelayCommand]
    private void CopyProfileUuid()
    {
        if (ProfileUuid is { Length: > 1 } u && u != "—")
        {
            try { Clipboard.SetText(u); ProfileActionMessage = "UUID скопирован."; }
            catch { ProfileActionMessage = "Буфер обмена занят, попробуйте ещё раз."; }
        }
    }

    [RelayCommand]
    private void OpenMinecraftPurchase() =>
        Process.Start(new ProcessStartInfo(MinecraftStoreUrl) { UseShellExecute = true });

    [RelayCommand]
    private async Task RecheckMinecraftLicenseAsync()
    {
        if (_session is null) { ProfileLicenseCheckMessage = "Сначала войдите через Microsoft."; return; }
        ProfileLicenseCheckMessage = "Проверяем лицензию…";
        try
        {
            var restored = await _auth.TryRestoreAsync();
            if (restored is not null) _session = restored;
            UpdateAccountState();
            ProfileLicenseCheckMessage = _session?.HasMinecraftLicense == true
                ? "Лицензия подтверждена Microsoft."
                : "Лицензия не найдена. Если вы только что купили игру — подождите пару минут.";
        }
        catch (Exception ex) { ProfileLicenseCheckMessage = "Проверка не удалась: " + ex.Message; }
    }

    [RelayCommand]
    private async Task SaveContactEmailAsync()
    {
        var email = ContactEmail?.Trim() ?? "";
        if (ContactEmailConsent && !System.Text.RegularExpressions.Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
        {
            ContactEmailMessage = "Введите корректный email.";
            return;
        }
        _settings.ContactEmail = email;
        _settings.ContactEmailConsent = ContactEmailConsent;
        _store.Save(_settings);
        ContactEmailMessage = "Сохранено на этом компьютере.";

        // в облако отправляем только после входа и только с согласием
        if (_session?.FirebaseUid is null) { ContactEmailMessage += " Войдите через Microsoft, чтобы синхронизировать."; return; }
        try
        {
            _session = await _auth.RefreshFirebaseTokenAsync(_session);
            var ok = await _cloud.SetPlayerContactAsync(_session, email, ContactEmailConsent);
            ContactEmailMessage = ok ? "Сохранено и синхронизировано." : "Сохранено локально, облако не приняло запрос.";
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Не удалось отправить контакт");
            ContactEmailMessage = "Сохранено локально, облако недоступно.";
        }
    }

    [RelayCommand]
    private void ExitLauncher() => Application.Current.Shutdown();
}
