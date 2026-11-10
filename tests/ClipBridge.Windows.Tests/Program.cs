using System.Reflection;
using ClipBridge;
internal static class Program
{
    [STAThread] private static void Main(string[] args)
    {
        if(args.Length>0&&args[0]=="provider-invite"){ProviderInvitation.Run(args[1]);return;}
        if(args.Length>0&&args[0]=="manual-pair"){ManualPairing.Run(args[1]);return;}
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        var originType=Assembly.Load("ClipBridge.Windows").GetType("ClipboardOrigin")!;var origin=Guid.NewGuid().ToString("N");var eventId=Guid.NewGuid().ToString("N");var remote=(DataObject)originType.GetMethod("RemoteText")!.Invoke(null,["mesh event",origin,eventId])!;
        for(int receiver=0;receiver<2;receiver++)if(!(bool)originType.GetMethod("IsRemote")!.Invoke(null,[remote])!)throw new Exception("Remote event lost provenance at a receiver");remote.SetText("new local text",TextDataFormat.UnicodeText);if((bool)originType.GetMethod("IsRemote")!.Invoke(null,[remote])!)throw new Exception("Stale marker suppressed a new local copy");Console.WriteLine("PASS one remote event is non-forwardable at both Windows receivers; changed local content remains sendable");
        var assembly=Assembly.Load("ClipBridge.Windows");
        using(var dialog=new Form()){assembly.GetType("AppStyle")!.GetMethod("Dialog",BindingFlags.Public|BindingFlags.Static)!.Invoke(null,[dialog,"test",new System.Drawing.Size(500,400)]);using var stream=assembly.GetManifestResourceStream("ClipBridge.App.ico")!;using var expected=new System.Drawing.Icon(stream);using var actualBitmap=dialog.Icon!.ToBitmap();using var expectedBitmap=expected.ToBitmap();if(actualBitmap.Size!=expectedBitmap.Size||Enumerable.Range(0,actualBitmap.Width).Any(x=>Enumerable.Range(0,actualBitmap.Height).Any(y=>actualBitmap.GetPixel(x,y)!=expectedBitmap.GetPixel(x,y))))throw new Exception("Dialog icon differs from app icon");}Console.WriteLine("PASS all app dialogs use the embedded ClipBridge icon");

        var wizardType=assembly.GetType("GroupWizard")!;
        var testIdentity=GroupIdentity.Create(Guid.NewGuid().ToString("N"),"Form order check");
        Func<string,GroupConnection,string,CancellationToken,Task<DeviceGroup>> noCreate=(_,_,_,_)=>throw new Exception("Layout check must not connect");
        foreach(bool joining in new[]{false,true}){
            using var wizard=(Form)Activator.CreateInstance(wizardType,[testIdentity,joining,null!,noCreate,(Action)(()=>{}),(Action<DeviceGroup>)(_=>{}),(Action<string>)(_=>{})])!;
            object Field(string name)=>wizardType.GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(wizard)!;
            var transport=(ComboBox)Field("transport");var account=(Control)Field("usernameRow");var password=(Control)Field("passwordRow");
            wizard.ShowInTaskbar=false;wizard.Show();Application.DoEvents();
            for(int change=0;change<3;change++){
                transport.SelectedIndex=1;wizard.PerformLayout();account.Parent!.PerformLayout();Application.DoEvents();
                if(!account.Visible||!password.Visible||account.Bottom>password.Top||account.TabIndex>=password.TabIndex)throw new Exception("WebDAV account must render and tab before password");
                if(!account.Controls.OfType<Label>().Any(c=>c.Text=="WebDAV账号")||!password.Controls.OfType<Label>().Any(c=>c.Text=="WebDAV密码"))throw new Exception("WebDAV field labels differ");
                transport.SelectedIndex=0;
            }
            wizard.Close();
        }
        Console.WriteLine("PASS WebDAV create/join forms render account above password with matching keyboard order after transport switches");

        if(assembly.EntryPoint!.GetCustomAttribute<STAThreadAttribute>() is null)throw new Exception("STA missing");
        string directory=Path.Combine(Path.GetTempPath(),"clipbridge-ui-"+Guid.NewGuid().ToString("N"));
        var type=assembly.GetType("BridgeWindow")!;using var form=(Form)Activator.CreateInstance(type,[Array.Empty<string>(),"ui-test-"+Guid.NewGuid().ToString("N"),directory])!;
        var data=type.GetField("data",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(form)!;
        var profiles=(List<BridgeProfile>)data.GetType().GetProperty("Profiles")!.GetValue(data)!;
        var dav=WebDavPairing.Create("https://example.invalid/dav/","test","test");
        profiles.Add(new(){Config=dav.Windows,Name="测试手机 · 很长的设备名称",Enabled=false});
        profiles.Add(new(){Config=dav.Windows with {Pair=Guid.NewGuid().ToString("N"),Transport="relay",Username="",Password=""},Name="测试手机",Enabled=false});
        var render=type.GetMethod("RenderPage",BindingFlags.NonPublic|BindingFlags.Instance)!;render.Invoke(form,null);
        static IEnumerable<Control> Tree(Control root)=>root.Controls.Cast<Control>().SelectMany(c=>new[]{c}.Concat(Tree(c)));
        var controls=Tree(form).ToArray();for(int i=0;i<5;i++)render.Invoke(form,null);
        if(!controls.SequenceEqual(Tree(form)))throw new Exception("Status refresh recreated controls");
        Console.WriteLine("PASS status refresh preserves controls and focus");
        if(Tree(form).OfType<Button>().Any(b=>b.Text is "配置" or "添加配置" or "导入配置"))throw new Exception("Obsolete configuration navigation remains");
        Console.WriteLine("PASS navigation uses groups without duplicate configuration page");
        var pendingType=assembly.GetType("ClipboardBatch")!;var pending=Activator.CreateInstance(pendingType)!;var targets=profiles.Select(p=>p.Group).Distinct().ToList();pendingType.GetProperty("Text")!.SetValue(pending,"test");pendingType.GetProperty("Groups")!.SetValue(pending,targets);data.GetType().GetProperty("Clipboard")!.SetValue(data,pending);type.GetMethod("Reconcile",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(form,null);if(targets.Any(g=>!((List<string>)pendingType.GetProperty("DoneGroups")!.GetValue(pending)!).Contains(g)))throw new Exception("Disabled group retained a pending text target");data.GetType().GetProperty("Clipboard")!.SetValue(data,null);
        Console.WriteLine("PASS disabled groups cannot resend queued text after re-enable");

        foreach(var size in new[]{new System.Drawing.Size(1000,720),new System.Drawing.Size(900,650),new System.Drawing.Size(1150,800)}){form.ClientSize=size;form.PerformLayout();render.Invoke(form,null);foreach(var card in Tree(form).Where(c=>c.GetType().Name=="SoftPanel"))foreach(Control c in card.Controls)if(c.Left<0||c.Right>card.ClientSize.Width+1)throw new Exception("Card content clipped after resize");}
        Console.WriteLine("PASS rounded cards retain content across window sizes");
        var summary=(Label)type.GetField("summaryText",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(form)!;
        var resize=type.GetMethod("ResizeDashboard",BindingFlags.NonPublic|BindingFlags.Instance)!;
        foreach(float size in new[]{16f,20f,24f,32f}){summary.Font=new System.Drawing.Font(summary.Font.FontFamily,size,System.Drawing.FontStyle.Bold);resize.Invoke(form,null);if(summary.Height<TextRenderer.MeasureText(summary.Text,summary.Font).Height||summary.Bottom>summary.Parent!.Height)throw new Exception("Summary text clipped at enlarged font size");}
        Console.WriteLine("PASS summary text fits enlarged fonts up to 200 percent");
        type.GetField("selectedPage",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(form,2);render.Invoke(form,null);
        if(Tree(form).OfType<CheckBox>().Any(c=>c.GetType().Name!="PillSwitch")||Tree(form).OfType<CheckBox>().Count()!=3)throw new Exception("Settings do not use capsule switches");
        Console.WriteLine("PASS settings use accessible capsule switches");
        if(!(bool)data.GetType().GetProperty("AutoSendImages")!.GetValue(data)!)throw new Exception("Image auto-send migration default changed");
        data.GetType().GetProperty("AutoSendImages")!.SetValue(data,false);
        var hint=type.GetMethod("ImageHint",BindingFlags.NonPublic|BindingFlags.Instance)!;
        if(!((string)hint.Invoke(form,null)!).Contains("快捷键"))throw new Exception("Disabled image auto-send has no manual-upload hint");
        data.GetType().GetProperty("UploadModifiers")!.SetValue(data,6u);data.GetType().GetProperty("UploadKey")!.SetValue(data,(uint)Keys.F8);
        if(!((string)hint.Invoke(form,null)!).Contains("F8"))throw new Exception("Manual-upload hint omits configured shortcut");
        Console.WriteLine("PASS image auto-send defaults on and disabled state explains configured shortcut");
        if((int)data.GetType().GetProperty("TextPollSeconds")!.GetValue(data)!!=2||(int)data.GetType().GetProperty("AttachmentPollSeconds")!.GetValue(data)!!=3)throw new Exception("Polling defaults mismatch");
        if(Tree(form).OfType<NumericUpDown>().Count()!=2)throw new Exception("Polling interval controls missing");
        var tray=type.GetField("tray",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(form) as NotifyIcon;
        if(tray!.ContextMenuStrip!.Items.Count!=3||tray.ContextMenuStrip.Items[0].Text!="发送文件"||tray.ContextMenuStrip.Items[2].Text!="退出")throw new Exception("Tray is not minimal");
        Console.WriteLine("PASS configurable polling intervals and three-item tray");
        var toast=(Microsoft.Toolkit.Uwp.Notifications.ToastContentBuilder)type.GetMethod("BuildReceivedToast",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[Guid.NewGuid().ToString("N"),"report.txt",Guid.NewGuid().ToString("N")])!;
        var xml=System.Xml.Linq.XDocument.Parse(toast.GetToastContent().GetContent());var buttons=xml.Descendants("action").ToArray();if(buttons.Length!=2||buttons[0].Attribute("content")?.Value!="打开文件"||buttons[1].Attribute("content")?.Value!="打开文件所在位置"||buttons.Any(b=>b.Attribute("activationType")?.Value!="protocol"))throw new Exception("Received notification actions missing");
        Console.WriteLine("PASS received-file toast has open-file and show-in-folder actions");



        type.GetField("selectedPage",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(form,0);render.Invoke(form,null);
        if(args.Contains("--preview")){form.Text="ClipBridge · 界面测试";Application.Run(form);}
        form.Dispose();
        Console.WriteLine("PASS Windows UI checks");
    }
}
