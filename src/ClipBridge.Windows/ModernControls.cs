using System.Drawing.Drawing2D;

static class AppStyle
{
    public static readonly Color Background = Color.FromArgb(245,246,248), Ink=Color.FromArgb(28,34,39), Muted=Color.FromArgb(107,116,122), Green=Color.FromArgb(0,126,111);
    public static GraphicsPath Shape(Rectangle r,int radius) { var p=new GraphicsPath(); int d=Math.Min(radius*2,Math.Min(r.Width,r.Height)); if(d<=0)return p; p.AddArc(r.Left,r.Top,d,d,180,90);p.AddArc(r.Right-d,r.Top,d,d,270,90);p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.Left,r.Bottom-d,d,d,90,90);p.CloseFigure();return p; }
    public static void Dialog(Form f,string title,Size size) {f.Text=title;f.ClientSize=size;f.MinimumSize=f.Size;f.Font=new Font("Microsoft YaHei UI",10);f.BackColor=Background;f.StartPosition=FormStartPosition.CenterParent;f.MinimizeBox=false;f.MaximizeBox=false;f.Padding=new Padding(24);}
}
sealed class SoftPanel:Panel
{
    private int Radius=>22;
    public SoftPanel(){DoubleBuffered=true;BackColor=Color.White;ResizeRedraw=true;}
    protected override void OnSizeChanged(EventArgs e){base.OnSizeChanged(e);if(Width>0&&Height>0){using var shape=AppStyle.Shape(ClientRectangle,Radius);var old=Region;Region=new Region(shape);old?.Dispose();}}
}
sealed class SoftButton:Button
{
    public SoftButton(){SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);FlatStyle=FlatStyle.Flat;FlatAppearance.BorderSize=0;Cursor=Cursors.Hand;UseVisualStyleBackColor=false;Padding=new Padding(12,2,12,2);}
    protected override void OnPaintBackground(PaintEventArgs e){e.Graphics.Clear(Parent?.BackColor??AppStyle.Background);}
    protected override void OnPaint(PaintEventArgs e){e.Graphics.Clear(Parent?.BackColor??AppStyle.Background);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;using var path=AppStyle.Shape(new Rectangle(0,0,Width-1,Height-1),12);using var brush=new SolidBrush(Enabled?BackColor:Color.FromArgb(230,233,234));e.Graphics.FillPath(brush,path);TextRenderer.DrawText(e.Graphics,Text,Font,ClientRectangle,Enabled?ForeColor:AppStyle.Muted,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);if(Focused&&ShowFocusCues)ControlPaint.DrawFocusRectangle(e.Graphics,Rectangle.Inflate(ClientRectangle,-5,-5));}
}
sealed class SmoothScrollPanel:Panel
{
    public SmoothScrollPanel(){DoubleBuffered=true;ResizeRedraw=true;}
}

sealed class PillSwitch:CheckBox
{
    public PillSwitch(){SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);AutoSize=false;Size=new Size(54,30);Cursor=Cursors.Hand;AccessibleRole=AccessibleRole.CheckButton;}
    protected override void OnPaint(PaintEventArgs e){e.Graphics.Clear(Parent?.BackColor??AppStyle.Background);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;var track=new Rectangle(1,1,Width-2,Height-2);using var path=AppStyle.Shape(track,Height/2);using var brush=new SolidBrush(!Enabled?Color.FromArgb(220,224,226):Checked?AppStyle.Green:Color.FromArgb(190,198,202));e.Graphics.FillPath(brush,path);int gap=Math.Max(3,Height/8),diameter=Height-gap*2;using var knob=new SolidBrush(Color.White);e.Graphics.FillEllipse(knob,Checked?Width-gap-diameter:gap,gap,diameter,diameter);if(Focused&&ShowFocusCues)ControlPaint.DrawFocusRectangle(e.Graphics,Rectangle.Inflate(ClientRectangle,-2,-2));}
}
