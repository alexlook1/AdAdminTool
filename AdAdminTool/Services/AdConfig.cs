using System.IO;
using System.Text.Json;

namespace AdAdminTool.Services;

public class AdConfig
{
    public string DcHost { get; set; } = "";
    public string Domain { get; set; } = "";
    public string BaseDn { get; set; } = "";

    public string PsExecPath { get; set; } = "";

    public string VncViewerPath { get; set; } = "";
    public int VncPort { get; set; } = 5900;
    public string VncPassword { get; set; } = "";

    public static AdConfig Load(string path = "appsettings.json")
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Не найден файл конфигурации: {path}");

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<AdConfig>(json)
               ?? throw new InvalidOperationException($"Не удалось прочитать {path}");
    }
}