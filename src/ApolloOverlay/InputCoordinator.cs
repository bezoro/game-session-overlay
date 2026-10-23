namespace ApolloOverlay;

public sealed class OpeningChord(PadButtons mask,int holdMs)
{
    private int? slot;private bool armed,fired,holding;private long since;
    public void Reset(){slot=null;armed=false;fired=false;holding=false;}
    public bool Update(IReadOnlyList<PadFrame> frames,long now)
    {
        if(slot is {} current){var f=frames[current];if(!f.Connected){Reset();return false;}if((f.Buttons&mask)==0){slot=null;armed=true;fired=false;holding=false;return false;}if((f.Buttons&mask)!=mask){holding=false;return false;}if(!holding){holding=true;since=now;return false;}if(!fired&&now-since>=holdMs){fired=true;return true;}return false;}
        if(!armed){armed=frames.All(f=>!f.Connected||(f.Buttons&mask)==0);return false;}
        for(var i=0;i<frames.Count;i++)if(frames[i].Connected&&(frames[i].Buttons&mask)==mask){slot=i;since=now;holding=true;break;}return false;
    }
}
public readonly record struct OverlayInput(bool Toggle,int? Slot,PadEvents Events,bool Connected);
public sealed class InputCoordinator
{
    private readonly OpeningChord chord;private readonly int deadzone;private PadInterpreter[] pads;private int? owner;private bool suppress;
    public InputCoordinator(UserSettings settings){var config=OpeningConfig.Parse(settings.Hotkey,settings.Chord,settings.ChordHoldMs);chord=new(config.Buttons,config.HoldMs);deadzone=settings.Deadzone;pads=Enumerable.Range(0,4).Select(_=>new PadInterpreter(deadzone)).ToArray();}
    public void Cancel(){chord.Reset();owner=null;suppress=true;pads=Enumerable.Range(0,4).Select(_=>new PadInterpreter(deadzone)).ToArray();}
    public OverlayInput Update(IReadOnlyList<PadFrame> frames,bool visible,long now)
    {
        var toggle=chord.Update(frames,now);if(toggle){owner=null;suppress=true;pads=Enumerable.Range(0,4).Select(_=>new PadInterpreter(deadzone)).ToArray();return new(true,null,default,false);}
        if(!visible)return default;
        if(suppress){if(frames.All(f=>!f.Connected||(f.Buttons==0&&Math.Abs((int)f.X)<deadzone*2/3&&Math.Abs((int)f.Y)<deadzone*2/3)))suppress=false;else return default;}
        if(owner is {} old&&!frames[old].Connected){Cancel();return new(false,old,new(0,0,false,true),false);}
        var events=frames.Select((f,i)=>pads[i].Update(f,now)).ToArray();
        if(owner==null)for(var i=0;i<frames.Count;i++)if(frames[i].Connected&&(frames[i].Buttons!=0||Math.Abs((int)frames[i].X)>deadzone||Math.Abs((int)frames[i].Y)>deadzone)){owner=i;break;}
        return owner is {} slot?new(false,slot,events[slot],frames[slot].Connected):default;
    }
}
