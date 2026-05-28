using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace CityStoryMod.Storyteller
{
    // Pure-C# pipeline that turns Carto's raw GeoJSON into LLM-friendly
    // markdown chunks inside the city dir. Reads from <cityDir>/carto/GeoJSON/
    // and writes to <cityDir>/carto/processed/.
    //
    // The agent never reads raw Carto output — only the processed chunks.
    // We do all geometry work here (centroids, bounding boxes, adjacency,
    // bearings) and emit derived facts in human-prose form so the agent's
    // context stays small and its claims stay grounded.
    //
    // No Unity / Game.dll references — the test project Compile-Links this
    // file directly so the processor can be exercised against fixture GeoJSON
    // without a CS2 build.
    public static class CartoProcessor
    {
        // Carto's peer-API FromRequest helper sets TargetProjection = UTM but
        // leaves ProjectionDefinition as `default` (zero-initialized), so the
        // emitted coordinates land in a degree-scale frame instead of UTM
        // meters. Reported upstream — when Carto fixes that helper (or adds a
        // Projection field to ExportRequest), we can drop this conversion.
        // For now we project to a local meters frame on parse so area,
        // distance, and adjacency work in honest units: at the equator
        // (which is where Carto's default origin sits) one degree is
        // ~111,320 m, and an equirectangular approximation is well under
        // 0.5 % off for a city-sized region.
        const double DegreesToMetersAtEquator = 111320.0;

        // Two districts count as adjacent if at least this many of their
        // boundary vertices land within VertexAdjacencyToleranceMeters of a
        // vertex on the other polygon. Carto traces each polygon's boundary
        // independently, so adjacent districts that share an edge may have
        // slightly different vertex placements along it — exact-match
        // detection is too brittle, but a 20 m / ≥2-pairs proximity check is
        // robust for CS2's cell-grid districts while still rejecting
        // accidental corner kisses (where only one vertex pair would match).
        const double VertexAdjacencyToleranceMeters = 20.0;
        const int MinClosePairsForAdjacency = 2;

        public class Result
        {
            public bool Success;
            public string ErrorMessage;
            public int DistrictsWritten;
            public int NamedBuildingsAssigned;
            public int RoadsWritten;
            public int MapTilesParsed;
            public bool ElevationWritten;
            public bool WaterWritten;
            public string IndexPath;
        }

        public class RasterSummary
        {
            public int Width, Height;
            public long Pixels;
            // For elevation: range across all pixels (meters above an unknown
            // floor near sea level — see comment in TryProcessElevation).
            // For depth: only water cells contribute; non-water pixels are -32768.
            public int Min, Max;
            public double Mean;
            public double StdDev;
            // English-language reading: "mostly flat", "hilly", "lake-dominated", etc.
            // Computed by the processor from stats above; renderers lead with it.
            public string Reading;
            // Where the extremes are. Quadrant label = "NE" / "NW" / "SE" / "SW".
            // null when the raster has no signal (e.g. all-water depth map).
            public string HighQuadrant;
            public string LowQuadrant;
            // Water-cell stats. Populated only for the depth raster.
            public long WaterCells;
            public double PercentWater;
            // Quadrant water shares for the depth raster (% of map per quadrant
            // that is water). null for elevation.
            public double[] WaterQuadrantPercent;   // [NW, NE, SW, SE]
            // Coastline stats from the depth raster. CoastlineCells counts
            // water pixels with at least one 4-neighbor that is land. Length
            // is meters using the TIFF's ModelPixelScale; 0 when scale was
            // unavailable. CoastlineQuadrantPercent slices the coast-cell
            // count by quadrant. ShorelineRatio = coastline cells /
            // sqrt(water cells × π) — a roughness index where 1.0 ≈ a circle
            // and higher values indicate islands / inlets / convoluted shore.
            public long CoastlineCells;
            public double CoastlineLengthM;
            public double[] CoastlineQuadrantPercent;
            public double ShorelineRatio;
        }

        public static Result Process(string cartoDir)
        {
            var result = new Result();
            try
            {
                string areaFile = Path.Combine(cartoDir, "GeoJSON", "Area_Boundary.json");
                if (!File.Exists(areaFile))
                {
                    result.ErrorMessage = $"Area_Boundary.json not found at {areaFile}";
                    return result;
                }

                string areaJson = File.ReadAllText(areaFile);
                List<District> districts = ParseDistricts(areaJson);
                List<MapTile> mapTiles = ParseMapTiles(areaJson);

                // Buildings are optional — if the file isn't there (e.g. an
                // earlier export that didn't request System.Building), we
                // still produce district chunks without a Buildings section.
                List<Building> namedBuildings = new List<Building>();
                string buildingFile = Path.Combine(cartoDir, "GeoJSON", "Building_Boundary.json");
                if (File.Exists(buildingFile))
                {
                    namedBuildings = ParseNamedBuildings(File.ReadAllText(buildingFile));
                }

                // Roads are optional too — the Network system was added in a
                // later Carto integration phase, and older exports won't have
                // the file. Missing → no roads.md chunk, no road row in the
                // index. Present → parse it and emit both.
                List<Road> roads = new List<Road>();
                string networkFile = Path.Combine(cartoDir, "GeoJSON", "Network_Centerline.json");
                if (File.Exists(networkFile))
                {
                    roads = ParseRoads(File.ReadAllText(networkFile));
                }

                // Re-center every coordinate around the city centroid so the
                // numbers the agent sees read like "(123, -456)" instead of
                // "(-20,872,758, -1,188)". Carto's projection chain applies
                // arbitrary-looking offsets we don't control — recentering
                // makes the chunks self-consistent regardless.
                RecenterCoordinates(districts, namedBuildings, mapTiles, roads);

                ComputeAdjacency(districts);
                AssignBuildingsToDistricts(namedBuildings, districts);

                Footprint footprint = ComputeFootprint(mapTiles, districts);

                string processedDir = Path.Combine(cartoDir, "processed");
                string districtsDir = Path.Combine(processedDir, "districts");
                Directory.CreateDirectory(districtsDir);

                // Clear any stale per-district files so renames don't leave
                // orphans. The index is rewritten unconditionally below.
                foreach (string f in Directory.GetFiles(districtsDir, "*.md"))
                {
                    try { File.Delete(f); } catch { }
                }

                foreach (District d in districts)
                {
                    string path = Path.Combine(districtsDir, d.Slug + ".md");
                    File.WriteAllText(path, RenderDistrictMarkdown(d));
                }

                // Roads chunk — separate file so the agent can pull it
                // independently when answering geography questions. Always
                // overwrite so it stays in sync with the latest export.
                string roadsPath = Path.Combine(processedDir, "roads.md");
                string roadsSvgPath = Path.Combine(processedDir, "roads.svg");
                if (roads.Count > 0)
                {
                    File.WriteAllText(roadsPath, RenderRoadsMarkdown(roads));
                    // Companion SVG render of the road network — gives the
                    // player (and a vision-capable agent) a quick visual
                    // alongside the text chunk. Falls back to skipping the
                    // file when the renderer can't compute a viewBox (no
                    // footprint, no usable centerlines).
                    string svg = RenderRoadsSvg(roads, districts, footprint);
                    if (svg != null) File.WriteAllText(roadsSvgPath, svg);
                    else { try { File.Delete(roadsSvgPath); } catch { } }
                }
                else
                {
                    // Empty Network export (or older Carto without Network).
                    // Delete any stale chunk from a prior export rather than
                    // leaving misleading data on disk.
                    try { File.Delete(roadsPath); } catch { }
                    try { File.Delete(roadsSvgPath); } catch { }
                }

                // Raster summaries — both optional. The TIFF files are
                // produced only when CartoBridge asked for them and the map
                // has the underlying data (worldHeightmap missing on some
                // editor saves). Read the grids once here so the combined-
                // map renderer below can reuse them without a second read
                // (each grid is ~64 MB at Carto's default 4096² resolution).
                GeoTiffReader.Grid elevGrid = SafeReadTiff(Path.Combine(cartoDir, "GeoTIFF", "Elevation.tif"));
                GeoTiffReader.Grid depthGrid = SafeReadTiff(Path.Combine(cartoDir, "GeoTIFF", "Depth.tif"));

                RasterSummary elevation = elevGrid != null ? ComputeElevationSummary(elevGrid) : null;
                if (elevation != null) File.WriteAllText(Path.Combine(processedDir, "elevation.md"), RenderElevationMarkdown(elevation));

                RasterSummary water = depthGrid != null ? ComputeWaterSummary(depthGrid) : null;
                if (water != null) File.WriteAllText(Path.Combine(processedDir, "water.md"), RenderWaterMarkdown(water));

                result.ElevationWritten = elevation != null;
                result.WaterWritten = water != null;

                // Combined map: terrain (hypsometric) + water (depth-shaded) +
                // districts + roads + labels in one SVG. Renders when there's
                // at least an elevation grid OR a road network to draw.
                string combinedMapPath = Path.Combine(processedDir, "map.svg");
                if (elevGrid != null || roads.Count > 0)
                {
                    string combined = RenderCombinedMapSvg(roads, districts, footprint, elevGrid, depthGrid, elevation, water);
                    if (combined != null) File.WriteAllText(combinedMapPath, combined);
                    else { try { File.Delete(combinedMapPath); } catch { } }
                }
                else
                {
                    try { File.Delete(combinedMapPath); } catch { }
                }

                string indexPath = Path.Combine(processedDir, "index.md");
                File.WriteAllText(indexPath, RenderIndexMarkdown(districts, namedBuildings, roads, footprint, elevation, water));

                result.Success = true;
                result.DistrictsWritten = districts.Count;
                result.NamedBuildingsAssigned = namedBuildings.Count(b => b.DistrictSlug != null);
                result.RoadsWritten = roads.Count;
                result.MapTilesParsed = mapTiles.Count;
                result.IndexPath = indexPath;
                return result;
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
                return result;
            }
        }

        internal class District
        {
            public string Name;
            public string Slug;
            public double[][] Polygon;         // [n][2] of [x, y] in meters
            public double CentroidX, CentroidY;
            public double MinX, MinY, MaxX, MaxY;
            public double AreaM2;
            public Dictionary<string, double> CartoProperties = new Dictionary<string, double>();
            public List<DistrictBorder> Borders = new List<DistrictBorder>();
            public List<Building> Buildings = new List<Building>();
        }

        internal class DistrictBorder
        {
            public string Name;
            public string Slug;
            public string Direction;   // "north", "north-east", etc., or "adjacent"
        }

        internal class Building
        {
            public string Name;
            public string Category;            // Carto's `Category` property (e.g. "Fire", "Education")
            public double CentroidX, CentroidY;
            public string DistrictSlug;        // null if outside every district polygon
            public Dictionary<string, double> CartoProperties = new Dictionary<string, double>();
        }

        internal class MapTile
        {
            public double[][] Polygon;         // [n][2] of [x, y] in meters — tile boundary
            public double CentroidX, CentroidY;
            public double MinX, MinY, MaxX, MaxY;
            public double AreaM2;
            public bool Unlocked;              // Carto's `Unlocked` property — true once player buys the tile
        }

        internal class Road
        {
            public string Name;                // Carto's `Name` (often empty for un-named segments)
            public string Object;              // Feature classification: Road, Track, Pathway, Waterway, etc.
            public string Category;            // NetworkCategory flags, comma-separated (e.g. "Car, Large")
            public string Form;                // Carto's `Form` (geometry style label)
            public double Length;              // Meters
            public int Lane;                   // Lane count (0 if not applicable)
            public int Limit;                  // Speed limit (0 if not applicable)
            public double[][] Centerline;      // [n][2] of [x, y] in meters (may be null if geometry unavailable)
        }

        // Approximate intersection between 2+ distinct named roads. Computed
        // by bucketing road-segment endpoints into a coarse grid and looking
        // for cells where multiple road names converge. Coordinates are in
        // the recentered meters frame.
        internal class Intersection
        {
            public double X, Y;
            public string Quadrant;            // "NE" / "NW" / "SE" / "SW"
            public List<string> RoadNames = new List<string>();
        }

        internal class Footprint
        {
            public int TileCount;              // Total MapTile features parsed
            public int UnlockedCount;          // Subset with Unlocked = true
            public double MinX, MinY, MaxX, MaxY;
            public double WidthM, HeightM;     // Bounding box dimensions
            public bool HasGeometry;           // False when no MapTiles available — falls back to district bounds
        }

        internal static List<District> ParseDistricts(string geoJson)
        {
            var root = JObject.Parse(geoJson);
            var features = root["features"] as JArray;
            var districts = new List<District>();
            if (features == null) return districts;

            foreach (var feat in features)
            {
                // Carto emits MapTile + Surface alongside District in the
                // Area system file. Filter to District only.
                string objectType = (string)feat["properties"]?["Object"];
                if (!string.Equals(objectType, "District", StringComparison.Ordinal)) continue;

                string name = (string)feat["properties"]?["Name"];
                if (string.IsNullOrWhiteSpace(name)) continue;

                var coordsArray = feat["geometry"]?["coordinates"] as JArray;
                if (coordsArray == null || coordsArray.Count == 0) continue;

                // GeoJSON polygon: coordinates[0] = exterior ring (we ignore
                // holes for districts — CS2 doesn't paint donut shapes).
                var exterior = coordsArray[0] as JArray;
                if (exterior == null || exterior.Count < 3) continue;

                var verts = new double[exterior.Count][];
                for (int i = 0; i < exterior.Count; i++)
                {
                    var pt = exterior[i] as JArray;
                    if (pt == null || pt.Count < 2) continue;
                    verts[i] = new[]
                    {
                        (double)pt[0] * DegreesToMetersAtEquator,
                        (double)pt[1] * DegreesToMetersAtEquator,
                    };
                }

                var d = new District
                {
                    Name = name,
                    Slug = TextUtils.Slugify(name) ?? "district-" + districts.Count,
                    Polygon = verts,
                };
                ComputeGeometry(d);

                // Pull numeric Carto properties so the agent has Carto's own
                // takes on this district (resident counts, employee counts,
                // area, etc.) alongside the geometry we derived.
                foreach (var prop in (feat["properties"] as JObject)?.Properties() ?? Enumerable.Empty<JProperty>())
                {
                    if (prop.Name == "Name" || prop.Name == "Object") continue;
                    if (prop.Value.Type == JTokenType.Integer || prop.Value.Type == JTokenType.Float)
                    {
                        d.CartoProperties[prop.Name] = (double)prop.Value;
                    }
                }

                districts.Add(d);
            }

            return districts;
        }

        static void ComputeGeometry(District d)
        {
            double sumX = 0, sumY = 0;
            d.MinX = double.PositiveInfinity; d.MinY = double.PositiveInfinity;
            d.MaxX = double.NegativeInfinity; d.MaxY = double.NegativeInfinity;
            int n = 0;
            foreach (var p in d.Polygon)
            {
                if (p == null) continue;
                sumX += p[0]; sumY += p[1];
                if (p[0] < d.MinX) d.MinX = p[0];
                if (p[1] < d.MinY) d.MinY = p[1];
                if (p[0] > d.MaxX) d.MaxX = p[0];
                if (p[1] > d.MaxY) d.MaxY = p[1];
                n++;
            }
            if (n > 0)
            {
                d.CentroidX = sumX / n;
                d.CentroidY = sumY / n;
            }
            d.AreaM2 = PolygonArea(d.Polygon);
        }

        // Shoelace formula for polygon area, absolute value. Coordinates are
        // assumed planar (Carto's WGS84 default is close enough to planar at
        // CS2's map scale to be useful as a relative size — we report it as
        // "m²" knowing it's approximate when the player has stuck with the
        // default projection).
        static double PolygonArea(double[][] verts)
        {
            if (verts == null || verts.Length < 3) return 0;
            double sum = 0;
            for (int i = 0; i < verts.Length; i++)
            {
                var a = verts[i];
                var b = verts[(i + 1) % verts.Length];
                if (a == null || b == null) continue;
                sum += (a[0] * b[1]) - (b[0] * a[1]);
            }
            return Math.Abs(sum) * 0.5;
        }

        internal static void ComputeAdjacency(List<District> districts)
        {
            // Proximity-pair adjacency. For each district pair, count how many
            // vertices of A land within VertexAdjacencyToleranceMeters of any
            // vertex of B. Bbox-overlap check first to skip obviously non-
            // adjacent pairs cheaply.
            double tol = VertexAdjacencyToleranceMeters;
            double tolSq = tol * tol;

            for (int i = 0; i < districts.Count; i++)
            {
                for (int j = i + 1; j < districts.Count; j++)
                {
                    var a = districts[i];
                    var b = districts[j];
                    if (!BoundingBoxesOverlap(a, b, tol)) continue;

                    int closePairs = 0;
                    foreach (var va in a.Polygon)
                    {
                        if (va == null) continue;
                        foreach (var vb in b.Polygon)
                        {
                            if (vb == null) continue;
                            double dx = va[0] - vb[0];
                            double dy = va[1] - vb[1];
                            if (dx * dx + dy * dy <= tolSq)
                            {
                                closePairs++;
                                if (closePairs >= MinClosePairsForAdjacency) goto adjacent;
                                break;  // only count each va once
                            }
                        }
                    }
                    continue;

                adjacent:
                    a.Borders.Add(new DistrictBorder
                    {
                        Name = b.Name,
                        Slug = b.Slug,
                        Direction = BearingLabel(a.CentroidX, a.CentroidY, b.CentroidX, b.CentroidY),
                    });
                    b.Borders.Add(new DistrictBorder
                    {
                        Name = a.Name,
                        Slug = a.Slug,
                        Direction = BearingLabel(b.CentroidX, b.CentroidY, a.CentroidX, a.CentroidY),
                    });
                }
            }

            foreach (var d in districts)
            {
                d.Borders = d.Borders.OrderBy(b => b.Name, StringComparer.Ordinal).ToList();
            }
        }

        // Subtract a shared origin from every coordinate. Pure shift —
        // preserves distances, areas, adjacency. Centroids in output prose
        // become "(123, -456)" relative to the city's middle rather than
        // whatever absolute frame Carto happens to be emitting.
        //
        // Origin selection: prefer the mean of MapTile centroids (the actual
        // map footprint) over district centroids. A brand-new city has no
        // districts but does have MapTiles — without MapTile fallback the
        // recenter would no-op and the agent would see raw projected
        // coordinates. When neither is available we early-return; nothing to
        // recenter against.
        internal static void RecenterCoordinates(List<District> districts, List<Building> buildings)
            => RecenterCoordinates(districts, buildings, new List<MapTile>(), new List<Road>());

        internal static void RecenterCoordinates(List<District> districts, List<Building> buildings, List<MapTile> mapTiles, List<Road> roads)
        {
            double ox = 0, oy = 0;
            int n = 0;
            if (mapTiles != null && mapTiles.Count > 0)
            {
                foreach (var t in mapTiles) { ox += t.CentroidX; oy += t.CentroidY; n++; }
            }
            else if (districts != null && districts.Count > 0)
            {
                foreach (var d in districts) { ox += d.CentroidX; oy += d.CentroidY; n++; }
            }
            if (n == 0) return;
            ox /= n;
            oy /= n;

            foreach (var d in districts)
            {
                d.CentroidX -= ox;
                d.CentroidY -= oy;
                d.MinX -= ox; d.MaxX -= ox;
                d.MinY -= oy; d.MaxY -= oy;
                foreach (var v in d.Polygon)
                {
                    if (v == null) continue;
                    v[0] -= ox;
                    v[1] -= oy;
                }
            }
            foreach (var b in buildings)
            {
                b.CentroidX -= ox;
                b.CentroidY -= oy;
            }
            if (mapTiles != null)
            {
                foreach (var t in mapTiles)
                {
                    t.CentroidX -= ox;
                    t.CentroidY -= oy;
                    t.MinX -= ox; t.MaxX -= ox;
                    t.MinY -= oy; t.MaxY -= oy;
                    if (t.Polygon != null)
                    {
                        foreach (var v in t.Polygon)
                        {
                            if (v == null) continue;
                            v[0] -= ox;
                            v[1] -= oy;
                        }
                    }
                }
            }
            if (roads != null)
            {
                foreach (var r in roads)
                {
                    if (r.Centerline == null) continue;
                    foreach (var v in r.Centerline)
                    {
                        if (v == null) continue;
                        v[0] -= ox;
                        v[1] -= oy;
                    }
                }
            }
        }

        static bool BoundingBoxesOverlap(District a, District b, double inflate)
        {
            return a.MaxX + inflate >= b.MinX
                && b.MaxX + inflate >= a.MinX
                && a.MaxY + inflate >= b.MinY
                && b.MaxY + inflate >= a.MinY;
        }

        internal static List<Building> ParseNamedBuildings(string geoJson)
        {
            // Only buildings with a non-empty Name get a chunk. CS2's
            // CustomName component drives Carto's Name output, so this
            // catches both player-renamed buildings and CS2's auto-named
            // civic / service / landmark buildings. Generic residential
            // and commercial buildings have empty Name and are dropped —
            // they aren't story-relevant.
            var root = JObject.Parse(geoJson);
            var features = root["features"] as JArray;
            var buildings = new List<Building>();
            if (features == null) return buildings;

            foreach (var feat in features)
            {
                string objectType = (string)feat["properties"]?["Object"];
                if (string.Equals(objectType, "Extractor", StringComparison.Ordinal)
                    || string.Equals(objectType, "Landfill", StringComparison.Ordinal))
                {
                    // Specialized industrial buildings get the same treatment
                    // as regular buildings. Keep going.
                }
                else if (!string.Equals(objectType, "Building", StringComparison.Ordinal))
                {
                    continue;
                }

                string name = (string)feat["properties"]?["Name"];
                if (string.IsNullOrWhiteSpace(name)) continue;

                var coordsArray = feat["geometry"]?["coordinates"] as JArray;
                if (coordsArray == null || coordsArray.Count == 0) continue;
                var exterior = coordsArray[0] as JArray;
                if (exterior == null || exterior.Count < 3) continue;

                // Compute centroid in the meters frame directly — buildings
                // don't need polygon retention, just a single anchor point.
                double sumX = 0, sumY = 0;
                int n = 0;
                foreach (var v in exterior)
                {
                    var pt = v as JArray;
                    if (pt == null || pt.Count < 2) continue;
                    sumX += (double)pt[0] * DegreesToMetersAtEquator;
                    sumY += (double)pt[1] * DegreesToMetersAtEquator;
                    n++;
                }
                if (n == 0) continue;

                var b = new Building
                {
                    Name = name,
                    CentroidX = sumX / n,
                    CentroidY = sumY / n,
                    Category = (string)feat["properties"]?["Category"],
                };

                foreach (var prop in (feat["properties"] as JObject)?.Properties() ?? Enumerable.Empty<JProperty>())
                {
                    if (prop.Name == "Name" || prop.Name == "Object" || prop.Name == "Category") continue;
                    if (prop.Value.Type == JTokenType.Integer || prop.Value.Type == JTokenType.Float)
                    {
                        b.CartoProperties[prop.Name] = (double)prop.Value;
                    }
                }

                buildings.Add(b);
            }

            return buildings;
        }

        internal static void AssignBuildingsToDistricts(List<Building> buildings, List<District> districts)
        {
            foreach (var b in buildings)
            {
                foreach (var d in districts)
                {
                    // Bbox pre-filter — cheap rejection for the common case
                    // where most buildings aren't in most districts.
                    if (b.CentroidX < d.MinX || b.CentroidX > d.MaxX) continue;
                    if (b.CentroidY < d.MinY || b.CentroidY > d.MaxY) continue;
                    if (!PointInPolygon(b.CentroidX, b.CentroidY, d.Polygon)) continue;
                    b.DistrictSlug = d.Slug;
                    d.Buildings.Add(b);
                    break;
                }
            }

            foreach (var d in districts)
            {
                d.Buildings = d.Buildings.OrderBy(b => b.Name, StringComparer.Ordinal).ToList();
            }
        }

        // Standard ray-casting point-in-polygon for a simple (non-self-
        // intersecting) polygon. Treats the polygon's exterior ring; we
        // don't handle holes because CS2 districts don't have them.
        internal static bool PointInPolygon(double x, double y, double[][] verts)
        {
            if (verts == null || verts.Length < 3) return false;
            bool inside = false;
            int j = verts.Length - 1;
            for (int i = 0; i < verts.Length; i++)
            {
                var a = verts[i];
                var b = verts[j];
                if (a == null || b == null) { j = i; continue; }
                bool intersects = (a[1] > y) != (b[1] > y)
                    && x < (b[0] - a[0]) * (y - a[1]) / (b[1] - a[1] + double.Epsilon) + a[0];
                if (intersects) inside = !inside;
                j = i;
            }
            return inside;
        }

        // Quadrant label for a point in the recentered meters frame. +x is
        // east, +y is north (same axis convention as BearingLabel). Used to
        // give the agent a coarse "where on the map" answer for roads and
        // un-districted buildings without making it parse coordinates.
        internal static string QuadrantOfPoint(double x, double y)
        {
            bool north = y >= 0;
            bool east = x >= 0;
            return (north ? "N" : "S") + (east ? "E" : "W");
        }

        // Mean of every vertex across every segment in a named-road group.
        // Returns (0, 0, false) when the group has no usable geometry.
        internal static (double x, double y, bool hasGeometry) RoadGroupCentroid(IEnumerable<Road> segments)
        {
            double sx = 0, sy = 0;
            int n = 0;
            foreach (var r in segments)
            {
                if (r?.Centerline == null) continue;
                foreach (var v in r.Centerline)
                {
                    if (v == null) continue;
                    sx += v[0];
                    sy += v[1];
                    n++;
                }
            }
            if (n == 0) return (0, 0, false);
            return (sx / n, sy / n, true);
        }

        // Approximate intersections of named roads. Strategy: bucket every
        // segment endpoint into a coarse grid (cellSizeM meters per cell)
        // and report cells where ≥2 distinct named roads converge. This
        // catches CS2 road-network nodes (where Carto splits a road into
        // segments) at real interchanges; it also folds in two roads that
        // happen to end within the same grid cell, which is fine for a
        // storytelling map.
        //
        // Endpoints only — not intermediate centerline vertices — because
        // CS2's road graph is built from edges whose endpoints sit on
        // network nodes. The middle vertices are smoothing samples.
        internal static List<Intersection> ComputeIntersections(List<Road> roads, double cellSizeM = 50.0)
        {
            var grid = new Dictionary<(int cx, int cy), (double sx, double sy, int n, HashSet<string> names)>();
            foreach (var r in roads)
            {
                if (r == null || string.IsNullOrWhiteSpace(r.Name)) continue;
                if (r.Centerline == null || r.Centerline.Length == 0) continue;
                var endpoints = new[] { r.Centerline[0], r.Centerline[r.Centerline.Length - 1] };
                foreach (var v in endpoints)
                {
                    if (v == null) continue;
                    int cx = (int)Math.Floor(v[0] / cellSizeM);
                    int cy = (int)Math.Floor(v[1] / cellSizeM);
                    var key = (cx, cy);
                    if (!grid.TryGetValue(key, out var entry))
                    {
                        entry = (0, 0, 0, new HashSet<string>(StringComparer.Ordinal));
                    }
                    entry.sx += v[0];
                    entry.sy += v[1];
                    entry.n++;
                    entry.names.Add(r.Name);
                    grid[key] = entry;
                }
            }

            var result = new List<Intersection>();
            foreach (var kv in grid)
            {
                if (kv.Value.names.Count < 2) continue;
                double x = kv.Value.sx / kv.Value.n;
                double y = kv.Value.sy / kv.Value.n;
                result.Add(new Intersection
                {
                    X = x,
                    Y = y,
                    Quadrant = QuadrantOfPoint(x, y),
                    RoadNames = kv.Value.names.OrderBy(n => n, StringComparer.Ordinal).ToList(),
                });
            }

            // Stable ordering: quadrant, then north→south, then west→east.
            // Within a quadrant the agent often wants to enumerate from one
            // corner inward, so y-desc / x-asc reads naturally.
            return result
                .OrderBy(i => i.Quadrant, StringComparer.Ordinal)
                .ThenByDescending(i => i.Y)
                .ThenBy(i => i.X)
                .ToList();
        }

        // Compass label for the bearing from (fromX,fromY) to (toX,toY).
        // Y is treated as north-positive — matches both WGS84 latitude and
        // CS2's z axis (which Carto maps onto y in its projection).
        internal static string BearingLabel(double fromX, double fromY, double toX, double toY)
        {
            double dx = toX - fromX;
            double dy = toY - fromY;
            if (Math.Abs(dx) < 1e-9 && Math.Abs(dy) < 1e-9) return "adjacent";
            double angle = Math.Atan2(dy, dx) * 180.0 / Math.PI;  // 0 = +x (east), 90 = +y (north)
            if (angle < 0) angle += 360.0;
            string[] labels = { "east", "north-east", "north", "north-west", "west", "south-west", "south", "south-east" };
            int idx = (int)Math.Round(angle / 45.0) % 8;
            return labels[idx];
        }

        internal static string RenderDistrictMarkdown(District d)
        {
            var sb = new StringBuilder();
            var ci = CultureInfo.InvariantCulture;
            sb.AppendLine($"# {d.Name}");
            sb.AppendLine();
            sb.AppendLine("| | Value |");
            sb.AppendLine("|---|---|");
            sb.AppendLine($"| Slug | `{d.Slug}` |");
            sb.AppendLine($"| Centroid | ({d.CentroidX.ToString("F2", ci)}, {d.CentroidY.ToString("F2", ci)}) |");
            sb.AppendLine($"| Bounding box | ({d.MinX.ToString("F2", ci)}, {d.MinY.ToString("F2", ci)}) → ({d.MaxX.ToString("F2", ci)}, {d.MaxY.ToString("F2", ci)}) |");
            sb.AppendLine($"| Area | {d.AreaM2.ToString("N0", ci)} m² |");

            foreach (var prop in d.CartoProperties.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                sb.AppendLine($"| Carto {prop.Key} | {prop.Value.ToString("N2", ci)} |");
            }
            sb.AppendLine();

            sb.AppendLine("## Borders");
            sb.AppendLine();
            if (d.Borders.Count == 0)
            {
                sb.AppendLine("- (no adjacent districts detected)");
            }
            else
            {
                foreach (var b in d.Borders)
                {
                    sb.AppendLine($"- **{b.Name}** (`{b.Slug}`) — to the {b.Direction}");
                }
            }
            sb.AppendLine();

            sb.AppendLine("## Named buildings");
            sb.AppendLine();
            if (d.Buildings.Count == 0)
            {
                sb.AppendLine("- (none in this district)");
            }
            else
            {
                // Carto's Name field is set for ALL placed buildings — generic
                // zoned ones like "Low Density Offices" show up dozens of
                // times. Dedupe by name: one bullet per unique name, with a
                // " (× N)" annotation when there are duplicates and aggregate
                // employee/resident totals. Civic buildings (unique names)
                // come through individually as before.
                var grouped = d.Buildings
                    .GroupBy(b => b.Name, StringComparer.Ordinal)
                    .OrderBy(g => g.Key, StringComparer.Ordinal);
                foreach (var group in grouped)
                {
                    int count = group.Count();
                    string sample = group.First().Category;
                    double totalEmp = group.Sum(b => b.CartoProperties.TryGetValue("Employee", out double e) ? e : 0);
                    double totalRes = group.Sum(b => b.CartoProperties.TryGetValue("Resident", out double r) ? r : 0);

                    sb.Append($"- **{group.Key}**");
                    if (count > 1) sb.Append($" (× {count})");
                    if (!string.IsNullOrEmpty(sample)) sb.Append($" — {sample}");
                    var bits = new List<string>();
                    if (totalEmp > 0) bits.Add($"{totalEmp.ToString("N0", ci)} employees");
                    if (totalRes > 0) bits.Add($"{totalRes.ToString("N0", ci)} residents");
                    if (bits.Count > 0) sb.Append($" ({string.Join(", ", bits)})");
                    sb.AppendLine();
                }
            }

            return sb.ToString();
        }

        internal static string RenderIndexMarkdown(List<District> districts, List<Building> allBuildings)
            => RenderIndexMarkdown(districts, allBuildings, new List<Road>(), null, null, null);

        internal static string RenderIndexMarkdown(List<District> districts, List<Building> allBuildings, List<Road> roads, Footprint footprint)
            => RenderIndexMarkdown(districts, allBuildings, roads, footprint, null, null);

        internal static string RenderIndexMarkdown(List<District> districts, List<Building> allBuildings, List<Road> roads, Footprint footprint, RasterSummary elevation, RasterSummary water)
        {
            var sb = new StringBuilder();
            var ci = CultureInfo.InvariantCulture;
            sb.AppendLine("# City spatial index");
            sb.AppendLine();
            sb.AppendLine($"_Generated by CityStoryMod at {DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", ci)} from Carto's GeoJSON output._");
            sb.AppendLine();
            sb.AppendLine("Per-district detail lives at `processed/districts/<slug>.md`. Road network detail (when present) lives at `processed/roads.md`. Read this file once per session for the lay of the land, drill into individual chunks as needed.");
            sb.AppendLine();

            if (footprint != null && footprint.HasGeometry)
            {
                sb.AppendLine("## Map footprint");
                sb.AppendLine();
                sb.AppendLine($"- Map tiles: {footprint.TileCount} total, {footprint.UnlockedCount} unlocked");
                sb.AppendLine($"- Footprint: ({footprint.MinX.ToString("F0", ci)}, {footprint.MinY.ToString("F0", ci)}) → ({footprint.MaxX.ToString("F0", ci)}, {footprint.MaxY.ToString("F0", ci)}) — {(footprint.WidthM / 1000.0).ToString("F2", ci)} km × {(footprint.HeightM / 1000.0).ToString("F2", ci)} km");
                sb.AppendLine();
            }

            sb.AppendLine("## Districts");
            sb.AppendLine();
            if (districts.Count == 0)
            {
                sb.AppendLine("- (none detected)");
            }
            else
            {
                sb.AppendLine("| District | Slug | Area (m²) | Centroid |");
                sb.AppendLine("|---|---|---|---|");
                foreach (var d in districts.OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    sb.AppendLine($"| {d.Name} | `{d.Slug}` | {d.AreaM2.ToString("N0", ci)} | ({d.CentroidX.ToString("F2", ci)}, {d.CentroidY.ToString("F2", ci)}) |");
                }
            }
            sb.AppendLine();

            sb.AppendLine("## Adjacency");
            sb.AppendLine();
            bool anyBorders = false;
            foreach (var d in districts.OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                if (d.Borders.Count == 0) continue;
                anyBorders = true;
                string list = string.Join(", ", d.Borders.Select(b => $"{b.Name} ({b.Direction})"));
                sb.AppendLine($"- **{d.Name}** ↔ {list}");
            }
            if (!anyBorders)
            {
                sb.AppendLine("- (no adjacencies detected — districts may be disconnected or geometry too sparse)");
            }
            sb.AppendLine();

            // Road network summary. Detail lives in roads.md — this section
            // is for the at-a-glance read of "how built-out is the city."
            if (roads != null && roads.Count > 0)
            {
                sb.AppendLine("## Road network");
                sb.AppendLine();
                double totalLengthM = roads.Sum(r => r.Length);
                int namedCount = roads.Count(r => !string.IsNullOrWhiteSpace(r.Name));
                sb.AppendLine($"{roads.Count} segment(s) total, {namedCount} named. Combined length {(totalLengthM / 1000.0).ToString("F2", ci)} km. See `processed/roads.md` for detail.");
                sb.AppendLine();
            }

            // Terrain summary. Single-line teaser; full histogram in elevation.md.
            if (elevation != null)
            {
                sb.AppendLine("## Terrain");
                sb.AppendLine();
                sb.AppendLine($"Elevation ranges across {(elevation.Max - elevation.Min)} m on a {elevation.Width}×{elevation.Height} grid (mean {elevation.Mean.ToString("F0", ci)} m). See `processed/elevation.md` for the full breakdown.");
                sb.AppendLine();
            }

            // Water summary. Single-line teaser; detail in water.md.
            if (water != null)
            {
                sb.AppendLine("## Water");
                sb.AppendLine();
                sb.Append($"{water.WaterCells:N0} water cell(s) — {water.PercentWater.ToString("F1", ci)}% of the map. Maximum depth {water.Max} m.");
                if (water.CoastlineLengthM > 0)
                    sb.Append($" Shoreline ≈ {(water.CoastlineLengthM / 1000.0).ToString("F1", ci)} km (complexity {water.ShorelineRatio.ToString("F1", ci)}).");
                sb.AppendLine(" See `processed/water.md` for detail.");
                sb.AppendLine();
            }

            // Named-buildings summary. Counts per district + a list of any
            // that fell outside every district polygon (typically buildings
            // on unzoned/un-districted land — worth surfacing because they
            // still show up in the player's city).
            sb.AppendLine("## Named buildings");
            sb.AppendLine();
            int assigned = allBuildings.Count(b => b.DistrictSlug != null);
            int unassigned = allBuildings.Count - assigned;
            sb.AppendLine($"{allBuildings.Count} named building(s) total — {assigned} in a district, {unassigned} on un-districted land.");
            sb.AppendLine();
            if (unassigned > 0)
            {
                sb.AppendLine("### Outside any district");
                sb.AppendLine();
                // Dedupe: CS2 maps come pre-populated with named landscape
                // features (Cairn 01, Stone Monument 02, etc.) that repeat
                // many times across the map. Listing each individually
                // wastes a lot of tokens on a fresh city. Group by name +
                // category and annotate with a count.
                var grouped = allBuildings
                    .Where(b => b.DistrictSlug == null)
                    .GroupBy(b => b.Name, StringComparer.Ordinal)
                    .OrderBy(g => g.Key, StringComparer.Ordinal);
                foreach (var group in grouped)
                {
                    int n = group.Count();
                    string sampleCat = group.Select(b => b.Category).FirstOrDefault(c => !string.IsNullOrEmpty(c));
                    string cat = string.IsNullOrEmpty(sampleCat) ? "" : $" — {sampleCat}";
                    string countTag = n > 1 ? $" (× {n})" : "";
                    string locationTag;
                    if (n == 1)
                    {
                        var only = group.First();
                        locationTag = $" in the {QuadrantOfPoint(only.CentroidX, only.CentroidY)}";
                    }
                    else
                    {
                        // Multiple instances of the same name (e.g. "Cairn 02 × 5")
                        // are usually scattered. Report quadrant counts if any quadrant
                        // holds them all, else label "scattered."
                        var byQuad = group
                            .GroupBy(b => QuadrantOfPoint(b.CentroidX, b.CentroidY), StringComparer.Ordinal)
                            .OrderBy(g => g.Key, StringComparer.Ordinal)
                            .ToList();
                        if (byQuad.Count == 1)
                        {
                            locationTag = $" in the {byQuad[0].Key}";
                        }
                        else
                        {
                            string mix = string.Join(", ", byQuad.Select(g => $"{g.Key} × {g.Count()}"));
                            locationTag = $" scattered ({mix})";
                        }
                    }
                    sb.AppendLine($"- **{group.Key}**{countTag}{cat}{locationTag}");
                }
            }

            return sb.ToString();
        }

        // MapTile features ship inside the Area_Boundary.json file alongside
        // District and Surface features. We pull them separately so the
        // index.md can describe the city's footprint — biggest gain is at
        // t=0, where a brand-new city has zero districts but the MapTiles
        // describe where the player can build at all.
        internal static List<MapTile> ParseMapTiles(string geoJson)
        {
            var root = JObject.Parse(geoJson);
            var features = root["features"] as JArray;
            var tiles = new List<MapTile>();
            if (features == null) return tiles;

            foreach (var feat in features)
            {
                string objectType = (string)feat["properties"]?["Object"];
                if (!string.Equals(objectType, "MapTile", StringComparison.Ordinal)) continue;

                var coordsArray = feat["geometry"]?["coordinates"] as JArray;
                if (coordsArray == null || coordsArray.Count == 0) continue;
                var exterior = coordsArray[0] as JArray;
                if (exterior == null || exterior.Count < 3) continue;

                var verts = new double[exterior.Count][];
                for (int i = 0; i < exterior.Count; i++)
                {
                    var pt = exterior[i] as JArray;
                    if (pt == null || pt.Count < 2) continue;
                    verts[i] = new[]
                    {
                        (double)pt[0] * DegreesToMetersAtEquator,
                        (double)pt[1] * DegreesToMetersAtEquator,
                    };
                }

                var t = new MapTile { Polygon = verts };
                ComputeTileGeometry(t);

                // Carto's Unlocked property is the only one we care about
                // for footprint — anything else stays in the raw GeoJSON for
                // future use without expanding our domain model.
                var unlocked = feat["properties"]?["Unlocked"];
                if (unlocked != null)
                {
                    if (unlocked.Type == JTokenType.Boolean) t.Unlocked = (bool)unlocked;
                    else if (unlocked.Type == JTokenType.Integer) t.Unlocked = (long)unlocked != 0;
                    else if (unlocked.Type == JTokenType.String) bool.TryParse((string)unlocked, out t.Unlocked);
                }

                tiles.Add(t);
            }
            return tiles;
        }

        static void ComputeTileGeometry(MapTile t)
        {
            double sumX = 0, sumY = 0;
            t.MinX = double.PositiveInfinity; t.MinY = double.PositiveInfinity;
            t.MaxX = double.NegativeInfinity; t.MaxY = double.NegativeInfinity;
            int n = 0;
            foreach (var p in t.Polygon)
            {
                if (p == null) continue;
                sumX += p[0]; sumY += p[1];
                if (p[0] < t.MinX) t.MinX = p[0];
                if (p[1] < t.MinY) t.MinY = p[1];
                if (p[0] > t.MaxX) t.MaxX = p[0];
                if (p[1] > t.MaxY) t.MaxY = p[1];
                n++;
            }
            if (n > 0)
            {
                t.CentroidX = sumX / n;
                t.CentroidY = sumY / n;
            }
            t.AreaM2 = PolygonArea(t.Polygon);
        }

        // Footprint covers ALL map tiles, not just unlocked ones, so the
        // agent can describe both "the world the city sits inside" and "the
        // current playable area." Falls back to district bounds when no
        // MapTiles came through (older Carto exports without MapTile in the
        // Feature mask).
        internal static Footprint ComputeFootprint(List<MapTile> mapTiles, List<District> districts)
        {
            var fp = new Footprint();
            if (mapTiles != null && mapTiles.Count > 0)
            {
                fp.TileCount = mapTiles.Count;
                fp.UnlockedCount = mapTiles.Count(t => t.Unlocked);
                fp.MinX = double.PositiveInfinity; fp.MinY = double.PositiveInfinity;
                fp.MaxX = double.NegativeInfinity; fp.MaxY = double.NegativeInfinity;
                foreach (var t in mapTiles)
                {
                    if (t.MinX < fp.MinX) fp.MinX = t.MinX;
                    if (t.MinY < fp.MinY) fp.MinY = t.MinY;
                    if (t.MaxX > fp.MaxX) fp.MaxX = t.MaxX;
                    if (t.MaxY > fp.MaxY) fp.MaxY = t.MaxY;
                }
                fp.WidthM = fp.MaxX - fp.MinX;
                fp.HeightM = fp.MaxY - fp.MinY;
                fp.HasGeometry = true;
                return fp;
            }
            if (districts != null && districts.Count > 0)
            {
                fp.MinX = double.PositiveInfinity; fp.MinY = double.PositiveInfinity;
                fp.MaxX = double.NegativeInfinity; fp.MaxY = double.NegativeInfinity;
                foreach (var d in districts)
                {
                    if (d.MinX < fp.MinX) fp.MinX = d.MinX;
                    if (d.MinY < fp.MinY) fp.MinY = d.MinY;
                    if (d.MaxX > fp.MaxX) fp.MaxX = d.MaxX;
                    if (d.MaxY > fp.MaxY) fp.MaxY = d.MaxY;
                }
                fp.WidthM = fp.MaxX - fp.MinX;
                fp.HeightM = fp.MaxY - fp.MinY;
                fp.HasGeometry = true;
                return fp;
            }
            return fp;
        }

        // Parses Network_Centerline.json. Each Carto Network feature is a
        // LineString of one road/track/pathway/waterway segment with attached
        // properties (Name, Object, Category, Form, Length, Lane, Limit).
        // Roads are not split or merged here — we surface them as-is so the
        // agent can group by Name itself (a "Riverside Highway" composed of
        // many segments appears once per segment, all sharing that name).
        internal static List<Road> ParseRoads(string geoJson)
        {
            var root = JObject.Parse(geoJson);
            var features = root["features"] as JArray;
            var roads = new List<Road>();
            if (features == null) return roads;

            foreach (var feat in features)
            {
                var props = feat["properties"] as JObject;
                string objectType = (string)props?["Object"];

                var geometry = feat["geometry"] as JObject;
                string geomType = (string)geometry?["type"];
                double[][] line = null;
                if (string.Equals(geomType, "LineString", StringComparison.Ordinal))
                {
                    var coords = geometry["coordinates"] as JArray;
                    line = ProjectLineString(coords);
                }
                else if (string.Equals(geomType, "MultiLineString", StringComparison.Ordinal))
                {
                    // Flatten — for now we only need the geometry for recentering,
                    // not for rendering. Concatenating the parts keeps the points
                    // in the same projection without inventing connecting segments.
                    var multi = geometry["coordinates"] as JArray;
                    var flat = new List<double[]>();
                    if (multi != null)
                    {
                        foreach (var part in multi)
                        {
                            var partLine = ProjectLineString(part as JArray);
                            if (partLine != null) flat.AddRange(partLine);
                        }
                    }
                    if (flat.Count > 0) line = flat.ToArray();
                }

                var road = new Road
                {
                    Name = (string)props?["Name"],
                    Object = objectType,
                    Category = (string)props?["Category"],
                    Form = (string)props?["Form"],
                    Length = ReadDouble(props, "Length"),
                    Lane = (int)ReadDouble(props, "Lane"),
                    Limit = (int)ReadDouble(props, "Limit"),
                    Centerline = line,
                };
                roads.Add(road);
            }
            return roads;
        }

        static double[][] ProjectLineString(JArray coords)
        {
            if (coords == null || coords.Count == 0) return null;
            var verts = new double[coords.Count][];
            for (int i = 0; i < coords.Count; i++)
            {
                var pt = coords[i] as JArray;
                if (pt == null || pt.Count < 2) continue;
                verts[i] = new[]
                {
                    (double)pt[0] * DegreesToMetersAtEquator,
                    (double)pt[1] * DegreesToMetersAtEquator,
                };
            }
            return verts;
        }

        static double ReadDouble(JObject props, string key)
        {
            if (props == null) return 0;
            var token = props[key];
            if (token == null) return 0;
            if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer) return (double)token;
            if (token.Type == JTokenType.String && double.TryParse((string)token, System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture, out double v)) return v;
            return 0;
        }

        // Safe wrapper around GeoTiffReader.Read — returns null when the
        // file is missing or the reader throws, instead of propagating the
        // exception. Used by Process() to load both rasters once and share
        // them between the summary writers and the combined-map renderer
        // without paying a double-read penalty (each grid is ~64 MB).
        internal static GeoTiffReader.Grid SafeReadTiff(string path)
        {
            try { return GeoTiffReader.Read(path); }
            catch { return null; }
        }

        // Read Carto's Elevation.tif, compute stats + a quadrant-aware
        // reading, write elevation.md. Pixel values are meters above the
        // map's internal floor (Carto's denormalization drops bounds.min —
        // see RasterSystem.DenormalizeToShort), so absolute height is
        // arbitrary. The storytelling-useful signals are: how much the
        // terrain VARIES (stdev), and WHERE the extremes sit (quadrant).
        // Returns null if the file isn't there or the reader can't parse
        // it; null means "no chunk emitted."
        internal static RasterSummary TryProcessElevation(string cartoDir, string processedDir)
        {
            GeoTiffReader.Grid grid = SafeReadTiff(Path.Combine(cartoDir, "GeoTIFF", "Elevation.tif"));
            if (grid == null) return null;
            var summary = ComputeElevationSummary(grid);
            if (summary == null) return null;
            File.WriteAllText(Path.Combine(processedDir, "elevation.md"), RenderElevationMarkdown(summary));
            return summary;
        }

        // Pure-function compute step factored out of TryProcessElevation
        // so the combined-map renderer can reuse the same numbers without
        // reading the TIFF a second time.
        internal static RasterSummary ComputeElevationSummary(GeoTiffReader.Grid grid)
        {
            // Single pass: stats + argmax/argmin (for quadrant labels).
            int min = int.MaxValue, max = int.MinValue;
            long argMaxIdx = -1, argMinIdx = -1;
            double sum = 0;
            long count = 0;
            int[] pixels = grid.Pixels;
            int nodata = grid.NoData;
            for (int i = 0; i < pixels.Length; i++)
            {
                int v = pixels[i];
                if (v == nodata) continue;
                if (v < min) { min = v; argMinIdx = i; }
                if (v > max) { max = v; argMaxIdx = i; }
                sum += v;
                count++;
            }
            if (count == 0) return null;
            double mean = sum / count;

            // Second pass for stdev.
            double sqDev = 0;
            for (int i = 0; i < pixels.Length; i++)
            {
                int v = pixels[i];
                if (v == nodata) continue;
                double d = v - mean;
                sqDev += d * d;
            }
            double stdev = Math.Sqrt(sqDev / count);

            return new RasterSummary
            {
                Width = grid.Width,
                Height = grid.Height,
                Pixels = count,
                Min = min,
                Max = max,
                Mean = mean,
                StdDev = stdev,
                HighQuadrant = QuadrantOf(argMaxIdx, grid.Width, grid.Height),
                LowQuadrant = QuadrantOf(argMinIdx, grid.Width, grid.Height),
                Reading = ClassifyTerrain(stdev, max - min, mean),
            };
        }

        // Map a row-major pixel index to a compass quadrant label. TIFF row 0
        // is north (Carto writes high-Z rows first), and column 0 is west, so
        // top-half = north, right-half = east.
        internal static string QuadrantOf(long idx, int width, int height)
        {
            if (idx < 0 || width <= 0 || height <= 0) return null;
            int row = (int)(idx / width);
            int col = (int)(idx % width);
            bool north = row < height / 2;
            bool east = col >= width / 2;
            return (north ? "N" : "S") + (east ? "E" : "W");
        }

        // English label for terrain character. Driven primarily by stdev
        // (how much the surface varies on average), with relief (max-min)
        // as a tiebreaker for the edge case of a flat plain with one
        // mountain — stdev stays low because the mountain is a small
        // fraction of pixels, but relief is large.
        //
        // Thresholds are eyeballed against a small set of CS2 maps; tune as
        // counterexamples appear. Glenville (stdev 28 m, relief 191 m,
        // mean 96 m) lands in "mostly flat" — matches what the player sees.
        internal static string ClassifyTerrain(double stdev, double relief, double mean)
        {
            string baseLabel;
            bool flatish;
            if (stdev < 30) { baseLabel = "Mostly flat"; flatish = true; }
            else if (stdev < 70) { baseLabel = "Gently rolling"; flatish = true; }
            else if (stdev < 150) { baseLabel = "Hilly"; flatish = false; }
            else { baseLabel = "Rugged / mountainous"; flatish = false; }

            // The "localized high point" suffix only earns its keep when the
            // base label is flatish — it tells the reader "the bulk of the
            // map is flat, but one corner spikes." When the base label is
            // already Hilly / Rugged, an outlier peak is implied by the
            // category and adding the suffix reads as redundant ("Hilly,
            // with a localized high point" → of course there are high
            // points, it's hilly). Threshold: max-min > 5× stdev means the
            // extreme pixel is well outside the bulk distribution.
            if (flatish && stdev > 0 && relief > 5 * stdev) baseLabel += ", with a localized high point";
            return baseLabel + ".";
        }

        // Read Carto's Depth.tif, count water cells with per-quadrant
        // breakdown, write water.md. Land cells are -32768 (NoData); water
        // cells are positive integers (depth in meters below sea level).
        internal static RasterSummary TryProcessWater(string cartoDir, string processedDir)
        {
            GeoTiffReader.Grid grid = SafeReadTiff(Path.Combine(cartoDir, "GeoTIFF", "Depth.tif"));
            if (grid == null) return null;
            var summary = ComputeWaterSummary(grid);
            if (summary == null) return null;
            File.WriteAllText(Path.Combine(processedDir, "water.md"), RenderWaterMarkdown(summary));
            return summary;
        }

        // Pure-function compute step factored out of TryProcessWater so
        // the combined-map renderer can reuse the depth-grid stats without
        // re-reading the TIFF.
        internal static RasterSummary ComputeWaterSummary(GeoTiffReader.Grid grid)
        {
            int max = 0;
            long argMaxIdx = -1;
            double sum = 0;
            long waterCells = 0;
            long coastlineCells = 0;
            // [NW, NE, SW, SE] water and coastline cell counts.
            long[] qCounts = new long[4];
            long[] qTotals = new long[4];
            long[] qCoast = new long[4];
            int[] pixels = grid.Pixels;
            int nodata = grid.NoData;
            int w = grid.Width, h = grid.Height;
            int halfW = w / 2, halfH = h / 2;
            for (int i = 0; i < pixels.Length; i++)
            {
                int row = i / w;
                int col = i % w;
                bool north = row < halfH;
                bool east = col >= halfW;
                int qIdx = (north ? 0 : 2) + (east ? 1 : 0);
                qTotals[qIdx]++;

                int v = pixels[i];
                if (v == nodata || v <= 0) continue;
                if (v > max) { max = v; argMaxIdx = i; }
                sum += v;
                waterCells++;
                qCounts[qIdx]++;

                // Coastline: this is a water cell; if any of its 4-neighbors
                // is out-of-bounds or land (NoData or <= 0), it's on the
                // shore. Map-edge water cells are treated as coastline too —
                // the lake / sea ends at the map boundary from the player's
                // perspective.
                bool onCoast = IsLandOrOutOfBounds(pixels, nodata, w, h, row - 1, col)
                            || IsLandOrOutOfBounds(pixels, nodata, w, h, row + 1, col)
                            || IsLandOrOutOfBounds(pixels, nodata, w, h, row, col - 1)
                            || IsLandOrOutOfBounds(pixels, nodata, w, h, row, col + 1);
                if (onCoast)
                {
                    coastlineCells++;
                    qCoast[qIdx]++;
                }
            }
            long totalCells = (long)w * h;
            double percent = totalCells == 0 ? 0 : (100.0 * waterCells / totalCells);
            double meanDepth = waterCells == 0 ? 0 : sum / waterCells;

            double[] qPercent = new double[4];
            double[] qCoastPercent = new double[4];
            for (int i = 0; i < 4; i++)
            {
                qPercent[i] = qTotals[i] == 0 ? 0 : 100.0 * qCounts[i] / qTotals[i];
                qCoastPercent[i] = coastlineCells == 0 ? 0 : 100.0 * qCoast[i] / coastlineCells;
            }

            // Length: each coastline cell contributes one "edge" of its
            // pixel-side to the shore. Approximating each cell as one
            // pixel-side underestimates slightly (a cell with two land
            // neighbors really has two shore edges) but is honest enough
            // for an order-of-magnitude figure. Use the mean of scaleX and
            // scaleY when both are known; fall back to 0 (rendered as
            // "unknown") when the TIFF didn't carry pixel scale.
            double pixelM = 0;
            if (grid.ScaleX > 0 && grid.ScaleY > 0) pixelM = (grid.ScaleX + grid.ScaleY) / 2.0;
            else if (grid.ScaleX > 0) pixelM = grid.ScaleX;
            else if (grid.ScaleY > 0) pixelM = grid.ScaleY;
            double coastlineLengthM = coastlineCells * pixelM;

            // Shoreline complexity. A circular lake has coast-cell-count
            // proportional to its circumference; this ratio normalizes by
            // the circumference of a circle with the same area. Values near
            // 1 = round, smooth basin. Values >> 1 = convoluted / fragmented.
            double shorelineRatio = waterCells > 0
                ? coastlineCells / Math.Sqrt(waterCells * Math.PI)
                : 0;

            var summary = new RasterSummary
            {
                Width = w,
                Height = h,
                Pixels = totalCells,
                Min = 0,
                Max = max,
                Mean = meanDepth,
                WaterCells = waterCells,
                PercentWater = percent,
                WaterQuadrantPercent = qPercent,
                CoastlineCells = coastlineCells,
                CoastlineLengthM = coastlineLengthM,
                CoastlineQuadrantPercent = qCoastPercent,
                ShorelineRatio = shorelineRatio,
                HighQuadrant = QuadrantOf(argMaxIdx, w, h),  // deepest cell's quadrant
                Reading = ClassifyWater(percent, qPercent, shorelineRatio),
            };
            return summary;
        }

        // True when (row, col) is outside the grid or is a land cell. Used
        // by the coastline pass to determine whether a water cell sits on
        // the shore.
        static bool IsLandOrOutOfBounds(int[] pixels, int nodata, int w, int h, int row, int col)
        {
            if (row < 0 || row >= h || col < 0 || col >= w) return true;
            int v = pixels[row * w + col];
            return v == nodata || v <= 0;
        }

        // English label for water setting. Combines three signals:
        //   percent     — total water coverage. Drives the magnitude band.
        //   qPercent[]  — per-quadrant water %. Tells us WHERE the water sits.
        //   complexity  — shoreline ratio from Cycle 3c. ~1 = round basin;
        //                 > 4 = fragmented coastline (rivers / islands / inlets).
        //
        // The previous version called any fragmented mid-water map an
        // "archipelago," which overstated for valley + coastal maps like
        // Mayworth (31% water + river system + southern coast). The reformed
        // classifier gates "archipelago" on water >= 50% AND fragmented AND
        // uniform distribution; everything else gets a more honest label
        // (river system / lake / coast / scattered ponds).
        internal static string ClassifyWater(double percent, double[] qPercent, double complexity)
        {
            if (percent < 1) return "Essentially landlocked.";

            // Magnitude band — total coverage.
            string magnitude;
            bool dominant = false;
            if (percent < 10) magnitude = "Modest water";
            else if (percent < 25) magnitude = "Significant water";
            else if (percent < 50) magnitude = "Heavy water";
            else { magnitude = "Water-dominated map"; dominant = true; }

            // Shape — what KIND of water arrangement, driven by total coverage
            // and shoreline complexity. "Archipelago" requires the map to be
            // mostly water; below that threshold a fragmented shoreline
            // reads as "river system" or "scattered lakes."
            bool fragmented = complexity > 4;
            string shape;
            if (dominant && fragmented) shape = "archipelago or island chain";
            else if (dominant) shape = "open sea or vast single body";
            else if (percent >= 25 && fragmented) shape = "river system threaded with lakes";
            else if (percent >= 25) shape = "major lake or coastline";
            else if (percent >= 10 && fragmented) shape = "rivers and scattered ponds";
            else if (percent >= 10) shape = "river or modest lake";
            else if (fragmented) shape = "small scattered water features";
            else shape = "minor water feature";

            // Location — where the water sits. Uses quadrant share spread
            // (max - min) rather than a hard threshold count: a 14-pt spread
            // is "evenly distributed"; > 15 means there's a dominant quadrant
            // worth naming.
            string[] qLabels = { "NW", "NE", "SW", "SE" };
            double maxQ = qPercent[0], minQ = qPercent[0];
            int maxIdx = 0;
            for (int i = 1; i < qPercent.Length; i++)
            {
                if (qPercent[i] > maxQ) { maxQ = qPercent[i]; maxIdx = i; }
                if (qPercent[i] < minQ) minQ = qPercent[i];
            }
            int significantQuadrants = qPercent.Count(p => p >= 10);

            string location;
            if (significantQuadrants <= 1) location = $"concentrated in the {qLabels[maxIdx]}";
            else if (significantQuadrants == 2) location = "split between two sides";
            else if (maxQ - minQ < 15) location = "evenly across the map";
            else location = $"weighted to the {qLabels[maxIdx]}";

            return $"{magnitude} — {shape}, {location}.";
        }

        internal static string RenderElevationMarkdown(RasterSummary s)
        {
            var sb = new StringBuilder();
            var ci = CultureInfo.InvariantCulture;
            sb.AppendLine("# Terrain elevation");
            sb.AppendLine();
            sb.AppendLine($"_From Carto's `Elevation.tif` ({s.Width}×{s.Height})._");
            sb.AppendLine();

            // Lead with the reading. Bold so the model picks it up first.
            sb.Append($"**{s.Reading}**");
            int relief = s.Max - s.Min;
            sb.Append($" Stdev {s.StdDev.ToString("F0", ci)} m on a {relief} m total range.");
            if (s.HighQuadrant != null && s.LowQuadrant != null && s.HighQuadrant != s.LowQuadrant)
                sb.Append($" Highest ground in the {s.HighQuadrant}; lowest in the {s.LowQuadrant}.");
            sb.AppendLine();
            sb.AppendLine();

            // Compact stats line for anyone wanting numbers. No histogram —
            // it's expensive in tokens and the reading already covers it.
            sb.AppendLine($"- Range: {s.Min} m to {s.Max} m above the map's floor");
            sb.AppendLine($"- Mean: {s.Mean.ToString("F0", ci)} m, stdev: {s.StdDev.ToString("F0", ci)} m");
            return sb.ToString();
        }

        internal static string RenderWaterMarkdown(RasterSummary s)
        {
            var sb = new StringBuilder();
            var ci = CultureInfo.InvariantCulture;
            sb.AppendLine("# Water bodies");
            sb.AppendLine();
            sb.AppendLine($"_From Carto's `Depth.tif` ({s.Width}×{s.Height})._");
            sb.AppendLine();

            // Lead with the reading.
            sb.Append($"**{s.Reading}**");
            sb.Append($" {s.PercentWater.ToString("F0", ci)}% of the map is water.");
            if (s.WaterCells > 0 && s.HighQuadrant != null)
                sb.Append($" Deepest water in the {s.HighQuadrant} ({s.Max} m).");
            if (s.CoastlineLengthM > 0)
                sb.Append($" Approximate shoreline {(s.CoastlineLengthM / 1000.0).ToString("F1", ci)} km.");
            sb.AppendLine();
            sb.AppendLine();

            // Per-quadrant breakdown for any agent that wants to reason about
            // where the lake/coast/river sits.
            if (s.WaterQuadrantPercent != null && s.WaterQuadrantPercent.Length == 4)
            {
                string[] labels = { "NW", "NE", "SW", "SE" };
                sb.AppendLine("Per-quadrant water coverage:");
                for (int i = 0; i < 4; i++)
                    sb.AppendLine($"- {labels[i]}: {s.WaterQuadrantPercent[i].ToString("F0", ci)}%");
                sb.AppendLine();
            }

            // Coastline: show where the shore lives and how convoluted it is.
            // Shoreline ratio interpretation: ~1 = a single round basin; 2-4
            // = irregular coast with bays / peninsulas; > 4 = archipelago or
            // heavily fragmented water.
            if (s.CoastlineCells > 0)
            {
                sb.AppendLine("Coastline:");
                sb.AppendLine($"- Cells on the water-land boundary: {s.CoastlineCells:N0}");
                if (s.CoastlineLengthM > 0)
                    sb.AppendLine($"- Approximate length: {(s.CoastlineLengthM / 1000.0).ToString("F1", ci)} km");
                sb.AppendLine($"- Shoreline complexity index: {s.ShorelineRatio.ToString("F1", ci)} (~1 = round basin, > 4 = fragmented / archipelago)");
                if (s.CoastlineQuadrantPercent != null && s.CoastlineQuadrantPercent.Length == 4)
                {
                    string[] labels = { "NW", "NE", "SW", "SE" };
                    string distribution = string.Join(", ", System.Linq.Enumerable.Range(0, 4)
                        .Select(i => $"{labels[i]} {s.CoastlineQuadrantPercent[i].ToString("F0", ci)}%"));
                    sb.AppendLine($"- Distribution: {distribution}");
                }
                sb.AppendLine();
            }

            if (s.WaterCells > 0)
            {
                sb.AppendLine($"- Water cells: {s.WaterCells:N0} of {s.Pixels:N0} ({s.PercentWater.ToString("F1", ci)}%)");
                sb.AppendLine($"- Max depth: {s.Max} m, mean depth (water cells): {s.Mean.ToString("F0", ci)} m");
            }
            return sb.ToString();
        }

        internal static string RenderRoadsMarkdown(List<Road> roads)
        {
            var sb = new StringBuilder();
            var ci = CultureInfo.InvariantCulture;
            sb.AppendLine("# Road network");
            sb.AppendLine();
            sb.AppendLine($"_Generated by CityStoryMod at {DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", ci)} from Carto's `Network_Centerline.json`._");
            sb.AppendLine();

            double totalLengthM = roads.Sum(r => r.Length);
            sb.AppendLine("## Summary");
            sb.AppendLine();
            sb.AppendLine($"- {roads.Count} segment(s), combined length {(totalLengthM / 1000.0).ToString("F2", ci)} km");

            // By Object (Road, Track, Pathway, Waterway, etc.). Useful to see
            // at a glance whether the city has rail, water routes, etc.
            var byObject = roads
                .GroupBy(r => string.IsNullOrEmpty(r.Object) ? "Unknown" : r.Object, StringComparer.Ordinal)
                .OrderByDescending(g => g.Sum(r => r.Length));
            foreach (var g in byObject)
            {
                double km = g.Sum(r => r.Length) / 1000.0;
                sb.AppendLine($"- {g.Key}: {g.Count()} segment(s), {km.ToString("F2", ci)} km");
            }
            sb.AppendLine();

            // Named roads — typically highways, signature streets the player
            // or CS2 has given a name to. Group by name so a long road
            // composed of many segments shows as one bullet, and emit the
            // group centroid + quadrant so the agent can answer "where is
            // X?" without rolling its own parser.
            var named = roads.Where(r => !string.IsNullOrWhiteSpace(r.Name))
                .GroupBy(r => r.Name, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToList();
            sb.AppendLine("## Named roads");
            sb.AppendLine();
            if (named.Count == 0)
            {
                sb.AppendLine("- (none — the city has no player-named roads yet)");
            }
            else
            {
                foreach (var group in named)
                {
                    double km = group.Sum(r => r.Length) / 1000.0;
                    int segs = group.Count();
                    string sampleCat = group.Select(r => r.Category).FirstOrDefault(c => !string.IsNullOrEmpty(c));
                    string sampleObj = group.Select(r => r.Object).FirstOrDefault(o => !string.IsNullOrEmpty(o));
                    var bits = new List<string>();
                    if (!string.IsNullOrEmpty(sampleObj)) bits.Add(sampleObj);
                    if (!string.IsNullOrEmpty(sampleCat)) bits.Add(sampleCat);
                    bits.Add($"{km.ToString("F2", ci)} km");
                    if (segs > 1) bits.Add($"{segs} segments");
                    var centroid = RoadGroupCentroid(group);
                    if (centroid.hasGeometry)
                    {
                        bits.Add($"centered ({centroid.x.ToString("F0", ci)}, {centroid.y.ToString("F0", ci)}) in the {QuadrantOfPoint(centroid.x, centroid.y)}");
                    }
                    sb.AppendLine($"- **{group.Key}** — {string.Join(", ", bits)}");
                }
            }
            sb.AppendLine();

            // Intersections: where two or more named roads meet. Naive
            // grid-bucket detection, see ComputeIntersections. Skip when
            // there's nothing to show — a fresh city with one named highway
            // produces zero intersections, and an empty section is noise.
            var intersections = ComputeIntersections(roads);
            if (intersections.Count > 0)
            {
                sb.AppendLine("## Intersections of named roads");
                sb.AppendLine();
                sb.AppendLine("_Approximate junctions where two or more named roads converge (50 m buckets on segment endpoints). Use to anchor 'near the interchange at X' questions._");
                sb.AppendLine();
                foreach (var x in intersections)
                {
                    string roads_ = string.Join(" × ", x.RoadNames);
                    sb.AppendLine($"- **{x.Quadrant}** at ({x.X.ToString("F0", ci)}, {x.Y.ToString("F0", ci)}) — {roads_}");
                }
            }

            return sb.ToString();
        }

        // Render the road network to a self-contained SVG. Coordinates are
        // taken from the recentered meters frame; +y world is north, but SVG
        // +y is down, so we flip when mapping world → pixel.
        //
        // Layers (back to front):
        //   1. District polygons (faint fill so they read as background)
        //   2. Anonymous road segments (thin gray)
        //   3. Named road segments (colored by name, thicker for highways)
        //   4. Intersections (black dots)
        //   5. Named-road labels (white halo for readability)
        //   6. North arrow
        //
        // Returns null when there's no usable footprint or geometry to draw.
        internal static string RenderRoadsSvg(List<Road> roads, List<District> districts, Footprint footprint)
        {
            if (roads == null || roads.Count == 0) return null;

            // Footprint preferred. Fall back to the geometric envelope of
            // every road centerline + district polygon if footprint is empty
            // (older export, missing MapTiles, etc.).
            double minX, minY, maxX, maxY;
            if (footprint != null && footprint.HasGeometry)
            {
                minX = footprint.MinX; minY = footprint.MinY;
                maxX = footprint.MaxX; maxY = footprint.MaxY;
            }
            else
            {
                minX = double.PositiveInfinity; minY = double.PositiveInfinity;
                maxX = double.NegativeInfinity; maxY = double.NegativeInfinity;
                foreach (var r in roads)
                {
                    if (r.Centerline == null) continue;
                    foreach (var v in r.Centerline)
                    {
                        if (v == null) continue;
                        if (v[0] < minX) minX = v[0];
                        if (v[1] < minY) minY = v[1];
                        if (v[0] > maxX) maxX = v[0];
                        if (v[1] > maxY) maxY = v[1];
                    }
                }
                if (districts != null)
                {
                    foreach (var d in districts)
                    {
                        if (d.MinX < minX) minX = d.MinX;
                        if (d.MinY < minY) minY = d.MinY;
                        if (d.MaxX > maxX) maxX = d.MaxX;
                        if (d.MaxY > maxY) maxY = d.MaxY;
                    }
                }
                if (double.IsInfinity(minX)) return null;
            }

            double worldW = maxX - minX;
            double worldH = maxY - minY;
            if (worldW <= 0 || worldH <= 0) return null;

            const int targetWidthPx = 1200;
            const double padding = 24;
            double scale = targetWidthPx / worldW;
            double drawH = worldH * scale;
            double cssW = targetWidthPx + padding * 2;
            double cssH = drawH + padding * 2;

            // World → SVG pixel. Flip Y so north stays up.
            double SX(double wx) => padding + (wx - minX) * scale;
            double SY(double wy) => padding + (maxY - wy) * scale;

            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{cssW.ToString("F0", ci)}\" height=\"{cssH.ToString("F0", ci)}\" viewBox=\"0 0 {cssW.ToString("F0", ci)} {cssH.ToString("F0", ci)}\" font-family=\"sans-serif\">");
            sb.AppendLine("  <rect width=\"100%\" height=\"100%\" fill=\"#f7f7f2\"/>");

            // Districts as faint background polygons. Keeps the road
            // overlay readable while still giving the reader a sense of
            // city footprint when districts exist.
            if (districts != null && districts.Count > 0)
            {
                sb.AppendLine("  <g id=\"districts\" fill=\"#dde6dd\" stroke=\"#a8b8a8\" stroke-width=\"0.5\" opacity=\"0.6\">");
                foreach (var d in districts)
                {
                    if (d.Polygon == null) continue;
                    var pts = string.Join(" ", d.Polygon
                        .Where(v => v != null)
                        .Select(v => $"{SX(v[0]).ToString("F1", ci)},{SY(v[1]).ToString("F1", ci)}"));
                    sb.AppendLine($"    <polygon points=\"{pts}\"/>");
                }
                sb.AppendLine("  </g>");
            }

            // Anonymous road segments. CS2's map-generated default roads
            // appear here at t=0 and stay anonymous unless the player names
            // them, so the gray underlayer is most of what you see early on.
            sb.AppendLine("  <g id=\"unnamed-roads\" stroke=\"#b8b8b8\" stroke-width=\"0.6\" fill=\"none\" stroke-linecap=\"round\">");
            foreach (var r in roads)
            {
                if (r.Centerline == null || r.Centerline.Length < 2) continue;
                if (!string.IsNullOrWhiteSpace(r.Name)) continue;
                AppendPolyline(sb, r.Centerline, SX, SY, ci);
            }
            sb.AppendLine("  </g>");

            // Named roads. Group so all segments of one road share a color,
            // and highways draw thicker. Palette is colorblind-aware (Okabe-Ito).
            string[] palette =
            {
                "#d55e00", "#0072b2", "#009e73", "#cc79a7",
                "#e69f00", "#56b4e9", "#f0e442", "#000000"
            };
            var namedGroups = roads
                .Where(r => !string.IsNullOrWhiteSpace(r.Name))
                .GroupBy(r => r.Name, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToList();

            sb.AppendLine("  <g id=\"named-roads\" fill=\"none\" stroke-linecap=\"round\">");
            for (int gi = 0; gi < namedGroups.Count; gi++)
            {
                var group = namedGroups[gi];
                string color = palette[gi % palette.Length];
                bool isHighway = group.Any(r => (r.Category ?? string.Empty).IndexOf("Highway", StringComparison.OrdinalIgnoreCase) >= 0);
                double strokeWidth = isHighway ? 2.4 : 1.4;
                sb.AppendLine($"    <g stroke=\"{color}\" stroke-width=\"{strokeWidth.ToString("F1", ci)}\">");
                foreach (var r in group)
                {
                    if (r.Centerline == null || r.Centerline.Length < 2) continue;
                    AppendPolyline(sb, r.Centerline, SX, SY, ci);
                }
                sb.AppendLine("    </g>");
            }
            sb.AppendLine("  </g>");

            // Intersections — black dots over the named-roads layer.
            var intersections = ComputeIntersections(roads);
            if (intersections.Count > 0)
            {
                sb.AppendLine("  <g id=\"intersections\" fill=\"#111\" stroke=\"white\" stroke-width=\"1.2\">");
                foreach (var ix in intersections)
                {
                    sb.AppendLine($"    <circle cx=\"{SX(ix.X).ToString("F1", ci)}\" cy=\"{SY(ix.Y).ToString("F1", ci)}\" r=\"3.5\"/>");
                }
                sb.AppendLine("  </g>");
            }

            // Labels. Two filters keep the diagram readable on cities full of
            // CS2-default named decorations like "Medium Roundabout with a
            // Tree and Bushes":
            //   1. Eligibility — highways always; non-highways only when at
            //      least 500 m long (drops short Carto-default decorations
            //      and player single-block streets).
            //   2. Greedy de-collision — place labels in priority order
            //      (highway first, then by length desc) and skip any whose
            //      bounding box overlaps a previously placed label. Roads
            //      still draw their colored strokes; only the text drops out.
            // The full unfiltered list still lives in roads.md, which is the
            // contract for the agent.
            const double minLabelLengthM = 500.0;
            const int maxLabels = 18;
            const double fontSize = 11.0;
            const double approxCharWidth = 6.0;     // empirical, sans-serif at 11px
            const double lineHeight = 14.0;
            const double labelPadding = 2.0;        // expand box for halo + breathing room

            var labelCandidates = namedGroups
                .Select(g => new
                {
                    Group = g,
                    Length = g.Sum(r => r.Length),
                    IsHighway = g.Any(r => (r.Category ?? string.Empty).IndexOf("Highway", StringComparison.OrdinalIgnoreCase) >= 0),
                })
                .Where(x => x.IsHighway || x.Length >= minLabelLengthM)
                .OrderByDescending(x => x.IsHighway)
                .ThenByDescending(x => x.Length)
                .ToList();

            // Track placed boxes in SVG-pixel space for overlap tests.
            var placed = new List<(double X1, double Y1, double X2, double Y2)>();
            sb.AppendLine($"  <g id=\"labels\" font-size=\"{fontSize.ToString("F0", ci)}\" fill=\"#222\" stroke=\"white\" stroke-width=\"3\" paint-order=\"stroke fill\" text-anchor=\"middle\">");
            int placedCount = 0;
            foreach (var cand in labelCandidates)
            {
                if (placedCount >= maxLabels) break;
                var centroid = RoadGroupCentroid(cand.Group);
                if (!centroid.hasGeometry) continue;

                double cx = SX(centroid.x);
                double cy = SY(centroid.y);
                double halfW = (cand.Group.Key.Length * approxCharWidth) / 2.0 + labelPadding;
                double halfH = lineHeight / 2.0 + labelPadding;
                double x1 = cx - halfW, x2 = cx + halfW;
                double y1 = cy - halfH, y2 = cy + halfH;

                bool overlaps = false;
                for (int p = 0; p < placed.Count; p++)
                {
                    var b = placed[p];
                    if (x1 < b.X2 && x2 > b.X1 && y1 < b.Y2 && y2 > b.Y1) { overlaps = true; break; }
                }
                if (overlaps) continue;

                placed.Add((x1, y1, x2, y2));
                placedCount++;
                sb.AppendLine($"    <text x=\"{cx.ToString("F1", ci)}\" y=\"{cy.ToString("F1", ci)}\">{EscapeXml(cand.Group.Key)}</text>");
            }
            sb.AppendLine("  </g>");

            // North arrow. Keep it small in the top-right corner.
            double arrowX = cssW - 36;
            double arrowY = 24;
            sb.AppendLine($"  <g id=\"north\" transform=\"translate({arrowX.ToString("F0", ci)} {arrowY.ToString("F0", ci)})\" fill=\"#222\" stroke=\"#222\" stroke-width=\"1.2\">");
            sb.AppendLine("    <line x1=\"0\" y1=\"6\" x2=\"0\" y2=\"24\" stroke-linecap=\"round\"/>");
            sb.AppendLine("    <polygon points=\"-5,6 5,6 0,-2\" stroke=\"none\"/>");
            sb.AppendLine("    <text x=\"0\" y=\"36\" font-size=\"12\" text-anchor=\"middle\" stroke=\"none\">N</text>");
            sb.AppendLine("  </g>");

            sb.AppendLine("</svg>");
            return sb.ToString();
        }

        static void AppendPolyline(StringBuilder sb, double[][] line, Func<double, double> sx, Func<double, double> sy, CultureInfo ci)
        {
            var pts = new StringBuilder();
            bool first = true;
            foreach (var v in line)
            {
                if (v == null) continue;
                if (!first) pts.Append(' ');
                pts.Append(sx(v[0]).ToString("F1", ci)).Append(',').Append(sy(v[1]).ToString("F1", ci));
                first = false;
            }
            sb.Append("    <polyline points=\"").Append(pts).AppendLine("\"/>");
        }

        static string EscapeXml(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");
        }

        // -- Combined map (carto/processed/map.svg) --
        //
        // Single SVG showing terrain (hypsometric tints), water bodies
        // (depth-shaded blue), districts (outlines), roads (colored, by
        // category), intersections, and labels. Designed as a player-facing
        // visual; the agent's contract remains the text chunks (roads.md,
        // index.md, etc.).
        //
        // Layer order (back to front):
        //   1. Background fill
        //   2. Terrain raster (downsampled to TerrainResolution, RLE'd rows)
        //   3. District outlines (no fill — terrain shows through)
        //   4. Anonymous roads (gray)
        //   5. Named roads (colored, highways thicker)
        //   6. Intersection markers
        //   7. Filtered, de-collided labels
        //   8. North arrow

        // Downsampled cells per side. 192 strikes a good balance for a
        // 14 km CS2 map: ~73 m per cell — enough relief structure shows
        // through, output stays around 600–900 KB after RLE compression.
        const int CombinedMapTerrainResolution = 192;

        internal static string RenderCombinedMapSvg(
            List<Road> roads,
            List<District> districts,
            Footprint footprint,
            GeoTiffReader.Grid elevation,
            GeoTiffReader.Grid depth,
            RasterSummary elevSummary,
            RasterSummary waterSummary)
        {
            // Need at least one of: a footprint with geometry, road centerlines,
            // or a raster. Without all three we have nothing useful to draw.
            double minX, minY, maxX, maxY;
            if (footprint != null && footprint.HasGeometry)
            {
                minX = footprint.MinX; minY = footprint.MinY;
                maxX = footprint.MaxX; maxY = footprint.MaxY;
            }
            else
            {
                minX = double.PositiveInfinity; minY = double.PositiveInfinity;
                maxX = double.NegativeInfinity; maxY = double.NegativeInfinity;
                if (roads != null)
                {
                    foreach (var r in roads)
                    {
                        if (r.Centerline == null) continue;
                        foreach (var v in r.Centerline)
                        {
                            if (v == null) continue;
                            if (v[0] < minX) minX = v[0];
                            if (v[1] < minY) minY = v[1];
                            if (v[0] > maxX) maxX = v[0];
                            if (v[1] > maxY) maxY = v[1];
                        }
                    }
                }
                if (districts != null)
                {
                    foreach (var d in districts)
                    {
                        if (d.MinX < minX) minX = d.MinX;
                        if (d.MinY < minY) minY = d.MinY;
                        if (d.MaxX > maxX) maxX = d.MaxX;
                        if (d.MaxY > maxY) maxY = d.MaxY;
                    }
                }
                if (double.IsInfinity(minX)) return null;
            }

            double worldW = maxX - minX;
            double worldH = maxY - minY;
            if (worldW <= 0 || worldH <= 0) return null;

            const int targetWidthPx = 1200;
            const double padding = 24;
            double scale = targetWidthPx / worldW;
            double drawW = worldW * scale;
            double drawH = worldH * scale;
            double cssW = drawW + padding * 2;
            double cssH = drawH + padding * 2;

            double SX(double wx) => padding + (wx - minX) * scale;
            double SY(double wy) => padding + (maxY - wy) * scale;

            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{cssW.ToString("F0", ci)}\" height=\"{cssH.ToString("F0", ci)}\" viewBox=\"0 0 {cssW.ToString("F0", ci)} {cssH.ToString("F0", ci)}\" font-family=\"sans-serif\">");

            // Plain neutral background — shown only at the corners outside the
            // terrain raster (rare, but possible when the map's playable area
            // doesn't span the full footprint).
            sb.AppendLine("  <rect width=\"100%\" height=\"100%\" fill=\"#1a2330\"/>");

            // 1. Terrain raster. Downsample elevation + depth in lockstep, then
            // emit row-wise RLE rectangles. Each cell maps to a world rect
            // whose SVG dimensions are the same across the grid.
            if (elevation != null)
            {
                int dstW = CombinedMapTerrainResolution;
                int dstH = CombinedMapTerrainResolution;
                int[] dsElev = DownsampleGrid(elevation, dstW, dstH);
                int[] dsDepth = depth != null ? DownsampleGrid(depth, dstW, dstH) : null;

                int elevMin = elevSummary != null ? elevSummary.Min : int.MinValue;
                int elevMax = elevSummary != null ? elevSummary.Max : int.MaxValue;
                if (elevMin == int.MinValue || elevMax == int.MaxValue)
                {
                    elevMin = int.MaxValue; elevMax = int.MinValue;
                    foreach (var v in dsElev)
                    {
                        if (v == int.MinValue) continue;
                        if (v < elevMin) elevMin = v;
                        if (v > elevMax) elevMax = v;
                    }
                }
                int depthMax = waterSummary != null && waterSummary.Max > 0
                    ? waterSummary.Max
                    : 1;

                double cellSvgW = drawW / dstW;
                double cellSvgH = drawH / dstH;

                sb.AppendLine("  <g id=\"terrain\" shape-rendering=\"crispEdges\">");
                for (int r = 0; r < dstH; r++)
                {
                    int rowStart = r * dstW;
                    int c = 0;
                    while (c < dstW)
                    {
                        string color = TerrainCellColor(dsElev[rowStart + c], dsDepth?[rowStart + c] ?? 0, elevMin, elevMax, depthMax);
                        int runStart = c;
                        c++;
                        while (c < dstW)
                        {
                            string nc = TerrainCellColor(dsElev[rowStart + c], dsDepth?[rowStart + c] ?? 0, elevMin, elevMax, depthMax);
                            if (!ReferenceEquals(nc, color) && nc != color) break;
                            c++;
                        }
                        if (color == null) continue;
                        int runLen = c - runStart;
                        double rx = padding + runStart * cellSvgW;
                        double ry = padding + r * cellSvgH;
                        double rw = runLen * cellSvgW + 0.5;   // +0.5 px overdraw so
                                                                // adjacent rows don't
                                                                // hairline-gap on
                                                                // sub-pixel boundaries
                        double rh = cellSvgH + 0.5;
                        sb.AppendLine($"    <rect x=\"{rx.ToString("F2", ci)}\" y=\"{ry.ToString("F2", ci)}\" width=\"{rw.ToString("F2", ci)}\" height=\"{rh.ToString("F2", ci)}\" fill=\"{color}\"/>");
                    }
                }
                sb.AppendLine("  </g>");
            }

            // 2. District outlines. Stroke only — the terrain raster is the
            // background, we don't want to obscure it with district fills.
            if (districts != null && districts.Count > 0)
            {
                sb.AppendLine("  <g id=\"districts\" fill=\"none\" stroke=\"rgba(255,255,255,0.55)\" stroke-width=\"1\" stroke-dasharray=\"4 3\">");
                foreach (var d in districts)
                {
                    if (d.Polygon == null) continue;
                    var pts = string.Join(" ", d.Polygon
                        .Where(v => v != null)
                        .Select(v => $"{SX(v[0]).ToString("F1", ci)},{SY(v[1]).ToString("F1", ci)}"));
                    sb.AppendLine($"    <polygon points=\"{pts}\"/>");
                }
                sb.AppendLine("  </g>");
            }

            // 3. Anonymous roads — quiet gray over the terrain.
            sb.AppendLine("  <g id=\"unnamed-roads\" stroke=\"rgba(40,40,40,0.7)\" stroke-width=\"0.7\" fill=\"none\" stroke-linecap=\"round\">");
            foreach (var r in roads)
            {
                if (r.Centerline == null || r.Centerline.Length < 2) continue;
                if (!string.IsNullOrWhiteSpace(r.Name)) continue;
                AppendPolyline(sb, r.Centerline, SX, SY, ci);
            }
            sb.AppendLine("  </g>");

            // 4. Named roads. Same palette / weight rules as roads.svg.
            string[] palette =
            {
                "#d62728", "#1f5fb0", "#0a9655", "#b035a6",
                "#e6a700", "#1da0c8", "#222222", "#7b3f00"
            };
            var namedGroups = roads
                .Where(r => !string.IsNullOrWhiteSpace(r.Name))
                .GroupBy(r => r.Name, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToList();

            sb.AppendLine("  <g id=\"named-roads\" fill=\"none\" stroke-linecap=\"round\">");
            for (int gi = 0; gi < namedGroups.Count; gi++)
            {
                var group = namedGroups[gi];
                string color = palette[gi % palette.Length];
                bool isHighway = group.Any(r => (r.Category ?? string.Empty).IndexOf("Highway", StringComparison.OrdinalIgnoreCase) >= 0);
                double strokeWidth = isHighway ? 2.6 : 1.6;
                sb.AppendLine($"    <g stroke=\"{color}\" stroke-width=\"{strokeWidth.ToString("F1", ci)}\">");
                foreach (var r in group)
                {
                    if (r.Centerline == null || r.Centerline.Length < 2) continue;
                    AppendPolyline(sb, r.Centerline, SX, SY, ci);
                }
                sb.AppendLine("    </g>");
            }
            sb.AppendLine("  </g>");

            // 5. Intersections — black dots with white halo for legibility.
            var intersections = ComputeIntersections(roads);
            if (intersections.Count > 0)
            {
                sb.AppendLine("  <g id=\"intersections\" fill=\"#111\" stroke=\"white\" stroke-width=\"1.4\">");
                foreach (var ix in intersections)
                {
                    sb.AppendLine($"    <circle cx=\"{SX(ix.X).ToString("F1", ci)}\" cy=\"{SY(ix.Y).ToString("F1", ci)}\" r=\"3.8\"/>");
                }
                sb.AppendLine("  </g>");
            }

            // 6. Labels — same filter / de-collision rules as roads.svg.
            const double minLabelLengthM = 500.0;
            const int maxLabels = 18;
            const double fontSize = 11.0;
            const double approxCharWidth = 6.0;
            const double lineHeight = 14.0;
            const double labelPadding = 2.0;

            var labelCandidates = namedGroups
                .Select(g => new
                {
                    Group = g,
                    Length = g.Sum(rr => rr.Length),
                    IsHighway = g.Any(rr => (rr.Category ?? string.Empty).IndexOf("Highway", StringComparison.OrdinalIgnoreCase) >= 0),
                })
                .Where(x => x.IsHighway || x.Length >= minLabelLengthM)
                .OrderByDescending(x => x.IsHighway)
                .ThenByDescending(x => x.Length)
                .ToList();

            var placed = new List<(double X1, double Y1, double X2, double Y2)>();
            sb.AppendLine($"  <g id=\"labels\" font-size=\"{fontSize.ToString("F0", ci)}\" fill=\"#fff\" stroke=\"#000\" stroke-width=\"3\" paint-order=\"stroke fill\" text-anchor=\"middle\">");
            int placedCount = 0;
            foreach (var cand in labelCandidates)
            {
                if (placedCount >= maxLabels) break;
                var centroid = RoadGroupCentroid(cand.Group);
                if (!centroid.hasGeometry) continue;
                double cx = SX(centroid.x);
                double cy = SY(centroid.y);
                double halfW = (cand.Group.Key.Length * approxCharWidth) / 2.0 + labelPadding;
                double halfH = lineHeight / 2.0 + labelPadding;
                double x1 = cx - halfW, x2 = cx + halfW;
                double y1 = cy - halfH, y2 = cy + halfH;
                bool overlaps = false;
                foreach (var b in placed)
                {
                    if (x1 < b.X2 && x2 > b.X1 && y1 < b.Y2 && y2 > b.Y1) { overlaps = true; break; }
                }
                if (overlaps) continue;
                placed.Add((x1, y1, x2, y2));
                placedCount++;
                sb.AppendLine($"    <text x=\"{cx.ToString("F1", ci)}\" y=\"{cy.ToString("F1", ci)}\">{EscapeXml(cand.Group.Key)}</text>");
            }
            sb.AppendLine("  </g>");

            // 7. North arrow.
            double arrowX = cssW - 36;
            double arrowY = 24;
            sb.AppendLine($"  <g id=\"north\" transform=\"translate({arrowX.ToString("F0", ci)} {arrowY.ToString("F0", ci)})\" fill=\"#fff\" stroke=\"#fff\" stroke-width=\"1.2\">");
            sb.AppendLine("    <line x1=\"0\" y1=\"6\" x2=\"0\" y2=\"24\" stroke-linecap=\"round\"/>");
            sb.AppendLine("    <polygon points=\"-5,6 5,6 0,-2\" stroke=\"none\"/>");
            sb.AppendLine("    <text x=\"0\" y=\"36\" font-size=\"12\" text-anchor=\"middle\" stroke=\"none\">N</text>");
            sb.AppendLine("  </g>");

            sb.AppendLine("</svg>");
            return sb.ToString();
        }

        // Block-average downsample for an int[] raster. NoData cells are
        // skipped from the average; if a block has only NoData cells, the
        // target cell gets int.MinValue (sentinel — caller maps it to a
        // "no terrain" color, typically the background).
        //
        // For Carto's depth raster, NoData is -32768 (land) and water cells
        // are positive. A block-averaged target cell is "water" when its
        // average is > 0 — which only happens when at least some source
        // cells in the block were water. The averaging itself smooths
        // shoreline pixels nicely.
        internal static int[] DownsampleGrid(GeoTiffReader.Grid grid, int dstW, int dstH)
        {
            int[] dst = new int[dstW * dstH];
            int srcW = grid.Width, srcH = grid.Height;
            int[] src = grid.Pixels;
            int nodata = grid.NoData;
            for (int dy = 0; dy < dstH; dy++)
            {
                int y0 = (int)((long)dy * srcH / dstH);
                int y1 = (int)((long)(dy + 1) * srcH / dstH);
                if (y1 <= y0) y1 = y0 + 1;
                for (int dx = 0; dx < dstW; dx++)
                {
                    int x0 = (int)((long)dx * srcW / dstW);
                    int x1 = (int)((long)(dx + 1) * srcW / dstW);
                    if (x1 <= x0) x1 = x0 + 1;
                    long sum = 0;
                    int count = 0;
                    for (int sy = y0; sy < y1; sy++)
                    {
                        int rowStart = sy * srcW;
                        for (int sx = x0; sx < x1; sx++)
                        {
                            int v = src[rowStart + sx];
                            if (v == nodata) continue;
                            sum += v;
                            count++;
                        }
                    }
                    dst[dy * dstW + dx] = count == 0 ? int.MinValue : (int)(sum / count);
                }
            }
            return dst;
        }

        // Pick a fill color for a single downsampled cell. Water takes
        // precedence — any cell where the depth block averaged > 0 reads as
        // water, shaded by depth. Otherwise hypsometric tint based on
        // elevation. Returns null for cells outside the playable map (both
        // sentinels) — the caller skips emission, the background shows
        // through.
        internal static string TerrainCellColor(int elev, int depth, int elevMin, int elevMax, int depthMax)
        {
            if (depth > 0)
            {
                double t = depthMax > 0 ? Math.Min(1.0, (double)depth / depthMax) : 0.0;
                return InterpolateColors(WaterRamp, t);
            }
            if (elev == int.MinValue) return null;
            double tt = elevMax > elevMin
                ? (double)(elev - elevMin) / (elevMax - elevMin)
                : 0.0;
            return InterpolateColors(LandRamp, tt);
        }

        // 5-stop hypsometric tint for land. Walks from a low forest green
        // through agricultural yellows up to bare rock white. Matches the
        // intuitive "what altitude is this" reading on a paper topo map.
        static readonly (double t, int r, int g, int b)[] LandRamp = new (double, int, int, int)[]
        {
            (0.00, 60, 110, 60),     // lowland green
            (0.25, 130, 165, 80),    // mid green
            (0.50, 200, 175, 100),   // tan / dry grass
            (0.75, 150, 115, 80),    // brown
            (1.00, 235, 230, 220),   // bare rock / snow
        };

        // 3-stop blue ramp for water depth. Shallow → deep.
        static readonly (double t, int r, int g, int b)[] WaterRamp = new (double, int, int, int)[]
        {
            (0.00, 150, 200, 230),   // shallow
            (0.50, 70, 130, 190),    // mid
            (1.00, 25, 60, 105),     // deep
        };

        // Linear interpolation along a color-ramp table. Caches the last
        // returned color string is NOT done here — same colors recur often
        // enough that callers could intern them, but the per-call cost is
        // small and the RLE pass collapses runs anyway.
        static string InterpolateColors((double t, int r, int g, int b)[] ramp, double t)
        {
            if (t <= ramp[0].t) return RgbHex(ramp[0].r, ramp[0].g, ramp[0].b);
            if (t >= ramp[ramp.Length - 1].t) return RgbHex(ramp[ramp.Length - 1].r, ramp[ramp.Length - 1].g, ramp[ramp.Length - 1].b);
            for (int i = 1; i < ramp.Length; i++)
            {
                if (t <= ramp[i].t)
                {
                    double f = (t - ramp[i - 1].t) / (ramp[i].t - ramp[i - 1].t);
                    int rr = (int)(ramp[i - 1].r + f * (ramp[i].r - ramp[i - 1].r));
                    int gg = (int)(ramp[i - 1].g + f * (ramp[i].g - ramp[i - 1].g));
                    int bb = (int)(ramp[i - 1].b + f * (ramp[i].b - ramp[i - 1].b));
                    return RgbHex(rr, gg, bb);
                }
            }
            var last = ramp[ramp.Length - 1];
            return RgbHex(last.r, last.g, last.b);
        }

        static string RgbHex(int r, int g, int b)
        {
            return "#" + ((r & 0xff) << 16 | (g & 0xff) << 8 | (b & 0xff)).ToString("x6", CultureInfo.InvariantCulture);
        }
    }
}
