using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using AdAdminTool.Models;

namespace AdAdminTool.Services;

public class PsExecService
{
    private readonly string _psExecPath;

    public PsExecService(string psExecPath)
    {
        if (string.IsNullOrWhiteSpace(psExecPath))
            throw new ArgumentException("PsExecPath пуст", nameof(psExecPath));
        _psExecPath = psExecPath;
    }

    public string PsExecPath => _psExecPath;

    private static bool IsLocalHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return true;
        var h = host.Trim().TrimStart('\\');
        if (h.Equals(".", StringComparison.OrdinalIgnoreCase)) return true;
        if (h.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (h.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)) return true;
        if (h.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string? host,
        string arguments,
        int timeoutSeconds = 60,
        CancellationToken ct = default)
    {
        bool local = IsLocalHost(host);

        var psi = new ProcessStartInfo
        {
            FileName = local ? "cmd.exe" : _psExecPath,
            Arguments = local
                ? $"/c {arguments}"
                : $"-accepteula -nobanner \\\\{host!.TrimStart('\\')} {arguments}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException(
                $"Команда не завершилась за {timeoutSeconds} сек: {arguments}");
        }

        await Task.Delay(50, CancellationToken.None);
        return (process.ExitCode, stdout.ToString(), stderr.ToString());
    }


    public async Task<SystemInfo> CollectAsync(
        string? host,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        var info = new SystemInfo();
        log ??= _ => { };

        async Task<string> Exec(string args, string label)
        {
            log($"> {label}: {args}");
            var r = await RunAsync(host, args, 60, ct);
            log($"  exit={r.ExitCode}, stdout={r.StdOut.Length} симв., stderr={r.StdErr.Length} симв.");
            if (r.ExitCode != 0 && r.StdErr.Length > 0)
                log("  stderr: " + FirstLines(r.StdErr, 3));
            return r.StdOut;
        }

        var sysOut = await Exec("systeminfo /fo csv", "systeminfo");
        ParseSystemInfoCsv(sysOut, info);

        var cpuOut = await Exec("wmic cpu get Name,NumberOfCores,MaxClockSpeed /format:csv", "cpu");
        ParseWmicCsv(cpuOut, row =>
        {
            info.CpuName = row.GetValueOrDefault("Name", "");
            info.CpuCores = row.GetValueOrDefault("NumberOfCores", "");
            info.CpuClockSpeed = row.GetValueOrDefault("MaxClockSpeed", "");
        });

        var memOut = await Exec("wmic ComputerSystem get TotalPhysicalMemory /format:csv", "ram");
        ParseWmicCsv(memOut, row =>
        {
            var total = ParseLong(row.GetValueOrDefault("TotalPhysicalMemory", "0"));
            if (total > 0) info.RamTotalGB = $"{total / 1024d / 1024 / 1024:F1} GB";
        });

        var diskOut = await Exec(
            "wmic LogicalDisk where DriveType=3 get DeviceID,Size,FreeSpace /format:csv",
            "disk");
        ParseWmicCsv(diskOut, row =>
        {
            var size = ParseLong(row.GetValueOrDefault("Size", "0"));
            var free = ParseLong(row.GetValueOrDefault("FreeSpace", "0"));
            if (size > 0)
            {
                info.DiskTotalGB = $"{size / 1024d / 1024 / 1024:F1} GB";
                info.DiskFreeGB = $"{free / 1024d / 1024 / 1024:F1} GB";
            }
        });

        var csOut = await Exec("wmic csproduct get Name,IdentifyingNumber /format:csv", "csproduct");
        ParseWmicCsv(csOut, row =>
        {
            info.Motherboard = row.GetValueOrDefault("Name", "");
            info.SerialNumber = row.GetValueOrDefault("IdentifyingNumber", "");
        });

        var biosOut = await Exec("wmic bios get Version /format:csv", "bios");
        ParseWmicCsv(biosOut, row => info.BiosVersion = row.GetValueOrDefault("Version", ""));

        var ipOut = await Exec("ipconfig", "ipconfig");
        ParseIpConfig(ipOut, info);

        var quserOut = await Exec("quser", "quser");
        ParseQuser(quserOut, info);

        if (string.IsNullOrWhiteSpace(info.SessionId))
            log($"[QUser] не нашли ID сессии. Вывод quser: {FirstLines(quserOut, 3)}");
        else
            log($"[QUser] LoggedUser={info.LoggedUser}, SessionId={info.SessionId}");
        return info;
    }

    private static string FirstLines(string s, int n) =>
        string.Join(" | ",
            s.Split('\n', StringSplitOptions.RemoveEmptyEntries)
             .Take(n)
             .Select(x => x.Trim()));

    // ====== Парсеры ======

    private static readonly Dictionary<string, string[]> SysInfoAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["OsName"] = new[] { "OS Name", "Имя ОС" },
            ["OsVersion"] = new[] { "OS Version", "Версия ОС" },
            ["OsBuild"] = new[] { "OS Build Type", "Тип сборки ОС" },
            ["OsArchitecture"] = new[] { "System Type", "Тип системы" },
            ["InstallDate"] = new[] { "Install Date", "Дата установки" },
            ["LastBootUpTime"] = new[] { "System Boot Time", "Время загрузки системы" },
            ["Domain"] = new[] { "Domain", "Домен" }
        };

    private static void ParseSystemInfoCsv(string csv, SystemInfo info)
    {
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim('\r')).Where(l => l.Length > 0).ToList();
        if (lines.Count < 2) return;

        var headers = SplitCsvLine(lines[0]);
        var values = SplitCsvLine(lines[1]);

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < Math.Min(headers.Count, values.Count); i++)
            dict[headers[i].Trim()] = values[i].Trim();

        string Pick(string key) =>
            SysInfoAliases[key]
                .Select(a => dict.GetValueOrDefault(a, ""))
                .FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "";

        info.OsName = Pick("OsName");
        info.OsVersion = Pick("OsVersion");
        info.OsBuild = Pick("OsBuild");
        info.OsArchitecture = Pick("OsArchitecture");
        info.InstallDate = Pick("InstallDate");
        info.LastBootUpTime = Pick("LastBootUpTime");
        info.Domain = Pick("Domain");
    }

    private static List<string> SplitCsvLine(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;
        foreach (char c in line)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (c == ',' && !inQuotes) { result.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        result.Add(current.ToString());
        return result;
    }

    private static void ParseWmicCsv(string csv, Action<Dictionary<string, string>> onRow)
    {
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim('\r')).Where(l => l.Length > 0).ToList();
        if (lines.Count < 2) return;

        var headers = lines[0].Split(',').Select(h => h.Trim()).ToArray();
        foreach (var line in lines.Skip(1))
        {
            var values = line.Split(',');
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < Math.Min(headers.Length, values.Length); i++)
                dict[headers[i]] = values[i].Trim();
            onRow(dict);
        }
    }

    private static void ParseIpConfig(string output, SystemInfo info)
    {
        var ipMatch = Regex.Match(output, @"IPv4[^:]*:\s*([0-9\.]+)");
        if (ipMatch.Success) info.IpAddress = ipMatch.Groups[1].Value;

        var macMatch = Regex.Match(
            output,
            @"(?:Physical Address|Физический адрес)[^:]*:\s*([0-9A-Fa-f\-]{17})");
        if (macMatch.Success) info.MacAddress = macMatch.Groups[1].Value;
    }

    private static void ParseQuser(string output, SystemInfo info)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimStart('>', ' ').Trim())
            .Where(l => l.Length > 0)
            .ToList();

        foreach (var line in lines)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;

            for (int i = 1; i < parts.Length; i++)
            {
                if (int.TryParse(parts[i], out var sid) && sid > 0)
                {
                    info.LoggedUser = parts[0];
                    info.SessionId = sid.ToString();
                    return;
                }
            }
        }
    }

    public async Task<(string User, string SessionId)> GetActiveSessionAsync(
    string? host, CancellationToken ct = default)
    {
        var r = await RunAsync(host, "quser", 30, ct);
        var tmp = new SystemInfo();
        ParseQuser(r.StdOut, tmp);
        return (tmp.LoggedUser, tmp.SessionId);
    }
    public async Task<string?> CaptureScreenshotAsync(
    string? host,
    string sessionId,
    CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(sessionId))
            return null;

        var remoteHost = host.TrimStart('\\');
        var fileName = $"screenshot_{Guid.NewGuid():N}.png";
        var remotePath = $@"C:\Windows\Temp\{fileName}";

        // PowerShell-скрипт: делает снимокprimary-экрана и сохраняет в PNG.
        var psScript = $@"
Add-Type -AssemblyName System.Windows.Forms,System.Drawing
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
$bmp.Save('{remotePath}', [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
";

        // Экранируем кавычки для передачи в командную строку.
        var escaped = psScript.Replace("\"", "\\\"");

        var arguments =
            $"-accepteula -nobanner -s -i {sessionId} \\\\{remoteHost} " +
            $"powershell.exe -WindowStyle Hidden -Command \"{escaped}\"";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _psExecPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = new Process { StartInfo = psi };
            process.Start();
            await process.WaitForExitAsync(ct);

            // Копируем файл с удалённой машины через административную шару C$.
            var uncPath = $@"\\{remoteHost}\C$\Windows\Temp\{fileName}";
            var localPath = Path.Combine(Path.GetTempPath(), fileName);

            // Иногда PsExec возвращается раньше, чем файл полностью записан.
            for (int i = 0; i < 10; i++)
            {
                if (File.Exists(uncPath))
                {
                    File.Copy(uncPath, localPath, overwrite: true);
                    try { File.Delete(uncPath); } catch { /* не критично */ }
                    return localPath;
                }
                await Task.Delay(300, ct);
            }
        }
        catch
        {
            // Ошибки скриншота не должны ломать основной сбор информации.
        }

        return null;
    }


    public async Task<string?> GetIdleTimeAsync(
        string? host, string sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(sessionId))
            return null;

        var remoteHost = host.TrimStart('\\');
        var fileName = $"idle_{Guid.NewGuid():N}.txt";
        var remotePath = $@"C:\Windows\Temp\{fileName}";

        // PowerShell-скрипт: считает idle и пишет результат в файл
        var psScript = $@"
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class IdleCheck {{
    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO {{ public uint cbSize; public uint dwTime; }}
    [DllImport(""user32.dll"")]
    public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    public static uint GetIdleMs() {{
        LASTINPUTINFO lii = new LASTINPUTINFO();
        lii.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
        if (!GetLastInputInfo(ref lii)) return 0;
        return ((uint)Environment.TickCount - lii.dwTime);
    }}
}}
'@
[IdleCheck]::GetIdleMs() | Out-File -FilePath '{remotePath}' -Encoding ascii
";

        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(psScript));

        // Ключ -s добавлен: SYSTEM имеет лучший доступ к сессиям пользователей.
        var arguments = $"-accepteula -nobanner -s -i {sessionId} \\\\{remoteHost} " +
                        $"powershell.exe -NoProfile -EncodedCommand {encoded}";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _psExecPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = new Process { StartInfo = psi };
            process.Start();
            await process.WaitForExitAsync(ct); // stdout нам больше не нужен

            // Копируем файл с удалённой машины
            var uncPath = $@"\\{remoteHost}\C$\Windows\Temp\{fileName}";
            var localPath = Path.Combine(Path.GetTempPath(), fileName);

            for (int i = 0; i < 10; i++) // ждём появления файла
            {
                if (File.Exists(uncPath))
                {
                    File.Copy(uncPath, localPath, overwrite: true);
                    try { File.Delete(uncPath); } catch { } // чистим за собой

                    var text = (await File.ReadAllTextAsync(localPath, ct)).Trim();
                    File.Delete(localPath);

                    if (long.TryParse(text, out _))
                        return text; // вернули миллисекунды
                }
                await Task.Delay(300, ct);
            }

            LastError = "Файл с результатом не появился на удалённой машине.";
            return null;
        }
        catch (Exception ex)
        {
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    public string? LastError { get; private set; }

    private static long ParseLong(string s) =>
        long.TryParse(s, out var v) ? v : 0;
}