namespace ApolloOverlay;

internal readonly record struct OverlayInputContext(bool Confirmed, bool Visible, bool Active, bool OwnForeground, bool OwnedDialog = false, bool Exiting = false);
