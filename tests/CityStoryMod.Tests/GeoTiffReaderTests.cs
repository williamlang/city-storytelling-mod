using System.IO;
using CityStoryMod.Storyteller;
using FluentAssertions;
using Xunit;

namespace CityStoryMod.Tests
{
    public class GeoTiffReaderTests
    {
        // Builds a Carto-shaped TIFF in memory: little-endian header, single
        // strip at offset 8, Int16 pixels, then an IFD with the tags the
        // reader probes for (256/257/258/259/273/277/339).
        //
        // We intentionally mirror Carto's writer layout (strip 0 first, then
        // StripOffsets + StripByteCounts tables, then IFD) so the test
        // exercises the same code path real Carto output exercises.
        static byte[] BuildCartoTiff(int width, int height, short[] pixels)
        {
            int bps = width * 2;              // bytes per strip
            int stripsStart = 8;
            int offsetsTableStart = stripsStart + bps * height;
            int byteCountsTableStart = offsetsTableStart + 4 * height;
            int ifdStart = byteCountsTableStart + 4 * height;

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);

            // Header.
            bw.Write((byte)'I'); bw.Write((byte)'I');
            bw.Write((short)42);
            bw.Write(ifdStart);

            // Strip data (Int16, row-major, little-endian).
            for (int i = 0; i < pixels.Length; i++) bw.Write(pixels[i]);

            // StripOffsets table — one uint per strip, pointing into the
            // strip data block. (Carto writes this out-of-line.)
            for (int i = 0; i < height; i++) bw.Write(stripsStart + i * bps);
            // StripByteCounts table — one uint per strip, all = bps.
            for (int i = 0; i < height; i++) bw.Write(bps);

            // IFD: entry count, entries (12 bytes each), next-IFD pointer.
            // We write the minimum set the reader needs.
            // Order tags ascending by tag number — TIFF requires this.
            void WriteEntry(ushort tag, ushort type, uint count, uint valueOrOffset)
            {
                bw.Write(tag);
                bw.Write(type);
                bw.Write(count);
                bw.Write(valueOrOffset);
            }

            ushort entryCount = 7;
            bw.Write(entryCount);
            WriteEntry(256, 3, 1, (uint)width);          // ImageWidth, SHORT
            WriteEntry(257, 3, 1, (uint)height);         // ImageLength
            WriteEntry(258, 3, 1, 16);                   // BitsPerSample
            WriteEntry(259, 3, 1, 1);                    // Compression (none)
            WriteEntry(273, 4, (uint)height, (uint)offsetsTableStart); // StripOffsets
            WriteEntry(277, 3, 1, 1);                    // SamplesPerPixel
            WriteEntry(339, 3, 1, 2);                    // SampleFormat (signed int)
            bw.Write(0u);                                // next-IFD pointer = none

            return ms.ToArray();
        }

        [Fact]
        public void Read_returns_null_for_missing_file()
        {
            GeoTiffReader.Read("Z:\\does-not-exist.tif").Should().BeNull();
        }

        [Fact]
        public void Read_parses_carto_shaped_tiff_dimensions_and_pixels()
        {
            short[] pixels = { 0, 1, 2, 3, 4, 5 };
            byte[] tif = BuildCartoTiff(3, 2, pixels);
            string tmp = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(tmp, tif);
                var grid = GeoTiffReader.Read(tmp);
                grid.Should().NotBeNull();
                grid.Width.Should().Be(3);
                grid.Height.Should().Be(2);
                grid.Pixels.Should().BeEquivalentTo(new[] { 0, 1, 2, 3, 4, 5 });
                grid.NoData.Should().Be(-32768);
            }
            finally
            {
                File.Delete(tmp);
            }
        }

        [Fact]
        public void Read_sign_extends_negative_int16_pixels()
        {
            short[] pixels = { -32768, -1, 0, 1, 32767, -50 };
            byte[] tif = BuildCartoTiff(3, 2, pixels);
            string tmp = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(tmp, tif);
                var grid = GeoTiffReader.Read(tmp);
                grid.Pixels.Should().BeEquivalentTo(new[] { -32768, -1, 0, 1, 32767, -50 });
            }
            finally
            {
                File.Delete(tmp);
            }
        }

        [Fact]
        public void Read_throws_on_compressed_tiff()
        {
            // Take a valid TIFF and patch the Compression tag (259) to 5 (LZW).
            byte[] tif = BuildCartoTiff(2, 1, new short[] { 0, 0 });
            // IFD lives right after the strip and offset/count tables.
            // For a 2×1 image with bps=4: stripsStart=8, offsetsTable=12,
            // byteCountsTable=16, ifd=20. Entry count is 2 bytes, then 12 bytes/entry.
            // Compression (tag 259) is the 4th entry (index 3) by ascending tag order:
            //   256 (ImageWidth), 257 (ImageLength), 258 (BitsPerSample), 259 (Compression).
            // Position of the value-offset field: ifdStart(20) + 2 + 3*12 + 8 = 66.
            // Write 5 (LZW) into that slot.
            int compressionValueOffset = 20 + 2 + 3 * 12 + 8;
            tif[compressionValueOffset] = 5;
            tif[compressionValueOffset + 1] = 0;
            tif[compressionValueOffset + 2] = 0;
            tif[compressionValueOffset + 3] = 0;

            string tmp = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(tmp, tif);
                System.Action act = () => GeoTiffReader.Read(tmp);
                act.Should().Throw<InvalidDataException>().WithMessage("*Compressed*");
            }
            finally
            {
                File.Delete(tmp);
            }
        }

        [Fact]
        public void Read_throws_on_unsupported_bit_depth()
        {
            byte[] tif = BuildCartoTiff(2, 1, new short[] { 0, 0 });
            // BitsPerSample is tag 258, entry index 2 (third). Patch to 8.
            int bpsValueOffset = 20 + 2 + 2 * 12 + 8;
            tif[bpsValueOffset] = 8;
            tif[bpsValueOffset + 1] = 0;

            string tmp = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(tmp, tif);
                System.Action act = () => GeoTiffReader.Read(tmp);
                act.Should().Throw<InvalidDataException>().WithMessage("*BitsPerSample*");
            }
            finally
            {
                File.Delete(tmp);
            }
        }
    }
}
