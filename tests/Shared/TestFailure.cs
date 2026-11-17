using System.Reflection;

namespace ApolloOverlay.Testing;

/// <summary>Developer-facing evidence retains every cause; product log redaction does not apply to test reports.</summary>
internal sealed record TestFailure(string Type, string Message, string? StackTrace, TestFailure[] Inner, string Details)
{
    internal static TestFailure Capture(Exception error)
    {
        var cause = error;
        while (cause is TargetInvocationException { InnerException: { } invoked })
            cause = invoked;
        var inner = cause is AggregateException aggregate ? aggregate.InnerExceptions.ToArray()
            : cause.InnerException is { } nested ? [nested] : Array.Empty<Exception>();
        return new(cause.GetType().FullName ?? cause.GetType().Name, cause.Message, cause.StackTrace,
            inner.Select(Capture).ToArray(), error.ToString());
    }
}
