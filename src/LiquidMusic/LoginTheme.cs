using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace LiquidMusic;

// Colours of the sign-in window. They follow the widget design chosen in
// Settings -> Appearance ("aurora" / "abyss"), exactly like the main window.
internal sealed class LoginTheme
{
    internal readonly Color Base,Blue,Indigo,Violet,OnAccent,Teal=Color.FromArgb(94,240,212);
    internal readonly bool Grain;
    internal readonly (double X,double Y,Color Color,double Radius)[] Blobs;
    private LoginTheme(Color baseColor,Color blue,Color indigo,Color violet,(double,double,Color,double)[] blobs,Color? onAccent=null,bool grain=false)
    {Base=baseColor;Blue=blue;Indigo=indigo;Violet=violet;Blobs=blobs;OnAccent=onAccent??Color.White;Grain=grain;}
    private static readonly LoginTheme Aurora=new(Color.FromArgb(44,14,40),Color.FromArgb(0xff,0x9c,0xcb),Color.FromArgb(0xd6,0x5f,0xb0),Color.FromArgb(0xa8,0x5b,0xd6),
    [
        (.18,.12,Color.FromArgb(255,140,200),.34),(.85,.30,Color.FromArgb(170,100,220),.30),
        (.60,.95,Color.FromArgb(255,122,184),.22),(.10,.75,Color.FromArgb(255,190,220),.16)
    ]);
    private static readonly LoginTheme Abyss=new(Color.FromArgb(6,14,28),Color.FromArgb(0x6f,0xd3,0xff),Color.FromArgb(0x2f,0x86,0xe0),Color.FromArgb(0x4a,0x62,0xd8),
    [
        (.18,.12,Color.FromArgb(80,190,255),.34),(.85,.30,Color.FromArgb(85,110,230),.30),
        (.60,.95,Color.FromArgb(70,170,255),.22),(.10,.75,Color.FromArgb(140,215,255),.16)
    ]);
    private static readonly LoginTheme Neon=new(Color.FromArgb(10,10,12),Color.FromArgb(0xa9,0xa6,0xff),Color.FromArgb(0x5b,0x52,0xff),Color.FromArgb(0x7b,0x6c,0xff),
    [
        (1.0,.0,Color.FromArgb(150,140,255),.60),(.0,1.0,Color.FromArgb(150,140,255),.55),
        (.88,.12,Color.FromArgb(220,216,255),.22),(.12,.88,Color.FromArgb(210,206,255),.20)
    ]);
    private static readonly LoginTheme Mono=new(Color.FromArgb(10,10,10),Color.FromArgb(244,244,244),Color.FromArgb(196,196,196),Color.FromArgb(142,142,142),
    [
        (1.10,.10,Color.FromArgb(236,236,236),.60),(1.08,.42,Color.FromArgb(255,255,255),.62),
        (1.10,.68,Color.FromArgb(180,180,180),.45),(1.08,.92,Color.FromArgb(120,120,120),.40)
    ],Color.FromArgb(10,10,10),true);
    internal static LoginTheme For(string design)=>design switch{"abyss"=>Abyss,"neon"=>Neon,"grain"=>Mono,_=>Aurora};
}

internal static class LoginPaint
{
    internal static Color Alpha(int alpha,Color color)=>Color.FromArgb(Math.Clamp(alpha,0,255),color);
    internal static Color White(int alpha)=>Color.FromArgb(Math.Clamp(alpha,0,255),255,255,255);
    internal static void Quality(Graphics g){g.SmoothingMode=SmoothingMode.AntiAlias;g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;}
    // Same recipe as the main window: dark base, drifting colour "drops", top light.
    internal static Bitmap Backdrop(LoginTheme theme,Size size)
    {
        var bitmap=new Bitmap(Math.Max(1,size.Width),Math.Max(1,size.Height),PixelFormat.Format32bppPArgb);
        using var g=Graphics.FromImage(bitmap);Quality(g);
        var area=new Rectangle(Point.Empty,bitmap.Size);
        using(var floor=new LinearGradientBrush(area,theme.Base,Color.FromArgb(theme.Base.R*2/3,theme.Base.G*2/3,theme.Base.B*2/3),115f))g.FillRectangle(floor,area);
        var scale=Math.Max(area.Width,area.Height);
        foreach(var (x,y,color,strength) in theme.Blobs)
        {
            var radius=(float)(scale*.52);
            var cx=(float)(x*area.Width);var cy=(float)(y*area.Height);
            using var path=new GraphicsPath();path.AddEllipse(cx-radius,cy-radius,radius*2,radius*2);
            using var brush=new PathGradientBrush(path){CenterColor=Alpha((int)(strength*255*1.35),color),SurroundColors=[Alpha(0,color)]};
            g.FillPath(brush,path);
        }
        if(theme.Grain)AddGrain(bitmap);
        using(var light=new LinearGradientBrush(new Rectangle(0,0,area.Width,Math.Max(2,area.Height*2/5)),White(22),White(0),90f))g.FillRectangle(light,0,0,area.Width,Math.Max(2,area.Height*2/5));
        Edge(g,Rectangle.Inflate(area,-1,-1),22,150);
        return bitmap;
    }
    // Film grain of the "Зерно" design: light and dark specks over the whole window.
    private static void AddGrain(Bitmap bitmap)
    {
        var rect=new Rectangle(0,0,bitmap.Width,bitmap.Height);
        var data=bitmap.LockBits(rect,ImageLockMode.ReadWrite,PixelFormat.Format32bppPArgb);
        try
        {
            var count=rect.Width*rect.Height;var pixels=new int[count];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0,pixels,0,count);
            var random=new Random(7);
            for(var i=0;i<count;i++)
            {
                var noise=random.Next(256);var alpha=noise>>4>8?12:9;   // premultiplied: add or remove a little light
                var delta=(noise-128)*alpha/255;
                int Channel(int value)=>Math.Clamp(value+delta,0,255);
                var p=pixels[i];
                pixels[i]=(int)(0xFF000000u|(uint)(Channel((p>>16)&255)<<16)|(uint)(Channel((p>>8)&255)<<8)|(uint)Channel(p&255));
            }
            System.Runtime.InteropServices.Marshal.Copy(pixels,0,data.Scan0,count);
        }
        finally{bitmap.UnlockBits(data);}
    }
    // Liquid-glass rim: bright top-left, dim middle, bright bottom-right (the app's --edge).
    internal static void Edge(Graphics g,Rectangle rect,int radius,int strength=170)
    {
        if(rect.Width<4||rect.Height<4)return;
        using var path=DesktopCapture.Rounded(new Rectangle(rect.X,rect.Y,rect.Width-1,rect.Height-1),radius);
        using var edge=new LinearGradientBrush(rect,White(strength),White(strength*2/3),LinearGradientMode.ForwardDiagonal)
        {InterpolationColors=new ColorBlend{Colors=[White(strength),White(strength/5),White(strength/12),White(strength*2/3)],Positions=[0f,.34f,.62f,1f]}};
        using var pen=new Pen(edge,1f);g.DrawPath(pen,path);
    }
    internal static void Glass(Graphics g,Rectangle rect,int radius,bool strong=false)
    {
        if(rect.Width<4||rect.Height<4)return;
        using(var path=DesktopCapture.Rounded(new Rectangle(rect.X,rect.Y,rect.Width-1,rect.Height-1),radius))
        using(var fill=new LinearGradientBrush(rect,White(strong?44:30),White(strong?12:8),LinearGradientMode.ForwardDiagonal))g.FillPath(fill,path);
        Edge(g,rect,radius);
        using var shine=new Pen(White(70),1f);
        g.DrawLine(shine,rect.Left+radius,rect.Top+1,rect.Right-radius,rect.Top+1);
    }
}

internal sealed class LoginHeader:Control
{
    private static readonly Font TitleFont=new("Segoe UI Semibold",14.5f,GraphicsUnit.Point);
    private static readonly Font SubFont=new("Segoe UI",9f,GraphicsUnit.Point);
    private static readonly Font ButtonFont=new("Segoe UI Semibold",10f,GraphicsUnit.Point);
    private static readonly Font NoteFont=new("Segoe UI Symbol",17f,GraphicsUnit.Point);
    private LoginTheme theme=LoginTheme.For("aurora");
    private string host="";
    private Rectangle button;
    private bool hover,pressed;
    private readonly string title;
    internal event Action? Done;
    internal event Action? DragRequested;
    internal LoginHeader(string service)
    {
        title="Вход · "+service;
        SetStyle(ControlStyles.SupportsTransparentBackColor|ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
        BackColor=Color.Transparent;
    }
    [System.ComponentModel.Browsable(false),System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal LoginTheme Theme{set{theme=value;Invalidate();}}
    [System.ComponentModel.Browsable(false),System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal string Host{set{if(host!=value){host=value;Invalidate();}}}
    protected override void OnPaint(PaintEventArgs e)
    {
        var g=e.Graphics;LoginPaint.Quality(g);
        var pill=new Rectangle(14,12,Width-28,Height-20);
        LoginPaint.Glass(g,pill,26,true);
        var compact=Width<640;
        var buttonWidth=compact?44:178;
        button=new Rectangle(pill.Right-16-buttonWidth,pill.Top+(pill.Height-38)/2,buttonWidth,38);
        // brand orb with the note
        var orb=new Rectangle(pill.Left+16,pill.Top+(pill.Height-42)/2,42,42);
        using(var fill=new LinearGradientBrush(orb,theme.Blue,theme.Violet,50f))g.FillEllipse(fill,orb);
        using(var shine=new LinearGradientBrush(new Rectangle(orb.X+5,orb.Y+2,orb.Width-10,orb.Height/2),LoginPaint.White(120),LoginPaint.White(0),90f))g.FillEllipse(shine,orb.X+5,orb.Y+2,orb.Width-10,orb.Height/2);
        using(var note=new StringFormat{Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center}){using var onAccent=new SolidBrush(theme.OnAccent);g.DrawString("♪",NoteFont,onAccent,orb,note);}
        // title and subtitle
        var chipWidth=Width>=900?250:0;
        var textLeft=orb.Right+14;
        var textRight=button.Left-(chipWidth>0?chipWidth+24:12);
        if(textRight-textLeft>60)
        {
            using var trim=new StringFormat{Trimming=StringTrimming.EllipsisCharacter,FormatFlags=StringFormatFlags.NoWrap};
            using var bright=new SolidBrush(Color.FromArgb(250,255,255,255));using var dim=new SolidBrush(LoginPaint.White(165));
            g.DrawString(title,TitleFont,bright,new RectangleF(textLeft,pill.Top+15,textRight-textLeft,26),trim);
            g.DrawString("Безопасный вход внутри Liquid Music",SubFont,dim,new RectangleF(textLeft,pill.Top+41,textRight-textLeft,20),trim);
        }
        // secure-connection chip
        if(chipWidth>0)
        {
            var chip=new Rectangle(button.Left-12-chipWidth,pill.Top+(pill.Height-30)/2,chipWidth,30);
            using(var path=DesktopCapture.Rounded(chip,15))using(var fill=new SolidBrush(LoginPaint.White(18)))g.FillPath(fill,path);
            LoginPaint.Edge(g,chip,15,90);
            using(var dot=new SolidBrush(theme.Teal))g.FillEllipse(dot,chip.Left+12,chip.Top+11,8,8);
            using var trim=new StringFormat{Trimming=StringTrimming.EllipsisCharacter,FormatFlags=StringFormatFlags.NoWrap,LineAlignment=StringAlignment.Center};
            using var dim=new SolidBrush(LoginPaint.White(190));
            g.DrawString(host.Length>0?host:"защищённое подключение",SubFont,dim,new RectangleF(chip.Left+28,chip.Top,chip.Width-36,chip.Height),trim);
        }
        // "back to widget" button, same gradient as the app's save button
        using(var path=DesktopCapture.Rounded(button,19))
        {
            using(var glow=new SolidBrush(LoginPaint.Alpha(hover?70:42,theme.Indigo)))g.FillEllipse(glow,button.Left+8,button.Bottom-10,button.Width-16,12);
            using var fill=new LinearGradientBrush(button,theme.Blue,theme.Violet,LinearGradientMode.Horizontal)
            {InterpolationColors=new ColorBlend{Colors=[theme.Blue,theme.Indigo,theme.Violet],Positions=[0f,.6f,1f]}};
            g.FillPath(fill,path);
            if(hover||pressed)using(var over=new SolidBrush(LoginPaint.White(pressed?10:34)))g.FillPath(over,path);
            using var rim=new Pen(LoginPaint.White(110),1f);g.DrawLine(rim,button.Left+18,button.Top+1,button.Right-18,button.Top+1);
        }
        using(var center=new StringFormat{Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center})
        {using var onAccent=new SolidBrush(theme.OnAccent);g.DrawString(compact?"×":"Вернуться в виджет",compact?TitleFont:ButtonFont,onAccent,button,center);}
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        var now=button.Contains(e.Location);
        if(now!=hover){hover=now;Cursor=now?Cursors.Hand:Cursors.Default;Invalidate();}
        base.OnMouseMove(e);
    }
    protected override void OnMouseLeave(EventArgs e){hover=pressed=false;Cursor=Cursors.Default;Invalidate();base.OnMouseLeave(e);}
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if(e.Button==MouseButtons.Left)
        {
            if(button.Contains(e.Location)){pressed=true;Invalidate();}
            else DragRequested?.Invoke();
        }
        base.OnMouseDown(e);
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        var click=pressed&&button.Contains(e.Location);
        pressed=false;Invalidate();
        if(click)Done?.Invoke();
        base.OnMouseUp(e);
    }
}

internal sealed class LoginFooter:Control
{
    private static readonly Font TextFont=new("Segoe UI",9.5f,GraphicsUnit.Point);
    private LoginTheme theme=LoginTheme.For("aurora");
    internal event Action? DragRequested;
    internal LoginFooter()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor|ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
        BackColor=Color.Transparent;
    }
    [System.ComponentModel.Browsable(false),System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal LoginTheme Theme{set{theme=value;Invalidate();}}
    protected override void OnPaint(PaintEventArgs e)
    {
        var g=e.Graphics;LoginPaint.Quality(g);
        using var trim=new StringFormat{Trimming=StringTrimming.EllipsisCharacter,FormatFlags=StringFormatFlags.NoWrap,LineAlignment=StringAlignment.Center};
        using var dim=new SolidBrush(LoginPaint.White(150));
        g.DrawString("Войдите в аккаунт — после входа останется только компактный виджет",TextFont,dim,new RectangleF(30,0,Math.Max(10,Width-190),Height-4),trim);
        var tag=new Rectangle(Width-150,(Height-4-8)/2,8,8);
        using(var dot=new LinearGradientBrush(tag,theme.Blue,theme.Violet,45f))g.FillEllipse(dot,tag);
        using var right=new StringFormat{Alignment=StringAlignment.Far,LineAlignment=StringAlignment.Center};
        g.DrawString("Liquid Music",TextFont,dim,new RectangleF(Width-136,0,106,Height-4),right);
    }
    protected override void OnMouseDown(MouseEventArgs e){if(e.Button==MouseButtons.Left)DragRequested?.Invoke();base.OnMouseDown(e);}
}

// Transparent holder: draws the glass rim around the rounded page.
internal sealed class LoginViewFrame:Panel
{
    internal const int Radius=16;
    internal LoginViewFrame()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor|ControlStyles.ResizeRedraw|ControlStyles.OptimizedDoubleBuffer,true);
        BackColor=Color.Transparent;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if(Controls.Count==0||Padding==Padding.Empty)return;
        LoginPaint.Quality(e.Graphics);
        var view=Controls[0].Bounds;
        LoginPaint.Edge(e.Graphics,new Rectangle(view.Left-1,view.Top-1,view.Width+2,view.Height+2),Radius+1,150);
    }
}
