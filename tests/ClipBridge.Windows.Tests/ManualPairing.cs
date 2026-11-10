using System.Reflection;
using System.Text.Json;
using ClipBridge;
static class ManualPairing {
 public static void Run(string directory){
  Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
  string start=Path.Combine(directory,"interop-start.json");long deadline=Environment.TickCount64+120000;while(!File.Exists(start)){if(Environment.TickCount64>deadline)throw new Exception("Missing host invitation");Thread.Sleep(100);}
  using var input=JsonDocument.Parse(File.ReadAllText(start));var connection=input.RootElement.GetProperty("connection").Deserialize<GroupConnection>(Wire.Json)!;string code=input.RootElement.GetProperty("code").GetString()!;var identity=GroupIdentity.Create(Guid.NewGuid().ToString("N"),"Windows manual-code guest");
  Type type=Assembly.Load("ClipBridge.Windows").GetType("GroupWizard")!;object Field(object target,string name)=>type.GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(target)!;
  Action<DeviceGroup> joined=group=>{var host=group.Members[input.RootElement.GetProperty("hostId").GetString()!].Member;File.WriteAllText(Path.Combine(directory,"interop-result.json"),JsonSerializer.Serialize(new{member=identity.Member,config=Groups.Channel(group,identity,host)},Wire.Json));};
  Func<string,GroupConnection,string,CancellationToken,Task<DeviceGroup>> create=(_,_,_,_)=>throw new Exception("Guest must not create group");
  using var form=(Form)Activator.CreateInstance(type,[identity,true,null!,create,(Action)(()=>{}),joined,(Action<string>)(_=>{})])!;
  ((TextBox)Field(form,"address")).Text=connection.Server.TrimEnd('/')+"/";((TextBox)Field(form,"invitation")).Text=GroupPake.Display(code).ToLowerInvariant();
  bool complete=false;Exception? error=null;var watch=new System.Windows.Forms.Timer{Interval=100};watch.Tick+=(_,_)=>{try{var session=(GroupSession?)type.GetField("session",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(form);if(session?.Ready==true&&!session.Approved){string hostSas=Path.Combine(directory,"host-sas.txt");if(File.Exists(hostSas)){if(File.ReadAllText(hostSas)!=session.Sas)throw new Exception("Safety numbers differ");File.WriteAllText(Path.Combine(directory,"java-sas.txt"),session.Sas);session.Approve();}}if(session?.Done==true){complete=true;form.Close();}if(Environment.TickCount64>deadline)throw new Exception("Manual join timeout: "+((Label)Field(form,"feedback")).Text);}catch(Exception e){error=e;form.Close();}};
  form.Shown+=async(_,_)=>{try{watch.Start();await (Task)type.GetMethod("Begin",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(form,null)!;var session=type.GetField("session",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(form);if(session is null)throw new Exception("Manual join failed: "+((Label)Field(form,"feedback")).Text);Console.WriteLine("PASS Windows form connects using typed server and displayed code without invitation link");}catch(Exception e){error=e;form.Close();}};
  Application.Run(form);watch.Dispose();if(error is not null)throw error;if(!complete)throw new Exception("Window closed before completing pairing");Console.WriteLine("PASS Windows manual-code form finishes real mutual-confirmation pairing");
 }
}
