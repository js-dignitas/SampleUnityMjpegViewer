using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
namespace Jpeg
{

    #region Extensions

    public static class Extensions
    {
        public static void Swap<T>(this IList<T> arr, int i1, int i2)
        {
            Debug.Assert(i1 > 0 && i1 < arr.Count);
            Debug.Assert(i2 > 0 && i2 < arr.Count);

            T tempT = arr[i1];
            arr[i1] = arr[i2];
            arr[i2] = tempT;
        }

        public static Int16 ReadInt16BE(this BinaryReader reader)
        {
            byte[] temp = reader.ReadBytes(2);
            return (short)(
                temp[0] << 8 |
                temp[1]
            );
        }
    }

    #endregion

    #region DCT

    static class DCT
    {
        const int Side = 8;
        const int SideSquared = Side * Side;

        private static readonly double[] Dct = GenerateDct();
        private static readonly double[] DctT = Transpose(GenerateDct());

        private static double[] GenerateDct()
        {
            const int Size2 = Side * Side;

            double[] result = new double[Size2];
            for (int y = 0, o = 0; y < Side; y++)
                for (int x = 0; x < Side; x++)
                    result[o++] =
                        Math.Sqrt(y == 0 ? .125 : .250) *
                        Math.Cos(((2 * x + 1) * y * Math.PI) * .0625);

            return result;
        }

        private static double[] Transpose(double[] m)
        {
            Debug.Assert(m != null && m.Length == SideSquared);

            for (int y = 0; y < Side; y++)
                for (int x = y + 1; x < Side; x++)
                    m.Swap(y * Side + x, x * Side + y);

            return m;
        }

        private static void MatrixMultiply(double[] m1, double[] m2, double[] result)
        {
            //Debug.Assert(m1 != null && m1.Length == SideSquared);
            //Debug.Assert(m2 != null && m1.Length == SideSquared);
            for (int y = 0; y < Side; y++)
                for (int x = 0; x < Side; x++)
                {
                    double sum = 0;
                    for (int k = 0; k < Side; k++)
                        sum += m1[y * Side + k] * m2[k * Side + x];
                    result[y * Side + x] = sum;
                }
        }

        public static void ToDouble(int[] m, double[] dest)
        {
            for (int i = 0; i < m.Length; i++)
                dest[i] = (double)m[i];
        }

        public static void ToInt(double[] m, int[] dest)
        {
            for (int i = 0; i < m.Length; i++)
                dest[i] = (int)Math.Round(m[i]);
        }

        static double[] doidctDoubleMat = null;
        static double[] doidctTempMat = null;
        static double[] doidctTempMat2 = null;
        public static void DoIdct(int[] m, int[] dest)
        {
            if (doidctDoubleMat == null || doidctDoubleMat.Length != m.Length)
            {
                doidctDoubleMat = new double[m.Length];
                doidctTempMat = new double[m.Length];
                doidctTempMat2 = new double[m.Length];
            }
            ToDouble(m, doidctDoubleMat);
            MatrixMultiply(DctT, doidctDoubleMat, doidctTempMat);
            MatrixMultiply(doidctTempMat, Dct, doidctTempMat2);
            ToInt(doidctTempMat2, dest);
        }

#if false
		public static void Transpose (double[] m)
		{
			Debug.Assert (m != null);
			
			int d = (int)Math.Sqrt (m.Length);
			Debug.Assert (d * d == m.Length);
			
			int o1 = 0, ot = -1;
			for (int oy = 0; oy < d; oy++) {
				o1 += 1 + oy;
				ot += 1 + d;
				for (int o2 = ot; o2 < d*d; o2 += d)
					m.Swap(d1, d2);
			}
		}
#endif

    }

    #endregion

    #region BitReader

    public class BitReader
    : IDisposable
    {
        private int _cnt = 0;
        private uint _buf = 0;
        private Stream _stream;

        public BitReader(byte[] data) { _stream = new MemoryStream(data); }
        public BitReader(Stream stream) { _stream = stream; }

        private void EnsureData(int bitCount)
        {
            int todo = ((bitCount - _cnt) + 7) / 8;
            for (int i = 0; i < todo; i++)
                _buf = (_buf << 8) | (uint)_stream.ReadByte();
            _cnt += (todo > 0) ? todo * 8 : 0;
        }

        public int Peek(int bitCount)
        {
            EnsureData(bitCount);
            int mask = ((1 << _cnt) - 1) ^ ((1 << (_cnt - bitCount)) - 1);
            return (int)(_buf & mask) >> (_cnt - bitCount);
        }

        public int Read(int bitCount)
        {
            EnsureData(bitCount);
            int mask = ((1 << _cnt) - 1) ^ ((1 << (_cnt - bitCount)) - 1);
            int val = (int)(_buf & mask) >> (_cnt - bitCount);
            _cnt -= bitCount;
            return val;
        }

        public void Skip(int bitCount)
        {
            EnsureData(bitCount);
            _cnt -= bitCount;
        }

        #region IDisposable Members

        public void Dispose()
        {
            if (_stream == null)
                return;

            _stream.Dispose();
        }

        #endregion
    }

    #endregion

    #region Jpeg

    public class Jpeg
    {

        #region Variables

        private int _xres, _yres;
        private byte[][] _dqt = new byte[4][];
        private Huff[][] _dht = new Huff[4][];
        private byte[] _scandata;
        int[][] block = new int[6][];

        #endregion

        #region Properties

        public int Width { get { return _xres; } }
        public int Height { get { return _yres; } }

        #endregion

        #region File

        public Jpeg(string filename)
        {
            if (String.IsNullOrEmpty(filename))
                throw new ArgumentNullException("filename");

            using (var stream = File.OpenRead(filename))
            using (var reader = new BinaryReader(stream))
                ParseFile(reader);
        }

        public Jpeg()
        {
            Init();
        }
        protected void Init()
        {
            for(int i = 0; i < block.Length; i++)
            {
                block[i] = new int[64];
            }
        }
        public void ParseData(byte[] data)
        {
            Init();
            using (var mem = new MemoryStream(data))
            using (var reader = new BinaryReader(mem))
                ParseFile(reader);
        }
        public void ParseFile(BinaryReader reader)
        {
            while (reader.PeekChar() >= 0)
            {
                if (reader.ReadByte() != 0xff)
                    throw new Exception("Cannot find next marker");

                byte b = reader.ReadByte();
                switch (b)
                {
                    // SOF0
                    case 0xc0: ParseSof0(reader); break;
                    // DHT
                    case 0xc4: ParseDht(reader); break;
                    // SOI
                    case 0xd8: break;
                    // EOI
                    case 0xd9: return;
                    // SOS
                    case 0xda: ParseSos(reader); break;
                    // DQT
                    case 0xdb: ParseDqt(reader); break;
                    // APP0-APP15
                    case 0xe0: ParseApp(reader, 0); break;
                    case 0xe1: ParseApp(reader, 1); break;
                    case 0xe2: ParseApp(reader, 2); break;
                    case 0xe3: ParseApp(reader, 3); break;
                    case 0xe4: ParseApp(reader, 4); break;
                    case 0xe5: ParseApp(reader, 5); break;
                    case 0xe6: ParseApp(reader, 6); break;
                    case 0xe7: ParseApp(reader, 7); break;
                    case 0xe8: ParseApp(reader, 8); break;
                    case 0xe9: ParseApp(reader, 9); break;
                    case 0xea: ParseApp(reader, 10); break;
                    case 0xeb: ParseApp(reader, 11); break;
                    case 0xec: ParseApp(reader, 12); break;
                    case 0xed: ParseApp(reader, 13); break;
                    case 0xee: ParseApp(reader, 14); break;
                    case 0xef: ParseApp(reader, 15); break;
                    case 0xfe: ParseComment(reader); break;

                    default:
                        throw new Exception("Unknown marker " + b);
                }
            }
        }

        #region AppN

        private void ParseComment(BinaryReader reader)
        {
            int length = reader.ReadInt16BE();
            reader.BaseStream.Seek(length - 2, SeekOrigin.Current);
        }
        private void ParseApp(BinaryReader reader, int app)
        {
            int length = reader.ReadInt16BE();
            reader.BaseStream.Seek(length - 2, SeekOrigin.Current);
        }

        #endregion

        #region SofN

        private void ParseSof0(BinaryReader reader)
        {
            int length = reader.ReadInt16BE();
            if (reader.ReadByte() != 8) throw new NotImplementedException();

            _yres = reader.ReadInt16BE();
            _xres = reader.ReadInt16BE();
            if (reader.ReadByte() != 3) throw new NotImplementedException();

            for (int i = 0; i < 3; i++)
            {
                reader.ReadByte();
                int samp = reader.ReadByte();
                if (i == 0 && samp != 0x22) throw new NotImplementedException();
                if (i != 0 && samp != 0x11) throw new NotImplementedException();
                reader.ReadByte();
            }
        }

        #endregion

        #region Dqt

        private readonly byte[] NaturalOrder = new byte[] {
             0,  1,  8, 16,  9,  2,  3, 10,
            17, 24, 32, 25, 18, 11,  4,  5,
            12, 19, 26, 33, 40, 48, 41, 34,
            27, 20, 13,  6,  7, 14, 21, 28,
            35, 42, 49, 56, 57, 50, 43, 36,
            29, 22, 15, 23, 30, 37, 44, 51,
            58, 59, 52, 45, 38, 31, 39, 46,
            53, 60, 61, 54, 47, 55, 62, 63,
        };

        private void ParseDqt(BinaryReader reader)
        {
            reader.BaseStream.Seek(2, SeekOrigin.Current);
            byte ident = reader.ReadByte();

            if ((ident & 0xf0) >> 4 != 0)
                throw new NotImplementedException();

            if (_dqt[ident] == null)
                _dqt[ident] = new byte[64];

            for (int i = 0; i < 64; i++)
                _dqt[ident][NaturalOrder[i]] = reader.ReadByte();
        }

        #endregion

        #region Dht

        private struct Huff { public int b, v; }

        private void ParseDht(BinaryReader reader)
        {
            int length = reader.ReadInt16BE();
            int select = reader.ReadByte();
            select = ((select & 0xf0) >> 3) | (select & 0x0f);

            if (_dht[select] == null)
                _dht[select] = new Huff[0x10000];

            byte[] lens = reader.ReadBytes(16);

            int code = 0;
            for (int i = 1; i <= 16; i++)
            {
                for (int j = 0; j < lens[i - 1]; j++)
                {
                    byte val = reader.ReadByte();

                    // Fill table
                    int x = 16 - i;
                    int lo = code << x;
                    int hi = code << x | ((1 << x) - 1);
                    for (int k = lo; k <= hi; k++)
                        _dht[select][k] = new Huff() { b = i, v = val };

                    code += 1;
                }
                code <<= 1;
            }
        }

        #endregion

        #region Sos

        private void ParseSos(BinaryReader reader)
        {
            int length = reader.ReadInt16BE();
            if (reader.ReadByte() != 3) throw new NotImplementedException();

            for (int i = 0; i < 3; i++)
            {
                reader.ReadByte();
                int samp = reader.ReadByte();
                if (i == 0 && samp != 0x00) throw new NotImplementedException();
                if (i != 0 && samp != 0x11) throw new NotImplementedException();
            }

            if (reader.ReadByte() != 0) throw new NotImplementedException();
            if (reader.ReadByte() != 63) throw new NotImplementedException();
            reader.ReadByte();

            ReadScanData(reader);
        }

        #endregion

        #endregion

        #region Scan

        private void ReadScanData(BinaryReader reader)
        {
            List<byte> scandata = new List<byte>(1024);
            while (true)
            {
                byte b = reader.ReadByte();

                if (b == 0xff)
                {
                    if (reader.ReadByte() == 0x00)
                        scandata.Add(0xff);
                    else
                    {
                        reader.BaseStream.Seek(-2, SeekOrigin.Current);
                        break;
                    }
                }
                else scandata.Add(b);
            }

            // Add two padding bytes for huffman decoding
            scandata.Add(0);
            scandata.Add(0);

            _scandata = scandata.ToArray();
        }

        public byte Clamp(int n)
        {
            if (n < 0x00) return 0x00;
            if (n > 0xff) return 0xff;
            return (byte)n;
        }

        private static void ScaleX2(byte[] src, byte[] dst, int w, int h)
        {
            Debug.Assert(src.Length == w * h);
            Debug.Assert(dst.Length == w * h * 4);

            int sl = src.Length;
            int w2 = w * 2;

            for (int sy = 0, dy = 0; sy < sl; sy += w, dy += w2)
                for (int sx = 0, dx = 0; sx < sy + w; sx++, dx += 2)
                    dst[dx + 0] =
                    dst[dx + 2] =
                    dst[dx + w2 + 0] =
                    dst[dx + w2 + 2] = src[sx];
        }

        const int BPP = 3;
        public void YCbCrToRgb(int[][] src, byte[] dst, int mx, int my, int stride, bool bgr)
        {
            int blk = (my * 16) * stride + (mx * 16 * BPP);
            for (int bl = 0; bl < 4; bl++)
            {
                int ro = (bl & 2) != 0 ? 8 : 0;
                int co = (bl & 1) != 0 ? 8 : 0;

                for (int y = 0, yy = 0, cy = 0; y < 8; y++, yy += 8, cy += 4)
                {
                    int off = blk + ((ro + y) * stride) + (co * BPP);
                    for (int x = 0; x < 8; x++)
                    {
                        int ty = src[bl][yy + x] + 128;
                        int tcb = src[5][cy + (x / 2)];
                        int tcr = src[4][cy + (x / 2)];
                        int r = (int)Math.Round(ty + (1.402 * tcr));
                        int g = (int)Math.Round(ty - (0.344 * tcb) - (0.714 * tcr));
                        int b = (int)Math.Round(ty + (1.772 * tcb));
                        if (bgr)
                        {
                        dst[off++] = Clamp(b);
                        dst[off++] = Clamp(g);
                        dst[off++] = Clamp(r);
                        }
                        else
                        {
                        dst[off++] = Clamp(r);
                        dst[off++] = Clamp(g);
                        dst[off++] = Clamp(b);
                        }
                    }
                }
            }
        }

        public void YCbCrToRgb(byte[] img, int off, int y, int cb, int cr)
        {
            int r = (int)(y + (1.402 * cr));
            int g = (int)(y - (0.344 * cb) - (0.714 * cr));
            int b = (int)(y + (1.772 * cb));

            img[off + 0] = Clamp(b);
            img[off + 1] = Clamp(g);
            img[off + 2] = Clamp(r);
        }


        public byte[] DecodeScan(byte[] reuse = null, bool bgr = false)
        {
            // Calculate the size of the image in mcu's
            int xMcu = (_xres + 15) / 16;
            int yMcu = (_yres + 15) / 16;
            int mcu = xMcu * yMcu;

            int stride = ((xMcu * 16 * BPP) + 3) & ~0x03;
            byte[] img = reuse;
            int numBytes = yMcu * 16 * stride;
            if (img == null || img.Length != numBytes)
            {
                img = new byte[numBytes];
            }
            using (MemoryStream ms = new MemoryStream(_scandata))
            {
                BitReader reader = new BitReader(ms);

                int dY = 0, dCb = 0, dCr = 0;
                for (int y = 0; y < yMcu; y++)
                    for (int x = 0; x < xMcu; x++)
                    {
                        for (int i = 0; i < 4; i++)
                        {
                            DecodeBlock(reader, false, block[i]);
                            block[i][0] += dY; dY = block[i][0];
                            DequantBlock(block[i], false);
                        }

                        DecodeBlock(reader, true, block[4]);
                        block[4][0] += dCb; dCb = block[4][0];
                        DequantBlock(block[4], true);

                        DecodeBlock(reader, true, block[5]);
                        block[5][0] += dCr; dCr = block[5][0];
                        DequantBlock(block[5], true);

                        for (int i = 0; i < 6; i++)
                            DCT.DoIdct(block[i], block[i]);

                        YCbCrToRgb(block, img, x, y, stride, bgr);
                    }
            }

            return img;
        }

        private void DequantBlock(int[] block, bool chroma)
        {
            byte[] dqt = _dqt[chroma ? 1 : 0];

            for (int i = 0; i < 8 * 8; i++)
                block[i] *= dqt[i];
        }

        private static int DecodeNumber(int num, int bits)
        {
            return num < (1 << (bits - 1))
                ? num - ((1 << bits) - 1)
                : num;
        }

        private void DecodeBlock(BitReader reader, bool chroma, int[] result)
        {
            Huff h;
            int tab = chroma ? 1 : 0;

            // Read DC value
            h = _dht[0 + tab][reader.Peek(16)];
            reader.Skip(h.b);
            result[0] = DecodeNumber(reader.Read(h.v), h.v);

            for (int i = 1; i < 64; i++)
            {
                result[i] = 0;
            }

            // Read AC values
            for (int i = 1; i < 64; i++)
            {
                h = _dht[2 + tab][reader.Peek(16)];
                reader.Skip(h.b);

                switch (h.v)
                {
                    case 0x00: i = 63; break;
                    case 0xf0: i += 16; continue;
                    default:
                        i += (h.v & 0xf0) >> 4;
                        result[NaturalOrder[i]] = DecodeNumber(reader.Read(h.v & 0x0f), h.v & 0x0f);
                        break;
                }
            }

        }

        #endregion

    }

    #endregion

    #region Program

    static class Program
    {
        static void Usage()
        {
            Console.WriteLine("jpegdecode <in.jpg> <out.png>");
            Console.WriteLine(" - probably crashes");
        }

        static void Main(string[] args)
        {
            if (args.Length != 2)
            {
                Usage();
                return;
            }

            var jpeg = new Jpeg(args[0]);
            byte[] img = jpeg.DecodeScan();
        }
    }

    #endregion

}