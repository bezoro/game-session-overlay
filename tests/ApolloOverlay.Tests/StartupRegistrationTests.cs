using ApolloOverlay;

internal static class StartupRegistrationTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("startup first registration quotes the executable and uses hidden launch", () =>
        {
            string? run = null;
            var configured = false;
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Overlay folder", "ApolloOverlay.exe");
            var ok = StartupRegistration.Apply(path, () => configured, () => run, value => run = value, () => configured = true);
            if (!ok || !configured || run != "\"" + path + "\" --startup")
                throw new Exception("First registration did not preserve the exact executable path.");
        });
        test("startup respects removal after first registration", () =>
        {
            var writes = 0;
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ApolloOverlay.exe");
            StartupRegistration.Apply(path, () => true, () => null, _ => writes++, () =>
            {
            });
            if (writes != 0)
                throw new Exception("The user's removed startup entry was recreated.");
        });
        test("startup relocates an existing entry and ignores foreign executables", () =>
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "new", "ApolloOverlay.exe");
            string? run = "old";
            StartupRegistration.Apply(path, () => true, () => run, value => run = value, () =>
            {
            });
            if (run != "\"" + path + "\" --startup")
                throw new Exception("Moved executable was not registered.");
            var writes = 0;
            var foreign = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dotnet.exe");
            if (StartupRegistration.Apply(foreign, () => false, () => null, _ => writes++, () =>
            {
            }) || writes != 0)
                throw new Exception("A different process was registered.");
        });
    }
}
