using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
namespace GuideProbe;
// Public retained v2 header SHA256 2df7aa891696c72e1c7e6e3570e8aa3b2cc5629aefe5ae614386b5ad087f0830.
// IUnknown 0..2; system callback9, stop11, unregister12, focus16. No private ordinal.
internal unsafe sealed class GameInput(LimitedQueue<RawGuide> queue,Func<double> elapsed)
{
 private readonly LimitedQueue<RawGuide> events=queue;private readonly Func<double> clock=elapsed;
 private nint library,instance;private ulong token,deviceToken;private GCHandle context;private volatile bool active;private bool retained;
 internal string Status{get;private set;}="unavailable";internal int Hresult{get;private set;}
 internal bool EnumerationComplete{get;private set;}internal int DeviceRegistrationHr{get;private set;}
 internal GameInput(nint test,LimitedQueue<RawGuide> queue,Func<double> elapsed):this(queue,elapsed){instance=test;}
 private static readonly Guid V2=new("BBAA66D2-837A-40F7-A303-917D500955F4");
 internal bool Start()
 {
  if(!Initialize())return false;context=GCHandle.Alloc(this);active=true;var table=*(nint**)instance;
  ((delegate* unmanaged[Stdcall]<nint,uint,void>)table[16])(instance,0x80);
  ulong next=0;Hresult=((delegate* unmanaged[Stdcall]<nint,nint,uint,nint,delegate* unmanaged[Stdcall]<ulong,nint,nint,ulong,uint,uint,void>,ulong*,int>)table[9])(instance,0,1,GCHandle.ToIntPtr(context),&Callback,&next);token=next;
  if(Hresult>=0&&token!=0){Status="registered_guide_edges_not_yet_observed";return true;}Status="registration_failed";StopAndUnregister();return false;
 }
 internal bool StartDeviceCheck()
 {
  if(!Initialize())return false;context=GCHandle.Alloc(this);active=true;var table=*(nint**)instance;
  ((delegate* unmanaged[Stdcall]<nint,uint,void>)table[16])(instance,0xC0);
  ulong next=0;DeviceRegistrationHr=((delegate* unmanaged[Stdcall]<nint,nint,uint,uint,uint,nint,delegate* unmanaged[Stdcall]<ulong,nint,nint,ulong,uint,uint,void>,ulong*,int>)table[8])(instance,0,0x4000E,1,2,GCHandle.ToIntPtr(context),&DeviceCallback,&next);deviceToken=next;
  EnumerationComplete=DeviceRegistrationHr>=0&&deviceToken!=0;
  if(!EnumerationComplete){Hresult=DeviceRegistrationHr;Status="device_enumeration_failed";StopAndUnregister();return false;}
  next=0;Hresult=((delegate* unmanaged[Stdcall]<nint,nint,uint,nint,delegate* unmanaged[Stdcall]<ulong,nint,nint,ulong,uint,uint,void>,ulong*,int>)table[9])(instance,0,1,GCHandle.ToIntPtr(context),&Callback,&next);token=next;
  if(Hresult>=0&&token!=0){Status="blocking_enumeration_complete; guide_callback_registered; policy=0xC0";return true;}
  Status="guide_registration_failed_after_enumeration";StopAndUnregister();return false;
 }
 private bool Initialize()
 {
  if(instance!=0)return true;
  foreach(var file in new[]{"GameInputRedist.dll","GameInput.dll"}){
   if(!NativeLibrary.TryLoad(Path.Combine(Environment.SystemDirectory,file),out library))continue;
   if(!NativeLibrary.TryGetExport(library,"GameInputCreate",out var create)){Release();continue;}
   nint original=0;Hresult=((delegate* unmanaged[Stdcall]<nint*,int>)create)(&original);
   if(Hresult>=0&&original!=0){var id=V2;nint queried=0;Hresult=((delegate* unmanaged[Stdcall]<nint,Guid*,nint*,int>)(*(nint**)original)[0])(original,&id,&queried);ReleaseObject(original);if(Hresult>=0&&queried!=0){instance=queried;return true;}ReleaseObject(queried);}else ReleaseObject(original);
   Release();
  }
  Status="trusted_runtime_v2_unavailable";return false;
 }
 [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])]
 private static void Callback(ulong token,nint context,nint device,ulong timestamp,uint current,uint previous)
 =>Enqueue(context,device,timestamp,current,previous,false);
 [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])]
 private static void DeviceCallback(ulong token,nint context,nint device,ulong timestamp,uint current,uint previous)
 =>Enqueue(context,device,timestamp,current,previous,true);
 private static void Enqueue(nint context,nint device,ulong timestamp,uint current,uint previous,bool deviceEvent)
 {
  nint retainedDevice=0;
  try{if(GCHandle.FromIntPtr(context).Target is not GameInput self||!self.active)return;var arrival=self.clock();if(device!=0){((delegate* unmanaged[Stdcall]<nint,uint>)(*(nint**)device)[1])(device);retainedDevice=device;}if(self.active&&self.events.Push(new(device,timestamp,current,previous,arrival,deviceEvent)))retainedDevice=0;}
  catch{/* Never unwind through native ABI; a failed event is an overflow/unreliable sequence. */try{if(GCHandle.FromIntPtr(context).Target is GameInput self)self.events.MarkDropped();}catch{}}
  finally{ReleaseObject(retainedDevice);}
 }
 internal bool StopAndUnregister()
 {
  active=false;if(retained)return false;
  if(instance!=0){var table=*(nint**)instance;var stop=(delegate* unmanaged[Stdcall]<nint,ulong,void>)table[11];var unregister=(delegate* unmanaged[Stdcall]<nint,ulong,byte>)table[12];
   if(token!=0)stop(instance,token);if(deviceToken!=0)stop(instance,deviceToken);
   if(token!=0&&unregister(instance,token)!=0)token=0;if(deviceToken!=0&&unregister(instance,deviceToken)!=0)deviceToken=0;
   if(token!=0||deviceToken!=0){retained=true;Status="unregister_failed_retained_until_exit";return false;}}
  if(context.IsAllocated)context.Free();return true;
 }
 internal void Release(){if(token!=0||deviceToken!=0||retained)return;if(context.IsAllocated)context.Free();ReleaseObject(instance);instance=0;if(library!=0){NativeLibrary.Free(library);library=0;}}
 // v2 prefix: 3 ushorts + Usage(4) + 2 Versions(8 each) + two APP_LOCAL_DEVICE_ID(32 each), padded to int alignment.
 // Only these allowed fields are read; opaque IDs and strings are never copied or logged.
 [StructLayout(LayoutKind.Explicit,Size=108)]internal struct InfoPrefix{[FieldOffset(92)]internal int Family;[FieldOffset(96)]internal uint SupportedInput;[FieldOffset(104)]internal uint SystemButtons;}
 internal static Capability Info(nint device)
 {
  nint info=0;var hr=((delegate* unmanaged[Stdcall]<nint,nint*,int>)(*(nint**)device)[3])(device,&info);
  if(hr<0||info==0)return new(hr,false,0,0,0);var prefix=(InfoPrefix*)info;return new(hr,prefix->Family is >=-1 and <=4,prefix->Family,prefix->SupportedInput,prefix->SystemButtons);
 }
 // v2 x64: container GUID108, strings128/136, eight device-info pointers144..200,
 // axis count208/pointer216, controller button count224/pointer232. No private fields are copied.
 [StructLayout(LayoutKind.Explicit,Size=240)]private struct ButtonPrefix{[FieldOffset(96)]internal uint SupportedInput;[FieldOffset(224)]internal uint Count;[FieldOffset(232)]internal nint Buttons;}
 [StructLayout(LayoutKind.Sequential)]private struct ButtonInfo{internal uint MappedInputKinds;internal int Label;}
 internal static RawDescriptor Descriptor(nint device)
 {
  nint info=0;var hr=((delegate* unmanaged[Stdcall]<nint,nint*,int>)(*(nint**)device)[3])(device,&info);
  if(hr<0||info==0)return new(false,hr,0,[],"descriptor_failed_or_null");var prefix=(ButtonPrefix*)info;
  if(prefix->Count>128||(prefix->Count>0&&(prefix->Buttons==0||prefix->Buttons%4!=0)))return new(false,hr,prefix->SupportedInput,[],"descriptor_count_or_pointer_invalid");
  var labels=new RawLabel[(int)prefix->Count];var native=(ButtonInfo*)prefix->Buttons;
  for(var i=0;i<labels.Length;i++)labels[i]=new(i,native[i].Label,native[i].MappedInputKinds);
  return new(true,hr,prefix->SupportedInput,labels,"v2_button_labels_only");
 }
 [StructLayout(LayoutKind.Sequential)]private struct GamepadState{internal uint Buttons;private fixed float ignoredAnalogs[6];}
 internal CommonReading Read(nint device,uint kind)
 {
  nint reading=0;var hr=((delegate* unmanaged[Stdcall]<nint,uint,nint,nint*,int>)(*(nint**)instance)[4])(instance,kind,device,&reading);
  try{
   if(hr<0||reading==0)return new(hr,false,0,0,0,0,"get_current_reading_failed_or_null");var table=*(nint**)reading;
   var actual=((delegate* unmanaged[Stdcall]<nint,uint>)table[3])(reading);var timestamp=((delegate* unmanaged[Stdcall]<nint,ulong>)table[4])(reading);
   if((actual&kind)!=kind)return new(hr,false,timestamp,0,0,0,"reading_kind_mismatch");
   if(kind==0x40000){GamepadState state=default;var valid=((delegate* unmanaged[Stdcall]<nint,GamepadState*,byte>)table[18])(reading,&state)!=0;return new(hr,valid,timestamp,state.Buttons&0x3FFF,0,14,valid?"gamepad":"gamepad_state_unavailable");}
   var count=((delegate* unmanaged[Stdcall]<nint,uint>)table[8])(reading);if(count is 0 or >128)return new(hr,false,timestamp,0,0,0,"controller_button_count_unavailable_or_over_limit");
   byte* values=stackalloc byte[128];new Span<byte>(values,128).Clear();var copied=((delegate* unmanaged[Stdcall]<nint,uint,byte*,uint>)table[9])(reading,count,values);
   if(copied!=count)return new(hr,false,timestamp,0,0,0,"controller_button_array_incomplete");ulong low=0,high=0;for(var i=0;i<count;i++)if(values[i]!=0){if(i<64)low|=1UL<<i;else high|=1UL<<(i-64);}return new(hr,true,timestamp,low,high,(int)count,"controller_button_indices_only");
  }finally{ReleaseObject(reading);}
 }
 internal static void ReleaseObject(nint value){if(value!=0)((delegate* unmanaged[Stdcall]<nint,uint>)(*(nint**)value)[2])(value);}
 [StructLayout(LayoutKind.Sequential)]private struct Pad{internal ushort Buttons;internal byte LT,RT;internal short LX,LY,RX,RY;}
 [StructLayout(LayoutKind.Sequential)]private struct State{internal uint Packet;internal Pad Pad;}
 [DllImport("xinput1_4.dll",EntryPoint="XInputGetState")]private static extern uint GetState(uint slot,out State state);
 internal static bool ReadPad(uint slot,out ushort mask){var result=GetState(slot,out var value);mask=(ushort)(value.Pad.Buttons&0xF3FF);return result==0;}
}
internal unsafe sealed class Devices
{
 private readonly Dictionary<nint,int> known=[];private static readonly Guid Unknown=new("00000000-0000-0000-C000-000000000046");
 internal int? Get(nint device)
 {
  if(device==0)return null;var id=Unknown;nint identity=0;var hr=((delegate* unmanaged[Stdcall]<nint,Guid*,nint*,int>)(*(nint**)device)[0])(device,&id,&identity);if(hr<0||identity==0){GameInput.ReleaseObject(identity);return null;}
  if(known.TryGetValue(identity,out var value)){GameInput.ReleaseObject(identity);return value;}if(known.Count>=16){GameInput.ReleaseObject(identity);return null;}var next=known.Count+1;known.Add(identity,next);return next;
 }
 internal void Release(){foreach(var identity in known.Keys)GameInput.ReleaseObject(identity);known.Clear();}
}
