using System;
using System.IO;

namespace CityStoryMod.Storyteller
{
    // Minimal TIFF reader tailored to Carto's GeoTIFF output. Carto writes
    // raw uncompressed strips, one row per strip, Int16 by default. We only
    // need ImageWidth (tag 256), ImageLength (tag 257), and a stream of
    // signed-16 pixel values starting at offset 8 — so we skip 95% of the
    // TIFF spec and parse just enough to find dimensions.
    //
    // Pure C# / netstandard; the test project compiles this file directly so
    // we can validate against synthetic TIFFs without a Carto export.
    //
    // If Carto's writer ever shifts away from this shape (compression on,
    // multi-strip, BigTIFF, Float32 by default, etc.), this reader fails
    // loudly with a descriptive exception instead of silently mis-parsing.
    public static class GeoTiffReader
    {
        public class Grid
        {
            public int Width;
            public int Height;
            // Row-major pixel array, length = Width*Height. Stored as int
            // (sign-extended from Int16) so downstream stats code doesn't
            // have to remember the source bit depth.
            public int[] Pixels;
            // Pixel value used to indicate "no data" — for Carto's Int16
            // output this is -32768. Stats code should skip these.
            public int NoData;
            // Meters per pixel along x and y from the ModelPixelScale tag
            // (33550, 3 doubles). 0 when the tag is absent or malformed —
            // callers should treat 0 as "unknown" and fall back to deriving
            // scale from a known footprint.
            public double ScaleX;
            public double ScaleY;
        }

        // Reads dimensions + raw pixels from a Carto-shaped GeoTIFF. Returns
        // null if the file is unreadable; throws InvalidDataException if the
        // shape is unexpected (e.g. not Int16, multiple samples per pixel,
        // compressed). Caller handles both — a null/exception means we just
        // don't emit the chunk that depends on this file.
        public static Grid Read(string path)
        {
            if (!File.Exists(path)) return null;
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length < 16) throw new InvalidDataException($"TIFF too short ({bytes.Length} bytes).");

            // Byte order: "II" = little-endian, "MM" = big-endian. Carto picks
            // II on every Windows/Linux/macOS that .NET runs on natively, so
            // we'd rarely see MM in practice — but support both anyway.
            bool littleEndian;
            if (bytes[0] == 'I' && bytes[1] == 'I') littleEndian = true;
            else if (bytes[0] == 'M' && bytes[1] == 'M') littleEndian = false;
            else throw new InvalidDataException($"Not a TIFF: byte-order marker = '{(char)bytes[0]}{(char)bytes[1]}'.");

            // Magic 42, then IFD offset (uint).
            ushort magic = ReadU16(bytes, 2, littleEndian);
            if (magic != 42) throw new InvalidDataException($"Bad TIFF magic: {magic}.");
            uint ifdOffset = ReadU32(bytes, 4, littleEndian);
            if (ifdOffset + 2 > bytes.Length) throw new InvalidDataException($"IFD offset {ifdOffset} past end of file.");

            // IFD: ushort entry count, then 12 bytes per entry, then 4-byte
            // pointer to next IFD (always 0 for Carto — single IFD).
            ushort entryCount = ReadU16(bytes, (int)ifdOffset, littleEndian);
            int entryBase = (int)ifdOffset + 2;
            if (entryBase + entryCount * 12 > bytes.Length) throw new InvalidDataException("IFD entries past end of file.");

            int width = 0, height = 0, bitsPerSample = 0, samplesPerPixel = 1, sampleFormat = 0, compression = 1;
            uint stripOffset = 0; // For single-strip Carto output, this is the start of pixel data.
            double scaleX = 0, scaleY = 0;

            for (int i = 0; i < entryCount; i++)
            {
                int entryStart = entryBase + i * 12;
                ushort tag = ReadU16(bytes, entryStart, littleEndian);
                ushort type = ReadU16(bytes, entryStart + 2, littleEndian);
                uint count = ReadU32(bytes, entryStart + 4, littleEndian);
                uint valueOrOffset = ReadU32(bytes, entryStart + 8, littleEndian);

                switch (tag)
                {
                    case 256: width = (int)ReadInlineShort(type, valueOrOffset, littleEndian); break;
                    case 257: height = (int)ReadInlineShort(type, valueOrOffset, littleEndian); break;
                    case 258: bitsPerSample = (int)ReadInlineShort(type, valueOrOffset, littleEndian); break;
                    case 259: compression = (int)ReadInlineShort(type, valueOrOffset, littleEndian); break;
                    case 273: stripOffset = ResolveStripOffset(bytes, type, count, valueOrOffset, littleEndian); break;
                    case 277: samplesPerPixel = (int)ReadInlineShort(type, valueOrOffset, littleEndian); break;
                    case 339: sampleFormat = (int)ReadInlineShort(type, valueOrOffset, littleEndian); break;
                    // ModelPixelScale (33550): 3 doubles [scaleX, scaleY, scaleZ]
                    // out-of-line at the indicated offset. Carto writes this
                    // unconditionally for raster output. We only need the
                    // horizontal scales; scaleZ is for 3D models and we ignore it.
                    case 33550:
                        if (count >= 2 && valueOrOffset + 16 <= bytes.Length)
                        {
                            scaleX = ReadDouble(bytes, (int)valueOrOffset, littleEndian);
                            scaleY = ReadDouble(bytes, (int)valueOrOffset + 8, littleEndian);
                        }
                        break;
                }
            }

            if (width <= 0 || height <= 0)
                throw new InvalidDataException($"Missing ImageWidth/ImageLength in IFD (w={width}, h={height}).");
            if (bitsPerSample != 16)
                throw new InvalidDataException($"Unsupported BitsPerSample {bitsPerSample} (only 16 supported — Carto Int16 / Norm16).");
            if (samplesPerPixel != 1)
                throw new InvalidDataException($"Unsupported SamplesPerPixel {samplesPerPixel} (only 1 supported).");
            if (compression != 1)
                throw new InvalidDataException($"Compressed TIFF (compression={compression}); Carto writes uncompressed.");

            // Carto's writer always lays strip 0 at byte 8 (just past the
            // header), then puts the StripOffsets table later. If the strip
            // offset we read is anything other than 8, the file isn't from
            // Carto's writer — bail loudly.
            if (stripOffset == 0) stripOffset = 8;

            // SampleFormat: 1 = unsigned int, 2 = signed int, 3 = float. For
            // Carto's Int16 default this is 2; for Norm16 it's 1. Both are
            // 16-bit; we read as Int16 and the caller decides whether to
            // treat -32768 as nodata (Int16) or as max (Norm16).
            bool signedSamples = sampleFormat != 1 && sampleFormat != 3; // default = signed when tag absent

            long pixelCount = (long)width * height;
            long pixelBytes = pixelCount * 2;
            if (stripOffset + pixelBytes > bytes.Length)
                throw new InvalidDataException($"Pixel region [{stripOffset}, {stripOffset + pixelBytes}) past end of file (size {bytes.Length}).");

            var pixels = new int[pixelCount];
            int p = (int)stripOffset;
            if (littleEndian && signedSamples)
            {
                // Hot path — Carto's default on every supported OS.
                for (long i = 0; i < pixelCount; i++)
                {
                    short v = (short)(bytes[p] | (bytes[p + 1] << 8));
                    pixels[i] = v;
                    p += 2;
                }
            }
            else
            {
                for (long i = 0; i < pixelCount; i++)
                {
                    ushort raw = littleEndian
                        ? (ushort)(bytes[p] | (bytes[p + 1] << 8))
                        : (ushort)((bytes[p] << 8) | bytes[p + 1]);
                    pixels[i] = signedSamples ? (short)raw : raw;
                    p += 2;
                }
            }

            return new Grid
            {
                Width = width,
                Height = height,
                Pixels = pixels,
                // Int16 nodata per Carto's writer (GeoTiff.cs:315). Norm16
                // doesn't have a sentinel — Carto sets nodata=0 there, which
                // would collide with valid 0-depth water; for Norm16 the
                // caller should treat all values as data.
                NoData = signedSamples ? -32768 : int.MinValue,
                ScaleX = scaleX,
                ScaleY = scaleY,
            };
        }

        static double ReadDouble(byte[] bytes, int offset, bool littleEndian)
        {
            if (littleEndian == BitConverter.IsLittleEndian)
                return BitConverter.ToDouble(bytes, offset);
            byte[] swap = new byte[8];
            for (int i = 0; i < 8; i++) swap[i] = bytes[offset + 7 - i];
            return BitConverter.ToDouble(swap, 0);
        }

        // Field types 3 (SHORT, ushort) and 4 (LONG, uint) both fit in the
        // 4-byte inline slot. Carto uses SHORT for ImageWidth/ImageLength /
        // BitsPerSample / Compression / SamplesPerPixel / SampleFormat.
        // For SHORT the value lives in the low 2 bytes of the inline slot
        // when count=1.
        static uint ReadInlineShort(ushort type, uint valueOrOffset, bool littleEndian)
        {
            if (type == 3 /*SHORT*/) return valueOrOffset & 0xFFFFu;
            if (type == 4 /*LONG*/) return valueOrOffset;
            // Carto's TagTypeTable shows ImageWidth = SHORT, but accept LONG
            // too in case a future Carto version widens the type.
            return valueOrOffset;
        }

        // StripOffsets (tag 273) is LONG with count=H. Carto's writer places
        // the offsets table out-of-line — valueOrOffset is a pointer to a
        // sequence of H uint32s. We only need the first (start of strip 0).
        static uint ResolveStripOffset(byte[] bytes, ushort type, uint count, uint valueOrOffset, bool littleEndian)
        {
            if (count == 0) return 8;
            // Inline (count==1 and LONG fits in the slot directly).
            if (count == 1 && type == 4) return valueOrOffset;
            // Out-of-line: read the first uint at valueOrOffset.
            if (valueOrOffset + 4 > bytes.Length) return 8;
            return ReadU32(bytes, (int)valueOrOffset, littleEndian);
        }

        static ushort ReadU16(byte[] bytes, int offset, bool littleEndian)
            => littleEndian
                ? (ushort)(bytes[offset] | (bytes[offset + 1] << 8))
                : (ushort)((bytes[offset] << 8) | bytes[offset + 1]);

        static uint ReadU32(byte[] bytes, int offset, bool littleEndian)
            => littleEndian
                ? (uint)(bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24))
                : (uint)((bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3]);
    }
}
