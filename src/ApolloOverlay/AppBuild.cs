using System.Reflection;

namespace ApolloOverlay;

public sealed record AppBuildInfo(string Version, string BuildId, string Commit, string Channel)
{
    public string ShortCommit => Commit.Length == 40 ? Commit[..7] : Commit;
    public string Display => "v" + Version;
}

/// <summary>Identity belongs to the application assembly, including when called by a test host.</summary>
public static class AppBuild
{
    public static AppBuildInfo Current { get; } = Read(typeof(AppBuild).Assembly);

    private static AppBuildInfo Read(Assembly assembly)
    {
        var fields = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(attribute => attribute.Key, attribute => attribute.Value ?? "", StringComparer.Ordinal);
        string Field(string key, string fallback) => fields.TryGetValue(key, out var value) && value.Length > 0 ? value : fallback;
        return new(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.2.0-dev+local",
            Field("OverlayBuildId", "local"), Field("OverlayCommit", "unknown"), Field("OverlayChannel", "testing"));
    }
}
