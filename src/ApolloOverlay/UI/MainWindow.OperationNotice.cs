using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace ApolloOverlay;

public partial class MainWindow
{
    private readonly OperationNoticeState operationNotice = new();

    internal void RefreshOperationNotice(long now)
    {
        var changed = operationNotice.Update(Controller.Operation, now);
        var operation = operationNotice.Operation;
        var show = operationNotice.Visible && !Confirming && subpage != "operation";
        // Reserve one quiet line so acknowledgements never move the selected action.
        OperationPanel.Visibility = Confirming ? Visibility.Collapsed : show ? Visibility.Visible : Visibility.Hidden;
        OperationPanel.Opacity = show && IsVisible && OpenAnimationAllowed ? operationNotice.Opacity(now) : 1;
        var actions = operationNotice.AllowsActions ? Visibility.Visible : Visibility.Collapsed;
        OperationDetailsButton.Visibility = actions;
        DismissOperationButton.Visibility = actions;
        OperationDetailsButton.IsEnabled = !busy && !TransitionPending;
        DismissOperationButton.IsEnabled = !busy && !TransitionPending;
        if (!changed)
            return;

        UpdateOperationAppearance(operation?.State);
        var text = operation is null ? "" : operation.State + " · " + operation.Message;
        OperationLabel.Text = text;
        OperationLabel.ToolTip = text;
        AutomationProperties.SetName(OperationPanel, text);
        UIElementAutomationPeer.FromElement(OperationLabel)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void UpdateOperationAppearance(string? state)
    {
        var problem = state is "Failed" or "Unavailable" or "Partial" or "Unconfirmed";
        var complete = state == "Completed";
        OperationGlyph.SetResourceReference(TextBlock.ForegroundProperty, problem ? ThemeSupport.Critical : complete ? ThemeSupport.Success : ThemeSupport.SecondaryText);
        OperationGlyph.Text = state switch
        {
            "Completed" => "✓",
            "Pending" => "…",
            "Cancelled" => "×",
            "Failed" or "Unavailable" or "Partial" or "Unconfirmed" => "!",
            _ => "i"
        };
        AutomationProperties.SetItemStatus(OperationPanel, state ?? "");
    }

    private void RetainOperation(string state, string message, string action = "", WindowRef? target = null, bool dispatched = false)
    {
        Controller.SetOperation(state, message, action, target, dispatched);
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
            SetSelection(false);
        }
    }
}
