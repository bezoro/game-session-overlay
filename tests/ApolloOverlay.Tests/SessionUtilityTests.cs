using System.IO;
using ApolloOverlay;

internal static class SessionUtilityTests
{
    private static readonly WindowRef Game=new(123,42,100,"Fixture game","fixture");
    private static void Require(bool value,string reason){if(!value)throw new Exception(reason);}
    private static T Complete<T>(Task<T> task){ReviewFixTests.Complete(task);return task.GetAwaiter().GetResult();}
    internal static void Run(Action<string,Action> test)
    {
        test("profiles create TV and desktop defaults without writing files",()=>
        {
            using var directory=new DirectoryFixture();var profiles=new SessionProfiles(directory.Root);
            Require(profiles.Items.Count==2&&profiles.Items.Single(p=>p.Id=="tv").ScalePercent==115&&profiles.Items.Single(p=>p.Id=="desktop").Presentation=="Desktop","Profile defaults lost their presentation");
            Require(!Directory.EnumerateFiles(directory.Root).Any(),"Read-only construction wrote a profile");
        });
        test("profiles roundtrip explicit monitor output volume and game association",()=>
        {
            using var directory=new DirectoryFixture();var profiles=new SessionProfiles(directory.Root);
            var profile=new SessionProfile("game-profile","Sala", "display",130,"Right","speaker","TV",45,"TV","fixture");profiles.Save(profile);profiles.SetActive(profile.Id);
            var restored=new SessionProfiles(directory.Root);Require(restored.ActiveId==profile.Id&&restored.Items.Single(p=>p.Id==profile.Id)==profile,"Stored profile differs from explicit settings");
            restored.SetActive("");Require(new SessionProfiles(directory.Root).ActiveId=="","Undo could not clear an active profile");
        });
        test("profiles reject invalid values while retaining exact prior bytes",()=>
        {
            using var directory=new DirectoryFixture();var profiles=new SessionProfiles(directory.Root);profiles.Save(profiles.Items[0]);
            var path=Path.Combine(directory.Root,"session-profiles.json");var before=File.ReadAllBytes(path);
            foreach(var invalid in new[]{profiles.Items[0] with{Id="../outside"},profiles.Items[0] with{Name=" "},profiles.Items[0] with{ScalePercent=99},profiles.Items[0] with{Side="Unknown"},profiles.Items[0] with{VolumePercent=101},profiles.Items[0] with{Presentation="Other"}})
            {var rejected=false;try{profiles.Save(invalid);}catch(ArgumentException){rejected=true;}Require(rejected&&before.SequenceEqual(File.ReadAllBytes(path)),"Invalid profile overwrote stored settings");}
        });
        test("profiles preserve corrupt evidence until an explicit save",()=>
        {
            using var directory=new DirectoryFixture();var path=Path.Combine(directory.Root,"session-profiles.json");File.WriteAllText(path,"{");
            var profiles=new SessionProfiles(directory.Root);Require(profiles.RecoveryNotice is not null&&profiles.Items.Count==2&&File.ReadAllText(path)=="{","Recovery erased evidence or lost defaults");
        });
        test("profiles undo changes only layout properties still owned by the operation",()=>
        {
            var before=new UserSettings(UiScalePercent:100,DisplayId:"a",PanelSide:"Left",SoundFeedbackEnabled:true);
            var applied=SessionProfiles.ApplyLayout(before,new("tv","TV","b",130,"Right"));var changed=applied with{UiScalePercent=150,SoundFeedbackEnabled=false};
            var restored=SessionProfiles.RestoreUnchangedLayout(before,applied,changed);
            Require(restored.DisplayId=="a"&&restored.PanelSide=="Left"&&restored.UiScalePercent==150&&!restored.SoundFeedbackEnabled,"Undo overwrote subsequent user preferences");
        });
        test("captures save a verified PNG and matching gallery metadata using only injected pixels",()=>
        {
            using var directory=new DirectoryFixture();var backend=new Pixels();var captures=new ScreenCaptures(directory.Root,backend);
            var result=Complete(captures.CaptureAsync(new("fixture",0,0,120,80,true),"Fixture game","fixture"));
            Require(result is{Result.Kind:ResultKind.Completed,Item:not null}&&backend.Calls==1,"Capture did not save exactly one injected frame");
            var items=Complete(captures.ListAsync());Require(items.Count==1&&items[0]==result.Item,"Gallery metadata differs from the saved artifact");
            var image=Complete(captures.ReadImageAsync(items[0]));Require(image.IsFrozen&&image.PixelWidth==120&&image.PixelHeight==80,"Saved PNG cannot be read or was needlessly enlarged");
            Require(File.Exists(Path.Combine(captures.DirectoryPath,items[0].Id+".png")),"Success did not leave its image file");
        });
        test("capture cancellation before dispatch never reads the screen or creates a file",()=>
        {
            using var directory=new DirectoryFixture();var backend=new Pixels();var captures=new ScreenCaptures(directory.Root,backend);using var cancellation=new CancellationTokenSource();cancellation.Cancel();
            try{var result=Complete(captures.CaptureAsync(new("fixture",0,0,120,80,true),"Fixture","fixture",cancellation.Token));Require(result.Result.Kind==ResultKind.Cancelled,"Cancelled capture claimed success");}catch(OperationCanceledException){}
            Require(backend.Calls==0&&!Directory.EnumerateFiles(directory.Root,"*",SearchOption.AllDirectories).Any(),"Cancelled capture read or wrote data");
        });
        test("gallery rejects traversal and ignores corrupt metadata without leaving file locks",()=>
        {
            using var directory=new DirectoryFixture();var captures=new ScreenCaptures(directory.Root,new Pixels());var item=Complete(captures.CaptureAsync(new("fixture",0,0,120,80,true),"Fixture","fixture")).Item!;
            var refused=false;try{Complete(captures.ReadImageAsync(item with{Id="../settings"}));}catch(InvalidDataException){refused=true;}
            Require(refused,"Gallery accepted a path outside its owned directory");
            var path=Path.Combine(captures.DirectoryPath,item.Id+".json");File.WriteAllText(path,"{");Require(Complete(captures.ListAsync()).Count==0,"Corrupt gallery record was rendered");
            File.Delete(Path.Combine(captures.DirectoryPath,item.Id+".png"));
        });
        test("microphone and output changes use explicit expected identities",()=>
        {
            var backend=new PreviewSystemAudioBackend();var devices=new SystemAudioDevices(backend);Complete(devices.RefreshAsync());
            Require(Complete(devices.SetMicrophoneMutedAsync("stale",true)).Kind==ResultKind.Cancelled&&!backend.State.MicrophoneMuted!.Value,"Stale microphone identity changed input");
            Require(Complete(devices.SetMicrophoneMutedAsync("preview-mic",true)).Kind==ResultKind.Simulated&&devices.Current.MicrophoneMuted==true,"Explicit microphone toggle was lost");
            Require(Complete(devices.SelectOutputAsync("foreign","preview-output","new")).Kind==ResultKind.Cancelled&&backend.State.ConsoleOutputId=="preview-output","Stale output selection changed the device");
            devices.InvalidateObservation();Require(devices.Current.MicrophoneMuted is null&&devices.Current.ConsoleOutputId=="","Stale audio state remained presented as confirmed");
        });
        test("audio observation cancellation keeps native exclusion until its owner drains",()=>
        {
            var backend=new BlockedAudio();var devices=new SystemAudioDevices(backend);using var cancellation=new CancellationTokenSource();
            try
            {
                var reading=devices.RefreshAsync(cancellation.Token);Require(backend.Entered.Wait(1000),"Audio owner did not begin");cancellation.Cancel();
                var cancelled=false;try{Complete(reading);}catch(OperationCanceledException){cancelled=true;}
                Require(cancelled&&Complete(devices.SetMicrophoneMutedAsync("mic",true)).Kind==ResultKind.Unavailable&&backend.Writes==0,"Cancellation released a still-running native owner");
            }
            finally{backend.Release.Set();}
        });
        test("application volume affects only exact positively identified process sessions",()=>
        {
            var first=new VolumeSession("one",42,.2f);var second=new VolumeSession("two",42,.8f);var foreign=new VolumeSession("foreign",43,.9f);
            using var audio=new ApplicationAudio(_=>true,(_,_)=>new VolumeCatalog([first,second,foreign]),()=>Environment.TickCount64);
            var changed=Complete(audio.SetVolumeAsync(Game,.45f,7));
            Require(changed.Result.Kind==ResultKind.Completed&&changed.Verified==2&&first.Volume==.45f&&second.Volume==.45f&&foreign.Volume==.9f&&foreign.Writes==0,"Volume crossed PID boundaries or was not verified");
            Require(changed.Snapshot.CanSetVolume&&changed.Snapshot.VolumeMinimum==.45f&&changed.Snapshot.VolumeMaximum==.45f,"Verified per-app volume was lost");
        });
        test("application volume rejects unknown reads nonfinite inputs and mismatched readback",()=>
        {
            var session=new VolumeSession("one",42,.3f){Readable=false};using var audio=new ApplicationAudio(_=>true,(_,_)=>new VolumeCatalog([session]),()=>Environment.TickCount64);
            Require(Complete(audio.SetVolumeAsync(Game,.5f,1)).Result.Kind==ResultKind.Unavailable&&session.Writes==0,"Unknown volume authorized a mutation");
            foreach(var invalid in new[]{float.NaN,float.PositiveInfinity,-.1f,1.1f})Require(Complete(audio.SetVolumeAsync(Game,invalid,1)).Result.Kind==ResultKind.Unavailable&&session.Writes==0,"Invalid level reached native audio");
            session.Readable=true;session.IgnoreWrite=true;var changed=Complete(audio.SetVolumeAsync(Game,.5f,1));
            Require(changed.Result.Kind!=ResultKind.Completed&&session.Volume==.3f&&session.Writes==1,"Accepted write was falsely treated as observed volume");
        });
        test("text entry rejects control characters invalid Unicode and stale windows before focus",()=>
        {
            var effects=new TextEffects();var service=new SessionTextEntry(effects);
            foreach(var invalid in new[]{"", "line\n",new string('x',513),"\ud800"})Require(Complete(service.InsertAsync(Game,invalid,()=>true)).Kind==ResultKind.Unavailable,"Invalid text accepted");
            effects.IsValid=false;Require(Complete(service.InsertAsync(Game,"hello",()=>true)).Kind==ResultKind.Unavailable&&effects.FocusCalls==0&&effects.Downs==0,"Stale text target was focused");
        });
        test("text entry qualifies a delayed foreground once and sends Unicode without Enter",()=>
        {
            var effects=new TextEffects{DelayReads=2};var service=new SessionTextEntry(effects);var result=Complete(service.InsertAsync(Game,"Olá 🎮",()=>true));
            Require(result.Kind==ResultKind.Requested&&effects.FocusCalls==1&&new string(effects.Characters.ToArray())=="Olá 🎮"&&effects.Characters.All(c=>!char.IsControl(c)),"Unicode changed or implicit submit/focus retry occurred");
            Require(effects.Elapsed.ElapsedMilliseconds>=500,"Text was sent before continuous foreground qualification");
        });
        test("text entry loss of foreground during qualification prevents all input",()=>
        {
            var effects=new TextEffects{LoseAfterRead=3};var result=Complete(new SessionTextEntry(effects).InsertAsync(Game,"hello",()=>true));
            Require(result.Kind==ResultKind.Cancelled&&effects.Downs==0&&effects.FocusCalls==1,"Text reached a target that lost focus");
        });
        foreach(var cleanup in new[]{true,false})
        {
            var confirmed=cleanup;
            test("text entry partial native result releases only its own packet cleanup="+confirmed,()=>
            {
                var effects=new TextEffects{Accepted=1,Cleanup=confirmed?1u:0u};var result=Complete(new SessionTextEntry(effects).InsertAsync(Game,"hello",()=>true));
                Require(result.Kind==(confirmed?ResultKind.Partial:ResultKind.Unknown)&&effects.Downs==1&&effects.Releases==1,"Partial text was retried or release uncertainty was hidden");
            });
        }
        test("text entry cancels subsequent downs after its target identity changes",()=>
        {
            var effects=new TextEffects{InvalidateAfterSend=true};var result=Complete(new SessionTextEntry(effects).InsertAsync(Game,"hello",()=>true));
            Require(result.Kind==ResultKind.Partial&&effects.Downs==1&&effects.Releases==0,"Text continued after a target identity change");
        });
        test("virtual keyboard backspace removes complete grapheme clusters",()=>
        {Require(SessionTextEntry.Backspace("A👩‍🚀")=="A"&&SessionTextEntry.Backspace("e\u0301")==""&&SessionTextEntry.Backspace("")=="","Backspace split a combining or joined character");});
    }
    private sealed class DirectoryFixture:IDisposable
    {
        internal string Root{get;}=Path.Combine(Path.GetTempPath(),"OverlaySessionUtilities",Guid.NewGuid().ToString("N"));
        internal DirectoryFixture(){Directory.CreateDirectory(Root);}
        public void Dispose(){Directory.Delete(Root,true);}
    }
    private sealed class Pixels:IScreenCaptureBackend
    {
        internal int Calls;
        public CapturePixels Capture(DisplayRef display){Calls++;var pixels=new byte[120*80*4];for(var i=0;i<pixels.Length;i+=4){pixels[i]=40;pixels[i+1]=80;pixels[i+2]=120;pixels[i+3]=255;}return new(120,80,pixels);}
    }
    private sealed class BlockedAudio:ISystemAudioBackend
    {
        internal readonly ManualResetEventSlim Entered=new(),Release=new();internal int Writes;
        public SystemAudioSnapshot Read(){Entered.Set();Require(Release.Wait(5000),"Blocked test audio was not released");return SystemAudioSnapshot.Empty;}
        public ActionResult SetMicrophoneMuted(string id,bool muted,Func<bool> continuing){Writes++;return new(ResultKind.Completed,"fixture");}
        public ActionResult SetOutputs(string a,string b,string c,string d,Func<bool> continuing){Writes++;return new(ResultKind.Completed,"fixture");}
    }
    private sealed class VolumeCatalog(IApplicationAudioSession[] sessions):IApplicationAudioCatalog
    {public ApplicationAudioCatalogSnapshot Discover(Func<bool> continuing)=>new(sessions,0);public void Dispose(){}}
    private sealed class VolumeSession(string key,uint pid,float volume):IApplicationAudioSession
    {
        public string Key=>key;internal float Volume=volume;internal int Writes;internal bool Readable=true,IgnoreWrite;
        public int ProcessId(out uint value){value=pid;return 0;}public int State(out int state){state=1;return 0;}
        public int ReadMute(out bool muted){muted=false;return 0;}public int WriteMute(bool muted)=>0;
        public int ReadVolume(out float value){value=Volume;return Readable?0:unchecked((int)0x80004005);}
        public int WriteVolume(float value){Writes++;if(!IgnoreWrite)Volume=value;return 0;}
    }
    private sealed class TextEffects:ITextEntryEffects
    {
        internal bool IsValid=true,InvalidateAfterSend;internal int FocusCalls,Downs,Releases,DelayReads,LoseAfterRead,reads;
        internal uint Accepted=2,Cleanup=1;internal readonly List<char> Characters=[];
        internal readonly System.Diagnostics.Stopwatch Elapsed=System.Diagnostics.Stopwatch.StartNew();
        public bool Valid(WindowRef target)=>IsValid;
        public bool Foreground(WindowRef target){reads++;return reads>DelayReads&&(LoseAfterRead==0||reads<=LoseAfterRead);}
        public Task<ActionResult> FocusAsync(WindowRef target,CancellationToken cancellation){FocusCalls++;return Task.FromResult(new ActionResult(ResultKind.Completed,"fixture"));}
        public uint Send(char value,bool releaseOnly){if(releaseOnly){Releases++;return Cleanup;}Downs++;Characters.Add(value);if(InvalidateAfterSend)IsValid=false;return Accepted;}
    }
}
