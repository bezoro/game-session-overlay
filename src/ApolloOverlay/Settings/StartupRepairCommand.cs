namespace ApolloOverlay;

/// <summary>A noninteractive administrative entry point for the shipped repair script.</summary>
internal static class StartupRepairCommand
{
    internal const string Argument = "--configure-startup";

    internal static int? Execute(string[] args)
    {
        if (args.Length == 0 || args[0] != Argument)
            return null;
        if (!TryReadAction(args, out var install))
            return 2;
        using var diagnostics = new LocalDiagnostics();
        using var command = diagnostics.Commands.Begin("startup-repair", "script");
        var result = StartupRegistration.Repair(install);
        command.Complete(result.Kind, install ? "startup-repair-install" : "startup-repair-uninstall");
        return result.Kind == ResultKind.Completed ? 0 : 3;
    }

    internal static bool TryReadAction(string[] args, out bool install)
    {
        install = false;
        if (args.Length != 2 || args[0] != Argument)
            return false;
        if (string.Equals(args[1], "Install", StringComparison.OrdinalIgnoreCase))
        {
            install = true;
            return true;
        }
        return string.Equals(args[1], "Uninstall", StringComparison.OrdinalIgnoreCase);
    }
}
