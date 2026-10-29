using System.IO;
using System.Security;

namespace ApolloOverlay;

public partial class MainWindow
{
    internal sealed record PlayniteFolder(string Name, string Path);
    internal sealed record PlayniteListing(PlayniteFolder[] Folders, string? Executable, string? Error, bool Truncated);
    private const int PlaynitePageSize = 20;
    private CancellationTokenSource? playniteScan;
    private string? playniteScanFolder;
    private long playniteScanCycle, playniteScanRevision;
    private bool playniteScanStarted;
    private PlayniteListing? playniteListing;
    private void CancelPlayniteScan()
    {
        playniteScanRevision++;
        playniteScan?.Cancel();
        playniteScan?.Dispose();
        playniteScan = null;
        playniteScanStarted = false;
        playniteListing = null;
    }

    private void EnsurePlayniteScan(string? folder)
    {
        if (playniteScanStarted && playniteScanFolder == folder && playniteScanCycle == focusCycle)
            return;
        CancelPlayniteScan();
        playniteScanStarted = true;
        playniteScanFolder = folder;
        playniteScanCycle = focusCycle;
        var revision = playniteScanRevision;
        var cycle = focusCycle;
        playniteScan = new CancellationTokenSource();
        var token = playniteScan.Token;
        _ = LoadPlayniteFolder(folder, revision, cycle, token);
    }

    private async Task LoadPlayniteFolder(string? folder, long revision, long cycle, CancellationToken token)
    {
        PlayniteListing result;
        try
        {
            result = await Task.Run(() => ReadPlayniteFolder(folder, token), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested
            || revision != playniteScanRevision
            || cycle != focusCycle
            || exiting
            || !IsVisible
            || view.Current.Route != OverlayRoute.PlayniteBrowser
            || view.Current.BrowserFolder != folder)
            return;
        playniteListing = result;
        if (navigation.Id == "playnite-loading")
            navigation = new(result.Executable is not null ? "playnite-use" : result.Folders.Length > 0 ? "playnite-folder:0" : "playnite-browser-back");
        RefreshRows();
    }

    internal static PlayniteListing ReadPlayniteFolder(string? folder, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            if (folder is null)
            {
                var drives = DriveInfo.GetDrives().Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable).Select(d => new PlayniteFolder(d.Name, d.RootDirectory.FullName)).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
                return new(drives, null, null, false);
            }

            var full = Path.GetFullPath(folder);
            var root = Path.GetPathRoot(full);
            if (root is null || !Path.IsPathFullyQualified(full) || new DriveInfo(root).DriveType is not (DriveType.Fixed or DriveType.Removable))
                return new([], null, "Choose a local drive.", false);
            var directory = new DirectoryInfo(full);
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                return new([], null, "This folder is a shortcut and cannot be opened here.", false);
            var executable = Path.Combine(full, "Playnite.FullscreenApp.exe");
            var found = File.Exists(executable) ? executable : null;
            var folders = new List<PlayniteFolder>();
            var truncated = false;
            foreach (var item in directory.EnumerateDirectories())
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                        continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }
                catch (IOException)
                {
                    continue;
                }

                if (folders.Count == 2000)
                {
                    truncated = true;
                    break;
                }

                folders.Add(new(item.Name, item.FullName));
            }

            return new(folders.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(), found, null, truncated);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException)
        {
            return new([], null, "Could not open this folder. Go back and choose another.", false);
        }
    }

    private void BuildPlayniteBrowser()
    {
        var frame = view.Current;
        EnsurePlayniteScan(frame.BrowserFolder);
        FeatureHeading("Locate Playnite", "Choose its installation folder. This does not launch or install Playnite.", "configure-playnite");
        if (frame.BrowserFolder is not null)
            FeatureFact("Folder", frame.BrowserFolder);
        var listing = playniteListing;
        if (listing is null)
            AddRow("playnite-loading", "Loading folders…", "Wait or go back", () =>
            {
            }, false);
        else
        {
            if (listing.Error is not null)
                Paragraph(listing.Error);
            if (listing.Executable is { } executable)
                AddRow("playnite-use", "Use Playnite from this folder", "Save settings to keep this choice.", () => ChoosePlayniteExecutable(executable));
            var pageCount = Math.Max(1, (listing.Folders.Length + PlaynitePageSize - 1) / PlaynitePageSize);
            var page = Math.Clamp(frame.BrowserPage, 0, pageCount - 1);
            for (var i = page * PlaynitePageSize; i < Math.Min((page + 1) * PlaynitePageSize, listing.Folders.Length); i++)
            {
                var item = listing.Folders[i];
                AddRow("playnite-folder:" + i, item.Name, "Open folder", () => OpenView(new(OverlayRoute.PlayniteBrowser, SelectedId: "playnite-loading", BrowserFolder: item.Path)));
            }

            if (listing.Folders.Length == 0 && listing.Executable is null && listing.Error is null)
                FeatureEmpty(frame.BrowserFolder is null ? "No local drives found" : "No installation found here", "Go back to choose another folder, or use the Windows file picker.", "capture-folder");
            if (listing.Truncated)
                Paragraph("This folder has many subfolders. Use the Windows file picker if the folder you need is not listed.");
            if (page > 0)
                AddRow("playnite-page-previous", "Previous page", $"{page} of {pageCount}", () => PlaynitePage(page - 1));
            if (page + 1 < pageCount)
                AddRow("playnite-page-next", "Next page", $"{page + 1} of {pageCount}", () => PlaynitePage(page + 1));
        }

        AddRow("playnite-native", "Choose using Windows", "Use mouse or keyboard", () => DialogTask = ConfigurePlayniteCore());
        AddRow("playnite-browser-back", "Back", "Does not change settings", Back);
    }

    private void PlaynitePage(int page)
    {
        var first = page * PlaynitePageSize;
        var selected = playniteListing is { } listing && first < listing.Folders.Length ? "playnite-folder:" + first : "playnite-browser-back";
        view.Replace(view.Current with
        {
            BrowserPage = page,
            SelectedId = selected,
            ScrollOffset = 0
        });
        navigation = new(selected);
        RefreshRows();
        ContentScroll.ScrollToTop();
    }

    private async void ChoosePlayniteExecutable(string executable)
    {
        if (!NavigationAllowed || view.Current.Route != OverlayRoute.PlayniteBrowser || playniteListing?.Executable != executable)
            return;
        var cycle = focusCycle;
        var revision = playniteScanRevision;
        FeedbackText("Checking Playnite…");
        var available = await Task.Run(() => File.Exists(executable));
        if (exiting || cycle != focusCycle || revision != playniteScanRevision || view.Current.Route != OverlayRoute.PlayniteBrowser || !NavigationAllowed)
            return;
        if (!available)
        {
            FeedbackText("Playnite is no longer available in this folder. Choose it again.");
            return;
        }

        Controller.Change(Controller.Settings with
        {
            PlaynitePath = executable
        });
        ApplySettings();
        CancelPlayniteScan();
        while (view.Current.Route == OverlayRoute.PlayniteBrowser && view.Back())
        {
        }

        if (view.Current.Route == OverlayRoute.PlayniteBrowser)
            view.SwitchTab(OverlayTab.System);
        SyncView();
        RefreshRows();
        FeedbackText("Playnite selected. Save settings to keep this choice.");
    }
}
