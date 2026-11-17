namespace ApolloOverlay;

public sealed record CommandLogStatus(int BufferedEvents, long DroppedEvents, long WriteFailures, long UntrackedOperations, int ActiveOperations, bool Closing, bool Closed, bool DrainTimedOut, string? LastErrorType, int? LastErrorHResult);
