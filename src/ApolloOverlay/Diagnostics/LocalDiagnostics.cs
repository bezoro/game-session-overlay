using System.IO;

namespace ApolloOverlay;

public sealed class LocalDiagnostics
{
    public string Root
    {
        get;
    }
    public string SettingsPath => Path.Combine(Root, "settings.json");

    public LocalDiagnostics(string? root = null) => Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bezoro", "GameSessionOverlay");
    public UserSettings Load(out string status)
    {
        if (File.Exists(SettingsPath))
            return UserSettings.Load(SettingsPath, out status);
        var legacy = Path.Combine(AppContext.BaseDirectory, "data", "settings.json");
        if (File.Exists(legacy))
        {
            var settings = UserSettings.Load(legacy, out status);
            status = "Local settings imported into memory; Save migrates them to AppData and preserves the original.";
            return settings;
        }

        status = "Ready · Guide or double-click the tray icon";
        return new();
    }

    public void Save(UserSettings settings) => UserSettings.Save(SettingsPath, settings);
    public void Log(string category, ResultKind outcome)
    {
        try
        {
            Directory.CreateDirectory(Root);
            var path = Path.Combine(Root, "overlay.log");
            if (File.Exists(path) && new FileInfo(path).Length >= 512 * 1024)
            {
                var old = path + ".2";
                if (File.Exists(old))
                    File.Delete(old);
                if (File.Exists(path + ".1"))
                    File.Move(path + ".1", old);
                File.Move(path, path + ".1");
            }

            File.AppendAllText(path, $"{DateTime.UtcNow:O} {category} {outcome}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
