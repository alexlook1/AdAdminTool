namespace AdAdminTool.Models;

public class AdUser
{
    public string SamAccountName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Mail { get; set; } = "";
    public string Description { get; set; } = "";
    public string TelephoneNumber { get; set; } = "";
    public string DistinguishedName { get; set; } = "";
    public string LogonWorkstationsRaw { get; set; } = "";  // сырое значение из AD

    public string LogonWorkstationsDisplay
    {
        get
        {
            if (string.IsNullOrWhiteSpace(LogonWorkstationsRaw))
                return "Все ПК";
            var items = LogonWorkstationsRaw
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            return items.Length == 0 ? "Все ПК" : string.Join(", ", items);
        }
    }


    // Цепочка OU от ближайшего к пользователю до OU=Пользователи (не включая её).
    // Пример: CN=Иван,OU=158,OU=Администрация,OU=Пользователи,DC=corp,DC=local
    //     →  "158/Администрация"
    public string Department
    {
        get
        {
            if (string.IsNullOrEmpty(DistinguishedName)) return "";

            var parts = DistinguishedName.Split(
                ',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            var ous = new List<string>();
            foreach (var p in parts)
            {
                if (p.StartsWith("DC=", StringComparison.OrdinalIgnoreCase))
                    break;

                if (p.StartsWith("OU=", StringComparison.OrdinalIgnoreCase))
                {
                    var value = p[3..];

                    // Останавливаемся на корневой OU «Пользователи» / «Users»
                    if (value.Equals("Пользователи", StringComparison.OrdinalIgnoreCase) ||
                        value.Equals("Users", StringComparison.OrdinalIgnoreCase))
                        break;

                    ous.Add(value);
                }
            }

            return string.Join("/", ous);
        }
    }
}