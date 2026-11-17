using System.IO;
using System.Text.Json;
using Velopack.Sources;

namespace ApolloOverlay;

/// <summary>Finds a channel across mixed GitHub release pages within a bounded metadata budget.</summary>
internal static class GitHubReleaseCatalog
{
    internal const int PageSize = 100;
    internal const int MaximumPages = 10;
    private const int MaximumCharacters = 8 * 1024 * 1024;
    private const int MaximumMatches = 12;

    internal static async Task<GithubRelease[]> ReadAsync(string channel, Func<int, Task<string>> readPage)
    {
        var matching = new List<(DateTimeOffset Date, GithubRelease Release)>();
        var characters = 0;
        for (var page = 1; page <= MaximumPages; page++)
        {
            var text = await readPage(page).ConfigureAwait(false);
            if (text.Length > MaximumCharacters - characters)
                throw new InvalidDataException("The release list exceeds the allowed size.");
            characters += text.Length;
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("The release list is invalid.");
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.GetProperty("draft").GetBoolean() || item.GetProperty("prerelease").GetBoolean() != (channel == "testing"))
                    continue;
                if (!item.GetProperty("assets").EnumerateArray().Any(a => a.GetProperty("name").GetString() == "releases." + channel + ".json"))
                    continue;
                var release = JsonSerializer.Deserialize<GithubRelease>(item.GetRawText()) ?? throw new InvalidDataException("Invalid release.");
                matching.Add((item.GetProperty("published_at").GetDateTimeOffset(), release));
            }

            if (matching.Count >= MaximumMatches || document.RootElement.GetArrayLength() < PageSize)
                break;
            if (page == MaximumPages && matching.Count == 0)
                throw new InvalidDataException("The release search reached its page limit before finding this channel. Try again later.");
        }

        return matching.OrderByDescending(r => r.Date).Take(MaximumMatches).Select(r => r.Release).ToArray();
    }
}
