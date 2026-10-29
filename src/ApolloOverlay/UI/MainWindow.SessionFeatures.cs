using System.IO;
using System.Windows.Controls;
using System.Windows.Input;

namespace ApolloOverlay;

public partial class MainWindow
{
    private CancellationTokenSource? featureRead;
    private long featureEpoch, nextFeatureRead;
    private Task featureTask = Task.CompletedTask;
    private Task audioObservation = Task.CompletedTask;
    internal Task FeatureTask => featureTask;

    private string renderedFeatureShape = "";
    private void InitializeSessionFeatures(ISessionMedia? media, ISystemAudioBackend? audioBackend, IApplicationAudio? mixer)
    {
        sessionMedia = media ?? (diagnostic || Controller.Platform.IsPreview ? new PreviewSessionMedia() : new SessionMedia());
        systemAudio = new(audioBackend ?? (diagnostic || Controller.Platform.IsPreview ? new PreviewSystemAudioBackend() : null));
        sessionProfiles = new(Controller.Diagnostics.Root);
        screenCaptures = new(Controller.Diagnostics.Root);
        mixerAudio = mixer ?? (diagnostic || Controller.Platform.IsPreview ? new PreviewApplicationAudio() : new ApplicationAudio(Controller.Platform.Valid));
        sessionMedia.Changed += SessionMediaChanged;
    }

    private void SuspendSessionFeatures()
    {
        featureEpoch++;
        featureRead?.Cancel();
        nextFeatureRead = 0;
        sessionMedia?.SetVisible(false);
        mixerAudio?.Suspend();
    }

    private void DisposeSessionFeatures()
    {
        SuspendSessionFeatures();
        sessionMedia.Changed -= SessionMediaChanged;
        sessionMedia.Dispose();
        mixerAudio.Dispose();
    }

    private void ResumeSessionFeatures()
    {
        if (!NavigationAllowed)
            return;
        systemAudio.InvalidateObservation();
        sessionMedia.SetVisible(true);
        mixerAudio.Track(null, true, featureEpoch);
        StartFeatureRead();
        var current = featureTask;
        featureTask = Task.WhenAll(current, LoadCaptures());
    }

    private void PollSessionFeatures(long now)
    {
        if (now < nextFeatureRead || busy || TransitionPending || Confirming || !NavigationAllowed)
            return;
        if (view.Current.Route is OverlayRoute.Hub or OverlayRoute.WindowsAudio or OverlayRoute.Mixer or OverlayRoute.AudioDevices or OverlayRoute.Profiles or OverlayRoute.ProfileEdit)
            StartFeatureRead();
    }

    private void StartFeatureRead()
    {
        if (exiting || !IsVisible || featureRead is not null || !NavigationAllowed)
            return;
        nextFeatureRead = Environment.TickCount64 + 2500;
        var request = new CancellationTokenSource(5000);
        featureRead = request;
        var epoch = featureEpoch;
        audioObservation = featureTask = ReadSessionFeatures(epoch, request);
    }

    internal void RefreshSessionFeatures()
    {
        nextFeatureRead = 0;
        StartFeatureRead();
    }

    private async Task ReadSessionFeatures(long epoch, CancellationTokenSource request)
    {
        try
        {
            var before = renderedFeatureShape;
            await systemAudio.RefreshAsync(request.Token);
            if (exiting || request.IsCancellationRequested || epoch != featureEpoch || !IsVisible)
                return;
            if (view.Current.Route == OverlayRoute.Mixer)
            {
                foreach (var target in Controller.Apps.Where(window => !window.Protected).DistinctBy(window => (window.Pid, window.Started)).Take(12))
                {
                    var state = await mixerAudio.RefreshAsync(target, epoch, request.Token);
                    if (exiting || request.IsCancellationRequested || epoch != featureEpoch || !IsVisible)
                        return;
                    mixerStates[WindowId(target)] = state;
                }
            }

            UpdateSessionFeatureLabels();
            if (view.Current.Route is OverlayRoute.AudioDevices or OverlayRoute.Mixer)
            {
                if (before != FeatureViewShape() && Keyboard.FocusedElement is not (Slider or TextBox) && !IsMouseCaptureWithin)
                    RefreshRows();
                else
                    UpdateMixerLabels();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            if (!exiting && epoch == featureEpoch)
                FeedbackText("Não foi possível atualizar todos os controles da sessão. Tente atualizar a lista.");
        }
        finally
        {
            if (ReferenceEquals(featureRead, request))
                featureRead = null;
            request.Dispose();
        }
    }

    private string FeatureViewShape() => view.Current.Route + "|" + (view.Current.Route == OverlayRoute.AudioDevices ? string.Join("|", systemAudio.Current.Outputs.Select(item => item.Id + ":" + item.Name)) : string.Join("|", Controller.Apps.Where(item => !item.Protected).DistinctBy(item => (item.Pid, item.Started)).Take(12).Select(WindowId)) + "|" + (mixerTarget is { } target ? mixerStates.GetValueOrDefault(WindowId(target))?.CanSetVolume + ":" + mixerStates.GetValueOrDefault(WindowId(target))?.CanSetMute : ""));
}
