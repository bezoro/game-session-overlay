using System.Windows.Controls;
using ApolloOverlay;

internal static class TvUsabilityTests
{
    private static void Require(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    private static MainWindow Window()
    {
        var target = new WindowRef(101, 42, 100, "Janela de teste", "fixture");
        var window = new MainWindow(renderOnly: true, platform: new PreviewPlatform(target))
        {
            Width = 360,
            Height = 480,
            Left = -12000,
            Top = -12000,
            InputContextReader = () => new(true, true, true, true),
            PhysicalKeyReader = _ => 0
        };
        window.RefreshRows();
        window.ShowDiagnostic();
        window.UpdateLayout();
        return window;
    }

    private static void UseController(MainWindow window)
    {
        var frames = new PadFrame[4];
        frames[0] = new(true, PadButtons.None);
        window.ReviewTick(frames);
        frames[0] = new(true, PadButtons.Down);
        window.ReviewTick(frames);
    }

    internal static void Run(Action<string, Action> test)
    {
        test("TV session hint reflects whether a window is selected", () =>
        {
            var window = Window();
            try
            {
                var hint = (TextBlock)window.FindName("SessionHint");
                Require(window.Controller.Session.Anchor is null, "The initial fixture unexpectedly selected a window");
                Require(hint.Text.Contains("Escolher", StringComparison.OrdinalIgnoreCase), "Empty session advertises resuming a window");
                UseController(window);
                Require(hint.Text.Contains("A", StringComparison.Ordinal) && hint.Text.Contains("Escolher", StringComparison.OrdinalIgnoreCase), "Controller hint does not explain the empty session action");
                window.Controller.CaptureReturn();
                window.RefreshRows();
                Require(hint.Text.Contains("Retomar", StringComparison.OrdinalIgnoreCase), "Selected session does not advertise resuming");
            }
            finally { window.Close(); }
        });

        test("TV compact hints do not advertise contextual options on the system page", () =>
        {
            var window = Window();
            try
            {
                UseController(window);
                window.Page(2);
                var hint = ((TextBlock)window.FindName("ModeLabel")).Text;
                Require(!hint.Contains("X opções", StringComparison.Ordinal), "System page misleadingly advertises contextual options");
                window.Page(1);
                hint = ((TextBlock)window.FindName("ModeLabel")).Text;
                Require(hint.Contains("X opções", StringComparison.Ordinal), "Apps page lost its contextual options hint");
            }
            finally { window.Close(); }
        });
    }
}
