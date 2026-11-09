using ClipBridge;
using System.Drawing.Drawing2D;

sealed partial class BridgeWindow
{
    private readonly Panel page = new() { Dock=DockStyle.Fill, Padding=new Padding(26), AutoScroll=true };
    private readonly Label liveStatus = new() { AutoSize=false,Height=36 };
    private readonly Label lastTime = new() { AutoSize=false,Height=30 };
    private readonly System.Windows.Forms.Timer uiTimer = new() { Interval=1000 };
    private int selectedPage;
    private void BuildDashboard()
    {
        Font=new Font("Microsoft YaHei UI",10);BackColor=Color.FromArgb(245,246,247);
        Size=new Size(810,590);MinimumSize=new Size(760,550);StartPosition=FormStartPosition.CenterScreen;
        var side=new Panel{Dock=DockStyle.Left,Width=160,BackColor=Color.White};
        var brand=MakeLabel("ClipBridge",18,FontStyle.Bold);brand.Location=new Point(18,28);side.Controls.Add(brand);
        string[] names=["状态","设备","设置"];
        for(int i=0;i<names.Length;i++){int index=i;var nav=MakeButton(names[i],false);nav.SetBounds(14,100+i*54,132,42);nav.Click+=(_,_)=>{selectedPage=index;RenderPage();};side.Controls.Add(nav);}
        Controls.Add(page);Controls.Add(side);
        uiTimer.Tick+=(_,_)=>{liveStatus.Text=config is null?"未配对":paused?"已暂停":status.Text;lastTime.Text="最后同步  "+SyncTime;};uiTimer.Start();RenderPage();
        Resize+=(_,_)=>{if(started && WindowState!=FormWindowState.Minimized)RenderPage();};
    }
    private void OpenDashboard(){Show();WindowState=FormWindowState.Normal;Activate();}
    private void RenderPage()
    {
        page.SuspendLayout();
        // Reused labels must be detached before their old parents are disposed.
        liveStatus.Parent?.Controls.Remove(liveStatus);lastTime.Parent?.Controls.Remove(lastTime);
        foreach(Control child in page.Controls.Cast<Control>().ToArray())child.Dispose();
        var stack=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true,FlowDirection=FlowDirection.TopDown,WrapContents=false};
        int width=Math.Max(510,page.ClientSize.Width-52);stack.Width=width;
        var title=MakeLabel(selectedPage switch{1=>"设备",2=>"设置",_=>"状态"},23,FontStyle.Bold);title.Margin=new Padding(0,0,0,22);stack.Controls.Add(title);
        if(selectedPage==0){
            var card=new SoftCard{Size=new Size(width,218),Margin=new Padding(0,0,0,18)};
            var peer=MakeLabel(PeerTitle,18,FontStyle.Bold);peer.Location=new Point(22,22);peer.MaximumSize=new Size(width-44,54);card.Controls.Add(peer);
            liveStatus.SetBounds(22,83,width-44,30);liveStatus.Text=config is null?"未配对":paused?"已暂停":status.Text;liveStatus.ForeColor=Color.FromArgb(60,110,97);card.Controls.Add(liveStatus);
            lastTime.SetBounds(22,124,width-44,28);lastTime.Text="最后同步  "+SyncTime;lastTime.ForeColor=Color.DimGray;card.Controls.Add(lastTime);
            var action=MakeButton(config is null?"添加配对":paused?"恢复同步":"暂停同步",true);action.SetBounds(22,166,136,36);action.Click+=(_,_)=>{if(config is null)AddPairing();else{paused=!paused;RenderPage();}};card.Controls.Add(action);stack.Controls.Add(card);
            if(config is not null){var server=MakeLabel(config.Server,10);server.ForeColor=Color.Gray;server.MaximumSize=new Size(width,50);stack.Controls.Add(server);}
        }else if(selectedPage==1){
            var card=new SoftCard{Size=new Size(width,155),Margin=new Padding(0,0,0,20)};
            var peer=MakeLabel(PeerTitle,17,FontStyle.Bold);peer.Location=new Point(22,20);peer.MaximumSize=new Size(width-44,50);card.Controls.Add(peer);
            var detail=MakeLabel(config?.Server??"",10);detail.Location=new Point(22,77);detail.MaximumSize=new Size(width-44,44);detail.ForeColor=Color.Gray;card.Controls.Add(detail);stack.Controls.Add(card);
            var buttons=new FlowLayoutPanel{Size=new Size(width,50)};
            var add=MakeButton("添加配对",true);add.Size=new Size(130,40);add.Click+=(_,_)=>AddPairing();buttons.Controls.Add(add);
            var import=MakeButton("导入配置",false);import.Size=new Size(130,40);import.Click+=(_,_)=>{Import();RenderPage();};buttons.Controls.Add(import);
            var remove=MakeButton("删除配置",false);remove.Size=new Size(130,40);remove.Enabled=config is not null || File.Exists(Path.Combine(folder,"config.bin"));remove.ForeColor=Color.Firebrick;remove.Click+=(_,_)=>DeletePairing();buttons.Controls.Add(remove);stack.Controls.Add(buttons);
        }else{
            var card=new SoftCard{Size=new Size(width,140),Margin=new Padding(0,0,0,20)};
            var startup=new CheckBox{Text="开机自启动（启动后驻留托盘）",AutoSize=true,Location=new Point(22,25),Checked=AutoStart()};startup.CheckedChanged+=(_,_)=>SetAutoStart(startup.Checked);card.Controls.Add(startup);
            var version=MakeLabel("版本  0.3.1",10);version.Location=new Point(22,88);version.ForeColor=Color.Gray;card.Controls.Add(version);stack.Controls.Add(card);
        }
        page.Controls.Add(stack);page.ResumeLayout();
    }
    private void AddPairing()
    {
        using var wizard=new PairingWizard(config?.Server,folder,config is not null);
        if(wizard.ShowDialog(this)==DialogResult.OK && wizard.SelectedConfig is {} selected){try{UsePairing(selected);File.Delete(Path.Combine(folder,"pending-pairing.bin"));}catch{MessageBox.Show(this,"本地保存失败，请检查磁盘权限。","ClipBridge");}}
    }
    internal static Label MakeLabel(string text,float size,FontStyle style=FontStyle.Regular)=>new(){Text=text,AutoSize=true,Font=new Font("Microsoft YaHei UI",size,style),ForeColor=Color.FromArgb(35,39,43),BackColor=Color.Transparent};
    internal static Button MakeButton(string text,bool primary)=>new(){Text=text,FlatStyle=FlatStyle.Flat,Cursor=Cursors.Hand,Font=new Font("Microsoft YaHei UI",10),BackColor=primary?Color.FromArgb(8,127,115):Color.FromArgb(237,240,240),ForeColor=primary?Color.White:Color.FromArgb(55,64,64),FlatAppearance={BorderSize=0},UseVisualStyleBackColor=false};
}
sealed class SoftCard:Panel
{
    public SoftCard(){DoubleBuffered=true;BackColor=Color.Transparent;}
    protected override void OnPaintBackground(PaintEventArgs e){base.OnPaintBackground(e);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;using var path=new GraphicsPath();int d=24;var r=new Rectangle(0,0,Width-1,Height-1);path.AddArc(0,0,d,d,180,90);path.AddArc(r.Right-d,0,d,d,270,90);path.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);path.AddArc(0,r.Bottom-d,d,d,90,90);path.CloseFigure();using var brush=new SolidBrush(Color.White);e.Graphics.FillPath(brush,path);}
}
