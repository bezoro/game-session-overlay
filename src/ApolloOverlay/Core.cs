using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApolloOverlay;

public sealed record WindowRef(nint Handle, int Pid, long Started, string Title, string ProcessName, bool Protected = false, long Generation = 0)
{
    public bool Same(WindowRef? other) => other is not null && Handle == other.Handle && Pid == other.Pid && Started == other.Started && Generation == other.Generation;
    public bool CanClose => Handle != 0 && Pid > 0 && Started > 0 && !Protected && !Launcher;
    public bool Launcher => ProcessName.Equals("steam", StringComparison.OrdinalIgnoreCase) || ProcessName.StartsWith("Playnite", StringComparison.OrdinalIgnoreCase);
}

public sealed class SessionTracker
{
    public WindowRef? Anchor { get; private set; }
    public long Sequence { get; private set; }
    public void Observe(WindowRef? window)
    {
        Sequence++;
        if (window is null || window.Protected || window.Launcher) return;
        Anchor = window;
    }
    public void Select(WindowRef window) { if (!window.Protected && !window.Launcher) Anchor = window; }
    public void ClearIfStale(Func<WindowRef, bool> valid) { if (Anchor is not null && !valid(Anchor)) Anchor = null; }
}

public enum ResultKind { Simulated, Requested, Completed, Failed, Unavailable, Partial, Unknown, Cancelled }
public sealed record ActionResult(ResultKind Kind, string Message)
{
    public override string ToString() => $"{Kind switch { ResultKind.Simulated => "Simulado", ResultKind.Requested => "Solicitado", ResultKind.Completed => "Concluído", ResultKind.Failed => "Falhou", ResultKind.Unavailable => "Indisponível", ResultKind.Partial => "Parcial", ResultKind.Unknown => "Não confirmado", _ => "Cancelado" }} · {Message}";
}

public static class Outcome
{
    public static ActionResult Keys(uint sent, uint expected) => sent == expected
        ? new(ResultKind.Requested, "Atalho enviado; abertura e visibilidade não confirmadas. Reabra pelo seu atalho ou combinação do controle.")
        : new(sent == 0 ? ResultKind.Failed : ResultKind.Partial, "O Windows recusou parte ou todo o atalho. Soltura de teclas tentada; não haverá repetição automática.");
    public static ActionResult Closure(bool gone, bool deadline) => gone ? new(ResultKind.Completed, "A janela confirmada foi fechada.")
        : new(deadline ? ResultKind.Unknown : ResultKind.Requested, deadline ? "A janela continua aberta: pode haver diálogo de salvar ou recusa. Nenhuma escalada." : "WM_CLOSE enviado somente à janela confirmada.");
}

public enum ModuleEvidence { Unknown, Absent, Denied, Loaded }
public sealed class SteamPermission
{
    private WindowRef? verified;
    private string shortcut = "Shift+Tab";
    public void Verify(WindowRef target, string keys) { verified = target; shortcut = keys; }
    public bool ToggleAllowed(WindowRef? target, ModuleEvidence evidence, string keys) => evidence == ModuleEvidence.Loaded && target is not null && target.Same(verified) && shortcut == keys;
    public void Invalidate() => verified = null;
    public static string Reason(ModuleEvidence evidence) => evidence switch {
        ModuleEvidence.Loaded => "Módulo carregado; teste e confirme nesta sessão antes de alternar.",
        ModuleEvidence.Absent => "O módulo do overlay não foi observado nesta janela.",
        ModuleEvidence.Denied => "Sem acesso aos módulos desta janela; suporte desconhecido.",
        _ => "Steam aberto ou origem do jogo não comprovam suporte." };
}

[Flags] public enum PadButtons : ushort { None=0, Up=1, Down=2, Left=4, Right=8, Start=16, Back=32, LB=256, RB=512, A=4096, B=8192, X=16384 }
public readonly record struct PadFrame(bool Connected, PadButtons Buttons, short X=0, short Y=0);
public readonly record struct PadEvents(PadButtons Pressed, int Direction, bool ADown, bool Disconnected);

public sealed record OpeningConfig(uint Modifiers,uint Key,PadButtons Buttons,int HoldMs)
{
    public static OpeningConfig Parse(string hotkey,string chord,int hold)
    {
        if(string.IsNullOrWhiteSpace(hotkey)||string.IsNullOrWhiteSpace(chord))throw new ArgumentException("Atalho e combinação são obrigatórios.");
        if(hold<300||hold>2000)throw new ArgumentException("Pressão deve ficar entre 300 e 2000 ms.");uint modifiers=0,key=0;
        foreach(var token in hotkey.Split('+')){var modifier=token.ToUpperInvariant() switch{"CTRL"=>2u,"ALT"=>1u,"SHIFT"=>4u,_=>0u};if(modifier!=0){if((modifiers&modifier)!=0)throw new ArgumentException("Modificador repetido.");modifiers|=modifier;}else{if(key!=0)throw new ArgumentException("Use uma tecla.");key=token.Equals("Space",StringComparison.OrdinalIgnoreCase)?32u:token.Length==1&&char.IsAsciiLetter(token[0])?(uint)char.ToUpperInvariant(token[0]):0;if(key==0)throw new ArgumentException("Use letra ou Space.");}}
        if(modifiers==0||key==0)throw new ArgumentException("Use modificador e tecla.");PadButtons buttons=0;
        foreach(var token in chord.Split('+')){var button=token.ToUpperInvariant() switch{"LB"=>PadButtons.LB,"RB"=>PadButtons.RB,"START"=>PadButtons.Start,"BACK"=>PadButtons.Back,"A"=>PadButtons.A,"B"=>PadButtons.B,"X"=>PadButtons.X,_=>PadButtons.None};if(button==0||(buttons&button)!=0)throw new ArgumentException("Combinação inválida.");buttons|=button;}
        var bits=(ushort)buttons;if((bits&(bits-1))==0)throw new ArgumentException("Use dois ou mais botões.");return new(modifiers|0x4000,key,buttons,hold);
    }
}
public sealed class PadInterpreter(int deadzone=18000)
{
    private bool armed;
    private PadButtons previous;
    private int lastDirection;
    private long repeatAt;
    public void Reset() { armed=false; previous=0; lastDirection=0; }
    public PadEvents Update(PadFrame frame, long now)
    {
        if (!frame.Connected) { Reset(); return new(0,0,false,true); }
        var vertical=frame.Buttons.HasFlag(PadButtons.Up) ? -1 : frame.Buttons.HasFlag(PadButtons.Down) ? 1 : 0;
        var horizontal=frame.Buttons.HasFlag(PadButtons.Left) ? -2 : frame.Buttons.HasFlag(PadButtons.Right) ? 2 : 0;
        var direction=vertical!=0 ? vertical : horizontal;
        if(direction==0 && Math.Max(Math.Abs((int)frame.X),Math.Abs((int)frame.Y))>deadzone)
            direction=Math.Abs((int)frame.X)>Math.Abs((int)frame.Y) ? frame.X<0 ? -2 : 2 : frame.Y>0 ? -1 : 1; // Vertical wins exact diagonal ties.
        if (!armed) { if (frame.Buttons == 0 && Math.Abs((int)frame.Y)<deadzone*2/3 && Math.Abs((int)frame.X)<deadzone*2/3) armed=true; previous=frame.Buttons; return default; }
        var pressed=frame.Buttons & ~previous; previous=frame.Buttons;
        var move=0;
        if (direction == 0) lastDirection=0;
        else if (direction != lastDirection) { move=direction; repeatAt=now+420; lastDirection=direction; }
        else if (now>=repeatAt) { move=direction; repeatAt=now+150; }
        return new(pressed,move,frame.Buttons.HasFlag(PadButtons.A),false);
    }
}

public sealed class CloseHold
{
    public WindowRef? Target { get; private set; }
    private bool released;
    private long? since;
    public double Progress { get; private set; }
    public void Open(WindowRef target) { Target=target; released=false; since=null; Progress=0; }
    public void Cancel() { Target=null; since=null; released=false; Progress=0; }
    public bool Tick(bool a, bool cancel, bool focused, bool connected, WindowRef? current, long now)
    {
        if (Target is null) return false;
        if (cancel || !focused || !connected || !Target.Same(current)) { Cancel(); return false; }
        if (!a) { released=true; since=null; Progress=0; return false; }
        if (!released) return false;
        since ??= now;
        Progress=Math.Clamp((now-since.Value)/1500d,0,1);
        if (Progress < 1) return false;
        Cancel(); return true;
    }
}

public sealed record DisplayRef(string Id, int X, int Y, int Width, int Height, bool Primary)
{
    public static DisplayRef Choose(IReadOnlyList<DisplayRef> displays, string? selected) => displays.FirstOrDefault(d=>d.Id==selected) ?? displays.FirstOrDefault(d=>d.Primary) ?? displays[0];
    public (double X,double Y,double Width,double Height) Dips(double dpi) => (X*96/dpi,Y*96/dpi,Width*96/dpi,Height*96/dpi);
}

public sealed record UserSettings(string? PlaynitePath=null, string? DisplayId=null, string SteamShortcut="Shift+Tab",int Version=1,string Hotkey="Ctrl+Alt+Space",string Chord="LB+RB+Start",int ChordHoldMs=650,int Deadzone=18000,bool ReducedMotion=false)
{
    public static UserSettings Parse(string text)
    {
        var parsed=JsonSerializer.Deserialize(text, SettingsJson.Default.UserSettings) ?? throw new InvalidDataException("Configuração vazia.");
        if (parsed.SteamShortcut is not ("Shift+Tab" or "Ctrl+Tab") || (parsed.PlaynitePath is not null && (!Path.IsPathFullyQualified(parsed.PlaynitePath) || !parsed.PlaynitePath.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)))) throw new InvalidDataException("Configuração inválida.");
        if(parsed.Version!=1 || parsed.Deadzone<8000 || parsed.Deadzone>28000)throw new InvalidDataException("Versão ou deadzone inválida.");
        try{_=OpeningConfig.Parse(parsed.Hotkey,parsed.Chord,parsed.ChordHoldMs);}catch(ArgumentException e){throw new InvalidDataException("Atalho ou combinação inválida.",e);}
        return parsed;
    }
    public static UserSettings Load(string path, out string message)
    {
        message="Configuração padrão; nada será salvo automaticamente.";
        if (!File.Exists(path)) return new();
        try { return Parse(File.ReadAllText(path)); }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidDataException) {
            try { var restored=Parse(File.ReadAllText(path+".previous")); message="Configuração anterior recuperada em memória; salve para restaurar."; return restored; }
            catch (Exception back) when (back is JsonException or IOException or UnauthorizedAccessException or InvalidDataException) { message="Configuração inválida; usando padrão seguro."; return new(); }
        }
    }
    public static void Save(string path, UserSettings settings)
    {
        _=Parse(JsonSerializer.Serialize(settings,SettingsJson.Default.UserSettings));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var pending=path+".pending";
        File.WriteAllText(pending,JsonSerializer.Serialize(settings,SettingsJson.Default.UserSettings));
        if (File.Exists(path)) {
            var currentIsGood=true;
            try { _=Parse(File.ReadAllText(path)); }
            catch(Exception e) when(e is JsonException or InvalidDataException) { currentIsGood=false; }
            if(!currentIsGood)File.Copy(path,path+".corrupt-"+DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"),false);
            File.Replace(pending,path,currentIsGood ? path+".previous" : null);
        } else File.Move(pending,path);
    }
}
[JsonSerializable(typeof(UserSettings))] internal partial class SettingsJson : JsonSerializerContext;
