using System.IO.Abstractions;

namespace EmailMcp;

public static class DotEnv
{
    public static void Load(string filePath = ".env", IFileSystem? fileSystem = null)
    {
        fileSystem ??= new FileSystem();

        if (!fileSystem.File.Exists(filePath))
            return;

        foreach (var line in fileSystem.File.ReadAllLines(filePath))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                continue;

            var parts = trimmed.Split('=', 2);
            if (parts.Length != 2)
                continue;

            var key = parts[0].Trim();
            var value = parts[1].Trim().Trim('"', '\'');

            Environment.SetEnvironmentVariable(key, value);
        }
    }
}