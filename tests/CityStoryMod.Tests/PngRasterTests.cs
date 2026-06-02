using CityStoryMod.Storyteller;
using FluentAssertions;
using Xunit;

namespace CityStoryMod.Tests
{
    // Covers the pure-C# raster + PNG-encoder primitives that back the
    // combined map.png (see CartoProcessor.RenderCombinedMapPng).
    public class PngRasterTests
    {
        // -- PngWriter --

        static (int w, int h) ReadHeader(byte[] png)
        {
            byte[] sig = { 137, 80, 78, 71, 13, 10, 26, 10 };
            for (int i = 0; i < 8; i++) png[i].Should().Be(sig[i]);
            ((char)png[12]).Should().Be('I');
            ((char)png[13]).Should().Be('H');
            ((char)png[14]).Should().Be('D');
            ((char)png[15]).Should().Be('R');
            int w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
            int h = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
            // bit depth 8, color type 6 (RGBA).
            png[24].Should().Be(8);
            png[25].Should().Be(6);
            return (w, h);
        }

        [Fact]
        public void Encode_writes_signature_and_ihdr_dimensions()
        {
            var rgba = new byte[3 * 2 * 4];
            byte[] png = PngWriter.Encode(rgba, 3, 2);
            var (w, h) = ReadHeader(png);
            w.Should().Be(3);
            h.Should().Be(2);
        }

        [Fact]
        public void Encode_ends_with_iend_chunk()
        {
            byte[] png = PngWriter.Encode(new byte[1 * 1 * 4], 1, 1);
            // IEND is the final chunk: type bytes sit 8 from the end
            // (4 length + 4 type + 0 data + 4 crc).
            int n = png.Length;
            ((char)png[n - 8]).Should().Be('I');
            ((char)png[n - 7]).Should().Be('E');
            ((char)png[n - 6]).Should().Be('N');
            ((char)png[n - 5]).Should().Be('D');
        }

        [Fact]
        public void Encode_throws_when_buffer_too_small()
        {
            System.Action act = () => PngWriter.Encode(new byte[4], 2, 2);
            act.Should().Throw<System.ArgumentException>();
        }

        // -- Rgba --

        [Fact]
        public void TryParseHex_parses_rgb_and_rgba()
        {
            Rgba.TryParseHex("#1a2330", out var rgb).Should().BeTrue();
            rgb.R.Should().Be(0x1a);
            rgb.G.Should().Be(0x23);
            rgb.B.Should().Be(0x30);
            rgb.A.Should().Be(255);

            Rgba.TryParseHex("#10203040", out var rgba).Should().BeTrue();
            rgba.A.Should().Be(0x40);
        }

        [Theory]
        [InlineData("1a2330")]   // missing #
        [InlineData("#12")]      // too short
        [InlineData("#12345")]   // odd length
        [InlineData("#1234zz")]  // non-hex
        [InlineData(null)]
        public void TryParseHex_rejects_malformed(string hex)
        {
            Rgba.TryParseHex(hex, out _).Should().BeFalse();
        }

        // -- Raster --

        static Rgba PixelAt(Raster r, int x, int y)
        {
            int i = (y * r.Width + x) * 4;
            return new Rgba(r.Pixels[i], r.Pixels[i + 1], r.Pixels[i + 2], r.Pixels[i + 3]);
        }

        [Fact]
        public void Clear_fills_every_pixel()
        {
            var r = new Raster(4, 4);
            r.Clear(new Rgba(10, 20, 30));
            var p = PixelAt(r, 2, 3);
            p.R.Should().Be(10);
            p.G.Should().Be(20);
            p.B.Should().Be(30);
            p.A.Should().Be(255);
        }

        [Fact]
        public void FillRect_paints_inside_and_leaves_outside()
        {
            var r = new Raster(10, 10);
            r.Clear(new Rgba(0, 0, 0));
            r.FillRect(2, 2, 4, 4, new Rgba(255, 0, 0));
            PixelAt(r, 3, 3).R.Should().Be(255);   // inside
            PixelAt(r, 0, 0).R.Should().Be(0);     // outside
            PixelAt(r, 9, 9).R.Should().Be(0);     // outside
        }

        [Fact]
        public void FillRect_clips_to_bounds()
        {
            var r = new Raster(5, 5);
            r.Clear(new Rgba(0, 0, 0));
            // Spills past the edges; must not throw and must paint the corner.
            r.FillRect(3, 3, 10, 10, new Rgba(0, 255, 0));
            PixelAt(r, 4, 4).G.Should().Be(255);
        }

        [Fact]
        public void BlendPixel_alpha_blends_over_background()
        {
            var r = new Raster(1, 1);
            r.Clear(new Rgba(0, 0, 0));
            r.BlendPixel(0, 0, new Rgba(255, 255, 255, 128)); // ~50%
            var p = PixelAt(r, 0, 0);
            p.R.Should().BeInRange(120, 135);
            p.A.Should().Be(255);
        }

        [Fact]
        public void FillPolygon_fills_a_triangle_interior()
        {
            var r = new Raster(20, 20);
            r.Clear(new Rgba(0, 0, 0));
            // Right triangle covering the lower-left region.
            r.FillPolygon(new double[] { 2, 2, 16 }, new double[] { 2, 16, 16 }, new Rgba(0, 0, 255));
            PixelAt(r, 4, 14).B.Should().Be(255);   // well inside
            PixelAt(r, 15, 3).B.Should().Be(0);      // outside the hypotenuse
        }

        [Fact]
        public void DrawThickSegment_paints_along_the_line()
        {
            var r = new Raster(20, 20);
            r.Clear(new Rgba(0, 0, 0));
            r.DrawThickSegment(2, 10, 18, 10, 3, new Rgba(255, 0, 0));
            PixelAt(r, 10, 10).R.Should().Be(255);   // on the line
            PixelAt(r, 10, 2).R.Should().Be(0);      // far above it
        }

        [Fact]
        public void ToPng_roundtrips_through_encoder()
        {
            var r = new Raster(8, 6);
            r.Clear(new Rgba(1, 2, 3));
            var (w, h) = ReadHeader(r.ToPng());
            w.Should().Be(8);
            h.Should().Be(6);
        }
    }
}
