using System.DirectoryServices;
using AdAdminTool.Models;

namespace AdAdminTool.Services;

public class AdService
{
    private readonly AdConfig _cfg;
    public AdService(AdConfig cfg) => _cfg = cfg;

    private DirectoryEntry CreateRoot()
    {
        var path = $"LDAP://{_cfg.DcHost}/{_cfg.BaseDn}";
        // Пустые логин/пароль => привязка к AD с текущим токеном Windows
        // (Kerberos/NTLM вошедшего пользователя), без явных учётных данных.
        return new DirectoryEntry(path, null, null, AuthenticationTypes.Secure);
    }

    public List<AdComputer> GetComputers()
    {
        var result = new List<AdComputer>();
        using var root = CreateRoot();
        using var searcher = new DirectorySearcher(root)
        {
            Filter = "(objectCategory=computer)",
            PageSize = 500,
            SizeLimit = 0
        };
        searcher.PropertiesToLoad.AddRange(new[]
        {
            "name", "dNSHostName", "operatingSystem", "distinguishedName"
        });

        foreach (SearchResult r in searcher.FindAll())
        {
            result.Add(new AdComputer
            {
                Name = Get(r, "name"),
                DnsHostName = Get(r, "dNSHostName"),
                OperatingSystem = Get(r, "operatingSystem"),
                DistinguishedName = Get(r, "distinguishedName")
            });
        }
        return result;
    }

    public List<AdUser> GetUsers()
    {
        var result = new List<AdUser>();
        using var root = CreateRoot();
        using var searcher = new DirectorySearcher(root)
        {
            Filter = "(&(objectCategory=person)(objectClass=user))",
            PageSize = 500
        };
        searcher.PropertiesToLoad.AddRange(new[]
        {
            "samAccountName", "displayName", "mail",
            "description", "telephoneNumber",
            "userWorkstations",          // ← новое
            "distinguishedName"
        });

        foreach (SearchResult r in searcher.FindAll())
        {
            result.Add(new AdUser
            {
                SamAccountName = Get(r, "samAccountName"),
                DisplayName = Get(r, "displayName"),
                Mail = Get(r, "mail"),
                Description = Get(r, "description"),
                TelephoneNumber = Get(r, "telephoneNumber"),
                LogonWorkstationsRaw = Get(r, "userWorkstations"),  // ← новое
                DistinguishedName = Get(r, "distinguishedName")
            });
        }
        return result;
    }

    private static string Get(SearchResult r, string name)
        => r.Properties.Contains(name) && r.Properties[name].Count > 0
            ? r.Properties[name][0]?.ToString() ?? ""
            : "";
}