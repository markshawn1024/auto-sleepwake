using System.Text.Json;

namespace WakeGlass.Core;

public sealed class SettingsStore(string? directory = null) : ISettingsStore
{
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WakeGlass");
    private readonly string root = directory ?? DataDirectory;
    private readonly JsonSerializerOptions json = new() { WriteIndented = true };
    public AppSettings Load() => Read<AppSettings>(Path.Combine(root, "settings.json")) ?? new();
    public void Save(AppSettings settings) => Write(Path.Combine(root, "settings.json"), settings, json);

    internal static T? Read<T>(string path) where T : class
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return JsonSerializer.Deserialize<T>(reader.ReadToEnd()) ?? throw new InvalidDataException("配置文件为空。请从设置中查看诊断。");
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    internal static void Write<T>(string path, T value, JsonSerializerOptions? options = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(value, options)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
