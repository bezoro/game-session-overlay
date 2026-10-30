using System.IO;

namespace ApolloOverlay;

public sealed class LocalDiagnostics : IDisposable
{
    private readonly Lazy<CommandLog> commands;
    public string Root
    {
        get;
    }
    public string SettingsPath => Path.Combine(Root, "settings.json");
    public CommandLog Commands => commands.Value;

    public LocalDiagnostics(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bezoro", "GameSessionOverlay");
        commands = new(() => new(Root));
    }
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
    public void Log(string category, ResultKind outcome) => Commands.Record(category, outcome);
    public void Dispose()
    {
        if (commands.IsValueCreated)
            commands.Value.Dispose();
    }
}
