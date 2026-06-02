using System;
using System.IO;
using System.IO.Compression;

namespace CityStoryMod.Storyteller
{
    // Minimal, dependency-free PNG encoder. Takes a top-down 32-bit RGBA pixel
    // buffer (row-major, 4 bytes/pixel: R,G,B,A) and produces a valid
    // 8-bit/channel truecolor-with-alpha PNG.
    //
    // Why hand-rolled: the storyteller's combined map needs to be a RASTER the
    // vision model can actually see — an SVG comes back to the agent as XML
    // text, not an image, and the terrain layer alone runs to ~2.4 MB of
    // <rect> hex soup (~600k tokens, none of it perceived as a picture). A PNG
    // of the same content is ~100-300 KB and a fixed ~2k image tokens.
    //
    // Why not UnityEngine.Texture2D.EncodeToPNG: CartoProcessor (the only
    // caller) is pure-C# and Compile-Linked into the net48 test project with no
    // Unity references. Pulling in UnityEngine here would break that build and
    // make the raster output un-unit-testable. DeflateStream (raw DEFLATE) ships
    // in both net48 and Unity's Mono, so we wrap it in a zlib stream by hand.
    //
    // PNG layout we emit:
    //   8-byte signature
    //   IHDR  (13 bytes: width, height, bitDepth=8, colorType=6 RGBA, no interlace)
    //   IDAT  (zlib-wrapped DEFLATE of filtered scanlines; filter byte 0 = None)
    //   IEND
    internal static class PngWriter
    {
        static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

        // rgba: width*height*4 bytes, row-major top-to-bottom, channel order R,G,B,A.
        public static byte[] Encode(byte[] rgba, int width, int height)
        {
            if (rgba == null) throw new ArgumentNullException(nameof(rgba));
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            int expected = checked(width * height * 4);
            if (rgba.Length < expected)
                throw new ArgumentException($"rgba buffer too small: have {rgba.Length}, need {expected}.", nameof(rgba));

            using (var outStream = new MemoryStream())
            {
                outStream.Write(Signature, 0, Signature.Length);

                // IHDR
                var ihdr = new byte[13];
                WriteBE32(ihdr, 0, (uint)width);
                WriteBE32(ihdr, 4, (uint)height);
                ihdr[8]  = 8;   // bit depth
                ihdr[9]  = 6;   // color type: truecolor + alpha
                ihdr[10] = 0;   // compression: deflate
                ihdr[11] = 0;   // filter: adaptive (per-scanline filter byte)
                ihdr[12] = 0;   // interlace: none
                WriteChunk(outStream, "IHDR", ihdr, 0, ihdr.Length);

                // Build the raw image: each scanline prefixed with filter type 0 (None).
                int stride = width * 4;
                var raw = new byte[(stride + 1) * height];
                for (int y = 0; y < height; y++)
                {
                    int rawRow = y * (stride + 1);
                    raw[rawRow] = 0; // filter: None
                    Buffer.BlockCopy(rgba, y * stride, raw, rawRow + 1, stride);
                }

                byte[] idat = ZlibCompress(raw);
                WriteChunk(outStream, "IDAT", idat, 0, idat.Length);

                WriteChunk(outStream, "IEND", Array.Empty<byte>(), 0, 0);
                return outStream.ToArray();
            }
        }

        // Wrap raw DEFLATE output in a zlib stream: 2-byte header + DEFLATE body
        // + 4-byte big-endian Adler-32 of the *uncompressed* data.
        static byte[] ZlibCompress(byte[] data)
        {
            using (var ms = new MemoryStream())
            {
                ms.WriteByte(0x78); // CMF: deflate, 32K window
                ms.WriteByte(0x9C); // FLG: default compression, header checksum-valid
                using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                {
                    deflate.Write(data, 0, data.Length);
                }
                uint adler = Adler32(data);
                ms.WriteByte((byte)(adler >> 24));
                ms.WriteByte((byte)(adler >> 16));
                ms.WriteByte((byte)(adler >> 8));
                ms.WriteByte((byte)adler);
                return ms.ToArray();
            }
        }

        static void WriteChunk(Stream s, string type, byte[] data, int offset, int length)
        {
            var lenBuf = new byte[4];
            WriteBE32(lenBuf, 0, (uint)length);
            s.Write(lenBuf, 0, 4);

            var typeBytes = new byte[4];
            for (int i = 0; i < 4; i++) typeBytes[i] = (byte)type[i];
            s.Write(typeBytes, 0, 4);

            if (length > 0) s.Write(data, offset, length);

            // CRC-32 over chunk type + chunk data.
            uint crc = Crc32.Start();
            crc = Crc32.Update(crc, typeBytes, 0, 4);
            if (length > 0) crc = Crc32.Update(crc, data, offset, length);
            crc = Crc32.Finish(crc);

            var crcBuf = new byte[4];
            WriteBE32(crcBuf, 0, crc);
            s.Write(crcBuf, 0, 4);
        }

        static void WriteBE32(byte[] buf, int offset, uint value)
        {
            buf[offset]     = (byte)(value >> 24);
            buf[offset + 1] = (byte)(value >> 16);
            buf[offset + 2] = (byte)(value >> 8);
            buf[offset + 3] = (byte)value;
        }

        // Adler-32 (RFC 1950).
        static uint Adler32(byte[] data)
        {
            const uint mod = 65521;
            uint a = 1, b = 0;
            // Process in blocks so the modulo isn't taken every byte.
            int i = 0, len = data.Length;
            while (len > 0)
            {
                int block = len < 5552 ? len : 5552;
                len -= block;
                for (int j = 0; j < block; j++)
                {
                    a += data[i++];
                    b += a;
                }
                a %= mod;
                b %= mod;
            }
            return (b << 16) | a;
        }
    }

    // Standard CRC-32 (PNG / zlib polynomial 0xEDB88320), table-driven.
    internal static class Crc32
    {
        static readonly uint[] Table = BuildTable();

        static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        public static uint Start() => 0xFFFFFFFF;

        public static uint Update(uint crc, byte[] data, int offset, int length)
        {
            for (int i = 0; i < length; i++)
                crc = Table[(crc ^ data[offset + i]) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        public static uint Finish(uint crc) => crc ^ 0xFFFFFFFF;
    }
}
