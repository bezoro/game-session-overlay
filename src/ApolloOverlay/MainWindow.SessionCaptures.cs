using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace ApolloOverlay;

public partial class MainWindow
{
    private void CaptureSession()
    {
        if(diagnostic||Controller.Platform.IsPreview){RecordResult(new(ResultKind.Simulated,"Prévia: nenhuma captura da tela foi realizada."),"capture");return;}
        var monitor=Native.MonitorFromWindow(new WindowInteropHelper(this).Handle,2);
        var info=new Native.MonitorInfo{Size=System.Runtime.InteropServices.Marshal.SizeOf<Native.MonitorInfo>(),Device=""};
        var display=Native.GetMonitorInfo(monitor,ref info)?Controller.RefreshDisplays().FirstOrDefault(item=>item.Id==info.Device):null;
        if(display is null){FeedbackText("O monitor da captura não está disponível.");return;}
        var target=Controller.ReturnTarget??Controller.Session.Anchor;
        StartSessionAction("capture",async token=>
        {
            HideNow(false,preservePending:true);
            var watch=Stopwatch.StartNew();while(FadePending&&watch.ElapsedMilliseconds<600)await Task.Delay(20,token);
            token.ThrowIfCancellationRequested();
            if(IsVisible){CancelOpenAnimation();return new(ResultKind.Cancelled,"A captura foi cancelada antes de ocultar o painel.");}
            if(target is not null&&(!Controller.Platform.Valid(target)||Native.GetForegroundWindow()!=target.Handle))return new(ResultKind.Cancelled,"A janela mudou antes da captura. Tente novamente no jogo desejado.");
            var captured=await screenCaptures.CaptureAsync(display,target?.Title??"Monitor",target?.ProcessName??"",token);
            if(captured.Item is {} item){captureItems=[item,..captureItems.Where(old=>old.Id!=item.Id).Take(95)];RequestResultNotification("Imagem salva. Abra Suas capturas para visualizar.");}
            return captured.Result;
        });
    }
    private void OpenCaptures()
    {
        OpenFeature(OverlayRoute.Captures,"capture-refresh");featureTask=LoadCaptures();
    }
    private async Task LoadCaptures()
    {
        try
        {
            var loaded=await screenCaptures.ListAsync();if(exiting)return;captureItems=loaded.ToArray();
            if(IsVisible&&view.Current.Route==OverlayRoute.Captures)RefreshRows();
            await UpdateSessionArtwork();
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or InvalidOperationException){if(!exiting)FeedbackText("Não foi possível abrir a galeria. Confira a pasta de capturas.");}
    }
    private void BuildCaptures()
    {
        Paragraph("Imagens salvas neste computador. A overlay não grava sua tela continuamente.");
        AddRow("capture-refresh","Atualizar galeria",captureItems.Length+" capturas recentes",()=>featureTask=LoadCaptures());
        foreach(var capture in captureItems)
        {var selected=capture;AddRow("capture:"+capture.Id,capture.Title.Length>0?capture.Title:"Captura do monitor",capture.Created.ToLocalTime().ToString("dd MMM · HH:mm")+$" · {capture.Width}×{capture.Height}",()=>{selectedCapture=selected;OpenFeature(OverlayRoute.CaptureDetail,"capture-folder");});}
        if(captureItems.Length==0)Paragraph("Sua primeira imagem aparecerá aqui. Use Capturar tela no painel da sessão.");
        AddRow("capture-folder","Abrir pasta","Ver os arquivos PNG no Windows",OpenCaptureFolder,Directory.Exists(screenCaptures.DirectoryPath));
        AddRow("back","Voltar","Painel da sessão",Back);
    }
    private void BuildCaptureDetail()
    {
        if(selectedCapture is not {} capture){Paragraph("A captura selecionada não está disponível.");AddRow("back","Voltar","Galeria",Back);return;}
        Paragraph(capture.Title.Length>0?capture.Title:"Captura do monitor",22);
        var image=new Image{MaxHeight=340,Stretch=Stretch.Uniform,Margin=new(0,8,0,12)};
        var row=Rows.RowDefinitions.Count;Rows.RowDefinitions.Add(new(){Height=GridLength.Auto});Grid.SetRow(image,row);Grid.SetColumnSpan(image,2);Rows.Children.Add(image);
        featureTask=LoadCaptureImage(capture,image);
        Paragraph(capture.Created.ToLocalTime().ToString("dd/MM/yyyy HH:mm")+$" · {capture.Width}×{capture.Height}");
        AddRow("capture-folder","Abrir pasta","Local dos arquivos PNG",OpenCaptureFolder);
        AddRow("back","Voltar","Galeria",Back);
    }
    private async Task LoadCaptureImage(ScreenCaptureItem capture,Image image)
    {
        try{var source=await screenCaptures.ReadImageAsync(capture);if(!exiting&&selectedCapture?.Id==capture.Id&&view.Current.Route==OverlayRoute.CaptureDetail)image.Source=source;}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException){if(!exiting)FeedbackText("A imagem foi removida ou não pôde ser lida. Atualize a galeria.");}
    }
    private void OpenCaptureFolder()
    {
        var folder=screenCaptures.DirectoryPath;
        if(diagnostic||Controller.Platform.IsPreview){RecordResult(new(ResultKind.Simulated,"Prévia: pasta não aberta."),"capture-folder");return;}
        if(!Directory.Exists(folder)){FeedbackText("A pasta será criada quando você salvar sua primeira captura.");return;}
        RunEffect(()=>
        {
            LocalUpdateHistory.RejectLink(folder);
            var process=Process.Start(new ProcessStartInfo(folder){UseShellExecute=true});process?.Dispose();
            return Task.FromResult(new ActionResult(ResultKind.Requested,"Pasta de capturas solicitada ao Windows."));
        });
    }
    private async Task UpdateSessionArtwork()
    {
        var game=Controller.Session.Anchor?.ProcessName??"";
        if(artworkGameKey!=game){artworkGameKey=game;artworkId="";SessionArtwork.Background=null;SessionArtwork.Visibility=Visibility.Collapsed;}
        if(Controller.Session.Anchor is not {} target)return;
        var image=captureItems.FirstOrDefault(item=>item.GameKey.Equals(target.ProcessName,StringComparison.OrdinalIgnoreCase));
        if(image is null||image.Id==artworkId)return;
        try
        {
            var source=await screenCaptures.ReadImageAsync(image,640);
            if(exiting||Controller.Session.Anchor?.Same(target)!=true)return;
            SessionArtwork.Background=new ImageBrush(source){Stretch=Stretch.UniformToFill};SessionArtwork.Visibility=Visibility.Visible;artworkId=image.Id;
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException){}
    }
}
