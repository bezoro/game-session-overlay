using System.Windows.Threading;

namespace ApolloOverlay;

public partial class MainWindow
{
    private ISessionMedia sessionMedia = null!;
    private string selectedMedia = "";
    private void SessionMediaChanged(SessionMediaSnapshot snapshot)
    {
        if (exiting || Dispatcher.HasShutdownStarted)
            return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => SessionMediaChanged(snapshot));
            return;
        }

        if (!IsVisible)
            return;
        if (!snapshot.Items.Any(item => item.Id == selectedMedia))
            selectedMedia = snapshot.CurrentId;
        UpdateSessionFeatureLabels();
        if (view.Current.Route == OverlayRoute.Media && !busy && !TransitionPending)
            RefreshRows();
    }

    private SessionMediaItem? CurrentMedia => sessionMedia.Current.Items.FirstOrDefault(item => item.Id == selectedMedia) ?? sessionMedia.Current.Items.FirstOrDefault(item => item.Id == sessionMedia.Current.CurrentId);

    private void BuildSessionMedia()
    {
        var media = CurrentMedia;
        if (media is null)
            Paragraph("Abra música ou vídeo em um aplicativo compatível com os controles de mídia do Windows.");
        else
        {
            Paragraph(media.Title.Length > 0 ? media.Title : "Reprodução de mídia", 24);
            if (media.Artist.Length > 0)
                Paragraph(media.Artist);
        }

        AddRow("media-toggle", media?.Playing == true ? "Pausar" : "Reproduzir", media is null ? "Nenhuma mídia ativa" : "Controlar a reprodução", () => MediaCommand(SessionMediaCommand.Toggle), media?.CanToggle == true);
        var first = Rows.RowDefinitions.Count;
        AddRow("media-previous", "Anterior", "Voltar à faixa anterior", () => MediaCommand(SessionMediaCommand.Previous), media?.CanPrevious == true, row: narrow ? null : first, column: 0);
        AddRow("media-next", "Próxima", "Avançar uma faixa", () => MediaCommand(SessionMediaCommand.Next), media?.CanNext == true, row: narrow ? null : first, column: 1);
        if (sessionMedia.Current.Items.Count > 1)
            foreach (var item in sessionMedia.Current.Items)
            {
                var selected = item;
                AddRow("media-source:" + item.Id, item.Title.Length > 0 ? item.Title : "Reprodução de mídia", item.Id == media?.Id ? "Selecionada" : "Selecionar esta reprodução", () =>
                {
                    selectedMedia = selected.Id;
                    RefreshRows();
                });
            }

        AddRow("media-refresh", "Atualizar mídia", sessionMedia.Current.Status, () => featureTask = sessionMedia.RefreshAsync());
        AddRow("back", "Voltar", "Painel da sessão", Back);
    }

    private void MediaCommand(SessionMediaCommand command)
    {
        if (CurrentMedia is { } media)
            StartSessionAction("media", token => sessionMedia.CommandAsync(media.Id, command, token), RefreshRows);
    }
}
