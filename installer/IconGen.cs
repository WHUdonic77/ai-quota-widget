using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace AIQuota.Build
{
    public static class IconGen
    {
        public static int Main(string[] args)
        {
            string outPath = args.Length > 0 ? args[0] : "app.ico";
            using (var master = RenderMaster(1024))
            {
                int[] sizes = { 16, 32, 48, 256 };
                var payloads = new List<byte[]>();
                foreach (int sz in sizes)
                {
                    using (var bmp = Resize(master, sz, sz))
                    {
                        if (sz == 256)
                        {
                            using (var ms = new MemoryStream())
                            {
                                bmp.Save(ms, ImageFormat.Png);
                                payloads.Add(ms.ToArray());
                            }
                        }
                        else
                        {
                            payloads.Add(EncodeDib32(bmp));
                        }
                    }
                }
                WriteIco(outPath, sizes, payloads);
            }
            return 0;
        }

        static Bitmap RenderMaster(int S)
        {
            var bmp = new Bitmap(S, S, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.Clear(Color.Transparent);

                // 1. Rounded squircle background
                float pad = S * 0.045f;
                var bgRect = new RectangleF(pad, pad, S - pad * 2, S - pad * 2);
                float rad = S * 0.22f;
                using (var path = RoundRect(bgRect, rad))
                {
                    using (var bgBrush = new LinearGradientBrush(bgRect,
                        Color.FromArgb(255, 0x1A, 0x1C, 0x22),
                        Color.FromArgb(255, 0x09, 0x0A, 0x0D), 90f))
                    {
                        g.FillPath(bgBrush, path);
                    }
                    using (var borderPen = new Pen(Color.FromArgb(220, 0x2E, 0x32, 0x3B), S * 0.016f))
                    {
                        g.DrawPath(borderPen, path);
                    }
                }

                // 2. Quota ring track + glowing neon green arc
                float ringR = S * 0.305f;
                float ringTh = S * 0.088f;
                var arcRect = new RectangleF(S / 2f - ringR, S / 2f - ringR, ringR * 2, ringR * 2);

                using (var trackPen = new Pen(Color.FromArgb(255, 0x25, 0x28, 0x30), ringTh))
                {
                    g.DrawEllipse(trackPen, arcRect);
                }

                using (var glowPen = new Pen(Color.FromArgb(52, 0x2E, 0xE5, 0x9D), ringTh * 1.55f))
                {
                    glowPen.StartCap = LineCap.Round;
                    glowPen.EndCap = LineCap.Round;
                    g.DrawArc(glowPen, arcRect, -90f, 265f);
                }

                using (var arcBrush = new LinearGradientBrush(arcRect,
                    Color.FromArgb(255, 0x5E, 0xFF, 0xBD),
                    Color.FromArgb(255, 0x16, 0xC7, 0x84), 65f))
                using (var arcPen = new Pen(arcBrush, ringTh))
                {
                    arcPen.StartCap = LineCap.Round;
                    arcPen.EndCap = LineCap.Round;
                    g.DrawArc(arcPen, arcRect, -90f, 265f);
                }

                // Amber accent dot in the remaining segment
                double dotAngle = (-90.0 + 312.5) * Math.PI / 180.0;
                float dx = S / 2f + (float)(ringR * Math.Cos(dotAngle));
                float dy = S / 2f + (float)(ringR * Math.Sin(dotAngle));
                float dr = ringTh * 0.48f;
                using (var amberGlow = new SolidBrush(Color.FromArgb(65, 0xFF, 0xB5, 0x47)))
                    g.FillEllipse(amberGlow, dx - dr * 1.5f, dy - dr * 1.5f, dr * 3f, dr * 3f);
                using (var amberBrush = new SolidBrush(Color.FromArgb(255, 0xFF, 0xB5, 0x47)))
                    g.FillEllipse(amberBrush, dx - dr, dy - dr, dr * 2f, dr * 2f);

                // 3. Center Antigravity wave curve (scaled from 24x24 viewBox)
                Func<float, float, PointF> pt = (x, y) =>
                {
                    float box = S * 0.31f;
                    float ox = (S - box) / 2f;
                    float oy = (S - box) / 2f + S * 0.005f;
                    return new PointF(ox + (x / 24f) * box, oy + (y / 24f) * box);
                };
                PointF[] bez = new PointF[]
                {
                    pt(2.2f, 21f),
                    pt(5.2f, 19.6f), pt(7.2f, 13f), pt(9f, 7.6f),
                    pt(10f, 4.6f), pt(10.9f, 3f), pt(12f, 3f),
                    pt(13.1f, 3f), pt(14f, 4.6f), pt(15f, 7.6f),
                    pt(16.8f, 13f), pt(18.8f, 19.6f), pt(21.8f, 21f)
                };
                using (var wavePen = new Pen(Color.White, S * 0.056f))
                {
                    wavePen.StartCap = LineCap.Round;
                    wavePen.EndCap = LineCap.Round;
                    wavePen.LineJoin = LineJoin.Round;
                    g.DrawBeziers(wavePen, bez);
                }
            }
            return bmp;
        }

        static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            float d = radius * 2f;
            var gp = new GraphicsPath();
            gp.AddArc(r.X, r.Y, d, d, 180, 90);
            gp.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            gp.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            gp.CloseFigure();
            return gp;
        }

        static Bitmap Resize(Bitmap src, int w, int h)
        {
            var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                using (var ia = new ImageAttributes())
                {
                    ia.SetWrapMode(WrapMode.TileFlipXY);
                    g.DrawImage(src, new Rectangle(0, 0, w, h), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
                }
            }
            return dst;
        }

        static byte[] EncodeDib32(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            int xorStride = w * 4;
            int andStride = ((w + 31) / 32) * 4;
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                // BITMAPINFOHEADER (40 bytes)
                bw.Write(40);
                bw.Write(w);
                bw.Write(h * 2);
                bw.Write((ushort)1);
                bw.Write((ushort)32);
                bw.Write(0);
                bw.Write(xorStride * h + andStride * h);
                bw.Write(0);
                bw.Write(0);
                bw.Write(0);
                bw.Write(0);

                var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    byte[] row = new byte[xorStride];
                    // Bottom-up XOR mask
                    for (int y = h - 1; y >= 0; y--)
                    {
                        Marshal.Copy(IntPtr.Add(bd.Scan0, y * bd.Stride), row, 0, xorStride);
                        bw.Write(row);
                    }
                    // Bottom-up 1bpp AND mask
                    byte[] andRow = new byte[andStride];
                    for (int y = h - 1; y >= 0; y--)
                    {
                        Array.Clear(andRow, 0, andStride);
                        Marshal.Copy(IntPtr.Add(bd.Scan0, y * bd.Stride), row, 0, xorStride);
                        for (int x = 0; x < w; x++)
                        {
                            byte alpha = row[x * 4 + 3];
                            if (alpha == 0)
                                andRow[x >> 3] |= (byte)(0x80 >> (x & 7));
                        }
                        bw.Write(andRow);
                    }
                }
                finally
                {
                    bmp.UnlockBits(bd);
                }
                return ms.ToArray();
            }
        }

        static void WriteIco(string path, int[] sizes, List<byte[]> payloads)
        {
            using (var fs = File.Create(path))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write((ushort)0);
                bw.Write((ushort)1);
                bw.Write((ushort)sizes.Length);

                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    int sz = sizes[i];
                    bw.Write((byte)(sz >= 256 ? 0 : sz));
                    bw.Write((byte)(sz >= 256 ? 0 : sz));
                    bw.Write((byte)0);
                    bw.Write((byte)0);
                    bw.Write((ushort)1);
                    bw.Write((ushort)32);
                    bw.Write(payloads[i].Length);
                    bw.Write(offset);
                    offset += payloads[i].Length;
                }
                for (int i = 0; i < payloads.Count; i++)
                    bw.Write(payloads[i]);
            }
        }
    }
}
