using System.Windows.Controls;
using System.Windows.Input;

namespace ApolloOverlay;

public partial class MainWindow
{
    internal Func<int, short> PhysicalKeyReader
    {
        private get;
        set;
    } = Native.GetAsyncKeyState;
    internal Func<ModifierKeys> KeyboardModifiersReader
    {
        private get;
        set;
    } = static () => Keyboard.Modifiers;

    private string inputMethod = "keyboard";
    private MouseButton? recoveryMouseRelease;
    private bool keyboardDown;
    private readonly HashSet<Key> heldKeys = [];
    internal static Key NormalizeKey(Key key, Key system, Key ime, Key dead)
    {
        var actual = key switch
        {
            Key.System => system,
            Key.ImeProcessed => ime,
            Key.DeadCharProcessed => dead,
            _ => key
        };
        return actual is Key.None or Key.System or Key.ImeProcessed or Key.DeadCharProcessed ? Key.None : actual;
    }

    private static bool SteamKeyOwned(int key, int mask) => (mask & 1) != 0 && key is 16 or 0xA0 or 0xA1 || (mask & 2) != 0 && key == 9;
    internal bool PhysicalKeysNeutral(int owned = 0)
    {
        if ((PhysicalKeyReader(0x03) & 0x8000) != 0)
            return false;
        for (var key = 0x08; key <= 0xFE; key++)
            if (!SteamKeyOwned(key, owned) && (PhysicalKeyReader(key) & 0x8000) != 0)
                return false;
        return heldKeys.All(k => SteamKeyOwned(KeyInterop.VirtualKeyFromKey(k), owned) || (PhysicalKeyReader(KeyInterop.VirtualKeyFromKey(k)) & 0x8000) == 0);
    }

    private void OnLocalPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        inputMethod = "mouse";
        UpdateHints();
        recoveryMouseRelease = null;
        if (fadingOut)
        {
            fadeInterrupted = true;
            recoveryMouseRelease = e.ChangedButton;
            e.Handled = true;
            return;
        }

        if (!CanRecoverLocalFocus)
        {
            if (!NavigationAllowed)
            {
                recoveryMouseRelease = e.ChangedButton;
                e.Handled = true;
            }

            return;
        }

        // The activation click confirms the panel, never the button underneath it.
        recoveryMouseRelease = e.ChangedButton;
        e.Handled = true;
        OpenPanel();
    }

    private void OnLocalPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (recoveryMouseRelease != e.ChangedButton)
            return;
        recoveryMouseRelease = null;
        e.Handled = true;
    }

    private static bool LocalRecoveryKey(Key key) => key is Key.Tab or Key.Up or Key.Down or Key.Left or Key.Right or Key.Enter or Key.Space or Key.PageUp or Key.PageDown or Key.X;
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (fadingOut)
        {
            fadeInterrupted = true;
            e.Handled = true;
            return;
        }

        inputMethod = "keyboard";
        UpdateHints();
        if (HandleSessionKeyboardKey(e) || HandleSessionSliderKey(e))
            return;
        var recoveryKey = NormalizeKey(e.Key, e.SystemKey, e.ImeProcessedKey, e.DeadCharProcessedKey);
        if (!e.IsRepeat
            && LocalRecoveryKey(recoveryKey)
            && (KeyboardModifiersReader() & (ModifierKeys.Alt | ModifierKeys.Control | ModifierKeys.Windows)) == ModifierKeys.None
            && CanRecoverLocalFocus)
        {
            // Keep this key in the release baseline while the explicit request runs.
            // Repeats cannot activate a control until the recovery key is released.
            heldKeys.Add(recoveryKey);
            e.Handled = true;
            OpenPanel();
            return;
        }

        var modifiers = KeyboardModifiersReader();
        var readerFocused = operationText?.IsKeyboardFocusWithin == true || detailsText?.IsKeyboardFocusWithin == true || updateNotesText?.IsKeyboardFocusWithin == true;
        if (readerFocused
            && ((e.Key is Key.Left or Key.Right)
            && modifiers == ModifierKeys.None
            || e.Key == Key.Tab
            && (modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == ModifierKeys.None))
        {
            if (NavigationAllowed && navigationKeysArmed)
            {
                if (e.Key == Key.Tab)
                    Sequential(modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
                else
                    Move(e.Key == Key.Left ? NavDirection.Left : NavDirection.Right);
            }

            e.Handled = true;
            return;
        }

        if (e.OriginalSource is Slider && e.Key == Key.Tab && (modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == ModifierKeys.None)
        {
            if (NavigationAllowed && navigationKeysArmed && !busy && !TransitionPending && !ownedDialog && !Confirming)
            {
                if (modifiers.HasFlag(ModifierKeys.Shift))
                    SetSelection(false);
                else
                    Sequential(1);
            }

            e.Handled = true;
            return;
        }

        if (e.OriginalSource is Slider or TextBox && e.Key is not Key.Escape)
        {
            if (!NavigationAllowed || !navigationKeysArmed || busy || TransitionPending || ownedDialog)
                e.Handled = true;
            return;
        }

        if (e.Key == Key.Tab
            && navigation.Id == "guide-limit"
            && !KeyboardModifiersReader().HasFlag(ModifierKeys.Shift)
            && entries.FirstOrDefault(x => x.Id == "guide-limit")?.Button.Content is ActionButtonContent limitContent
            && limitContent.Texts.Children.OfType<Slider>().FirstOrDefault() is { } editor)
        {
            if (NavigationAllowed && navigationKeysArmed && !busy && !TransitionPending && !ownedDialog && !Confirming)
                editor.Focus();
            e.Handled = true;
            return;
        }

        var key = NormalizeKey(e.Key, e.SystemKey, e.ImeProcessedKey, e.DeadCharProcessedKey);
        var firstPress = key != Key.None && heldKeys.Add(key);
        if (Confirming && closeSource != 0 && firstPress && key != Key.Escape)
        {
            CancelHold();
            FeedbackText("Origem da pressão mudou; fechamento cancelado.");
            e.Handled = true;
            return;
        }

        if (key == Key.None && TransitionPending)
        {
            RejectHandoff("Transição cancelada; tecla não identificada.");
            e.Handled = true;
            return;
        }

        if (TransitionPending)
        {
            if (e.Key == Key.Escape && firstPress && !e.IsRepeat)
                Back(trustedDismiss: true);
            e.Handled = true;
            return;
        }

        if (ownedDialog)
        {
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Enter or Key.Space)
        {
            if (!NavigationAllowed || !navigationKeysArmed)
            {
                e.Handled = true;
                return;
            }

            if (Confirming && navigation.Id == "confirm-cancel")
            {
                Back();
                e.Handled = true;
                return;
            }

            keyboardDown = true;
            if (Confirming && closeSource != 0)
            {
                CancelHold();
                FeedbackText("Origem da pressão mudou; fechamento cancelado.");
            }
            else if (!e.IsRepeat && !Confirming)
            {
                dispatchSource = 0;
                ActivateEntry();
                dispatchSource = null;
            }

            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (firstPress && !e.IsRepeat)
                Back();
            e.Handled = true;
            return;
        }

        if (!NavigationAllowed || !navigationKeysArmed)
        {
            e.Handled = true;
            return;
        }

        if (Confirming)
        {
            if (e.Key == Key.PageUp)
                ConfirmScroll.PageUp();
            else if (e.Key == Key.PageDown)
                ConfirmScroll.PageDown();
            else if (e.Key == Key.Tab)
                Sequential(KeyboardModifiersReader().HasFlag(ModifierKeys.Shift) ? -1 : 1);
            else if (e.Key is Key.Up or Key.Down or Key.Left or Key.Right)
                Move(e.Key is Key.Up or Key.Left ? NavDirection.Up : NavDirection.Down);
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Up:
                Move(NavDirection.Up);
                break;
            case Key.Down:
                Move(NavDirection.Down);
                break;
            case Key.Left:
                Move(NavDirection.Left);
                break;
            case Key.Right:
                Move(NavDirection.Right);
                break;
            case Key.Tab:
                Sequential(KeyboardModifiersReader().HasFlag(ModifierKeys.Shift) ? -1 : 1);
                break;
            case Key.PageUp:
                NavigatePrimaryTab(-1);
                break;
            case Key.PageDown:
                NavigatePrimaryTab(1);
                break;
            case Key.X:
                Details();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void WireLocalPointerInput()
    {
        PreviewMouseDown += OnLocalPreviewMouseDown;
        PreviewMouseUp += OnLocalPreviewMouseUp;
    }

    private void WireKeyboardInput()
    {
        PreviewKeyDown += OnKeyDown;
        PreviewKeyUp += (_, e) =>
        {
            var key = NormalizeKey(e.Key, e.SystemKey, e.ImeProcessedKey, e.DeadCharProcessedKey);
            if (key != Key.None)
                heldKeys.Remove(key);
            else if (TransitionPending)
                RejectHandoff("Transição cancelada; tecla não identificada.");
            if (!navigationKeysArmed && heldKeys.Count == 0 && PhysicalKeysNeutral())
                navigationKeysArmed = true;
            if (e.Key is Key.Enter or Key.Space)
            {
                keyboardDown = false;
                e.Handled = true;
            }
        };
    }
}
