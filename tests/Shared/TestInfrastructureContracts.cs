using System.Reflection;
using System.IO;

namespace ApolloOverlay.Testing;

internal static class TestInfrastructureContracts
{
    internal static void Run(Action<string, Action> test)
    {
        test("runner rejects an unknown suite without selecting or running a command", () =>
        {
            var request = TestRunnerArguments.Parse(["unused.json", "--focus-typo"], _ => false);
            Require(request.Mode == TestRunnerMode.Invalid && request.Output is null && request.Command is null,
                "An invalid argument could fall through to the full suite");
        });
        test("runner distinguishes discovery, focused runs and the full suite", () =>
        {
            Require(TestRunnerArguments.Parse(["--help"], _ => false).Mode == TestRunnerMode.Help, "Help was treated as a path");
            Require(TestRunnerArguments.Parse(["--list"], _ => false).Mode == TestRunnerMode.List, "List was treated as a path");
            Require(TestRunnerArguments.Parse(["all.json"], _ => false) is { Mode: TestRunnerMode.All, Output: "all.json" }, "Default run changed");
            Require(TestRunnerArguments.Parse(["focused.json", "--focused"], value => value == "--focused")
                is { Mode: TestRunnerMode.Focused, Output: "focused.json", Command: "--focused" }, "A valid focused suite was rejected");
            foreach (var invalid in new[] { Array.Empty<string>(), new[] { "--typo" }, new[] { "" }, new[] { "a.json", "--list", "extra" } })
                Require(TestRunnerArguments.Parse(invalid, _ => true).Mode == TestRunnerMode.Invalid, "Malformed arguments selected tests");
        });
        test("test failure preserves the invoked assertion, its stack and nested cause", () =>
        {
            Exception wrapped;
            try
            {
                typeof(TestInfrastructureContracts).GetMethod(nameof(ThrowNested), BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);
                throw new Exception("The fixture did not fail");
            }
            catch (TargetInvocationException error) { wrapped = error; }
            var failure = TestFailure.Capture(wrapped);
            Require(failure.Type == typeof(InvalidOperationException).FullName && failure.Message == "outer assertion"
                && failure.StackTrace?.Contains(nameof(ThrowNested), StringComparison.Ordinal) == true,
                "The reflection wrapper hid the assertion or its source");
            Require(failure.Inner is [{ Type: "System.ArgumentException", Message: "inner cause" }]
                && failure.Details.Contains("outer assertion", StringComparison.Ordinal)
                && failure.Details.Contains("inner cause", StringComparison.Ordinal), "The nested cause disappeared");
        });
        test("test failure retains every aggregate branch", () =>
        {
            var failure = TestFailure.Capture(new AggregateException(new IOException("read failure"), new TimeoutException("deadline")));
            Require(failure.Inner.Length == 2 && failure.Inner[0].Type == typeof(IOException).FullName
                && failure.Inner[1].Type == typeof(TimeoutException).FullName, "Only the first parallel failure was reported");
        });
    }

    private static void ThrowNested() => throw new InvalidOperationException("outer assertion", new ArgumentException("inner cause"));

    private static void Require(bool condition, string reason)
    {
        if (!condition)
            throw new Exception(reason);
    }
}
