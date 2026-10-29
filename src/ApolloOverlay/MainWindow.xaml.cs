using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ApolloOverlay;

public partial class MainWindow : Window
{
    internal readonly OverlayController Controller;
    private readonly bool diagnostic;
    private bool busy, exiting, opening;
    public MainWindow(
        bool renderOnly = false,
        IPlatform? platform = null,
        LocalDiagnostics? diagnostics = null,
        IApplicationAudio? appAudioService = null,
        IControllerDevices? devices = null,
        IFeedbackService? feedback = null,
        IUpdateSource? updates = null,
        ISessionMedia? media = null,
        ISystemAudioBackend? systemAudioBackend = null,
        IApplicationAudio? mixerAudioService = null,
        IControllerIdentitySource? controllerIdentitySource = null
    )
    {
        diagnostic = renderOnly;
        FocusFallbackRequest = renderOnly ? null : WindowActivation.RequestFallbackAsync;
        updateSource = updates ?? (renderOnly || platform?.IsPreview == true ? new PreviewUpdateSource() : new GitHubUpdateSource());
        InitializeComponent();
        Controller = new(platform ?? (renderOnly ? new PreviewPlatform() : null), diagnostics);
        appliedSettings = Controller.Settings;
        input = new(Controller.Settings);
        if (!diagnostic && !Controller.Platform.IsPreview)
            updateChannel = UpdateChannelSettings.Read(Controller.Diagnostics.Root);
        InitializeIconUpdates();
        controllerDevices = devices ?? (diagnostic || Controller.Platform.IsPreview ? new PreviewControllerDevices() : new ControllerDevices());
        applicationAudio = appAudioService ?? (diagnostic || Controller.Platform.IsPreview ? new PreviewApplicationAudio() : new ApplicationAudio(Controller.Platform.Valid));
        feedbackService = feedback ?? (diagnostic || Controller.Platform.IsPreview ? new PreviewFeedbackService() : new FeedbackService(controllerDevices));
        applicationAudio.Changed += ApplicationAudioChanged;
        feedbackService.Changed += FeedbackStatusChanged;
        feedbackService.Configure(Controller.Settings.SoundFeedbackEnabled, Controller.Settings.HapticFeedbackEnabled);
        InitializeSessionFeatures(media, systemAudioBackend, mixerAudioService);
        this.controllerIdentitySource = controllerIdentitySource ?? (diagnostic || Controller.Platform.IsPreview ? new PreviewControllerIdentitySource() : new WindowsControllerIdentitySource());
        timer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        timer.Tick += Tick;
        WireFixedActions();
        WireCloseConfirmation();
        WireLocalPointerInput();
        WireAdaptiveLayout();
        WireKeyboardInput();
        WireWindowLifecycle();
        audio.Changed += AudioChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        UpdateAppearance();
        new WindowInteropHelper(this).EnsureHandle();
        ConfigureGuide();
        if (!diagnostic)
            timer.Start();
        RefreshRows();
        FeedbackText(Controller.Status);
    }
}
