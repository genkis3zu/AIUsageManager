using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Windows.Forms;

namespace AIUsageManager
{
    internal static class Artwork
    {
        public static Font Font(float size, bool bold)
        { return new Font("Yu Gothic UI", size, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel); }
        public static void Text(Graphics g, string text, float size, bool bold, Color color, RectangleF rect, bool right)
        {
            using (var font = Font(size, bold)) using (var brush = new SolidBrush(color))
            using (var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap, Alignment = right ? StringAlignment.Far : StringAlignment.Near, LineAlignment = StringAlignment.Center })
                g.DrawString(text, font, brush, rect, format);
        }
        public static void Pill(Graphics g, Color color, RectangleF r)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            float d = Math.Min(r.Height, r.Width);
            using (var p = new GraphicsPath()) using (var b = new SolidBrush(color))
            {
                p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right-d,r.Y,d,d,270,90);
                p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.X,r.Bottom-d,d,d,90,90);p.CloseFigure();g.FillPath(b,p);
            }
        }
        public static void Provider(Graphics g, string name, float x, float y, ThemePalette palette)
        {
            var state=g.Save();g.TranslateTransform(x,y);
            using(var bg=new SolidBrush(name=="Claude" ? palette.ClaudeBackground : palette.IconBackground))g.FillEllipse(bg,0,0,28,28);
            if(name=="Claude")
            {
                using(var pen=new Pen(palette.ClaudeInk,1.7f))
                    for(int i=0;i<12;i++){double a=i*Math.PI/6;g.DrawLine(pen,14+(float)Math.Cos(a)*4,14+(float)Math.Sin(a)*4,14+(float)Math.Cos(a)*10,14+(float)Math.Sin(a)*10);}
            }
            else if(name=="Codex")
            {
                using(var pen=new Pen(palette.Ink,1.6f)){g.DrawLines(pen,new[]{new PointF(10,8),new PointF(5,14),new PointF(10,20)});g.DrawLines(pen,new[]{new PointF(18,8),new PointF(23,14),new PointF(18,20)});g.DrawLine(pen,16,7,12,21);}
            }
            else
            {
                using(var pen=new Pen(palette.Ink,1.8f)){g.DrawArc(pen,5,5,18,18,35,290);g.DrawLine(pen,8,21,23,6);g.DrawLine(pen,13,17,23,17);}
            }
            g.Restore(state);
        }
        public static Icon TrayIcon()
        {
            using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("AIUsageManager.App.ico"))
            using(var icon=new Icon(stream,SystemInformation.SmallIconSize))return (Icon)icon.Clone();
        }
    }
}
