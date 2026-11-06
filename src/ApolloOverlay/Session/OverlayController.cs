using System.IO;
using Microsoft.Win32;

namespace ApolloOverlay;

public sealed class OverlayController : IDisposable
{
    public IPlatform Platform
    {
        get;
    }
    public LocalDiagnostics Diagnostics
    {
        get;
    }

    private UserSettings settings = new(), savedSettings = new();
    public UserSettings Settings
    {
        get => SessionHubSettings.Copy(settings);
        private set => settings = SessionHubSettings.Copy(value);
    }
    public UserSettings SavedSettings
    {
        get => SessionHubSettings.Copy(savedSettings);
        private set => savedSettings = SessionHubSettings.Copy(value);
    }
    public bool SettingsDirty => !SessionHubSettings.Equivalent(settings, savedSettings);
    public OverlayOperation? Operation
    {
        get;
        private set;
    }

    public void SetOperation(string state, string message, string action = "", WindowRef? target = null, bool dispatched = false, string? summary = null) => Operation = new(state, message, action, target, dispatched) { Summary = summary };
    public void DismissOperation() => Operation = null;
    public void Discard() => Settings = SavedSettings;
    public SessionTracker Session { get; } = new();
    public RecentWindows Recent { get; } = new();
    public WindowRef? ReturnTarget
    {
        get;
        private set;
    }
    public IReadOnlyList<WindowRef> Apps
    {
        get;
        private set;
    } = [];
    public IReadOnlyList<DisplayRef> Displays
    {
        get;
        private set;
    } = [];
    public ModuleEvidence SteamEvidence
    {
        get;
        private set;
    }
    public SteamPermission Steam { get; } = new();
    public bool SteamAvailable
    {
        get;
        private set;
    }
    public string Status
    {
        get;
        private set;
    }

    private readonly bool ownsDiagnostics;
    private long revision;
    private bool disposed;
    private CancellationTokenSource? refresh;
    private CancellationTokenSource? cardResolution;
    private Task<SessionTargetCandidate?>? cardScan;
    private WindowRef? steamLaunchAnchor, steamLaunchReturn;
    public bool SteamOverlayEligible(WindowRef? target) => !disposed
        && target is not null
        && !target.Protected
        && !target.Launcher
        && Platform.Valid(target)
        && (Platform.SteamEvidence(target) == ModuleEvidence.Loaded
        || HasSteamLaunchObservation(target));
    public bool HasSteamLaunchObservation(WindowRef? target) => !disposed && target is not null && Platform.Valid(target) && (target.Same(steamLaunchAnchor) || target.Same(steamLaunchReturn));
    private void PruneSteamLaunchObservations()
    {
        bool Current(WindowRef? target) => target is not null && (target.Same(Session.Anchor) || target.Same(ReturnTarget)) && Platform.Valid(target);
        if (!Current(steamLaunchAnchor))
            steamLaunchAnchor = null;
        if (!Current(steamLaunchReturn))
            steamLaunchReturn = null;
    }

    public OverlayController(IPlatform? platform = null, LocalDiagnostics? diagnostics = null)
    {
        Platform = platform ?? new WindowsPlatform();
        Diagnostics = diagnostics ?? new();
        ownsDiagnostics = diagnostics is null;
        if (Platform.IsPreview)
        {
            Settings = new();
            Status = "Preview · window actions are simulated";
        }
        else
        {
            Settings = Diagnostics.Load(out var status);
            Status = status;
        }

        if (!Platform.IsPreview && Settings.PlaynitePath is null)
        {
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Playnite", "Playnite.FullscreenApp.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Playnite", "Playnite.FullscreenApp.exe")
            };
            Settings = Settings with
            {
                PlaynitePath = candidates.FirstOrDefault(File.Exists)
            };
        }

        SavedSettings = Settings;
    }

    public void CaptureReturn()
    {
        CancelCardResolution();
        ReturnTarget = Platform.Foreground();
        Recent.Open(ReturnTarget, Platform.Valid);
        Session.ClearIfStale(Platform.Valid);
        Session.Observe(ReturnTarget, Platform.Valid);
        PruneSteamLaunchObservations();
    }

    internal bool CardTargetAvailable(WindowRef? target) => target is not null && target.Same(Session.CardTarget)
        && (Session.CardResumeOnly ? target.Same(ReturnTarget) : Platform.Valid(target))
        && (Session.CardDisplayId is not { } display || Platform.OnSessionDisplay(target, display));

    internal bool CanResumeCard(WindowRef target) => !Session.CardPending && CardTargetAvailable(target)
        && (!Session.CardResumeOnly || Platform.CanSteamMenu(target));

    internal async Task<ActionResult> ResumeCard(WindowRef target, CancellationToken token)
    {
        if (token.IsCancellationRequested || !CanResumeCard(target))
            return new(ResultKind.Cancelled, "The session window changed before activation.");
        return Session.CardResumeOnly ? await Platform.ResumeBigPicture(target, token) : await Switch(target);
    }

    internal void CancelCardResolution()
    {
        cardResolution?.Cancel();
        Session.CancelCardResolution();
    }

    internal async Task ResolveCardTarget(string? displayId, IReadOnlySet<string> profileKeys)
    {
        if (disposed || Session.BeginCardResolution() is not { } revision)
            return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        cardResolution?.Cancel();
        cardResolution = deadline;
        var token = deadline.Token;
        var foreground = ReturnTarget;
        var history = Session.ForegroundHistory;
        var verifiedLauncher = new TaskCompletionSource<SessionTargetCandidate?>();
        SessionTargetCandidate? selected = null;
        bool StillValid(SessionTargetCandidate candidate) => (candidate.ResumeOnly || Platform.Valid(candidate.Window))
            && (displayId is null || Platform.OnSessionDisplay(candidate.Window, displayId));
        try
        {
            // A non-cancellable native call may outlive an opening. Reuse its
            // slot only after it exits, within this opening's own budget.
            if (cardScan is { IsCompleted: false } previous)
                await previous.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if (!Session.ResolvingCard(revision))
                return;
            cardScan = Task.Run(() => ScanCardTarget(displayId, foreground, history, profileKeys, token, verified =>
            {
                var launcher = SessionTracker.ChooseLauncher(verified, foreground, history);
                if (!token.IsCancellationRequested)
                    verifiedLauncher.TrySetResult(launcher);
            }));
            var result = await cardScan.WaitAsync(token);
            if (result is { } candidate && !StillValid(candidate))
                result = null;
            token.ThrowIfCancellationRequested();
            selected = result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(cardResolution, deadline))
                cardResolution = null;
            if (selected is null && Session.ResolvingCard(revision) && verifiedLauncher.Task.IsCompletedSuccessfully
                && verifiedLauncher.Task.Result is { } launcher && StillValid(launcher))
                selected = launcher;
            Session.SettleCard(revision, selected, displayId);
        }
    }

    private SessionTargetCandidate? ScanCardTarget(string? displayId, WindowRef? foreground, WindowRef[] history,
        IReadOnlySet<string> profileKeys, CancellationToken token, Action<IReadOnlyList<SessionTargetCandidate>> reportVerified)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var candidates = Platform.ScanSessionTargets(displayId, foreground, token, reportVerified);
            token.ThrowIfCancellationRequested();
            return SessionTracker.ChooseCard(candidates, foreground, history, profileKeys);
        }
        catch (Exception error) when (error is OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public IReadOnlyList<DisplayRef> RefreshDisplays() => Displays = Platform.Displays();
    public async Task Refresh()
    {
        refresh?.Cancel();
        refresh?.Dispose();
        refresh = new();
        var token = refresh.Token;
        var epoch = ++revision;
        var target = Session.Anchor;
        var returned = ReturnTarget;
        var result = await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var apps = Platform.Windows();
            var evidence = Platform.SteamEvidence(target);
            bool Launch(WindowRef? window) => window is not null && !window.Protected && !window.Launcher && Platform.HasSteamLaunchContext(window);
            token.ThrowIfCancellationRequested();
            var anchorLaunch = Launch(target);
            token.ThrowIfCancellationRequested();
            var returnLaunch = target?.Same(returned) == true ? anchorLaunch : Launch(returned);
            token.ThrowIfCancellationRequested();
            return (apps, evidence, anchorLaunch, returnLaunch);
        }, token);
        if (disposed || token.IsCancellationRequested || epoch != revision)
            return;
        Apps = result.apps;
        Recent.RefreshTitles(Apps, Platform.Valid);
        RefreshDisplays();
        SteamEvidence = result.evidence;
        Session.ClearIfStale(Platform.Valid, window => CardTargetAvailable(window) && (!Session.CardResumeOnly || Platform.CanSteamMenu(window)));
        if (target is not null && !target.Same(Session.Anchor))
            Steam.Invalidate();
        if (SteamEvidence != ModuleEvidence.Loaded)
            Steam.Invalidate();
        var anchorConfirmed = result.anchorLaunch || HasSteamLaunchObservation(target);
        var returnConfirmed = result.returnLaunch || HasSteamLaunchObservation(returned);
        steamLaunchAnchor = anchorConfirmed && target is not null && target.Same(Session.Anchor) && Platform.Valid(target) ? target : null;
        steamLaunchReturn = returnConfirmed && returned is not null && returned.Same(ReturnTarget) && Platform.Valid(returned) ? returned : null;
        if (!Platform.IsPreview)
        {
            try
            {
                using var registration = Registry.ClassesRoot.OpenSubKey("steam");
                SteamAvailable = registration is not null;
            }
            catch (System.Security.SecurityException)
            {
                SteamAvailable = false;
            }
        }
    }

    public void Select(WindowRef window)
    {
        if (window.Protected || window.Launcher && !window.SessionLauncher || !Platform.Valid(window))
            return;
        CancelCardResolution();
        Session.Select(window);
        Steam.Invalidate();
        PruneSteamLaunchObservations();
    }

    public void ClearSelection()
    {
        CancelCardResolution();
        Session.Clear();
        Steam.Invalidate();
        PruneSteamLaunchObservations();
    }

    public void Change(UserSettings value)
    {
        Settings = UserSettings.Parse(System.Text.Json.JsonSerializer.Serialize(value, SettingsJson.Default.UserSettings));
    }

    public async Task<ActionResult> Switch(WindowRef target)
    {
        var result = await Platform.Switch(target);
        if (!disposed)
            Recent.ConfirmSwitch(target, result, Platform.Foreground(), Platform.Valid);
        return result;
    }

    public void Save()
    {
        if (Platform.IsPreview)
        {
            Status = "Preview · nothing was saved; changes apply only to this session.";
            return;
        }

        Diagnostics.Save(Settings);
        SavedSettings = Settings;
        Status = "Settings saved.";
    }

    internal ActionResult SaveAppearance(AppearanceSettings appearance)
    {
        foreach (var light in new[] { false, true })
            if (!AppearanceCatalog.TryValidate(AppearanceCatalog.Resolve(appearance, light), out var reason))
                return new(ResultKind.Unavailable, reason);
        var value = AppearanceCatalog.Normalize(appearance);
        if (Platform.IsPreview)
        {
            Settings = Settings with
            {
                Appearance = value
            };
            Status = "Preview · colors apply only to this session.";
            return new(ResultKind.Simulated, Status);
        }

        // Persist this editor's scope without committing any unrelated settings draft.
        var saved = SavedSettings with
        {
            Appearance = value
        };
        Diagnostics.Save(saved);
        SavedSettings = saved;
        Settings = Settings with
        {
            Appearance = value
        };
        Status = "Appearance saved.";
        return new(ResultKind.Completed, Status);
    }

    internal WindowRef? LaunchTarget(bool playnite) => Apps.FirstOrDefault(w => playnite ? w.ProcessName.Equals("Playnite.FullscreenApp", StringComparison.OrdinalIgnoreCase) : w.ProcessName.Equals("steam", StringComparison.OrdinalIgnoreCase));
    public async Task<ActionResult> Launch(bool playnite, CancellationToken cancellation = default, Func<bool>? continuing = null)
    {
        bool Allowed() => !disposed && !cancellation.IsCancellationRequested && (continuing?.Invoke() ?? true);
        if (!Allowed())
            return new(ResultKind.Cancelled, "Request cancelled; no launch command was sent.");
        var existing = LaunchTarget(playnite);
        if (playnite && existing is not null && Platform.Valid(existing))
        {
            var focused = await Platform.Switch(existing);
            return Allowed() ? focused : new(ResultKind.Unknown, "Activation was requested before cancellation; check the window.");
        }

        if (!playnite && existing is not null && Platform.Valid(existing))
        {
            var focused = await Platform.Switch(existing);
            if (!Allowed())
                return new(ResultKind.Cancelled, "Focus was already requested; launch cancelled before dispatch.");
            if (focused.Kind != ResultKind.Completed && focused.Kind != ResultKind.Simulated)
                return focused;
        }

        if (!Allowed())
            return new(ResultKind.Cancelled, "Request cancelled before launch.");
        if (!playnite && !SteamAvailable && !Platform.IsPreview)
            return new(ResultKind.Unavailable, "Steam is not installed or its protocol is unavailable.");
        return Platform.Launch(playnite, Settings.PlaynitePath);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        revision++;
        CancelCardResolution();
        refresh?.Cancel();
        refresh?.Dispose();
        steamLaunchAnchor = null;
        steamLaunchReturn = null;
        try
        {
            Platform.Dispose();
        }
        finally
        {
            if (ownsDiagnostics)
                Diagnostics.Dispose();
        }
    }
}

public sealed record OverlayOperation(string State, string Message, string Action, WindowRef? Target, bool Dispatched)
{
    public string? Summary
    {
        get; init;
    }
}
