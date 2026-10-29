namespace ApolloOverlay;

public enum FeedbackCue
{
    Navigation,
    Confirmation,
    Unavailable,
    Test,
    Cancellation,
    Completion
}

public enum FeedbackSourceKind
{
    Keyboard,
    Pointer,
    Controller
}

public readonly record struct FeedbackSource(FeedbackSourceKind Kind, uint? Slot = null, long ConnectionEpoch = 0);
public readonly record struct FeedbackContext(bool Visible, bool ConfirmedForeground, uint? ActiveSlot, long ConnectionEpoch = 0);
public sealed record FeedbackStatus(bool SoundAvailable, bool HapticsAvailable, string Reason);
public readonly record struct FeedbackDispatch(bool SoundRequested, bool HapticRequested, string Reason);
public interface IFeedbackService : IDisposable
{
    event Action<FeedbackStatus>? Changed;
    FeedbackStatus Status
    {
        get;
    }

    void Configure(bool sound, bool haptics);
    void SetContext(FeedbackContext context);
    FeedbackDispatch Request(FeedbackCue cue, FeedbackSource source);
    void Suspend();
}
