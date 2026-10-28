using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ApolloOverlay;

public partial class MainWindow
{
    private int interfaceActionDepth;

    private void ClickTab(Button button, int index)
    {
        if (!NavigationAllowed || busy || TransitionPending || Confirming || page == index) return;
        var previousSource = dispatchSource;
        dispatchSource = -1;
        try { RunInterfaceAction(button, () => Page(index), FeedbackCue.Navigation); }
        finally { dispatchSource = previousSource; }
    }

    private void ActivatePointerEntry(Button button)
    {
        if (button.Tag is not string id) return;
        var previousSource = dispatchSource;
        dispatchSource = -1;
        try
        {
            navigation = new(id);
            SetSelection(false);
            if (Confirming && id == "confirm-cancel" && NavigationAllowed && !busy)
                RunInterfaceAction(button, Back, FeedbackCue.Cancellation);
            else ActivateEntry();
        }
        finally { dispatchSource = previousSource; }
    }

    private void RunInterfaceAction(Button button, Action action, FeedbackCue? cue)
    {
        if (interfaceActionDepth == 0 && cue is {} signal) RequestCue(signal);
        PulseActivation(button);
        interfaceActionDepth++;
        try { action(); }
        finally
        {
            interfaceActionDepth--;
            // Settings rebuild their row. Keep the acknowledgement on the replacement too.
            if (button.Tag is string id && entries.FirstOrDefault(e => e.Id == id)?.Button is {} current && current != button)
                PulseActivation(current);
        }
    }

    private void PulseActivation(Button button)
    {
        button.ApplyTemplate();
        if (button.Template.FindName("ActivationFeedback", button) is not Border highlight) return;
        if (button.Resources["ActivationTimer"] is DispatcherTimer previous) previous.Stop();
        highlight.BeginAnimation(OpacityProperty, null);
        highlight.Opacity = .18;
        if (OpenAnimationAllowed)
        {
            highlight.BeginAnimation(OpacityProperty, new DoubleAnimation(.18, 0, TimeSpan.FromMilliseconds(180)));
            return;
        }
        // Reduced motion still acknowledges the press, without a fade or scale effect.
        var reset = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        reset.Tick += (_, _) => { reset.Stop(); highlight.Opacity = 0; button.Resources.Remove("ActivationTimer"); };
        button.Resources["ActivationTimer"] = reset;
        reset.Start();
    }

    private void UpdateOperationAppearance(string? state)
    {
        var problem = state is "Falhou" or "Indisponível" or "Parcial" or "Não confirmado";
        var complete = state == "Concluído";
        var brush = problem ? ThemeSupport.Critical : complete ? ThemeSupport.Success : ThemeSupport.Stroke;
        OperationPanel.SetResourceReference(Border.BorderBrushProperty, brush);
        OperationGlyph.SetResourceReference(TextBlock.ForegroundProperty, problem ? ThemeSupport.Critical : complete ? ThemeSupport.Success : ThemeSupport.SecondaryText);
        OperationGlyph.Text = state switch { "Concluído" => "✓", "Pendente" => "…", "Cancelado" => "×", "Falhou" or "Indisponível" or "Parcial" or "Não confirmado" => "!", _ => "i" };
        AutomationProperties.SetItemStatus(OperationPanel, state ?? "");
    }

    private void TestFeedback()
    {
        var sent = RequestCue(FeedbackCue.Test);
        FeedbackText(sent.SoundRequested || sent.HapticRequested
            ? "Teste solicitado. Confira o som e a vibração ativados; Salvar mantém seus ajustes."
            : sent.Reason);
    }

    private string FeedbackDescription()
    {
        if (!Controller.Settings.SoundFeedbackEnabled && !Controller.Settings.HapticFeedbackEnabled)
            return "Ative sons ou vibração para testar";
        if (Controller.Settings.SoundFeedbackEnabled && !feedbackService.Status.SoundAvailable)
            return feedbackService.Status.Reason;
        return Controller.Settings.SoundFeedbackEnabled
            ? "Ouvir navegação, confirmação e retorno · testar vibração se ativada"
            : "Testar um pulso curto no controle ativo";
    }
}
