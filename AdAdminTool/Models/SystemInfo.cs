namespace AdAdminTool.Models;

public class SystemInfo
{
    public string OsName { get; set; } = "";
    public string OsVersion { get; set; } = "";
    public string OsBuild { get; set; } = "";
    public string OsArchitecture { get; set; } = "";
    public string InstallDate { get; set; } = "";
    public string LastBootUpTime { get; set; } = "";
    public string CpuName { get; set; } = "";
    public string CpuCores { get; set; } = "";
    public string CpuClockSpeed { get; set; } = "";
    public string RamTotalGB { get; set; } = "";
    public string RamFreeGB { get; set; } = "";
    public string DiskTotalGB { get; set; } = "";
    public string DiskFreeGB { get; set; } = "";
    public string Motherboard { get; set; } = "";
    public string BiosVersion { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public string MacAddress { get; set; } = "";
    public string Domain { get; set; } = "";
    public string LoggedUser { get; set; } = "";
    public string SessionId { get; set; } = "";

    public bool HasAnyData => !string.IsNullOrEmpty(OsName) || !string.IsNullOrEmpty(CpuName);
}