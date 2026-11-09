sealed class FileSendDialog:Form
{
    public int SelectedTarget{get;private set;}
    public FileSendDialog(string[] paths,string[] devices)
    {
        AppStyle.Dialog(this,"发送文件",new Size(550,530));
        var title=BridgeWindow.MakeLabel("发送文件",22,FontStyle.Bold);title.SetBounds(26,24,450,45);Controls.Add(title);
        var files=new SoftPanel{Location=new Point(24,88),Size=new Size(502,125)};
        var detail=BridgeWindow.MakeLabel(string.Join("\n",paths.Take(3).Select(Path.GetFileName))+(paths.Length>3?$"\n另有 {paths.Length-3} 个文件":""),10);detail.AutoSize=false;detail.AutoEllipsis=true;detail.SetBounds(20,15,460,96);files.Controls.Add(detail);Controls.Add(files);
        var heading=BridgeWindow.MakeLabel($"接收设备 · {paths.Length} 个文件",11,FontStyle.Bold);heading.Location=new Point(26,237);Controls.Add(heading);
        var targets=new FlowLayoutPanel{Location=new Point(24,276),Size=new Size(502,155),FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true};
        for(int i=0;i<devices.Length;i++){int index=i;var item=new RadioButton{Text=devices[i],Checked=i==0,AutoEllipsis=true,Size=new Size(470,43),Padding=new Padding(12,0,0,0),BackColor=Color.White};item.CheckedChanged+=(_,_)=>{if(item.Checked)SelectedTarget=index;};targets.Controls.Add(item);}Controls.Add(targets);
        var note=BridgeWindow.MakeLabel("对方同意后开始传输 · 单文件上限 100 MB",9);note.ForeColor=AppStyle.Muted;note.Location=new Point(26,448);Controls.Add(note);
        var send=BridgeWindow.MakeButton("发送请求",true);send.SetBounds(376,479,150,42);send.Click+=(_,_)=>DialogResult=DialogResult.OK;Controls.Add(send);AcceptButton=send;
        var cancel=BridgeWindow.MakeButton("取消",false);cancel.SetBounds(262,479,100,42);cancel.DialogResult=DialogResult.Cancel;Controls.Add(cancel);CancelButton=cancel;
    }
}
