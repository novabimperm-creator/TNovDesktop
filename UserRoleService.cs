using System.IO;

namespace TNovDesktop
{
    /// <summary>
    /// Общая логика определения подразделения и роли пользователя по файлу roles.txt.
    /// Используется вкладкой «Задания».
    /// </summary>
    internal static class UserRoleService
    {
        private const string RolesFile = @"\\fs-nova\Distr\0.For Admin\_TNov\roles.txt";

        /// <summary>Код подразделения пользователя из roles.txt (пустая строка, если не найден).</summary>
        public static string GetDepartmentCode(string userName)
        {
            if (string.IsNullOrEmpty(userName))
                return "";

            try
            {
                foreach (string role in File.ReadAllLines(RolesFile))
                {
                    if (role.Contains(userName))
                    {
                        string[] parts = role.Split(',');
                        if (parts.Length > 1)
                            return parts[1];
                    }
                }
            }
            catch { /* сеть недоступна или файл отсутствует */ }

            return "";
        }

        /// <summary>Человекочитаемая метка роли по коду подразделения.</summary>
        public static string GetDepartmentLabel(string departmentCode) => departmentCode switch
        {
            "BIM" => "BIM",
            "AR" => "АР",
            "ST" => "КР",
            "VK" => "ВК",
            "OV" => "ОВ",
            "EL" => "ЭЛ",
            "SS" => "СС",
            _ => ""
        };
    }
}
