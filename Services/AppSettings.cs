using System.Text.Json;

namespace PhotoTools2.Services;

public static class AppSettings
{
    private static readonly string SettingsFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PhotoTools2");

    private static readonly string SettingsFile = Path.Combine(SettingsFolder, "settings.json");
    private static readonly Dictionary<string, string> Values = Load();

    public static string? Get(string key) => Values.GetValueOrDefault(key);

    public static void Set(string key, string value)
    {
        Values[key] = value;
        Directory.CreateDirectory(SettingsFolder);
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(Values, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }

    private static Dictionary<string, string> Load()
    {
        try
        {
            return File.Exists(SettingsFile)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(SettingsFile)) ?? []
                : [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
