using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ApolloOverlay;

public partial class MainWindow
{
    private ScreenCaptures screenCaptures = null!;
    private ScreenCaptureItem[] captureItems = [];
    private ScreenCaptureItem? selectedCapture;
    private const int CapturesPerPage = 6;
    private int capturePage;
    private CancellationTokenSource? captureListRequest, capturePresentationRequest;
    private Task capturePresentationTask = Task.CompletedTask;
    internal Task CapturePresentationTask => capturePresentationTask;
    internal Func<ScreenCaptureItem, int, CancellationToken, Task<BitmapSource>>? CaptureImageReaderFixture
    {
        private get; set;
    }
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
        capturePage = 0;
        OpenFeature(OverlayRoute.Captures, "capture-refresh");
        featureTask = LoadCaptures();
    }

    private async Task LoadCaptures()
    {
        captureListRequest?.Cancel();
        using var request = new CancellationTokenSource();
        captureListRequest = request;
        try
        {
            var loaded = await screenCaptures.ListAsync(request.Token);
            if (exiting || request.IsCancellationRequested || !ReferenceEquals(captureListRequest, request))
                return;
            captureItems = loaded.ToArray();
            if (IsVisible && view.Current.Route == OverlayRoute.Captures)
            {
                var selected = Array.FindIndex(captureItems, item => "capture:" + item.Id == navigation.Id);
                capturePage = selected >= 0 ? selected / CapturesPerPage : Math.Clamp(capturePage, 0, LastCapturePage);
                RefreshRows();
            }
            await UpdateSessionArtwork();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (!exiting && ReferenceEquals(captureListRequest, request))
                FeedbackText("Could not open the gallery. Check the captures folder.");
        }
        finally
        {
            if (ReferenceEquals(captureListRequest, request))
                captureListRequest = null;
        }
    }

    private int LastCapturePage => Math.Max(0, (captureItems.Length - 1) / CapturesPerPage);

    private void BuildCaptures()
    {
        CancelCapturePresentation();
        capturePage = Math.Clamp(capturePage, 0, LastCapturePage);
        var toolbar = Rows.RowDefinitions.Count;
        CompactCaptureCommand(AddRow("capture-refresh", "Refresh", "Reload saved screenshots", () => featureTask = LoadCaptures(), row: toolbar, column: 0, compactGrid: true));
        CompactCaptureCommand(AddRow("capture-folder", "Open folder", "View PNG files in Windows", OpenCaptureFolder,
            Directory.Exists(screenCaptures.DirectoryPath), row: toolbar, column: 1, compactGrid: true));
        if (captureItems.Length == 0)
        {
            AddCaptureEmptyState();
            AddRow("back", "Back", "Session panel", Back);
            return;
        }

        var items = captureItems.Skip(capturePage * CapturesPerPage).Take(CapturesPerPage).ToArray();
        var frames = new List<(ScreenCaptureItem Item, CaptureThumbnail Thumbnail, Button Owner)>();
        var first = Rows.RowDefinitions.Count;
        for (var index = 0; index < items.Length; index++)
        {
            var capture = items[index];
            var title = capture.Title.Length > 0 ? capture.Title : "Display capture";
            var metadata = capture.Created.ToLocalTime().ToString("MMM d, yyyy HH:mm") + $" · {capture.Width} × {capture.Height}";
            var button = AddRow("capture:" + capture.Id, title, metadata, () =>
            {
                selectedCapture = capture;
                OpenFeature(OverlayRoute.CaptureDetail, "capture-folder");
            }, row: first + index / 2, column: index % 2, compactGrid: true);
            var thumbnail = new CaptureThumbnail(this, capture, compactChrome);
            button.Content = thumbnail;
            button.Padding = new(ResourceNumber("SessionSpace.8", 8));
            button.VerticalContentAlignment = VerticalAlignment.Top;
            frames.Add((capture, thumbnail, button));
        }

        if (LastCapturePage > 0)
        {
            Paragraph($"Page {capturePage + 1} of {LastCapturePage + 1} · {captureItems.Length} screenshots", 13);
            var pagination = Rows.RowDefinitions.Count;
            CompactCaptureCommand(AddRow("capture-previous", "Previous", "Earlier gallery page", () => ChangeCapturePage(-1), capturePage > 0, row: pagination, column: 0, compactGrid: true));
            CompactCaptureCommand(AddRow("capture-next", "Next", "More screenshots", () => ChangeCapturePage(1), capturePage < LastCapturePage, row: pagination, column: 1, compactGrid: true));
        }
        AddRow("back", "Back", "Session panel", Back);
        var request = BeginCapturePresentation(frames[0].Thumbnail.Preview);
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var scale = ((FrameworkElement)Content).LayoutTransform.Value.M11;
        var width = Math.Clamp((int)Math.Ceiling(Math.Max(160, ContentScroll.ActualWidth / 2 - 24) * dpi * scale), 160, 768);
        capturePresentationTask = ReadCaptureThumbnails(frames, width, request);
    }

    private void ChangeCapturePage(int step)
    {
        capturePage = Math.Clamp(capturePage + step, 0, LastCapturePage);
        navigation = new("capture:" + captureItems[capturePage * CapturesPerPage].Id);
        RefreshRows();
        ContentScroll.ScrollToTop();
    }

    private void CompactCaptureCommand(Button button)
    {
        button.MinHeight = 44;
        if (button.Content is ActionButtonContent content)
        {
            content.Description.Visibility = Visibility.Collapsed;
            content.Title.SetResourceReference(TextBlock.FontSizeProperty, "SessionType.CompactBody");
            content.Title.TextWrapping = TextWrapping.NoWrap;
            content.Title.TextTrimming = TextTrimming.CharacterEllipsis;
            content.ColumnDefinitions[0].Width = new GridLength(28);
            content.Icon.Width = content.Icon.Height = 20;
        }
    }

    private void AddCaptureEmptyState()
    {
        var content = new StackPanel
        {
            Margin = new(16, compactChrome ? 20 : 48, 16, compactChrome ? 20 : 48),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var icon = ActionIconCatalog.Create(this, "session-gallery", compactChrome ? 40 : 56);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        content.Children.Add(icon);
        var heading = new TextBlock
        {
            Text = "No captures yet",
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            Margin = new(0, 16, 0, 8)
        };
        heading.SetResourceReference(TextBlock.FontSizeProperty, "SessionType.Section");
        heading.SetResourceReference(TextBlock.ForegroundProperty, ThemeSupport.Text);
        content.Children.Add(heading);
        var message = new TextBlock
        {
            Text = "Use Capture screen from Session to save screenshots here.",
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 320
        };
        message.SetResourceReference(TextBlock.FontSizeProperty, "SessionType.Body");
        message.SetResourceReference(TextBlock.ForegroundProperty, ThemeSupport.SecondaryText);
        content.Children.Add(message);
        AddVisual(content);
    }

    private void BuildCaptureDetail()
    {
        CancelCapturePresentation();
        if (selectedCapture is not { } capture)
        {
            Paragraph("The selected screenshot is unavailable.");
            AddRow("capture-folder", "Open folder", "View saved PNG files", OpenCaptureFolder, Directory.Exists(screenCaptures.DirectoryPath));
            AddRow("back", "Back", "Gallery", Back);
            return;
        }

        var image = new CapturePreview(this, compactChrome ? 128 : 340, crop: false)
        {
            Margin = new(0, 8, 0, 12)
        };
        AddVisual(image);
        Paragraph(capture.Title.Length > 0 ? capture.Title : "Display capture", compactChrome ? 17 : 22);
        Paragraph(capture.Created.ToLocalTime().ToString("MMM d, yyyy HH:mm") + $" · {capture.Width} × {capture.Height}", 13);
        AddRow("capture-folder", "Open folder", "PNG file location", OpenCaptureFolder, Directory.Exists(screenCaptures.DirectoryPath));
        AddRow("back", "Back", "Gallery", Back);
        var request = BeginCapturePresentation(image);
        capturePresentationTask = ReadCaptureDetail(capture, image, request);
    }

    private CancellationTokenSource BeginCapturePresentation(FrameworkElement visual)
    {
        var request = new CancellationTokenSource();
        capturePresentationRequest = request;
        visual.Unloaded += (_, _) =>
        {
            if (ReferenceEquals(capturePresentationRequest, request))
                CancelCapturePresentation();
        };
        return request;
    }

    private bool CurrentCapturePresentation(CancellationTokenSource request, FrameworkElement visual, OverlayRoute route) =>
        !exiting && !request.IsCancellationRequested && ReferenceEquals(capturePresentationRequest, request)
        && view.Current.Route == route && visual.IsDescendantOf(Rows);

    private void CancelCapturePresentation()
    {
        capturePresentationRequest?.Cancel();
        capturePresentationRequest = null;
    }

    private void SuspendCapturePresentation()
    {
        captureListRequest?.Cancel();
        CancelCapturePresentation();
        CancelArtworkRead();
    }

    private Task<BitmapSource> ReadCaptureSource(ScreenCaptureItem capture, int width, CancellationToken cancellation) =>
        CaptureImageLoader.ReadAsync(CaptureImageReaderFixture ?? screenCaptures.ReadImageAsync, capture, width, cancellation);

    private async Task ReadCaptureThumbnails(IReadOnlyList<(ScreenCaptureItem Item, CaptureThumbnail Thumbnail, Button Owner)> frames, int width, CancellationTokenSource request)
    {
        try
        {
            await Task.WhenAll(frames.Select(async frame =>
            {
                try
                {
                    var source = await ReadCaptureSource(frame.Item, width, request.Token);
                    // A fast decode can finish before the button's content
                    // presenter creates its visual children. The row owner is
                    // already attached, and its exact content identifies this
                    // thumbnail without discarding a valid pre-layout result.
                    if (CurrentCapturePresentation(request, frame.Owner, OverlayRoute.Captures)
                        && ReferenceEquals(frame.Owner.Content, frame.Thumbnail))
                        frame.Thumbnail.Preview.ShowImage(source);
                }
                catch (OperationCanceledException) when (request.IsCancellationRequested)
                {
                }
                catch (Exception error) when (CaptureImageError(error))
                {
                    if (CurrentCapturePresentation(request, frame.Owner, OverlayRoute.Captures)
                        && ReferenceEquals(frame.Owner.Content, frame.Thumbnail))
                        frame.Thumbnail.Preview.ShowUnavailable();
                }
            }));
        }
        finally
        {
            if (ReferenceEquals(capturePresentationRequest, request))
                capturePresentationRequest = null;
            request.Dispose();
        }
    }

    private async Task ReadCaptureDetail(ScreenCaptureItem capture, CapturePreview image, CancellationTokenSource request)
    {
        try
        {
            var source = await ReadCaptureSource(capture, 1200, request.Token);
            if (CurrentCapturePresentation(request, image, OverlayRoute.CaptureDetail) && selectedCapture?.Id == capture.Id)
                image.ShowImage(source);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
        }
        catch (Exception error) when (CaptureImageError(error))
        {
            if (CurrentCapturePresentation(request, image, OverlayRoute.CaptureDetail))
            {
                image.ShowUnavailable();
                // The compact page may scroll the preview's placeholder out of
                // view to reveal its recovery action. Keep the reason beside
                // that action as well as in the full preview.
                SetEntryDescription("capture-folder", "File unavailable · check saved PNGs");
                FeedbackText("The screenshot was removed or could not be read. Refresh the gallery.");
            }
        }
        finally
        {
            if (ReferenceEquals(capturePresentationRequest, request))
                capturePresentationRequest = null;
            request.Dispose();
        }
    }

    private static bool CaptureImageError(Exception error) => error is IOException or UnauthorizedAccessException
        or InvalidOperationException or NotSupportedException or FormatException or ArgumentException;

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
    private CancellationTokenSource? artworkRequest;
    internal Task ArtworkTask => artworkRead;

    private void ClearSessionArtwork()
    {
        CancelArtworkRead();
        artworkId = "";
        SessionArtwork.Background = null;
        SessionArtwork.Visibility = Visibility.Collapsed;
    }

    private void CancelArtworkRead()
    {
        artworkGeneration++;
        artworkRequest?.Cancel();
        artworkRequest = null;
        artworkPendingId = "";
    }

    private bool ArtworkFits => !compactChrome && ContentScroll.ViewportHeight >= 320;

    private Task UpdateSessionArtwork()
    {
        if (exiting || !IsVisible)
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
        CancelArtworkRead();
        artworkPendingId = image.Id;
        var request = new CancellationTokenSource();
        artworkRequest = request;
        return artworkRead = ReadSessionArtwork(image, target!, artworkGeneration, request);
    }

    private async Task ReadSessionArtwork(ScreenCaptureItem image, WindowRef target, long generation, CancellationTokenSource request)
    {
        try
        {
            var source = await CaptureImageLoader.ReadAsync(screenCaptures.ReadImageAsync, image, 640, request.Token);
            if (request.IsCancellationRequested || exiting || !IsVisible || generation != artworkGeneration || Controller.Session.Anchor?.Same(target) != true)
                return;
            SessionArtwork.Background = new ImageBrush(source)
            {
                Stretch = Stretch.UniformToFill
            };
            SessionArtwork.Visibility = ArtworkFits ? Visibility.Visible : Visibility.Collapsed;
            artworkId = image.Id;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
        }
        catch (Exception error) when (CaptureImageError(error))
        {
        }
        finally
        {
            if (generation == artworkGeneration)
                artworkPendingId = "";
            if (ReferenceEquals(artworkRequest, request))
                artworkRequest = null;
            request.Dispose();
        }
    }
}
