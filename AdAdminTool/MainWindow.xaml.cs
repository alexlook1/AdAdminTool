using AdAdminTool.Models;
using AdAdminTool.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AdAdminTool;

public partial class MainWindow : Window
{
    private readonly AdConfig _cfg;
    private readonly AdService _ad;
    private readonly ConnectivityService _connectivity = new();
    private readonly PsExecService _psExec;

    private List<ComputerView> _allComputers = new();
    private ICollectionView? _view;

    public ObservableCollection<InfoItem> PcInfo { get; } = new();
    public ObservableCollection<InfoItem> UserInfo { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        PcInfoItems.ItemsSource = PcInfo;
        UserInfoItems.ItemsSource = UserInfo;
        UpdateStatusBar(null);

        _cfg = AdConfig.Load();
        _ad = new AdService(_cfg);

        if (string.IsNullOrWhiteSpace(_cfg.PsExecPath))
            throw new InvalidOperationException("В appsettings.json не задан PsExecPath.");

        _psExec = new PsExecService(_cfg.PsExecPath);

        Loaded += MainWindow_Loaded;


    }

    // ====== Старт ======

    private void LoadScreenshot(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(path, UriKind.Absolute);
        bmp.EndInit();
        bmp.Freeze();
        ScreenshotImage.Source = bmp;
    }

    private async Task TryCaptureScreenshotAsync(ComputerView cv)
    {
        if (!cv.Computer.IsOnline) return;

        var host = !string.IsNullOrWhiteSpace(cv.Computer.DnsHostName)
            ? cv.Computer.DnsHostName
            : cv.Computer.Name;

        var (user, sessionId) = await _psExec.GetActiveSessionAsync(host);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            Log("[Screenshot] Активная сессия не найдена — скриншот пропущен.");
            return;
        }

        Log($"[Screenshot] Сессия {sessionId}, пользователь {user}. Снимаем...");
        var path = await _psExec.CaptureScreenshotAsync(host, sessionId);
        if (path != null)
        {
            Dispatcher.Invoke(() => LoadScreenshot(path));
            Log("[Screenshot] Снимок получен.");
        }
        else
        {
            Log("[Screenshot] Не удалось получить снимок.");
        }
    }

    private async void RefreshScreenshot_Click(object sender, RoutedEventArgs e)
    {
        if (ComputersList.SelectedItem is not ComputerView cv)
        {
            Log("[Screenshot] ПК не выбран.");
            return;
        }
        if (!cv.Computer.IsOnline)
        {
            Log("[Screenshot] ПК офлайн.");
            return;
        }

        try
        {
            await TryCaptureScreenshotAsync(cv);
        }
        catch (Exception ex)
        {
            Log($"[Screenshot] Ошибка: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(_cfg.PsExecPath))
            Log($"[!] PsExec не найден: {_cfg.PsExecPath} — сбор информации работать не будет.");
        if (!string.IsNullOrWhiteSpace(_cfg.VncViewerPath) && !File.Exists(_cfg.VncViewerPath))
            Log($"[!] vncviewer.exe не найден: {_cfg.VncViewerPath} — кнопка VNC работать не будет.");

        await LoadAllAsync();
    }

    private async Task LoadAllAsync()
    {
        try
        {
            Log("Загрузка данных из AD...");
            var computers = await Task.Run(() => _ad.GetComputers());
            var users = await Task.Run(() => _ad.GetUsers());

            var userLookup = new Dictionary<string, AdUser>(StringComparer.OrdinalIgnoreCase);
            foreach (var u in users)
            {
                var key = NormalizeLogin(u.SamAccountName);
                if (!string.IsNullOrEmpty(key) && !userLookup.ContainsKey(key))
                    userLookup[key] = u;
            }

            _allComputers = computers
                .Select(pc =>
                {
                    var key = NormalizePcName(pc.Name);
                    userLookup.TryGetValue(key, out var matched);
                    return new ComputerView(pc, matched);
                })
                .OrderBy(c => c.DisplayTitle, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            _view = CollectionViewSource.GetDefaultView(_allComputers);
            _view.Filter = FilterComputer;
            ComputersList.ItemsSource = _view;

            var matched = _allComputers.Count(c => c.MatchedUser != null);
            Log($"ПК: {computers.Count}, пользователей: {users.Count}, сопоставлено: {matched}");

            _ = CheckOnlineStatusAsync();
        }
        catch (Exception ex)
        {
            Log("Ошибка загрузки AD: " + ex.Message);
        }
    }

    // ====== Сопоставление имён ======

    private static string NormalizeLogin(string s) =>
        new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static string NormalizePcName(string pcName)
    {
        var dash = pcName.IndexOf('-');
        var core = dash >= 0 ? pcName[(dash + 1)..] : pcName;
        return NormalizeLogin(core);
    }

    // ====== Поиск ======

    private bool FilterComputer(object obj)
    {
        if (obj is not ComputerView cv) return false;
        var q = SearchBox.Text?.Trim();
        if (string.IsNullOrEmpty(q)) return true;
        return cv.SearchText.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => _view?.Refresh();

    // ====== Проверка онлайн-статуса ======

    private async Task CheckOnlineStatusAsync()
    {
        using var semaphore = new SemaphoreSlim(10);

        var tasks = _allComputers.Select(async cv =>
        {
            await semaphore.WaitAsync();
            try
            {
                var pc = cv.Computer;
                var primary = !string.IsNullOrWhiteSpace(pc.DnsHostName)
                    ? pc.DnsHostName
                    : pc.Name;

                var (ok, details) = await _connectivity.CheckAsync(primary);

                if (!ok &&
                    !string.IsNullOrWhiteSpace(pc.DnsHostName) &&
                    !string.Equals(primary, pc.Name, StringComparison.OrdinalIgnoreCase))
                {
                    var (ok2, d2) = await _connectivity.CheckAsync(pc.Name);
                    if (ok2) { ok = true; details = $"fallback Name: {d2}"; }
                    else { details += $" | fallback Name тоже не ответил: {d2}"; }
                }

                pc.IsOnline = ok;
                Log($"[Online] {pc.Name} (проверяли: {primary}) — {(ok ? "ОНЛАЙН" : "офлайн")} | {details}");
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        _view?.Refresh();
        Log("Проверка доступности завершена.");
    }

    // ====== Выбор ПК ======

    private async void ComputersList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ComputersList.SelectedItem is not ComputerView cv)
        {
            PcInfo.Clear(); UserInfo.Clear(); UpdateStatusBar(null); VncButton.IsEnabled = false;
            return;
        }

        FillPcInfo(cv.Computer);
        FillUserInfo(cv.MatchedUser);
        UpdateStatusBar(cv.Computer);
        VncButton.IsEnabled = cv.Computer.IsOnline;

        if (!cv.Computer.IsOnline)
        {
            Log($"[PsExec] {cv.Computer.Name} офлайн, сбор информации пропущен.");
            return;
        }

        Log($"[PsExec] Сбор информации с {cv.Computer.Name} ({cv.Computer.DnsHostName})...");
        try
        {
            // 1. Сначала — снимок экрана активной сессии
            await TryCaptureScreenshotAsync(cv);

            // 2. Затем — полный сбор системной информации
            var sysInfo = await _psExec.CollectAsync(
                cv.Computer.DnsHostName,
                msg => Dispatcher.Invoke(() => Log(msg)));

            FillPcInfoAdvanced(cv.Computer, sysInfo);

            // 3. Если первичный quser не нашёл сессию, а CollectAsync нашёл — попробуем ещё раз
            if (ScreenshotImage.Source is null && !string.IsNullOrWhiteSpace(sysInfo.SessionId))
            {
                var path = await _psExec.CaptureScreenshotAsync(
                    cv.Computer.DnsHostName, sysInfo.SessionId);
                if (path != null)
                    Dispatcher.Invoke(() => LoadScreenshot(path));
            }

            Log($"[PsExec] Готово: {cv.Computer.Name}");
        }
        catch (Exception ex)
        {
            Log($"[PsExec] Ошибка: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ====== Заполнение панелей ======

    private void FillPcInfo(AdComputer pc)
    {
        PcInfo.Clear();
        AddPcInfo("Имя", pc.Name);
        AddPcInfo("DNS-имя", pc.DnsHostName);
        AddPcInfo("ОС", pc.OperatingSystem);
        AddPcInfo("DN", pc.DistinguishedName);
        AddPcInfo("Статус", pc.IsOnline ? "Онлайн" : "Офлайн");
    }

    private void AddPcInfo(string label, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            PcInfo.Add(new InfoItem(label, value));
    }

    private void FillPcInfoAdvanced(AdComputer pc, SystemInfo si)
    {
        PcInfo.Clear();

        // --- Базовая часть из AD ---
        AddPcInfo("Имя", pc.Name);
        AddPcInfo("DNS-имя", pc.DnsHostName);
        AddPcInfo("Статус", pc.IsOnline ? "Онлайн" : "Офлайн");

        // --- Дополнение из PsExec ---
        // ОС: если PsExec вернул пусто (частичный сбор) — оставляем значение из AD.
        AddPcInfo("ОС", string.IsNullOrWhiteSpace(si.OsName)
                                     ? pc.OperatingSystem
                                     : si.OsName);
        AddPcInfo("Версия", si.OsVersion);
        AddPcInfo("Сборка", si.OsBuild);
        AddPcInfo("Архитектура", si.OsArchitecture);
        AddPcInfo("Установлена", si.InstallDate);
        AddPcInfo("Загрузка", si.LastBootUpTime);
        AddPcInfo("Домен", si.Domain);

        AddPcInfo("CPU", si.CpuName);
        AddPcInfo("Ядра", si.CpuCores);
        AddPcInfo("Частота", si.CpuClockSpeed);
        AddPcInfo("RAM", si.RamTotalGB);
        AddPcInfo("Диск", si.DiskTotalGB);
        AddPcInfo("Свободно", si.DiskFreeGB);
        AddPcInfo("Материнская плата", si.Motherboard);
        AddPcInfo("BIOS", si.BiosVersion);
        AddPcInfo("S/N", si.SerialNumber);

        AddPcInfo("IP", si.IpAddress);
        AddPcInfo("MAC", si.MacAddress);
        AddPcInfo("Сессия", si.LoggedUser);

        // --- Метаданные AD в конце ---
        AddPcInfo("DN", pc.DistinguishedName);

        if (PcInfo.Count == 0)
            PcInfo.Add(new InfoItem("Данные", "не получены"));
    }

    private void FillUserInfo(AdUser? user)
    {
        UserInfo.Clear();

        if (user is null)
        {
            UserInfo.Add(new InfoItem("ФИО", "не определён"));
            UserInfo.Add(new InfoItem("Логин", "—"));
            UserInfo.Add(new InfoItem("Отдел", "—"));
            UserInfo.Add(new InfoItem("Описание", "—"));
            UserInfo.Add(new InfoItem("Телефон", "—"));
            return;
        }

        UserInfo.Add(new InfoItem("ФИО", string.IsNullOrEmpty(user.DisplayName)
            ? user.SamAccountName : user.DisplayName));
        UserInfo.Add(new InfoItem("Логин", user.SamAccountName));
        UserInfo.Add(new InfoItem("Отдел", string.IsNullOrWhiteSpace(user.Department)
            ? "—" : user.Department));
        UserInfo.Add(new InfoItem("Описание", string.IsNullOrWhiteSpace(user.Description)
            ? "—" : user.Description));
        UserInfo.Add(new InfoItem("Телефон", string.IsNullOrWhiteSpace(user.TelephoneNumber)
            ? "—" : user.TelephoneNumber));
        UserInfo.Add(new InfoItem("Разрешен вход на:", string.IsNullOrWhiteSpace(user.LogonWorkstationsDisplay)
            ? "Все ПК" : user.LogonWorkstationsDisplay));
        UserInfo.Add(new InfoItem("DN", user.DistinguishedName));
    }

    private void UpdateStatusBar(AdComputer? pc)
    {
        if (pc is null)
        {
            StatusLabel.Text = "не выбран";
            StatusDot.Fill = Brushes.Gray;
            return;
        }
        StatusLabel.Text = pc.IsOnline ? "Онлайн" : "Офлайн";
        StatusDot.Fill = pc.IsOnline ? Brushes.LimeGreen : Brushes.Gray;
    }

    // ====== Действия ======

    private void VncButton_Click(object sender, RoutedEventArgs e)
    {
        if (ComputersList.SelectedItem is not ComputerView cv) return;
        Log($"[VNC] TODO: подключение к {cv.Computer.DnsHostName} " +
            $"(viewer={_cfg.VncViewerPath}, port={_cfg.VncPort})");
    }

    private void ClearConsole_Click(object sender, RoutedEventArgs e) => ConsoleOutput.Clear();

    private void Log(string message)
    {
        ConsoleOutput.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
        ConsoleOutput.ScrollToEnd();
    }
}