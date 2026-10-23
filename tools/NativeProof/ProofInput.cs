using ApolloOverlay;

namespace Bezoro.NativeProof;

internal sealed record ProofConfig(uint Modifiers,uint Key,PadButtons Chord,int HoldMs)
{
    internal static ProofConfig Parse(string hotkey,string chord,int hold)
    {
        if(hold<300 || hold>2000)throw new ArgumentException("hold-ms deve estar entre 300 e 2000.");
        uint modifiers=0,key=0;
        foreach(var token in hotkey.Split('+')){
            var modifier=token.ToLowerInvariant() switch{"ctrl"=>2u,"alt"=>1u,"shift"=>4u,_=>0u};
            if(modifier!=0){if((modifiers&modifier)!=0)throw new ArgumentException("Modificador repetido.");modifiers|=modifier;continue;}
            if(key!=0)throw new ArgumentException("Use uma única tecla.");
            key=token.Equals("Space",StringComparison.OrdinalIgnoreCase)?32u:token.Length==1&&char.IsAsciiLetter(token[0])?(uint)char.ToUpperInvariant(token[0]):0;
            if(key==0)throw new ArgumentException("Atalho aceita letras ou Space.");
        }
        if(modifiers==0 || key==0)throw new ArgumentException("Atalho exige modificador e tecla.");
        PadButtons buttons=0;
        foreach(var token in chord.Split('+')){
            var button=token.ToUpperInvariant() switch{"LB"=>PadButtons.LB,"RB"=>PadButtons.RB,"START"=>PadButtons.Start,"BACK"=>PadButtons.Back,"A"=>PadButtons.A,"B"=>PadButtons.B,"X"=>PadButtons.X,_=>PadButtons.None};
            if(button==0 || (buttons&button)!=0)throw new ArgumentException("Combinação inválida ou repetida.");buttons|=button;
        }
        var bits=(ushort)buttons;if((bits&(bits-1))==0)throw new ArgumentException("Use ao menos dois botões comuns.");
        return new(modifiers|0x4000,key,buttons,hold); // MOD_NOREPEAT
    }
}

internal sealed class ChordLatch(PadButtons mask,int holdMs)
{
    private int? slot;
    private long since;
    private bool armed,fired,holding;
    internal void Reset(){slot=null;armed=false;fired=false;holding=false;}
    internal bool Update(IReadOnlyList<PadFrame> frames,long now)
    {
        if(slot is {} current){
            var frame=frames[current];
            if(!frame.Connected){Reset();return false;}
            if((frame.Buttons&mask)==0){slot=null;armed=true;fired=false;holding=false;return false;}
            if((frame.Buttons&mask)!=mask){holding=false;return false;}
            if(!holding){holding=true;since=now;return false;}
            if(!fired && now-since>=holdMs){fired=true;return true;}return false;
        }
        if(!armed){armed=frames.All(frame=>!frame.Connected || (frame.Buttons&mask)==0);return false;}
        for(var i=0;i<frames.Count;i++)if(frames[i].Connected && (frames[i].Buttons&mask)==mask){slot=i;since=now;holding=true;break;}
        return false;
    }
}

internal static class ProofNavigation
{
    internal static int Move(int selected,NavDirection direction){var row=selected/2;var col=selected%2;if(direction==NavDirection.Up)row=0;if(direction==NavDirection.Down)row=1;if(direction==NavDirection.Left)col=0;if(direction==NavDirection.Right)col=1;return row*2+col;}
    internal static List<CaseResult> Tests()
    {
        var results=new List<CaseResult>();
        void Test(string name,Action action){try{action();results.Add(new(name,true,null));}catch(Exception e){results.Add(new(name,false,e.Message));}}
        void Assert(bool value){if(!value)throw new InvalidOperationException("Comportamento inesperado.");}
        Test("valid configurable hotkey/chord",()=>{var c=ProofConfig.Parse("Ctrl+Alt+Space","LB+RB+Start",650);Assert(c.Key==32 && c.HoldMs==650);});
        Test("invalid configuration rejected",()=>{foreach(var pair in new[]{("Space","LB+RB"),("Ctrl+Ctrl+O","LB+RB"),("Alt+F4","Guide"),("Ctrl+O","A")}){var rejected=false;try{ProofConfig.Parse(pair.Item1,pair.Item2,650);}catch(ArgumentException){rejected=true;}Assert(rejected);}});
        Test("initial-held chord requires release",()=>{var latch=new ChordLatch(PadButtons.LB|PadButtons.RB,650);Assert(!latch.Update([new(true,PadButtons.LB|PadButtons.RB)],0));Assert(!latch.Update([new(true,PadButtons.LB|PadButtons.RB)],1000));});
        Test("chord duration debounce and release",()=>{var latch=new ChordLatch(PadButtons.LB|PadButtons.RB,650);latch.Update([new(true,0)],0);latch.Update([new(true,PadButtons.LB|PadButtons.RB)],1);Assert(!latch.Update([new(true,PadButtons.LB|PadButtons.RB)],650));Assert(latch.Update([new(true,PadButtons.LB|PadButtons.RB)],651));Assert(!latch.Update([new(true,PadButtons.LB|PadButtons.RB)],5000));latch.Update([new(true,0)],5001);latch.Update([new(true,PadButtons.LB|PadButtons.RB)],5002);Assert(latch.Update([new(true,PadButtons.LB|PadButtons.RB)],5652));});
        Test("chord never combines slots",()=>{var latch=new ChordLatch(PadButtons.LB|PadButtons.RB,650);latch.Update([new(true,0),new(true,0)],0);Assert(!latch.Update([new(true,PadButtons.LB),new(true,PadButtons.RB)],1000));Assert(!latch.Update([new(true,PadButtons.LB),new(true,PadButtons.RB)],2000));});
        Test("disconnect cancels chord and requires neutral",()=>{var latch=new ChordLatch(PadButtons.LB|PadButtons.RB,650);latch.Update([new(true,0)],0);latch.Update([new(true,PadButtons.LB|PadButtons.RB)],1);latch.Update([default],700);Assert(!latch.Update([new(true,PadButtons.LB|PadButtons.RB)],2000));});
        Test("partial release requires a complete new hold interval",()=>{var latch=new ChordLatch(PadButtons.LB|PadButtons.RB,650);latch.Update([new(true,0)],0);latch.Update([new(true,PadButtons.LB|PadButtons.RB)],1);latch.Update([new(true,PadButtons.LB)],500);Assert(!latch.Update([new(true,PadButtons.LB|PadButtons.RB)],600));Assert(!latch.Update([new(true,PadButtons.LB|PadButtons.RB)],1150));Assert(latch.Update([new(true,PadButtons.LB|PadButtons.RB)],1250));});
        Test("deactivation reset cancels elapsed hold and requires release",()=>{var latch=new ChordLatch(PadButtons.LB|PadButtons.RB,650);latch.Update([new(true,0)],0);latch.Update([new(true,PadButtons.LB|PadButtons.RB)],1);latch.Reset();Assert(!latch.Update([new(true,PadButtons.LB|PadButtons.RB)],2000));latch.Update([new(true,0)],2001);latch.Update([new(true,PadButtons.LB|PadButtons.RB)],2002);Assert(!latch.Update([new(true,PadButtons.LB|PadButtons.RB)],2651));Assert(latch.Update([new(true,PadButtons.LB|PadButtons.RB)],2652));});
        Test("navigation boundaries retain explicit focus",()=>{Assert(Move(0,NavDirection.Left)==0);Assert(Move(0,NavDirection.Right)==1);Assert(Move(1,NavDirection.Down)==3);Assert(Move(3,NavDirection.Right)==3);Assert(Move(3,NavDirection.Up)==1);});
        Test("pad activation and reconnect require release",()=>{var p=new PadInterpreter();Assert(p.Update(new(true,PadButtons.A),0).Pressed==0);p.Update(new(true,0),1);Assert(p.Update(new(true,PadButtons.A),2).Pressed==PadButtons.A);Assert(p.Update(new(true,PadButtons.A),1000).Pressed==0);p.Update(default,1001);Assert(p.Update(new(true,PadButtons.A),1002).Pressed==0);});
        Test("direction repeat is bounded",()=>{var p=new PadInterpreter();p.Update(new(true,0),0);Assert(p.Update(new(true,PadButtons.Right),1).Direction==2);Assert(p.Update(new(true,PadButtons.Right),200).Direction==0);Assert(p.Update(new(true,PadButtons.Right),421).Direction==2);});
        return results;
    }
}
