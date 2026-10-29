using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ApolloOverlay;

internal static class AppearanceTests
{
    internal static void Run(Action<string, Action> test)
    {
        foreach (var light in new[] { false, true })
            test("unsafe color explanation stays beside pinned commands light=" + light, () =>
            {
                var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                ThemeSupport.ForceForValidation(app, light);
                using var host = new SessionInterfaceTests.Host(root => UserSettings.Save(Path.Combine(root, "settings.json"),
                    new(Appearance: new(Mode: light ? "Light" : "Dark"))), preview: false);
                var window = host.Window;
                host.Open(360, 480);
                window.OpenAppearance();
                window.OpenAppearanceColor(PaletteRole.Focus);
                var background = ((SolidColorBrush)window.FindResource(ThemeSupport.FocusBackground)).Color.ToString()[3..];
                EnterHex(window, background);
                var status = PremiumInterfaceTests.Descendants<TextBlock>((DependencyObject)window.FindName("Rows"))
                    .Single(text => Equals(text.Tag, "color-status"));
                var viewport = (ScrollViewer)window.FindName("ContentScroll");
                var bounds = status.TransformToAncestor(viewport).TransformBounds(new Rect(status.RenderSize));
                Require(status.IsVisible && status.Text.Contains("Draft only", StringComparison.Ordinal)
                    && bounds.Top >= 0 && bounds.Bottom <= viewport.ViewportHeight,
                    "The unsafe-draft explanation scrolled away from Keep/Cancel");
                SessionInterfaceTests.Choose(window, "color-apply");
                Require(!ReviewFixTests.Button(window, "appearance-save").IsEnabled && window.AppearanceDirty,
                    "The visible warning allowed an unreadable palette to be saved");
            });
        test("dynamic color hints keep commands concise and the edited role identifiable", () =>
        {
            using var host = DarkHost();
            var window = host.Window;
            host.Open(360, 480);
            window.OpenAppearance();
            window.OpenAppearanceColor(PaletteRole.Focus);
            window.UpdateLayout();
            var keep = ReviewFixTests.Button(window, "color-apply");
            Require(((ActionButtonContent)keep.Content).Description.Visibility == Visibility.Collapsed && keep.ActualHeight < 60,
                "A dynamic hint expanded the pinned color command");
            Require(((TextBlock)window.FindName("PageTitle")).Text == AppearanceCatalog.Roles.First(role => role.Role == PaletteRole.Focus).Name,
                "Scrolling the RGB controls hides the identity of the edited color");
        });
        test("critical text requires body contrast on every action surface", () =>
        {
            var palette = AppearanceCatalog.Resolve(new(), false) with
            {
                Critical = "#888888",
                CriticalBackground = "#000000"
            };
            Require(AppearanceCatalog.Contrast(palette.Critical, palette.CriticalBackground) >= 4.5,
                "The fixture must satisfy dedicated status contrast");
            Require(AppearanceCatalog.Contrast(palette.Critical, palette.FocusBackground) is >= 3 and < 4.5,
                "The fixture must distinguish icon contrast from body contrast");
            Require(!AppearanceCatalog.TryValidate(palette, out var reason) && reason.Contains("4.5:1", StringComparison.Ordinal),
                "A color readable as an icon was accepted for destructive action text");
        });
        test("discard restores the original theme after previewing the opposite palette", () =>
        {
            using var host = DarkHost();
            var window = host.Window;
            host.Open(360, 480);
            var original = ResourceColor(window, ThemeSupport.Surface);
            var settings = window.Controller.Settings;
            window.OpenAppearance();
            SessionInterfaceTests.Choose(window, "appearance-preview");
            Require(ResourceColor(window, ThemeSupport.Surface) != original, "The opposite palette was not actually previewed");
            SessionInterfaceTests.Choose(window, "appearance-preset:Ember");
            SessionInterfaceTests.Choose(window, "appearance-discard");
            Require(ResourceColor(window, ThemeSupport.Surface) == original && !window.AppearanceDirty
                && SessionHubSettings.Equivalent(settings, window.Controller.Settings), "Discard kept the preview theme or changed saved settings");
        });
        test("explicit appearance mode overrides the simulated Windows preference used by previews", () =>
        {
            var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            ThemeSupport.ForceForValidation(app, false);
            try
            {
                ThemeSupport.ApplyAppearance(app, new(Mode: "Light"));
                Require(ResourceColor(app.MainWindow ?? new FrameworkElement(), ThemeSupport.Surface)
                    == ColorFrom(AppearanceCatalog.Resolve(new(), true).Surface), "Choosing Light kept the dark Windows preference");
                ThemeSupport.ApplyAppearance(app, new(Mode: "System"));
                Require(ResourceColor(app.MainWindow ?? new FrameworkElement(), ThemeSupport.Surface)
                    == ColorFrom(AppearanceCatalog.Resolve(new(), false).Surface), "Follow Windows ignored the simulated preference");
            }
            finally
            {
                ThemeSupport.ApplyAppearance(app, null);
                ThemeSupport.ForceForValidation(app, false);
            }
        });
        test("every appearance preset resolves a complete readable dark and light palette", () =>
        {
            foreach (var preset in AppearanceCatalog.Presets)
                foreach (var light in new[] { false, true })
                {
                    var palette = AppearanceCatalog.Resolve(new(Preset: preset.Id), light);
                    Require(AppearanceCatalog.TryValidate(palette, out var reason), preset.Id + " has unreadable contrast: " + reason);
                    foreach (var role in AppearanceCatalog.Roles)
                        Require(AppearanceCatalog.TryColor(palette.Get(role.Role), out _), "A preset has no actual color for " + role.Name);
                }
        });
        foreach (var compact in new[] { false, true })
            test("edge feedback preserves text glyph and slider contrast in a custom palette compact=" + compact, () =>
            {
                var palette = AppearanceCatalog.Resolve(new(), false) with
                {
                    Text = "#A0A0A0",
                    Hover = "#FFFFFF",
                    Press = "#FFFFFF",
                    Sheen = "#FFFFFF"
                };
                Require(AppearanceCatalog.TryValidate(palette, out var reason), "A readable palette was rejected because a decorative color differs: " + reason);
                var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                ThemeSupport.ForceForValidation(app, false);
                using var host = new SessionInterfaceTests.Host(root => UserSettings.Save(Path.Combine(root, "settings.json"),
                    new(Appearance: new(Mode: "Dark", Dark: palette))), preview: false);
                var window = host.Window;
                host.Open(compact ? 360 : 572, compact ? 480 : 940);
                Require(ResourceColor(window, ThemeSupport.Text) == ColorFrom(palette.Text), "The custom palette was replaced rather than displayed safely");
                CheckFeedbackGeometry(window);
                SessionInterfaceTests.Pick(window, "session-card");
                var selected = ReviewFixTests.Button(window, "session-card");
                selected.ApplyTemplate();
                var outline = (Border)selected.Template.FindName("SelectionBreath", selected);
                Require(outline.Opacity == 1 && outline.BorderThickness.Left > 0,
                    "A selected control briefly loses its essential focus outline");
                window.OpenAppearance();
                window.OpenAppearanceColor(PaletteRole.Focus);
                window.UpdateLayout();
                CheckFeedbackGeometry(window);
                SessionInterfaceTests.Choose(window, "color-hex");
                CheckFeedbackGeometry(window);
            });
        test("palette drafts retain invalid contrast until validation and recover variants independently", () =>
        {
            var source = AppearanceCatalog.WithColor(new(Mode: "Dark"), true, PaletteRole.Shadow, "#141516");
            var dark = AppearanceCatalog.Resolve(source, false);
            var invalid = AppearanceCatalog.WithColor(source, false, PaletteRole.Text, dark.Surface);
            Require(!AppearanceCatalog.TryValidate(AppearanceCatalog.Resolve(invalid, false), out var reason)
                && !string.IsNullOrWhiteSpace(reason), "An invisible text draft was silently accepted or normalized before validation");
            var recovered = AppearanceCatalog.Normalize(invalid);
            Require(AppearanceCatalog.Resolve(recovered, false).Text == AppearanceCatalog.Resolve(new(), false).Text,
                "Unsafe persisted text was allowed to hide the interface");
            Require(AppearanceCatalog.Resolve(recovered, true).Shadow == "#141516" && recovered.Mode == "Dark",
                "Recovering one unsafe variant discarded valid preferences in the other variant");
            Require(AppearanceCatalog.Resolve(source, false) == dark, "Editing a draft mutated its source palette");
        });
        test("legacy settings and malformed appearance data preserve unrelated options and Guide opt-out", () =>
        {
            foreach (var payload in new[]
            {
                "", ",\"Appearance\":17", ",\"Appearance\":[]", ",\"Appearance\":\"unrecognized\""
            })
            {
                var settings = UserSettings.Parse("{\"Version\":3,\"GuideXInputEnabled\":false,\"SoundFeedbackEnabled\":true,\"PanelSide\":\"Right\",\"GuideOpenMaxHoldMs\":1700" + payload + "}");
                Require(!settings.GuideXInputEnabled && settings.SoundFeedbackEnabled && settings.PanelSide == "Right"
                    && settings.GuideOpenMaxHoldMs == 1700, "Recovering optional colors reset unrelated valid settings");
                Require(AppearanceCatalog.Normalize(settings.Appearance) == new AppearanceSettings(), "Missing or invalid appearance did not recover to the default preset");
            }
            var partial = UserSettings.Parse("""
                {"Version":3,"GuideXInputEnabled":false,"Appearance":{"Mode":"Dark","Preset":"Warm","Dark":{"Text":5,"Shadow":"#121314"},"Light":{"Shadow":"#161718"}}}
                """);
            Require(!partial.GuideXInputEnabled && AppearanceCatalog.Resolve(partial.Appearance, false).Shadow == "#121314"
                && AppearanceCatalog.Resolve(partial.Appearance, true).Shadow == "#161718", "One malformed role discarded the rest of the palette or settings");
        });
        test("appearance preferences round-trip atomically and participate in settings equivalence", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "OverlayAppearanceSettings", Guid.NewGuid().ToString("N"));
            try
            {
                var path = Path.Combine(root, "settings.json");
                var first = new UserSettings(GuideXInputEnabled: false, SoundFeedbackEnabled: true,
                    Appearance: AppearanceCatalog.WithColor(new(Mode: "Dark"), false, PaletteRole.Focus, "#F0C08A"));
                UserSettings.Save(path, first);
                var restored = UserSettings.Load(path, out _);
                Require(SessionHubSettings.Equivalent(first, restored) && !restored.GuideXInputEnabled && restored.SoundFeedbackEnabled,
                    "Saving appearance dropped colors or unrelated settings");
                Require(SessionHubSettings.Equivalent(new(), new(Appearance: new())), "An explicit default falsely marks legacy settings as changed");
                Require(!SessionHubSettings.Equivalent(first, first with
                {
                    Appearance = new(Mode: "Dark")
                }), "Custom colors are ignored by settings dirty detection");
                var before = File.ReadAllText(path);
                UserSettings.Save(path, restored with
                {
                    PanelSide = "Right"
                });
                Require(File.ReadAllText(path + ".previous") == before && !File.Exists(path + ".pending"), "Palette persistence bypassed the existing atomic settings transaction");
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
        });
        test("custom resources cannot override the Windows high contrast validation palette", () =>
        {
            var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            try
            {
                ThemeSupport.ForceHighContrastForValidation(app);
                ThemeSupport.ApplyAppearance(app, AppearanceCatalog.WithColor(new(Mode: "Dark"), false, PaletteRole.Focus, "#F0C08A"));
                Require(((SolidColorBrush)app.FindResource(ThemeSupport.Surface)).Color == SystemColors.WindowColor
                    && ((SolidColorBrush)app.FindResource(ThemeSupport.Focus)).Color == SystemColors.HighlightColor,
                    "A custom palette replaced high contrast system colors");
            }
            finally { ThemeSupport.ForceForValidation(app, false); }
        });
        test("appearance preview and Back preserve current settings and restore the original resources", () =>
        {
            using var host = DarkHost();
            var window = host.Window;
            host.Open(360, 480);
            var before = window.Controller.Settings;
            var color = ResourceColor(window, ThemeSupport.Focus);
            window.OpenAppearance();
            SessionInterfaceTests.Choose(window, "appearance-preset:Graphite");
            Require(window.AppearanceDirty && SessionHubSettings.Equivalent(before, window.Controller.Settings), "A preset preview mutated live user settings");
            Require(ResourceColor(window, ThemeSupport.Focus) != color, "Selecting another preset produced no visual preview");
            window.Back();
            Require(ResourceColor(window, ThemeSupport.Focus) == color && SessionHubSettings.Equivalent(before, window.Controller.Settings),
                "Leaving Appearance retained an unsaved preview");
        });
        test("appearance Save persists only colors and leaves unrelated settings drafts pending", () =>
        {
            using var host = DarkHost();
            var window = host.Window;
            host.Open();
            window.Controller.Change(window.Controller.Settings with
            {
                SoundFeedbackEnabled = true,
                PanelSide = "Right"
            });
            window.RefreshRows();
            window.OpenAppearance();
            SessionInterfaceTests.Choose(window, "appearance-preset:Graphite");
            SessionInterfaceTests.Choose(window, "appearance-save");
            var saved = UserSettings.Load(Path.Combine(host.Root, "settings.json"), out _);
            Require(saved.Appearance?.Preset == "Graphite" && !saved.SoundFeedbackEnabled && saved.PanelSide == "Left",
                "Saving colors also persisted unrelated drafts");
            Require(window.Controller.Settings.SoundFeedbackEnabled && window.Controller.Settings.PanelSide == "Right"
                && window.Controller.SettingsDirty && !window.AppearanceDirty, "Scoped color save lost pending edits or kept the saved color draft dirty");
        });
        test("local hexadecimal keyboard changes no external window and applies only after confirmation", () =>
        {
            using var host = DarkHost();
            var window = host.Window;
            host.Open(360, 480);
            var before = window.Controller.Settings;
            window.OpenAppearance();
            window.OpenAppearanceColor(PaletteRole.Focus);
            EnterHex(window, "F0C08A");
            Require(window.SelectedAction == "color-hex" && ResourceColor(window, ThemeSupport.Focus) == Color.FromRgb(240, 192, 138),
                "A valid local hex entry did not return to its color preview");
            Require(SessionHubSettings.Equivalent(before, window.Controller.Settings)
                && ((PremiumInterfaceTests.FixturePlatform)window.Controller.Platform).Calls == 0, "Editing a local color changed settings or sent an external command");
            SessionInterfaceTests.Choose(window, "color-apply");
            Require(window.AppearanceDirty && SessionHubSettings.Equivalent(before, window.Controller.Settings), "Apply color skipped the unsaved palette draft");
            SessionInterfaceTests.Choose(window, "appearance-discard");
            Require(!window.AppearanceDirty && ResourceColor(window, ThemeSupport.Focus) == ColorFrom(AppearanceCatalog.Resolve(before.Appearance, false).Focus),
                "Discard did not restore the prior palette");
        });
        test("an unreadable color draft keeps readable chrome and cannot be saved", () =>
        {
            using var host = DarkHost();
            var window = host.Window;
            host.Open(360, 480);
            var settings = window.Controller.Settings;
            var before = ResourceColor(window, ThemeSupport.Surface);
            var persisted = File.ReadAllText(Path.Combine(host.Root, "settings.json"));
            window.OpenAppearance();
            window.OpenAppearanceColor(PaletteRole.Surface);
            var text = AppearanceCatalog.Resolve(settings.Appearance, false).Text;
            EnterHex(window, text.TrimStart('#'));
            Require(ResourceColor(window, ThemeSupport.Surface) == before, "An unreadable proposal replaced the safe editor chrome");
            SessionInterfaceTests.Choose(window, "color-apply");
            var save = ReviewFixTests.Button(window, "appearance-save");
            Require(window.AppearanceDirty && !save.IsEnabled && AutomationProperties.GetHelpText(save).Length > 0,
                "Unsafe paired-color draft was discarded or remained saveable without an explanation");
            Require(SessionHubSettings.Equivalent(settings, window.Controller.Settings)
                && File.ReadAllText(Path.Combine(host.Root, "settings.json")) == persisted, "An invalid color draft escaped into live or saved settings");
            SessionInterfaceTests.Choose(window, "appearance-discard");
            Require(!window.AppearanceDirty && ResourceColor(window, ThemeSupport.Surface) == before && window.IsActive,
                "Discarding an invalid draft lost the original palette or focus");
        });
        test("cancelling a valid nested color preview restores the previous safe paint of an invalid draft", () =>
        {
            using var host = DarkHost();
            var window = host.Window;
            host.Open(360, 480);
            var original = ResourceColor(window, ThemeSupport.Surface);
            window.OpenAppearance();
            window.OpenAppearanceColor(PaletteRole.Surface);
            EnterHex(window, AppearanceCatalog.Resolve(window.Controller.Settings.Appearance, false).Text.TrimStart('#'));
            SessionInterfaceTests.Choose(window, "color-apply");
            Require(!ReviewFixTests.Button(window, "appearance-save").IsEnabled, "PRECONDITION: the staged palette is not invalid");
            window.OpenAppearanceColor(PaletteRole.Surface);
            EnterHex(window, "1C1A19");
            Require(ResourceColor(window, ThemeSupport.Surface) == Color.FromRgb(28, 26, 25), "PRECONDITION: the nested valid preview was not painted");
            SessionInterfaceTests.Choose(window, "color-cancel");
            Require(window.AppearanceDirty && !ReviewFixTests.Button(window, "appearance-save").IsEnabled
                && ResourceColor(window, ThemeSupport.Surface) == original, "Cancelling a nested edit retained a color that was never kept");
        });
        test("a failed appearance save keeps its draft and the prior saved baseline", () =>
        {
            using var host = DarkHost();
            var window = host.Window;
            host.Open();
            var saved = window.Controller.SavedSettings;
            window.OpenAppearance();
            SessionInterfaceTests.Choose(window, "appearance-preset:Graphite");
            var preview = ResourceColor(window, ThemeSupport.Focus);
            var path = Path.Combine(host.Root, "settings.json");
            File.Move(path, path + ".fixture-baseline");
            Directory.CreateDirectory(path);
            SessionInterfaceTests.Choose(window, "appearance-save");
            Require(window.AppearanceDirty && SessionHubSettings.Equivalent(saved, window.Controller.SavedSettings)
                && ResourceColor(window, ThemeSupport.Focus) == preview && window.Controller.Operation?.State == "Failed",
                "A failed save discarded its editable preview or falsely advanced the saved baseline");
        });
        test("color channel adjustment preserves its focused slider when input hints change", () =>
        {
            using var host = DarkHost();
            var window = host.Window;
            host.Open(360, 480);
            window.OpenAppearance();
            window.OpenAppearanceColor(PaletteRole.Focus);
            SessionInterfaceTests.Pick(window, "color-red");
            window.ActivateEntry();
            Require(Keyboard.FocusedElement is Slider, "Color action did not enter the channel slider");
            var slider = (Slider)Keyboard.FocusedElement;
            slider.Value = Math.Min(255, slider.Value + 1);
            window.ObservePointerPosition(new(100, 100));
            window.ObservePointerPosition(new(120, 100));
            Require(ReferenceEquals(slider, Keyboard.FocusedElement) && slider.IsKeyboardFocusWithin,
                "Live preview or input adaptation rebuilt the focused color channel");
        });
    }

    private static SessionInterfaceTests.Host DarkHost()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        ThemeSupport.ForceForValidation(app, false);
        return new SessionInterfaceTests.Host(root => UserSettings.Save(Path.Combine(root, "settings.json"), new(Appearance: new(Mode: "Dark"))), preview: false);
    }

    private static void CheckFeedbackGeometry(MainWindow window)
    {
        window.UpdateLayout();
        var inspected = 0;
        foreach (var button in PremiumInterfaceTests.Descendants<Button>(window))
        {
            if (!button.IsVisible || button.ActualWidth <= 0 || button.ActualHeight <= 0)
                continue;
            button.ApplyTemplate();
            var feedback = new[] { "SelectionSheen", "Interaction", "ActivationFeedback" }
                .Select(name => button.Template.FindName(name, button) as FrameworkElement).Where(element => element is not null).ToArray();
            if (feedback.Length == 0)
                continue;
            var protectedContent = PremiumInterfaceTests.Descendants<FrameworkElement>(button)
                .Where(element => element.IsVisible && element.ActualWidth > 0 && element.ActualHeight > 0
                    && element is TextBlock or VectorIcon or Slider)
                .Select(element => element is TextBlock text ? TextInk(text, button)
                    : element.TransformToAncestor(button).TransformBounds(new Rect(element.RenderSize)))
                .Where(bounds => !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0).ToArray();
            foreach (var effect in feedback)
            {
                var bounds = effect!.TransformToAncestor(button).TransformBounds(new Rect(effect.RenderSize));
                Require(!bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0, "Feedback has no visible geometry: " + button.Tag);
                foreach (var content in protectedContent)
                {
                    var overlap = Rect.Intersect(bounds, content);
                    Require(overlap.IsEmpty || overlap.Width * overlap.Height < .1,
                        "Decorative feedback overlaps essential content in " + button.Tag + ": " + effect.Name);
                }
                inspected++;
            }
        }
        Require(inspected >= 3, "No actual control-template feedback geometry was inspected");
    }

    private static Rect TextInk(TextBlock text, Visual ancestor)
    {
        if (string.IsNullOrEmpty(text.Text))
            return Rect.Empty;
        var formatted = new FormattedText(text.Text, CultureInfo.CurrentUICulture, text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize,
            Brushes.Black, VisualTreeHelper.GetDpi(text).PixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, text.ActualWidth),
            MaxTextHeight = Math.Max(1, text.ActualHeight)
        };
        var left = text.TextAlignment switch
        {
            TextAlignment.Center => Math.Max(0, (text.ActualWidth - formatted.WidthIncludingTrailingWhitespace) / 2),
            TextAlignment.Right => Math.Max(0, text.ActualWidth - formatted.WidthIncludingTrailingWhitespace),
            _ => 0
        };
        var bounds = formatted.BuildGeometry(new Point(left, 0)).Bounds;
        return bounds.IsEmpty ? Rect.Empty : text.TransformToAncestor(ancestor).TransformBounds(bounds);
    }

    private static void EnterHex(MainWindow window, string hex)
    {
        SessionInterfaceTests.Choose(window, "color-hex");
        SessionInterfaceTests.Choose(window, "keyboard-editor");
        Require(Keyboard.FocusedElement is TextBox { IsReadOnly: false }, "The local color buffer is not editable");
        var editor = (TextBox)Keyboard.FocusedElement;
        editor.Text = hex;
        SessionInterfaceTests.TapKey(editor, Key.Tab);
        SessionInterfaceTests.Choose(window, "keyboard-insert");
    }

    private static Color ResourceColor(FrameworkElement element, string key) => ((SolidColorBrush)element.FindResource(key)).Color;
    private static Color ColorFrom(string value) => (Color)ColorConverter.ConvertFromString(value);
    private static void Require(bool value, string message)
    {
        if (!value)
            throw new Exception(message);
    }
}
