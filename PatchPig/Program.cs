using System;
using LibDescent.Data;
using System.IO;
using System.Collections.Generic;

namespace PatchPig
{
    class PatchPig
    {
        static byte[] Pal6ToPal8(byte[] pal6)
        {
            var pal8 = new byte[pal6.Length];
            for (int i = 0; i < pal6.Length; i++)
                pal8[i] = (byte)(pal6[i] * 65 / 16);
            return pal8;
        }
        
        static Color[] Pal8ToColors(byte[] pal8)
        {
            int n = pal8.Length / 3;
            var colors = new Color[n];
            for (int i = 0; i < n; i++)
                colors[i] = new Color(255, pal8[i * 3], pal8[i * 3 + 1], pal8[i * 3 + 2]);
            return colors;
        }

        static void Run(string[] args)
        {
            var dpal = new byte[256 * 3];
            var colorDict = new Dictionary<Color, byte>();
            Console.WriteLine("Reading Descent 1 palette from palette.256");
            File.OpenRead("palette.256").Read(dpal, 0, dpal.Length);
            var dcolors = Pal8ToColors(Pal6ToPal8(dpal));
            for (int i = 0; i < dcolors.Length; i++)
                if (!colorDict.ContainsKey(dcolors[i]))
                    colorDict[dcolors[i]] = (byte)i;

            Console.WriteLine("Reading Descent 1 pig from descent.pig");
            var pig = new Descent1PIGFile();
            var pigData = File.ReadAllBytes("descent.pig");
            pig.Read(new MemoryStream(pigData));
            var pigImgs = new Dictionary<string, PIGImage>();
            foreach (var img in pig.Bitmaps)
            {
                string name = img.Name;
                if (img.IsAnimated)
                    name += "#" + img.Frame;
                pigImgs.Add(name, img);
            }
            var ms = new MemoryStream(pigData);
            var pr = new BinaryReader(ms);
            var mediaOfs = pr.ReadUInt32();
            pr.BaseStream.Position = mediaOfs;
            var imgCount = pr.ReadUInt32();
            var sndCount = pr.ReadUInt32();
            var imgDataOfs = mediaOfs + 8 + imgCount * 17 + sndCount * 20;

            var fns = new List<Tuple<string, string>>();
             foreach (var arg in args)
            {
                int j = arg.LastIndexOf(':');
                string filename = j >= 2 ? arg.Substring(0, j) : arg;
                string name = j >= 2 ? arg.Substring(j + 1) : Path.GetFileNameWithoutExtension(filename);
                fns.Add(new Tuple<string, string>(filename, name));
            }
            foreach (var x in fns) {
                string fn = x.Item1;
                string name = x.Item2;
                PIGImage pigImg;
                if (!pigImgs.TryGetValue(name, out pigImg))
                {
                	Console.WriteLine("Bitmap " + name + " not found in pig, skipped.");
                	continue;
                }
                
                Console.WriteLine("Reading bmp bitmap from " + fn);
                var r = new BinaryReader(File.OpenRead(fn));
                if (r.ReadUInt16() != 0x4d42)
                    throw new Exception("Invalid bmp");
                r.ReadUInt32(); // len
                r.ReadUInt16(); // res1
                r.ReadUInt16(); // res2
                var dataOfs = r.ReadUInt32(); // ofs bmp
                var hdrPos = r.BaseStream.Position;
                var hdrLen = r.ReadUInt32();
                int width = r.ReadInt32();
                int height = r.ReadInt32();
                int planes = r.ReadInt16();
                int bitsPerPixel = r.ReadInt16();
                if (planes != 1 || bitsPerPixel != 8)
                {
                    Console.WriteLine(name + " unsupported, must be 8-bit bitmap: planes " + planes + " bitsPerPixel " + bitsPerPixel);
                    continue;
                }
                r.BaseStream.Position = hdrPos + hdrLen;
                var pal32 = r.ReadBytes(256 * 4);
                var pal = new byte[256 * 3];
                for (int i = 0; i < 256; i++)
                {
                    pal[i * 3] = pal32[i * 4 + 2];
                    pal[i * 3 + 1] = pal32[i * 4 + 1];
                    pal[i * 3 + 2] = pal32[i * 4];
                }
                r.BaseStream.Position = dataOfs;
                var bmpData = r.ReadBytes((int)(r.BaseStream.Length - dataOfs));
                var used = new int[256];
                foreach (var b in bmpData)
                    used[b]++;
                for (int i = 0; i < pal.Length; i++)
                    pal[i] = (byte)((pal[i] & 0xfc) | (pal[i] >> 6));
                /*
                for (int i = 0; i < pal.Length; i += 3) {
                    byte b = pal[i];
                    pal[i] = pal[i + 2];
                    pal[i + 2] = b;
                }
                */
                var colors = Pal8ToColors(pal);
                var cmap = new byte[256];
                var remap = false;
                var mapfail = false;
                for (int i = 0; i < 256; i++)
                {
                    if (used[i] == 0)
                        continue;
                    if (!colorDict.TryGetValue(colors[i], out byte idx))
                    {
                        Color c = colors[i];
                        Color best;
                        int bestd = 0x7fffffff;
                        idx = 0;
                        for (int j = 0; j < 256; j++) {
                            int dr = dcolors[j].R - c.R;
                            int dg = dcolors[j].G - c.G;
                            int db = dcolors[j].B - c.B;
                            int d = dr * dr + dg * dg + db * db;
                            if (d < bestd) {
                                bestd = d;
                                idx = (byte)j;
                                best = dcolors[j];
                            }
                        }
                        Console.WriteLine(name + " has " + used[i] + " times color " + colors[i] + " but cannot resolve using " + idx + " " + dcolors[idx]);
                        mapfail = true;
                    }
                    else if (i != idx)
                        remap = true;
                    //Console.WriteLine("found color " + i + " " + colors[i] + " -> " + idx);
                    cmap[i] = idx;
                }
                if (mapfail)
                {
                    Console.WriteLine("Colors outside Descent palette found, aborted.");
                    return;
                }
                //Console.WriteLine("Adding bitmap " + name + " (" + width + "x" + height + ") dataofs " + dataOfs + " len " + r.BaseStream.Length + " " + (r.BaseStream.Length - dataOfs) + " " + (width * height));
                byte[] data;
                if (height > 0)
                {
                    data = new byte[width * height];
                    for (int y = 0; y < height; y++)
                        Array.Copy(bmpData, (height - y - 1) * width, data, y * width, width);
                }
                else
                {
                    data = bmpData;
                    height = -height;
                }
                var enc = RLEEncoder.EncodeImage(width, height, data, out bool big);
                var newSize = enc.Length < data.Length ? 4 + enc.Length : data.Length;
                var orgSize = pigImg.GetSize();
                if (newSize > orgSize)
                    Console.WriteLine("Failed to add " + name + " (" + width + "x" + height + ") new size = " + newSize + ", original size = " + orgSize);
                else
                    Console.WriteLine("Adding " + name + " (" + width + "x" + height + ") (" + (remap ? "remapped palette" : "equal palette") + ") (new size = " + newSize + ", original size = " + orgSize + ")");
                ms.Position = imgDataOfs + pigImg.Offset;
                ms.Write(BitConverter.GetBytes((int)enc.Length + 4), 0, 4);
                ms.Write(enc, 0, enc.Length);
            }
            File.WriteAllBytes("new.pig", ms.ToArray());
        }

        static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("Usage: PatchPig bitmap1.bmp bitmap2.bmp:bitmapname ...");
                return;
            }
            try
            {
                Run(args);
            }
            catch (FileNotFoundException ex)
            {
                Console.WriteLine("File not found: " + ex.FileName);
                Console.WriteLine("Aborted.");
            }
        }
    }
}
