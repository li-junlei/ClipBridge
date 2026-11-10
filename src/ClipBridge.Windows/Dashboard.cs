using ClipBridge;
using Microsoft.Win32;
sealed partial class BridgeWindow
{
    private FlowLayoutPanel? dashboardStack;
    private string dashboardStructure="";
    private readonly Dictionary<string,(Label Name,Label Detail,PillSwitch Toggle)> profileViews=[];
    private Label? summaryText,lastText;
    private readonly List<Button> navigation=[];
    private void BuildDashboard()
    {
        Font=new Font("Microsoft YaHei UI",10);BackColor=AppStyle.Background;ClientSize=new Size(1000,720);MinimumSize=new Size(880,640);StartPosition=FormStartPosition.CenterScreen;DoubleBuffered=true;
        var side=new Panel{Dock=DockStyle.Left,Width=195,BackColor=Color.White};
        var brand=MakeLabel("ClipBridge",14,FontStyle.Bold);brand.Location=new Point(20,34);brand.AutoSize=true;side.Width=Math.Max(195,brand.PreferredWidth+40);side.Controls.Add(brand);
        for(int i=0;i<3;i++){int index=i;var nav=MakeButton(new[]{"状态","同步组","设置"}[i],false);nav.SetBounds(16,116+i*60,side.Width-32,46);nav.Click+=(_,_)=>{selectedPage=index;RenderPage();};side.Controls.Add(nav);navigation.Add(nav);}
        Controls.Add(page);Controls.Add(side);page.Resize+=(_,_)=>ResizeDashboard();
        uiTimer.Tick+=(_,_)=>{if(Visible)RenderPage();};uiTimer.Start();RenderPage();
    }
    protected override void OnDpiChanged(DpiChangedEventArgs e){base.OnDpiChanged(e);ResizeDashboard();}
    private void OpenDashboard(){Show();WindowState=FormWindowState.Normal;Activate();}
    private void ResizeDashboard(){
        if(dashboardStack is null)return;int width=Math.Max(540,page.ClientSize.Width-page.Padding.Horizontal-22);dashboardStack.Width=width;
        foreach(Control control in dashboardStack.Controls)control.Width=width;if(dashboardStack.Controls.Count>0&&dashboardStack.Controls[0] is Label title)title.Height=TextRenderer.MeasureText(title.Text,title.Font).Height+18;
        if(summaryText is not null&&!summaryText.IsDisposed&&lastText is not null&&!lastText.IsDisposed){
            summaryText.Height=TextRenderer.MeasureText("同步已开启 · 1 台设备",summaryText.Font).Height+10;
            lastText.Top=summaryText.Bottom+10;lastText.Height=TextRenderer.MeasureText("最后同步",lastText.Font).Height+8;
            summaryText.Parent!.Height=lastText.Bottom+20;
        }
        foreach(var view in profileViews.Values){
            int heading=Math.Max(40,TextRenderer.MeasureText("设备名称",view.Name.Font).Height+10);
            view.Name.Height=heading;view.Detail.Top=view.Name.Top+heading+6;
            view.Detail.Height=Math.Max(TextRenderer.MeasureText("文字",view.Detail.Font).Height*3,TextRenderer.MeasureText(view.Detail.Text,view.Detail.Font,new Size(Math.Max(1,view.Detail.Width),int.MaxValue),TextFormatFlags.WordBreak).Height)+14;
            view.Name.Parent!.Height=view.Detail.Bottom+20;
        }
    }
    private void RenderPage()
    {
        if(IsDisposed)return;
        string structure=selectedPage+":"+string.Join(",",data.Profiles.Select(p=>p.Config.Pair+":"+p.Group+":"+p.Name))+":"+string.Join(";",data.Groups.Select(g=>g.Enabled+":"+g.Id+":"+string.Join(",",g.Members.Keys)+":"+string.Join(",",g.Removed)+":"+string.Join(",",g.SuppressedPeers)));
        if(structure!=dashboardStructure||dashboardStack is null){
            dashboardStructure=structure;page.SuspendLayout();int scroll=-page.AutoScrollPosition.Y;
            foreach(Control control in page.Controls.Cast<Control>().ToArray())control.Dispose();profileViews.Clear();groupViews.Clear();
            dashboardStack=new FlowLayoutPanel{AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,FlowDirection=FlowDirection.TopDown,WrapContents=false,Location=new Point(page.Padding.Left,page.Padding.Top),Margin=Padding.Empty};
            page.Controls.Add(dashboardStack);int width=Math.Max(540,page.ClientSize.Width-page.Padding.Horizontal-22);
            var title=MakeLabel(new[]{"状态","同步组","设置"}[selectedPage],25,FontStyle.Bold);title.AutoSize=false;title.Height=65;AddBlock(title);
            if(selectedPage==0){
                var summary=new SoftPanel{Width=width,Height=103,BackColor=Color.FromArgb(222,245,231)};summaryText=MakeLabel("",16,FontStyle.Bold);summaryText.SetBounds(22,18,width-44,32);summaryText.AutoSize=false;summaryText.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;summary.Controls.Add(summaryText);lastText=MakeLabel("",10);lastText.SetBounds(22,58,width-44,25);lastText.AutoSize=false;lastText.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;summary.Controls.Add(lastText);AddBlock(summary);
                foreach(var group in data.Groups){var card=new SoftPanel{Width=width,Height=130};var label=MakeLabel(group.Name,15,FontStyle.Bold);label.AutoSize=false;label.AutoEllipsis=true;label.SetBounds(22,17,width-44,34);label.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;card.Controls.Add(label);var detail=MakeLabel("",10);detail.AutoSize=false;detail.SetBounds(22,58,width-44,58);detail.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;detail.ForeColor=AppStyle.Muted;card.Controls.Add(detail);groupViews[group.Id]=detail;AddBlock(card);}
                foreach(var legacy in LegacyGroups()){var text=MakeLabel(legacy.First().Title+" · 双设备组\n最后同步  "+Time(legacy.Max(p=>p.LastSync)),11);text.AutoSize=false;text.Height=65;groupViews["legacy:"+legacy.Key]=text;AddBlock(text);}
                if(data.Groups.Count==0&&!LegacyGroups().Any()){var empty=MakeLabel("尚无同步组",13);empty.Height=45;AddBlock(empty);}
                var actions=new FlowLayoutPanel{Height=56,WrapContents=false};var manage=MakeButton("管理同步组",true);manage.Size=new Size(145,44);manage.Click+=(_,_)=>OpenGroups();actions.Controls.Add(manage);var send=MakeButton("发送文件",false);send.Size=new Size(133,44);send.Click+=(_,_)=>_=ChooseFiles();actions.Controls.Add(send);AddBlock(actions);
            }else if(selectedPage==1)BuildGroupsPage(width);else BuildSettings(width);
            ResizeDashboard();page.ResumeLayout(true);page.AutoScrollPosition=new Point(0,scroll);
        }
        foreach(var g in data.Groups)if(groupViews.TryGetValue(g.Id,out var label)){string detail=(g.Removed.Contains(data.Identity)?"已退出":!g.Enabled?"已关闭":paused?"已暂停":g.Status)+" · "+g.Members.Keys.Count(id=>!g.Removed.Contains(id))+" 台设备 · "+(g.Connection.Transport=="webdav"?"WebDAV":"专用中转")+"\n最后同步  "+Time(g.LastSync);if(label.Text!=detail)label.Text=detail;}
        foreach(var legacy in LegacyGroups())if(groupViews.TryGetValue("legacy:"+legacy.Key,out var legacyView))legacyView.Text=legacy.First().Title+" · 双设备组 · "+(paused?"已暂停":legacy.Any(p=>p.Enabled)?"已开启":"已关闭")+"\n最后同步  "+Time(legacy.Max(p=>p.LastSync));
        for(int i=0;i<navigation.Count;i++){navigation[i].BackColor=i==selectedPage?Color.FromArgb(222,245,231):Color.White;navigation[i].ForeColor=i==selectedPage?AppStyle.Green:AppStyle.Muted;}
        foreach(var p in data.Profiles)if(profileViews.TryGetValue(p.Config.Pair,out var view)){
            if(view.Name.Text!=p.Title)view.Name.Text=p.Title;
            if(view.Toggle.Checked!=p.Enabled)view.Toggle.Checked=p.Enabled;
            string detail=(p.GroupId.Length>0?"同步组 · 文本 / 图片 / 文件":p.Config.Transport=="webdav"?"WebDAV · 文本 / 图片 / 文件":"专用中转 · 文本 / 图片 / 文件")+"    "+(p.Enabled?(paused?"已暂停":p.Status):"已关闭")+"\n最后同步  "+Time(p.LastSync)+"\n"+p.Config.Server;
            if(view.Detail.Text!=detail){view.Detail.Text=detail;ResizeDashboard();}
        }
        if(selectedPage==0&&summaryText is not null&&lastText is not null){string summary=paused?"同步已暂停":data.Profiles.Any(p=>p.Enabled)?"同步已开启 · "+data.Profiles.Where(p=>p.Enabled).Select(p=>p.Group).Distinct().Count()+" 台设备":"未开启同步组";if(summaryText.Text!=summary)summaryText.Text=summary;string last="最后同步  "+Time(data.Profiles.Select(p=>p.LastSync).Concat(data.Groups.Select(g=>g.LastSync)).DefaultIfEmpty().Max());if(lastText.Text!=last)lastText.Text=last;}
    }
    private static string Time(long value)=>value==0?"暂无记录":DateTimeOffset.FromUnixTimeMilliseconds(value).LocalDateTime.ToString("MM-dd HH:mm:ss");
    private void AddBlock(Control control){control.Margin=new Padding(0,0,0,14);dashboardStack!.Controls.Add(control);}
    private void BuildSettings(int width)
    {
        var card=new SoftPanel{Width=width,Height=255};var startup=new PillSwitch{AccessibleName="开机自启动",Checked=AutoStart(),Location=new Point(width-82,25),Anchor=AnchorStyles.Top|AnchorStyles.Right};var startupLabel=MakeLabel("开机自启动",10);startupLabel.Location=new Point(24,26);card.Controls.Add(startupLabel);startup.CheckedChanged+=(_,_)=>SetAutoStart(startup.Checked);card.Controls.Add(startup);
        var shell=new PillSwitch{AccessibleName="资源管理器右键发送",Checked=ShellEnabled(),Location=new Point(width-82,72),Anchor=AnchorStyles.Top|AnchorStyles.Right};var shellLabel=MakeLabel("资源管理器右键：通过 ClipBridge 发送",10);shellLabel.AutoSize=false;shellLabel.SetBounds(24,73,width-125,40);card.Controls.Add(shellLabel);shell.CheckedChanged+=(_,_)=>SetShell(shell.Checked);card.Controls.Add(shell);
        var label=MakeLabel("文件接收目录\n"+data.ReceiveDirectory,10);label.AutoSize=false;label.SetBounds(24,120,width-48,65);label.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;card.Controls.Add(label);
        var browse=MakeButton("更改目录",false);browse.SetBounds(24,196,140,40);browse.Click+=(_,_)=>{using var dialog=new FolderBrowserDialog{SelectedPath=data.ReceiveDirectory};if(dialog.ShowDialog(this)==DialogResult.OK){data.ReceiveDirectory=dialog.SelectedPath;Save();label.Text="文件接收目录\n"+data.ReceiveDirectory;}};card.Controls.Add(browse);AddBlock(card);
        AddClipboardSettings(width);AddPollingSettings(width);
        var info=MakeLabel("WebDAV：加密上传后通知，按设置的间隔检查。\n单文件上限 100 MB · 0.6.3",10);info.AutoSize=false;info.Height=70;info.ForeColor=AppStyle.Muted;AddBlock(info);
    }
    internal static Label MakeLabel(string text,float size,FontStyle style=FontStyle.Regular)=>new(){Text=text,AutoSize=true,Font=new Font("Microsoft YaHei UI",size,style),ForeColor=AppStyle.Ink,BackColor=Color.Transparent};
    internal static Button MakeButton(string text,bool primary)=>new SoftButton{Text=text,Font=new Font("Microsoft YaHei UI",10),BackColor=primary?AppStyle.Green:Color.FromArgb(233,237,239),ForeColor=primary?Color.White:AppStyle.Ink};
    private static bool AutoStart(){using var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");return key?.GetValue("ClipBridge") is string;}
    private void SetAutoStart(bool value){try{using var key=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");if(value)key.SetValue("ClipBridge","\""+Application.ExecutablePath+"\" --startup");else key.DeleteValue("ClipBridge",false);}catch{MessageBox.Show(this,"无法修改自启动设置。");}}
    private static bool ShellEnabled(){using var key=Registry.CurrentUser.OpenSubKey(@"Software\Classes\*\shell\ClipBridge\command");return key?.GetValue("") is string;}
    private void SetShell(bool enabled){try{const string path=@"Software\Classes\*\shell\ClipBridge";if(!enabled){Registry.CurrentUser.DeleteSubKeyTree(path,false);return;}using var key=Registry.CurrentUser.CreateSubKey(path);key.SetValue("","通过 ClipBridge 发送");key.SetValue("Icon",Application.ExecutablePath);key.SetValue("MultiSelectModel","Player");using var command=key.CreateSubKey("command");command.SetValue("","\""+Application.ExecutablePath+"\" --send \"%1\"");}catch{MessageBox.Show(this,"无法修改右键菜单。");}}
}
