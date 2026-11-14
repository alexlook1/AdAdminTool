using System.DirectoryServices;
using System.DirectoryServices.AccountManagement;
using System.Security.Principal;

namespace AdAdminTool.Services;

/// <summary>
/// Авторизация в приложении под учётной записью, под которой выполнен вход в Windows.
/// Логин берётся из текущего токена Windows (Environment.UserDomainName\UserName),
/// затем выполняется проверка подлинности в Active Directory (LDAP-привязка с текущими
/// мандатами процесса — Kerberos/NTLM через контекст безопасности вошедшего пользователя),
/// загрузка его учётной записи из AD и проверка членства в группе администраторов
/// приложения (по умолчанию "ConnectME_Admins", включая вложенные группы).
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

    /// <summary>SID группы администраторов приложения (если группа найдена в AD).</summary>
    public string? AdminGroupSid { get; private set; }

    /// <summary>DN группы администраторов приложения (если группа найдена в AD).</summary>
    public string? AdminGroupDn { get; private set; }

    /// <summary>
    /// Выполняет авторизацию текущего пользователя Windows в AD И проверяет,
    /// что пользователь состоит в группе администраторов приложения
    /// (<see cref="AdConfig.EffectiveAdminGroupName"/>), включая вложенные группы.
    /// Возвращает false, если учётная запись не найдена, привязка к AD отклонена
    /// или пользователь не является администратором.
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

            // 3. Проверка прав администратора приложения: членство в группе
            //    ConnectME_Admins (или иной, заданной в конфигурации), включая вложенные группы.
            if (!IsMemberOfAdminGroup(out error))
                return false;

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

    /// <summary>
    /// Проверяет членство текущего пользователя Windows в группе администраторов
    /// приложения (по умолчанию "ConnectME_Admins"). Учитываются вложенные группы:
    ///  - основной способ: SID группы сверяется со свойством tokenGroups учётной
    ///    записи в AD и с токеном текущего процесса — оба содержат все группы
    ///    рекурсивно (т.е. членство через вложенные группы тоже засчитывается);
    ///  - запасной способ: GroupPrincipal.GetMembers(true) через AccountManagement.
    /// </summary>
    private bool IsMemberOfAdminGroup(out string error)
    {
        var groupName = _cfg.EffectiveAdminGroupName;

        // Находим группу в AD — получаем её SID и DN.
        string? groupSid = null;
        try
        {
            using (var groupEntry = FindGroupEntry(groupName))
            {
                if (groupEntry != null)
                {
                    try
                    {
                        var sidBytes = groupEntry.Properties["objectSid"][0] as byte[];
                        if (sidBytes is { Length: > 0 })
                            groupSid = new SecurityIdentifier(sidBytes, 0).Value;

                        AdminGroupDn = groupEntry.Properties.Contains("distinguishedName")
                            ? groupEntry.Properties["distinguishedName"][0]?.ToString()
                            : groupEntry.Path;
                    }
                    catch { /* свойства читаем по возможности */ }

                    AdminGroupSid = groupSid;
                }
            }
        }
        catch (Exception ex)
        {
            error = $"Ошибка поиска группы администраторов \"{groupName}\" в Active Directory: " +
                    $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }

        if (groupSid == null)
        {
            error = $"Группа \"{groupName}\" не найдена в Active Directory ({_cfg.DcHost}/{_cfg.BaseDn}). " +
                    "Обратитесь к администратору домена: необходимо создать группу безопасности " +
                    "и включить в неё учётные записи (или группы) администраторов приложения.";
            return false;
        }

        // Способ 1: SID группы в tokenGroups пользователя (рекурсивно, включая вложенные группы)
        // либо в токене текущего процесса.
        if (IsInTokenGroups(groupSid))
            return true;

        // Способ 2: прямая рекурсивная проверка состава группы через AccountManagement.
        try
        {
            using var ctx = string.IsNullOrWhiteSpace(_cfg.Domain)
                ? new PrincipalContext(ContextType.Domain)
                : new PrincipalContext(ContextType.Domain, _cfg.Domain);
            using var group = GroupPrincipal.FindByIdentity(ctx, IdentityType.Sid, groupSid);
            if (group != null &&
                group.GetMembers(true).Any(m => string.Equals(m.SamAccountName, Environment.UserName,
                    StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        catch
        {
            // Если запасной способ недоступен, решение остаётся по первому (токен/tokenGroups).
        }

        error = $"Пользователь {CurrentLogin} не входит в группу администраторов \"{groupName}\" " +
                "(с учётом вложенных групп). Доступ к управлению приложением запрещён.";
        return false;
    }

    /// <summary>
    /// Проверяет присутствие SID группы в свойстве tokenGroups учётной записи
    /// пользователя в AD и (как резерв) в токене текущего процесса.
    /// Оба источника содержат группы рекурсивно, включая вложенные.
    /// </summary>
    private bool IsInTokenGroups(string groupSid)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(DistinguishedName))
            {
                using var userEntry = new DirectoryEntry($"LDAP://{DistinguishedName}", null, null,
                    AuthenticationTypes.Secure);
                foreach (var item in userEntry.Properties["tokenGroups"])
                {
                    if (item is byte[] sidBytes && sidBytes.Length > 0)
                    {
                        var sid = new SecurityIdentifier(sidBytes, 0);
                        if (string.Equals(sid.Value, groupSid, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }

            // Дополнительно сверяем с токеном текущего процесса (SID в нём тоже рекурсивны).
            using var identity = WindowsIdentity.GetCurrent();
            return identity.Groups != null &&
                   identity.Groups.Any(g => string.Equals(g.Value, groupSid, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Ищет объект группы в AD по имени (sAMAccountName).</summary>
    private DirectoryEntry? FindGroupEntry(string groupName)
    {
        using var root = OpenRoot();
        using var searcher = new DirectorySearcher(root)
        {
            Filter = $"(&(objectClass=group)(sAMAccountName={Escape(groupName)}))",
            PropertiesToLoad = { "objectSid", "distinguishedName" },
            PageSize = 5
        };

        var r = searcher.FindOne();
        return r?.GetDirectoryEntry();
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
