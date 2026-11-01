using Velopack;

namespace ApolloOverlay;

internal static class UpdateAvailability
{
    internal static UpdateCandidate? Newer(IEnumerable<UpdateCandidate> releases, AppBuildInfo current, string selectedChannel)
    {
        if (selectedChannel is not ("stable" or "testing") || !SemanticVersion.TryParse(current.Version, out var installed))
            return null;
        UpdateCandidate? newest = null;
        SemanticVersion? newestVersion = null;
        foreach (var candidate in releases)
        {
            if (candidate is null || candidate.Channel != selectedChannel || candidate.BuildId == current.BuildId
                || !SemanticVersion.TryParse(candidate.Version, out var version) || version <= installed
                || candidate.Asset?.Version is { } assetVersion && assetVersion != version)
                continue;
            if (newestVersion is null || version > newestVersion
                || version == newestVersion && candidate.PublishedAt > newest!.PublishedAt)
            {
                newest = candidate;
                newestVersion = version;
            }
        }
        return newest;
    }
}
