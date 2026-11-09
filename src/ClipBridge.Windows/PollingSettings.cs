sealed partial class BridgeWindow
{
    private void AddPollingSettings(int width)
    {
        var card=new SoftPanel{Width=width,Height=220};var title=MakeLabel("检查间隔",13,FontStyle.Bold);title.Location=new Point(24,18);card.Controls.Add(title);
        void Interval(string text,int y,int current,Action<int> save){var label=MakeLabel(text,10);label.Location=new Point(24,y+3);card.Controls.Add(label);var value=new NumericUpDown{Minimum=1,Maximum=300,Value=Math.Clamp(current,1,300),AccessibleName=text,ThousandsSeparator=false};value.SetBounds(width-145,y,115,36);value.Anchor=AnchorStyles.Top|AnchorStyles.Right;value.ValueChanged+=(_,_)=>save((int)value.Value);card.Controls.Add(value);}
        Interval("文本轮询间隔（秒）",67,data.TextPollSeconds,value=>{data.TextPollSeconds=value;Save();foreach(var route in running.Values){route.Stop.Cancel();route.Client.Dispose();}running.Clear();if(started)Reconcile();});
        Interval("图片 / 文件检查间隔（秒）",119,data.AttachmentPollSeconds,value=>{data.AttachmentPollSeconds=value;Save();});
        var help=MakeLabel("范围 1–300 秒。文本轮询用于 WebDAV；专用中转文本即时接收。\n间隔越短请求越多，可按服务商限制调整。",9);help.AutoSize=false;help.SetBounds(24,169,width-48,46);help.ForeColor=AppStyle.Muted;card.Controls.Add(help);void Fit(){help.Width=card.Width-48;help.Height=TextRenderer.MeasureText(help.Text,help.Font,new Size(Math.Max(1,help.Width),int.MaxValue),TextFormatFlags.WordBreak).Height+10;card.Height=help.Bottom+22;}card.SizeChanged+=(_,_)=>Fit();Fit();AddBlock(card);
    }
}
