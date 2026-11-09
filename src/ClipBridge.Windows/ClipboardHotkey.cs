using System.Runtime.InteropServices;
sealed partial class BridgeWindow
{
    private const int UploadHotkey=0xCB01;
    [DllImport("user32.dll",SetLastError=true)]private static extern bool RegisterHotKey(IntPtr window,int id,uint modifiers,uint key);
    [DllImport("user32.dll")]private static extern bool UnregisterHotKey(IntPtr window,int id);
    private bool SetUploadHotkey(uint modifiers,uint key)
    {
        UnregisterHotKey(Handle,UploadHotkey);
        if(key!=0&&!RegisterHotKey(Handle,UploadHotkey,modifiers|0x4000,key)){
            if(data.UploadKey!=0)RegisterHotKey(Handle,UploadHotkey,data.UploadModifiers|0x4000,data.UploadKey);
            MessageBox.Show(this,"此快捷键已被其他程序占用，请换一个组合。","ClipBridge");return false;
        }
        data.UploadModifiers=modifiers;data.UploadKey=key;Save();return true;
    }
    private static string HotkeyName(uint modifiers,uint key)=>key==0?"未设置":string.Join(" + ",new[]{(modifiers&2)!=0?"Ctrl":null,(modifiers&1)!=0?"Alt":null,(modifiers&4)!=0?"Shift":null,((Keys)key).ToString()}.Where(s=>s is not null));
    private string ImageHint()=>data.AutoSendImages?"复制图片后自动同步到已开启设备。":data.UploadKey==0?"已关闭自动发送；设置下方快捷键后，可手动触发图片同步。":"已关闭自动发送；按 "+HotkeyName(data.UploadModifiers,data.UploadKey)+" 手动同步当前图片。";
    private void UploadClipboard()
    {
        if(paused){Notify("全部同步已暂停，请先恢复。");return;}
        if(!data.Profiles.Any(p=>p.Enabled)){Notify("请先开启至少一套配置。");return;}
        try{
            if(Clipboard.ContainsImage()){
                using var image=Clipboard.GetImage();if(image is null)return;if((long)image.Width*image.Height>20000000){Notify("图片超过 2000 万像素，请作为文件发送。");return;}
                _=SendCopiedImage(new Bitmap(image),true);
            }else if(Clipboard.ContainsText()){
                string text=Clipboard.GetText();data.Clipboard=new(){Text=text,Clock=checked(++data.NextClock),Targets=data.Profiles.Where(p=>p.Enabled).Select(p=>p.Config.Pair).ToList(),Groups=data.Profiles.Where(p=>p.Enabled).Select(p=>p.Group).Distinct().ToList()};Save();Notify("当前文本已加入发送队列。");
            }else Notify("当前剪贴板没有可同步的文本或图片。");
        }catch{Notify("暂时无法读取剪贴板，请稍后重试。");}
    }
    private void AddClipboardSettings(int width)
    {
        var card=new SoftPanel{Width=width,Height=340};var title=MakeLabel("剪贴板发送",13,FontStyle.Bold);title.Location=new Point(24,20);card.Controls.Add(title);
        var label=MakeLabel("复制图片自动发送",10);label.Location=new Point(24,69);card.Controls.Add(label);
        var auto=new PillSwitch{AccessibleName="复制图片自动发送",Checked=data.AutoSendImages,Location=new Point(width-82,66),Anchor=AnchorStyles.Top|AnchorStyles.Right};card.Controls.Add(auto);
        var hint=MakeLabel(ImageHint(),9);hint.AutoSize=false;hint.SetBounds(24,112,width-48,86);hint.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;hint.ForeColor=AppStyle.Muted;card.Controls.Add(hint);
        auto.CheckedChanged+=(_,_)=>{data.AutoSendImages=auto.Checked;Save();hint.Text=ImageHint();};
        var hotkeyLabel=MakeLabel("手动上传当前剪贴板",10);hotkeyLabel.Location=new Point(24,211);card.Controls.Add(hotkeyLabel);
        var input=new TextBox{ReadOnly=true,Text=HotkeyName(data.UploadModifiers,data.UploadKey),AccessibleName="上传当前剪贴板的快捷键",BackColor=Color.White,BorderStyle=BorderStyle.FixedSingle};input.SetBounds(24,247,width-48,36);input.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;
        input.KeyDown+=(_,e)=>{
            e.SuppressKeyPress=true;
            if(e.KeyCode is Keys.Back or Keys.Delete){if(SetUploadHotkey(0,0)){input.Text="未设置";hint.Text=ImageHint();}return;}
            if(e.KeyCode is Keys.ControlKey or Keys.ShiftKey or Keys.Menu)return;
            uint modifiers=(e.Control?2u:0)|(e.Alt?1u:0)|(e.Shift?4u:0);
            if(modifiers==0){input.Text="请同时按 Ctrl、Alt 或 Shift";return;}
            if(SetUploadHotkey(modifiers,(uint)e.KeyCode)){input.Text=HotkeyName(modifiers,(uint)e.KeyCode);hint.Text=ImageHint();}
        };card.Controls.Add(input);
        var help=MakeLabel("点击输入组合键；Backspace / Delete 清除。",9);help.Location=new Point(24,298);help.ForeColor=AppStyle.Muted;card.Controls.Add(help);AddBlock(card);
    }
}
