using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Xml.Linq;

// The source SVG uses only rounded rectangles. Keep icon generation dependency-free.
internal static class GenerateIcon
{
    static float Number(XElement element,string name)
    { return float.Parse(element.Attribute(name).Value,System.Globalization.CultureInfo.InvariantCulture); }
    static Bitmap Render(XDocument source,int size)
    {
        using(var canvas=new Bitmap(size*4,size*4,PixelFormat.Format32bppArgb))
        using(var g=Graphics.FromImage(canvas))
        {
            g.Clear(Color.Transparent);g.SmoothingMode=SmoothingMode.AntiAlias;g.ScaleTransform(size/8f,size/8f);
            foreach(var rect in source.Root.Elements().Where(e=>e.Name.LocalName=="rect"))
            {
                float x=Number(rect,"x"),y=Number(rect,"y"),w=Number(rect,"width"),h=Number(rect,"height"),d=Number(rect,"rx")*2;
                using(var path=new GraphicsPath())using(var brush=new SolidBrush(ColorTranslator.FromHtml(rect.Attribute("fill").Value)))
                {
                    path.AddArc(x,y,d,d,180,90);path.AddArc(x+w-d,y,d,d,270,90);
                    path.AddArc(x+w-d,y+h-d,d,d,0,90);path.AddArc(x,y+h-d,d,d,90,90);path.CloseFigure();g.FillPath(brush,path);
                }
            }
            var result=new Bitmap(size,size,PixelFormat.Format32bppArgb);
            using(var scaled=Graphics.FromImage(result))
            {
                scaled.CompositingMode=CompositingMode.SourceCopy;scaled.InterpolationMode=InterpolationMode.HighQualityBicubic;
                scaled.PixelOffsetMode=PixelOffsetMode.HighQuality;scaled.DrawImage(canvas,new Rectangle(0,0,size,size),0,0,canvas.Width,canvas.Height,GraphicsUnit.Pixel);
            }
            return result;
        }
    }
    static int Main(string[] args)
    {
        if(args.Length!=2){Console.Error.WriteLine("Usage: GenerateIcon source.svg output-directory");return 1;}
        var source=XDocument.Load(args[0]);Directory.CreateDirectory(args[1]);
        int[] sizes={16,20,24,32,40,48,64,128,256};byte[][] frames=new byte[sizes.Length][];
        for(int i=0;i<sizes.Length;i++)
            using(var bitmap=Render(source,sizes[i]))using(var stream=new MemoryStream())
            {
                bitmap.Save(stream,ImageFormat.Png);frames[i]=stream.ToArray();
                if(sizes[i]==256)File.WriteAllBytes(Path.Combine(args[1],"app.png"),frames[i]);
            }
        using(var writer=new BinaryWriter(File.Create(Path.Combine(args[1],"app.ico"))))
        {
            writer.Write((ushort)0);writer.Write((ushort)1);writer.Write((ushort)sizes.Length);
            int offset=6+16*sizes.Length;
            for(int i=0;i<sizes.Length;i++)
            {
                writer.Write((byte)(sizes[i]==256?0:sizes[i]));writer.Write((byte)(sizes[i]==256?0:sizes[i]));
                writer.Write((byte)0);writer.Write((byte)0);writer.Write((ushort)1);writer.Write((ushort)32);
                writer.Write(frames[i].Length);writer.Write(offset);offset+=frames[i].Length;
            }
            foreach(var frame in frames)writer.Write(frame);
        }
        Console.WriteLine("Generated icon: 16, 20, 24, 32, 40, 48, 64, 128, 256 px");return 0;
    }
}
