namespace ApolloOverlay;

internal sealed class PreviewFeedbackService : IFeedbackService
{
    public event Action<FeedbackStatus>? Changed
    {
        add
        {
        }
        remove
        {
        }
    }

    public FeedbackStatus Status => new(false, false, "Preview · sound and haptics were not sent");

    public void Configure(bool sound, bool haptics)
    {
    }

    public void SetContext(FeedbackContext context)
    {
    }

    public FeedbackDispatch Request(FeedbackCue cue, FeedbackSource source) => new(false, false, Status.Reason);
    public void Suspend()
    {
    }

    public void Dispose()
    {
    }
}
