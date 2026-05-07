namespace KiloImportService.Api.Configuration;

/// <summary>
/// Минимальный загрузчик `.env` без NuGet-зависимостей.
/// Корневой `.env` репозитория — единственный источник секретов
/// (Visary__BearerToken, VITE_VISARY_API_TOKEN и т.п.). Используется и docker-compose'ом,
/// и локальным `dotnet run`, и Vite.
///
/// При запуске в контейнере docker-compose сам инжектит env-переменные из `.env`
/// в процесс — этот загрузчик в таком случае ничего не находит и тихо ничего не делает.
/// При локальном `dotnet run` ищет `.env` вверх по дереву каталогов, начиная от cwd,
/// и подставляет переменные в `Environment` ДО `WebApplication.CreateBuilder(args)`,
/// чтобы стандартный `AddEnvironmentVariables` их подхватил.
/// </summary>
internal static class DotEnvLoader
{
    /// <summary>
    /// Существующая переменная окружения имеет приоритет: env, заданное в shell или в
    /// docker-compose, не перетирается значением из файла.
    /// </summary>
    public static void LoadFromAncestors(string startDir, int maxDepth = 6)
    {
        var dir = new DirectoryInfo(startDir);
        for (var d = 0; dir is not null && d < maxDepth; d++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, ".env");
            if (File.Exists(candidate))
            {
                LoadFile(candidate);
                return;
            }
        }
    }

    private static void LoadFile(string path)
    {
        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();

            // Снимаем парные кавычки, если они есть.
            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') ||
                 (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            if (Environment.GetEnvironmentVariable(key) is null)
                Environment.SetEnvironmentVariable(key, value);
        }
    }
}
