# Controller focus and isolation

The overlay works with controllers connected directly to the PC and with controllers exposed by Apollo. It currently reads XInput. Window focus, administrator privileges and waiting for button release do not stop another process from reading the same controller.

## Guide and window activation

A short Guide press opens the hidden panel after release. Guide on a visible panel requests dismissal, including when Windows has refused focus. Dismissal waits for released input; uncertain input uses the existing recovery path and does not request a return to another app. B and Escape remain available. Opening from the tray explicitly starts another activation attempt.

An accepted `SetForegroundWindow` call does not prove that the panel is active. The overlay confirms both its native foreground window and WPF activation before enabling navigation. A bounded background broker can retry an explicit opening against its original foreground window. It keeps the WPF input queue separate, waits for its activation notification and detaches its own queue. A changed foreground, cancellation, dismissal or a newer opening prevents confirmation of an old attempt. No synthetic Alt presses or global foreground-lock changes are used.

The Win32 behavior is documented in [SetForegroundWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow) and [asynchronous foreground activation](https://devblogs.microsoft.com/oldnewthing/20161118-00/?p=94745). Windows can still refuse activation. The panel must report that state rather than dispatching controller actions to an unconfirmed window. Local diagnostics record the requested window and observed foreground handles.

Focus and controller isolation are separate capabilities. An app that reads XInput in the background may continue responding after the overlay gains focus. The product's shared-input warning describes this limitation.

## Independent controller routing

A possible PC implementation would receive each source controller and expose a separate virtual controller to games. While the overlay owns navigation, the router would deliver original input to the overlay and neutral input to the public device. Games must also lose access to the source device. That requires device integration and configuration before games open the devices, preservation of existing configuration and recovery when the router stops.

This routing is not implemented or certified. Guide, USB/Bluetooth, multiple controllers, slot changes, virtual Apollo devices, Steam Input and game-specific input paths need separate validation. HidHide can help with some configurations, but dynamically hiding a HID does not prove that existing handles or every XInput path have been revoked. Do not automatically change its global configuration. See the [official HidHide setup guide](https://docs.nefarius.at/projects/HidHide/Simple-Setup-Guide/).

## Optional Apollo integration

The examined Apollo revision is `adc5c5a0bd80831ce495434bb16aee2cd4175fb8`. It creates virtual controllers through ViGEm and sends received states to `platf::gamepad_update`:

- [Controller creation](https://github.com/ClassicOldSong/Apollo/blob/adc5c5a0bd80831ce495434bb16aee2cd4175fb8/src/platform/windows/input.cpp#L198-L266).
- [Input forwarding and delayed tasks](https://github.com/ClassicOldSong/Apollo/blob/adc5c5a0bd80831ce495434bb16aee2cd4175fb8/src/input.cpp#L1124-L1193).
- [Virtual Guide writes](https://github.com/ClassicOldSong/Apollo/blob/adc5c5a0bd80831ce495434bb16aee2cd4175fb8/src/platform/windows/input.cpp#L1278).

Neutralizing that public virtual device alone would also remove the overlay's navigation input. A separate authenticated channel must deliver original states to the overlay while the device owner confirms neutral states at every public write, including delayed writes, Guide and disconnects. This backend would remain optional so local PC use does not require Apollo.

Isolation must not be reported until source access is excluded and public neutral input is confirmed. Release must wait for stable neutral input and handle reconnection, new input, IPC loss, crashes and expiration. Test the original and public states simultaneously in separate processes, then repeat with the actual controller, TV, Apollo/client versions and installed drivers. Screenshots and synthetic fixtures cannot establish that isolation works.
