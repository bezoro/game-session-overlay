using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace ApolloOverlay;

public partial class MainWindow
{
    private ScreenCaptures screenCaptures = null!;
    private ScreenCaptureItem[] captureItems = [];
    private ScreenCaptureItem? selectedCapture;
    private string artworkId = "";
    private void CaptureSession()
    {
        if (diagnostic || Controller.Platform.IsPreview)
        {
            RecordResult(new(ResultKind.Simulated, "Preview: no screenshot was taken."), "capture");
            return;
        }

        var monitor = Native.MonitorFromWindow(new WindowInteropHelper(this).Handle, 2);
        var info = new Native.MonitorInfo
        {
            Size = System.Runtime.InteropServices.Marshal.SizeOf<Native.MonitorInfo>(),
            Device = ""
        };
        var display = Native.GetMonitorInfo(monitor, ref info) ? Controller.RefreshDisplays().FirstOrDefault(item => item.Id == info.Device) : null;
        if (display is null)
        {
            FeedbackText("The capture display is unavailable.");
            return;
        }

        var target = Controller.ReturnTarget ?? Controller.Session.Anchor;
        StartSessionAction("capture", async token =>
        {
            HideNow(false, preservePending: true);
            var watch = Stopwatch.StartNew();
            while (FadePending && watch.ElapsedMilliseconds < 600)
                await Task.Delay(20, token);
            token.ThrowIfCancellationRequested();
            if (IsVisible)
            {
                CancelOpenAnimation();
                return new(ResultKind.Cancelled, "Capture cancelled before hiding the panel.");
            }

            if (target is not null && (!Controller.Platform.Valid(target) || Native.GetForegroundWindow() != target.Handle))
                return new(ResultKind.Cancelled, "The window changed before capture. Try again in the intended game.");
            var captured = await screenCaptures.CaptureAsync(display, target?.Title ?? "Display", target?.ProcessName ?? "", token);
            if (captured.Item is { } item)
            {
                captureItems = [item, .. captureItems.Where(old => old.Id != item.Id).Take(95)];
                RequestResultNotification("Screenshot saved. Open Your captures to view it.");
            }

            return captured.Result;
        });
    }

    private void OpenCaptures()
    {
        OpenFeature(OverlayRoute.Captures, "capture-refresh");
        featureTask = LoadCaptures();
    }

    private async Task LoadCaptures()
    {
        try
        {
            var loaded = await screenCaptures.ListAsync();
            if (exiting)
                return;
            captureItems = loaded.ToArray();
            if (IsVisible && view.Current.Route == OverlayRoute.Captures)
                RefreshRows();
            await UpdateSessionArtwork();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (!exiting)
                FeedbackText("Could not open the gallery. Check the captures folder.");
        }
    }

    private void BuildCaptures()
    {
        Paragraph("Screenshots saved on this computer. The overlay does not continuously record your screen.");
        AddRow("capture-refresh", "Refresh gallery", captureItems.Length + " recent captures", () => featureTask = LoadCaptures());
        foreach (var capture in captureItems)
        {
            var selected = capture;
            AddRow("capture:" + capture.Id, capture.Title.Length > 0 ? capture.Title : "Display capture", capture.Created.ToLocalTime().ToString("dd MMM · HH:mm") + $" · {capture.Width}×{capture.Height}", () =>
            {
                selectedCapture = selected;
                OpenFeature(OverlayRoute.CaptureDetail, "capture-folder");
            });
        }

        if (captureItems.Length == 0)
            Paragraph("Your first screenshot will appear here. Use Capture screen in the session panel.");
        AddRow("capture-folder", "Open folder", "View PNG files in Windows", OpenCaptureFolder, Directory.Exists(screenCaptures.DirectoryPath));
        AddRow("back", "Back", "Session panel", Back);
    }

    private void BuildCaptureDetail()
    {
        if (selectedCapture is not { } capture)
        {
            Paragraph("The selected screenshot is unavailable.");
            AddRow("back", "Back", "Gallery", Back);
            return;
        }

        Paragraph(capture.Title.Length > 0 ? capture.Title : "Display capture", 22);
        var image = new Image
        {
            MaxHeight = 340,
            Stretch = Stretch.Uniform,
            Margin = new(0, 8, 0, 12)
        };
        var row = Rows.RowDefinitions.Count;
        Rows.RowDefinitions.Add(new()
        {
            Height = GridLength.Auto
        });
        Grid.SetRow(image, row);
        Grid.SetColumnSpan(image, 2);
        Rows.Children.Add(image);
        featureTask = LoadCaptureImage(capture, image);
        Paragraph(capture.Created.ToLocalTime().ToString("MMM d, yyyy HH:mm") + $" · {capture.Width}×{capture.Height}");
        AddRow("capture-folder", "Open folder", "PNG file location", OpenCaptureFolder);
        AddRow("back", "Back", "Gallery", Back);
    }

    private async Task LoadCaptureImage(ScreenCaptureItem capture, Image image)
    {
        try
        {
            var source = await screenCaptures.ReadImageAsync(capture);
            if (!exiting && selectedCapture?.Id == capture.Id && view.Current.Route == OverlayRoute.CaptureDetail)
                image.Source = source;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or FormatException)
        {
            if (!exiting)
                FeedbackText("The screenshot was removed or could not be read. Refresh the gallery.");
        }
    }

    private void OpenCaptureFolder()
    {
        var folder = screenCaptures.DirectoryPath;
        if (diagnostic || Controller.Platform.IsPreview)
        {
            RecordResult(new(ResultKind.Simulated, "Preview: folder not opened."), "capture-folder");
            return;
        }

        if (!Directory.Exists(folder))
        {
            FeedbackText("The folder will be created when you save your first screenshot.");
            return;
        }

        RunEffect(() =>
        {
            LocalUpdateHistory.RejectLink(folder);
            var process = Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            process?.Dispose();
            return Task.FromResult(new ActionResult(ResultKind.Requested, "Captures folder requested from Windows."));
        });
    }

    private WindowRef? artworkTarget;
    private Task artworkRead = Task.CompletedTask;
    private string artworkPendingId = "";
    private long artworkGeneration;
    internal Task ArtworkTask => artworkRead;

    private void ClearSessionArtwork()
    {
        artworkGeneration++;
        artworkPendingId = "";
        artworkId = "";
        SessionArtwork.Background = null;
        SessionArtwork.Visibility = Visibility.Collapsed;
    }

    private bool ArtworkFits => !compactChrome && ContentScroll.ViewportHeight >= 320;

    private Task UpdateSessionArtwork()
    {
        if (exiting)
            return Task.CompletedTask;
        var target = Controller.Session.Anchor;
        if (target is null ? artworkTarget is not null : artworkTarget?.Same(target) != true)
        {
            artworkTarget = target;
            ClearSessionArtwork();
        }

        var image = target is null ? null : captureItems.FirstOrDefault(item => item.GameKey.Equals(target.ProcessName, StringComparison.OrdinalIgnoreCase));
        if (image is null)
        {
            if (artworkId.Length > 0 || artworkPendingId.Length > 0)
                ClearSessionArtwork();
            return Task.CompletedTask;
        }

        if (image.Id == artworkId)
        {
            SessionArtwork.Visibility = ArtworkFits ? Visibility.Visible : Visibility.Collapsed;
            return Task.CompletedTask;
        }

        if (image.Id == artworkPendingId)
            return artworkRead;
        artworkPendingId = image.Id;
        var generation = ++artworkGeneration;
        return artworkRead = ReadSessionArtwork(image, target!, generation);
    }

    private async Task ReadSessionArtwork(ScreenCaptureItem image, WindowRef target, long generation)
    {
        try
        {
            var source = await screenCaptures.ReadImageAsync(image, 640);
            if (exiting || generation != artworkGeneration || Controller.Session.Anchor?.Same(target) != true)
                return;
            SessionArtwork.Background = new ImageBrush(source)
            {
                Stretch = Stretch.UniformToFill
            };
            SessionArtwork.Visibility = ArtworkFits ? Visibility.Visible : Visibility.Collapsed;
            artworkId = image.Id;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or FormatException)
        {
        }
        finally
        {
            if (generation == artworkGeneration)
                artworkPendingId = "";
        }
    }
}
