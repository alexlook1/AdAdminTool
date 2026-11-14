using System.DirectoryServices;
using System.Security.Principal;

namespace AdAdminTool.Services;

/// <summary>
/// Авторизация в приложении под учётной записью, под которой выполнен вход в Windows.
/// Логин берётся из текущего токена Windows (Environment.UserDomainName\UserName),
/// затем выполняется проверка подлинности в Active Directory (LDAP-привязка с текущими
/// мандатами процесса — Kerberos/NTLM через контекст безопасности вошедшего пользователя)
/// и загрузка его учётной записи из AD.
/// </summary>
public class AuthService
{
    private readonly AdConfig _cfg;

    public AuthService(AdConfig cfg) => _cfg = cfg;

    /// <summary>Имя вошедшего пользователя: домен\логин.</summary>
    public static string CurrentLogin =>
        $@"{Environment.UserDomainName}\{Environment.UserName}";

    /// <summary>Отображаемое имя пользователя (DisplayName из AD), если удалось получить.</summary>
    public string DisplayName { get; private set; } = "";

    /// <summary>UPN пользователя (userPrincipalName из AD).</summary>
    public string UserPrincipalName { get; private set; } = "";

    /// <summary>DN найденной учётной записи в AD.</summary>
    public string DistinguishedName { get; private set; } = "";

    /// <summary>
    /// Выполняет авторизацию текущего пользователя Windows в AD.
    /// Возвращает false, если учётная запись не найдена или привязка к AD отклонена.
    /// </summary>
    public bool AuthenticateCurrentUser(out string error)
    {
        error = "";

        // 1. Убеждаемся, что процесс запущен под доменной учётной записью Windows.
        using (var id = WindowsIdentity.GetCurrent())
        {
            if (id.IsSystem || id.IsGuest)
            {
                error = "Приложение запущено не от пользовательской учётной записи.";
                return false;
            }
        }

        var login = CurrentLogin;

        try
        {
            // 2. Проверяем наличие пользователя в AD и читаем его атрибуты.
            //    Привязка выполняется с текущими мандатами процесса (без пароля):
            //    AuthenticationTypes.Secure использует Kerberos/NTLM вошедшего пользователя.
            using var root = OpenRoot();
            using var searcher = new DirectorySearcher(root)
            {
                Filter = $"(&(objectCategory=person)(objectClass=user)(|(sAMAccountName={Escape(Environment.UserName)})(userPrincipalName={Escape(Environment.UserName)})))",
                PageSize = 10,
                PropertiesToLoad =
                {
                    "samAccountName", "displayName", "userPrincipalName",
                    "distinguishedName", "mail"
                }
            };

            var result = searcher.FindOne();
            if (result == null)
            {
                error = $"Пользователь {login} не найден в Active Directory ({_cfg.DcHost}/{_cfg.BaseDn}).";
                return false;
            }

            DistinguishedName = Get(result, "distinguishedName");
            DisplayName = Get(result, "displayName");
            UserPrincipalName = Get(result, "userPrincipalName");

            return true;
        }
        catch (DirectoryServicesCOMException ex)
        {
            error = $"Ошибка обращения к Active Directory: {ex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            error = $"Ошибка авторизации: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private DirectoryEntry OpenRoot()
    {
        var path = string.IsNullOrWhiteSpace(_cfg.DcHost)
            ? $"LDAP://{_cfg.BaseDn}"
            : $"LDAP://{_cfg.DcHost}/{_cfg.BaseDn}";

        // Пустые логин/пароль => привязка с текущим токеном Windows (вошедший пользователь).
        return new DirectoryEntry(path, null, null, AuthenticationTypes.Secure);
    }

    private static string Escape(string value)
        => value.Replace("\\", "\\5c").Replace("*", "\\2a")
                .Replace("(", "\\28").Replace(")", "\\29").Replace("\0", "\\00");

    private static string Get(SearchResult r, string name)
        => r.Properties.Contains(name) && r.Properties[name].Count > 0
            ? r.Properties[name][0]?.ToString() ?? ""
            : "";
}
