using System.Windows;
using System.Windows.Threading;
using Forms=System.Windows.Forms;
using Drawing=System.Drawing;

namespace ApolloOverlay;

internal sealed class TrayHost:IDisposable
{
    private readonly Dispatcher dispatcher;private readonly Action open,exit;private readonly Forms.NotifyIcon icon;private readonly Drawing.Icon artwork;private bool disposed;
    internal Forms.ContextMenuStrip Menu {get;}
    internal TrayHost(Dispatcher dispatcher,Action open,Action exit,bool visible=true)
    {
        this.dispatcher=dispatcher;this.open=open;this.exit=exit;
        using var resource=Application.GetResourceStream(new Uri("/ApolloOverlay;component/Assets/overlay-controller.ico",UriKind.Relative))?.Stream??throw new InvalidOperationException("Ícone da bandeja ausente.");
        artwork=new Drawing.Icon(resource);Menu=new Forms.ContextMenuStrip();Menu.Items.Add("Abrir overlay",null,(_,_)=>Dispatch(open));Menu.Items.Add("Sair",null,(_,_)=>Dispatch(exit));
        icon=new Forms.NotifyIcon{Icon=artwork,Text="Bezoro · Controle da sessão",ContextMenuStrip=Menu};icon.MouseDoubleClick+=OnDoubleClick;icon.BalloonTipClicked+=OnResultClick;icon.Visible=visible;
    }
    internal void OnDoubleClick(object? sender,Forms.MouseEventArgs args){if(args.Button==Forms.MouseButtons.Left&&args.Clicks==2)Dispatch(open);}
    private void OnResultClick(object? sender,EventArgs args)=>Dispatch(open);
    internal void NotifyResult(string message){if(disposed||!icon.Visible)return;icon.ShowBalloonTip(5000,"Bezoro · resultado da operação",message,Forms.ToolTipIcon.Warning);}
    private void Dispatch(Action action){if(disposed||dispatcher.HasShutdownStarted)return;dispatcher.BeginInvoke(()=>{if(!disposed&&!dispatcher.HasShutdownStarted)action();});}
    public void Dispose(){if(disposed)return;disposed=true;icon.MouseDoubleClick-=OnDoubleClick;icon.BalloonTipClicked-=OnResultClick;icon.Visible=false;icon.Dispose();Menu.Dispose();artwork.Dispose();}
}
