using System;
using System.Collections.Generic;

namespace CityStoryMod.Storyteller
{
    // Straightforward 8-bit RGBA pixel color. Stored channel-separate so the
    // raster can blend without packing/unpacking.
    internal readonly struct Rgba
    {
        public readonly byte R, G, B, A;
        public Rgba(byte r, byte g, byte b, byte a = 255) { R = r; G = g; B = b; A = a; }

        // "#rrggbb" or "#rrggbbaa". Returns false on anything malformed so the
        // caller can fall back rather than throw mid-render.
        public static bool TryParseHex(string hex, out Rgba color)
        {
            color = default;
            if (string.IsNullOrEmpty(hex) || hex[0] != '#') return false;
            int n = hex.Length - 1;
            if (n != 6 && n != 8) return false;
            if (!TryByte(hex, 1, out byte r)) return false;
            if (!TryByte(hex, 3, out byte g)) return false;
            if (!TryByte(hex, 5, out byte b)) return false;
            byte a = 255;
            if (n == 8 && !TryByte(hex, 7, out a)) return false;
            color = new Rgba(r, g, b, a);
            return true;
        }

        static bool TryByte(string s, int i, out byte v)
        {
            v = 0;
            int hi = HexVal(s[i]), lo = HexVal(s[i + 1]);
            if (hi < 0 || lo < 0) return false;
            v = (byte)((hi << 4) | lo);
            return true;
        }

        static int HexVal(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }
    }

    // Tiny software rasterizer over a top-down RGBA buffer. Just enough
    // primitives to reproduce the combined-map layers the SVG drew: solid cell
    // blits for the terrain grid, filled polygons for zoning, thick polylines
    // for roads, and discs for intersections / service markers.
    //
    // Pure-C# and Unity-free so CartoProcessor (its only caller) stays
    // Compile-Linkable into the net48 test project. Coordinates are in pixel
    // space; callers do the world→pixel transform (north-up flip included),
    // exactly as the SVG path did.
    internal sealed class Raster
    {
        public readonly int Width;
        public readonly int Height;
        public readonly byte[] Pixels; // Width*Height*4, RGBA, row-major top-down

        public Raster(int width, int height)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            Width = width;
            Height = height;
            Pixels = new byte[width * height * 4];
        }

        public void Clear(Rgba color)
        {
            for (int i = 0; i < Pixels.Length; i += 4)
            {
                Pixels[i] = color.R;
                Pixels[i + 1] = color.G;
                Pixels[i + 2] = color.B;
                Pixels[i + 3] = color.A;
            }
        }

        // Source-over blend of one pixel. Opaque writes take a fast path.
        public void BlendPixel(int x, int y, Rgba c)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height) return;
            int idx = (y * Width + x) * 4;
            if (c.A == 0) return;
            if (c.A == 255)
            {
                Pixels[idx] = c.R;
                Pixels[idx + 1] = c.G;
                Pixels[idx + 2] = c.B;
                Pixels[idx + 3] = 255;
                return;
            }
            // out = src*a + dst*(1-a), straight (non-premultiplied) over an
            // opaque-ish background; result alpha is forced opaque since the
            // canvas starts opaque.
            int sa = c.A;
            int ia = 255 - sa;
            Pixels[idx]     = (byte)((c.R * sa + Pixels[idx]     * ia) / 255);
            Pixels[idx + 1] = (byte)((c.G * sa + Pixels[idx + 1] * ia) / 255);
            Pixels[idx + 2] = (byte)((c.B * sa + Pixels[idx + 2] * ia) / 255);
            Pixels[idx + 3] = 255;
        }

        // Axis-aligned fill. x/y/w/h are in pixel space; clamped to bounds.
        // Opaque colors use a fast row blit; translucent ones blend per pixel.
        public void FillRect(double x, double y, double w, double h, Rgba c)
        {
            int x0 = (int)Math.Floor(x);
            int y0 = (int)Math.Floor(y);
            int x1 = (int)Math.Ceiling(x + w);
            int y1 = (int)Math.Ceiling(y + h);
            if (x0 < 0) x0 = 0;
            if (y0 < 0) y0 = 0;
            if (x1 > Width) x1 = Width;
            if (y1 > Height) y1 = Height;
            for (int py = y0; py < y1; py++)
                for (int px = x0; px < x1; px++)
                    BlendPixel(px, py, c);
        }

        public void FillCircle(double cx, double cy, double radius, Rgba c)
        {
            if (radius <= 0) return;
            int x0 = (int)Math.Floor(cx - radius);
            int y0 = (int)Math.Floor(cy - radius);
            int x1 = (int)Math.Ceiling(cx + radius);
            int y1 = (int)Math.Ceiling(cy + radius);
            double r2 = radius * radius;
            for (int py = y0; py <= y1; py++)
            {
                for (int px = x0; px <= x1; px++)
                {
                    double dx = px + 0.5 - cx;
                    double dy = py + 0.5 - cy;
                    if (dx * dx + dy * dy <= r2) BlendPixel(px, py, c);
                }
            }
        }

        // Even-odd scanline polygon fill. Points in pixel space; the polygon is
        // implicitly closed (last → first). Handles convex and concave shapes.
        public void FillPolygon(IReadOnlyList<double> xs, IReadOnlyList<double> ys, Rgba c)
        {
            int n = xs.Count;
            if (n < 3 || ys.Count != n) return;

            double minYd = double.MaxValue, maxYd = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                if (ys[i] < minYd) minYd = ys[i];
                if (ys[i] > maxYd) maxYd = ys[i];
            }
            int yStart = Math.Max(0, (int)Math.Floor(minYd));
            int yEnd = Math.Min(Height - 1, (int)Math.Ceiling(maxYd));

            var nodes = new List<double>(n);
            for (int py = yStart; py <= yEnd; py++)
            {
                double scanY = py + 0.5;
                nodes.Clear();
                int j = n - 1;
                for (int i = 0; i < n; i++)
                {
                    double yi = ys[i], yj = ys[j];
                    if ((yi <= scanY && yj > scanY) || (yj <= scanY && yi > scanY))
                    {
                        double t = (scanY - yi) / (yj - yi);
                        nodes.Add(xs[i] + t * (xs[j] - xs[i]));
                    }
                    j = i;
                }
                if (nodes.Count < 2) continue;
                nodes.Sort();
                for (int k = 0; k + 1 < nodes.Count; k += 2)
                {
                    int xa = (int)Math.Round(nodes[k]);
                    int xb = (int)Math.Round(nodes[k + 1]);
                    if (xb < xa) { int tmp = xa; xa = xb; xb = tmp; }
                    if (xa < 0) xa = 0;
                    if (xb >= Width) xb = Width - 1;
                    for (int px = xa; px <= xb; px++) BlendPixel(px, py, c);
                }
            }
        }

        // Thick line segment as a filled quad plus round caps. width is the full
        // stroke width in pixels.
        public void DrawThickSegment(double x0, double y0, double x1, double y1, double width, Rgba c)
        {
            double dx = x1 - x0, dy = y1 - y0;
            double len = Math.Sqrt(dx * dx + dy * dy);
            double half = width / 2.0;
            if (len < 1e-6)
            {
                FillCircle(x0, y0, half, c);
                return;
            }
            double nx = -dy / len * half;
            double ny = dx / len * half;
            var qx = new[] { x0 + nx, x1 + nx, x1 - nx, x0 - nx };
            var qy = new[] { y0 + ny, y1 + ny, y1 - ny, y0 - ny };
            FillPolygon(qx, qy, c);
            // Round caps / joins so a multi-segment polyline has no gaps or
            // mitre spikes at the vertices.
            FillCircle(x0, y0, half, c);
            FillCircle(x1, y1, half, c);
        }

        // Polyline from a flat [x,y] vertex list (pixel space).
        public void DrawPolyline(IReadOnlyList<double> px, IReadOnlyList<double> py, double width, Rgba c)
        {
            int n = px.Count;
            if (n < 2 || py.Count != n) return;
            for (int i = 0; i + 1 < n; i++)
                DrawThickSegment(px[i], py[i], px[i + 1], py[i + 1], width, c);
        }

        public byte[] ToPng() => PngWriter.Encode(Pixels, Width, Height);
    }
}
