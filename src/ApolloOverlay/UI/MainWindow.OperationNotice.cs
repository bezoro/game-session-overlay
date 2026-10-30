using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;

namespace ApolloOverlay;

public partial class MainWindow
{
    private readonly OperationNoticeState operationNotice = new();

    internal void RefreshOperationNotice(long now)
    {
        var changed = operationNotice.Update(Controller.Operation, now);
        var operation = operationNotice.Operation;
        var show = operationNotice.Visible && !Confirming && subpage != "operation";
        // A desktop panel reserves its quiet line. Compact panels reclaim empty
        // space and reveal the current selection when the viewport changes.
        OperationPanel.Visibility = Confirming || compactChrome && !show
            ? Visibility.Collapsed : show ? Visibility.Visible : Visibility.Hidden;
        OperationPanel.Height = compactChrome && !operationNotice.AllowsActions ? 24 : 44;
        OperationPanel.Opacity = show && IsVisible && OpenAnimationAllowed ? operationNotice.Opacity(now) : 1;
        var actions = operationNotice.AllowsActions ? Visibility.Visible : Visibility.Collapsed;
        OperationDetailsButton.Visibility = actions;
        DismissOperationButton.Visibility = actions;
        OperationDetailsButton.IsEnabled = !busy && !TransitionPending;
        DismissOperationButton.IsEnabled = !busy && !TransitionPending;
        OperationLabel.TextWrapping = operationNotice.AllowsActions ? TextWrapping.Wrap : TextWrapping.NoWrap;
        if (!changed)
            return;

        UpdateOperationAppearance(operation?.State);
        var text = operation is null ? "" : operation.State + " · " + operation.Message;
        OperationLabel.Text = operation?.Summary ?? (operation?.State is "Failed" or "Unavailable" or "Partial" or "Unconfirmed"
            ? operation.State + " · Check details."
            : text);
        OperationLabel.ToolTip = text;
        AutomationProperties.SetName(OperationPanel, text);
        UIElementAutomationPeer.FromElement(OperationLabel)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void UpdateOperationAppearance(string? state)
    {
        var (tone, surface, icon) = state switch
        {
            "Completed" => (ThemeSupport.Success, ThemeSupport.SuccessBackground, "check"),
            "Failed" => (ThemeSupport.Critical, ThemeSupport.CriticalBackground, "close"),
            "Unavailable" or "Partial" or "Unconfirmed" => (ThemeSupport.Warning, ThemeSupport.WarningBackground, "info"),
            "Pending" => (ThemeSupport.Information, ThemeSupport.InformationBackground, "timer"),
            "Cancelled" => (ThemeSupport.Information, ThemeSupport.InformationBackground, "close"),
            _ => (ThemeSupport.Information, ThemeSupport.InformationBackground, "info")
        };
        OperationGlyph.SetResourceReference(VectorIcon.FillProperty, tone);
        OperationGlyph.Data = (Geometry)FindResource("Icon." + icon);
        OperationPanel.SetResourceReference(Border.BackgroundProperty, surface);
        AutomationProperties.SetItemStatus(OperationPanel, state ?? "");
    }

    private void RetainOperation(string state, string message, string action = "", WindowRef? target = null, bool dispatched = false, string? summary = null)
    {
        Controller.SetOperation(state, message, action, target, dispatched, summary);
        UpdatePersistentState();
    }

    private void OpenOperation()
    {
        if (!NavigationAllowed || busy || TransitionPending || Confirming || Controller.Operation is null)
            return;
        OpenView(new(OverlayRoute.OperationResult, view.Current.Target, "operation-text"));
    }

    private void DismissOperation()
    {
        if (!NavigationAllowed || busy || TransitionPending || Confirming)
            return;
        Controller.DismissOperation();
        if (subpage == "operation")
            Back();
        else
        {
            UpdatePersistentState();
            SetSelection();
        }
    }
}
