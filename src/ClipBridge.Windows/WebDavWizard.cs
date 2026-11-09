using ClipBridge;
using QRCoder;
sealed class WebDavWizard:Form
{
    private readonly TextBox address=new(),username=new(),password=new(){UseSystemPasswordChar=true};
    private readonly Label feedback=BridgeWindow.MakeLabel("",9);
    private readonly PictureBox qr=new(){SizeMode=PictureBoxSizeMode.Zoom};
    private readonly Button generate=BridgeWindow.MakeButton("连接并生成二维码",true),finish=BridgeWindow.MakeButton("手机扫码后完成",true);
    private readonly CancellationTokenSource lifetime=new();
    public ClientConfig? SelectedConfig{get;private set;}
    public WebDavWizard(){
        Text="WebDAV 配对";ClientSize=new Size(740,590);FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;StartPosition=FormStartPosition.CenterParent;BackColor=Color.FromArgb(245,246,247);Font=new Font("Microsoft YaHei UI",10);
        var title=BridgeWindow.MakeLabel("WebDAV",23,FontStyle.Bold);title.Location=new Point(28,24);Controls.Add(title);
        TextBox[] fields=[address,username,password];string[] names=["已有目录的 HTTPS 地址","用户名","密码 / 应用密码"];
        for(int i=0;i<3;i++){var label=BridgeWindow.MakeLabel(names[i],10);label.Location=new Point(30,90+i*82);Controls.Add(label);fields[i].SetBounds(30,118+i*82,330,32);Controls.Add(fields[i]);}
        address.PlaceholderText="https://dav.example.com/folder/";
        generate.SetBounds(30,368,330,42);generate.Click+=async(_,_)=>await Generate();Controls.Add(generate);
        qr.SetBounds(395,90,310,310);Controls.Add(qr);
        feedback.SetBounds(30,435,680,70);feedback.AutoSize=false;feedback.Text="二维码包含 WebDAV 凭据及配对密钥，仅供自己的手机扫描。\n此二维码不会自动过期，也不具备一次性领取限制。";Controls.Add(feedback);
        finish.SetBounds(490,520,215,40);finish.Enabled=false;finish.Click+=(_,_)=>{DialogResult=DialogResult.OK;Close();};Controls.Add(finish);
    }
    private async Task Generate(){
        generate.Enabled=false;finish.Enabled=false;SelectedConfig=null;qr.Image?.Dispose();qr.Image=null;
        try{
            if(!Uri.TryCreate(address.Text.Trim(),UriKind.Absolute,out var uri)||uri.Scheme!="https")throw new ArgumentException("请填写已有目录的完整 HTTPS 地址。");
            var draft=WebDavPairing.Create(address.Text,username.Text,password.Text);
            string content=WebDavPairing.Qr(draft.Android);
            using var client=new RelayClient(draft.Windows);await client.ExchangeName(draft.Windows,Environment.MachineName,lifetime.Token);
            using var generator=new QRCodeGenerator();using var data=generator.CreateQrCode(content,QRCodeGenerator.ECCLevel.M);using var renderer=new PngByteQRCode(data);using var stream=new MemoryStream(renderer.GetGraphic(6));using var source=Image.FromStream(stream);qr.Image=new Bitmap(source);
            SelectedConfig=draft.Windows;finish.Enabled=true;feedback.Text="WebDAV 已连接。手机扫码确认后，点击完成。\n二维码包含凭据和密钥，无自动过期限制，请勿分享。";
        }catch(OperationCanceledException) when(lifetime.IsCancellationRequested){}
        catch(ArgumentException ex){feedback.Text=ex.Message;}
        catch{feedback.Text="连接失败：请检查目录、账号、应用密码及 GET / PUT / MKCOL 权限。";}
        finally{if(!IsDisposed)generate.Enabled=true;}
    }
    protected override void OnFormClosed(FormClosedEventArgs e){lifetime.Cancel();qr.Image?.Dispose();base.OnFormClosed(e);}
}
