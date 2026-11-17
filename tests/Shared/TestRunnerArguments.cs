namespace ApolloOverlay.Testing;

internal enum TestRunnerMode
{
    All, Focused, Help, List, Invalid
}

internal sealed record TestRunnerRequest(TestRunnerMode Mode, string? Output = null, string? Command = null, string? Error = null);

internal static class TestRunnerArguments
{
    internal static TestRunnerRequest Parse(string[] args, Func<string, bool> knownCommand)
    {
        if (args is ["--help"])
            return new(TestRunnerMode.Help);
        if (args is ["--list"])
            return new(TestRunnerMode.List);
        if (args.Length is not (1 or 2) || string.IsNullOrWhiteSpace(args[0]) || args[0].StartsWith("--", StringComparison.Ordinal))
            return new(TestRunnerMode.Invalid, Error: "Expected a report path and an optional known suite.");
        if (args.Length == 1)
            return new(TestRunnerMode.All, Output: args[0]);
        return knownCommand(args[1]) ? new(TestRunnerMode.Focused, args[0], args[1])
            : new(TestRunnerMode.Invalid, Error: "Unknown suite: " + args[1]);
    }
}
