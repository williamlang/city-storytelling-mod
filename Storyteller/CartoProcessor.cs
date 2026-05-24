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
            public string IndexPath;
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

                List<District> districts = ParseDistricts(File.ReadAllText(areaFile));

                // Buildings are optional — if the file isn't there (e.g. an
                // earlier export that didn't request System.Building), we
                // still produce district chunks without a Buildings section.
                List<Building> namedBuildings = new List<Building>();
                string buildingFile = Path.Combine(cartoDir, "GeoJSON", "Building_Boundary.json");
                if (File.Exists(buildingFile))
                {
                    namedBuildings = ParseNamedBuildings(File.ReadAllText(buildingFile));
                }

                // Re-center every coordinate around the city centroid so the
                // numbers the agent sees read like "(123, -456)" instead of
                // "(-20,872,758, -1,188)". Carto's projection chain applies
                // arbitrary-looking offsets we don't control — recentering
                // makes the chunks self-consistent regardless.
                RecenterCoordinates(districts, namedBuildings);

                ComputeAdjacency(districts);
                AssignBuildingsToDistricts(namedBuildings, districts);

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

                string indexPath = Path.Combine(processedDir, "index.md");
                File.WriteAllText(indexPath, RenderIndexMarkdown(districts, namedBuildings));

                result.Success = true;
                result.DistrictsWritten = districts.Count;
                result.NamedBuildingsAssigned = namedBuildings.Count(b => b.DistrictSlug != null);
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

        // Subtract the mean of all district centroids from every coordinate.
        // Pure shift — preserves distances, areas, adjacency. Centroids in
        // output prose become "(123, -456)" relative to the city's middle
        // rather than whatever absolute frame Carto happens to be emitting.
        internal static void RecenterCoordinates(List<District> districts, List<Building> buildings)
        {
            if (districts.Count == 0) return;
            double ox = 0, oy = 0;
            foreach (var d in districts)
            {
                ox += d.CentroidX;
                oy += d.CentroidY;
            }
            ox /= districts.Count;
            oy /= districts.Count;

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
        {
            var sb = new StringBuilder();
            var ci = CultureInfo.InvariantCulture;
            sb.AppendLine("# City spatial index");
            sb.AppendLine();
            sb.AppendLine($"_Generated by CityStoryMod at {DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", ci)} from Carto's GeoJSON output._");
            sb.AppendLine();
            sb.AppendLine("Per-district detail lives at `processed/districts/<slug>.md`. Read this file once per session for the lay of the land, drill into individual districts as needed.");
            sb.AppendLine();

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
                foreach (var b in allBuildings.Where(b => b.DistrictSlug == null).OrderBy(b => b.Name, StringComparer.Ordinal))
                {
                    string cat = string.IsNullOrEmpty(b.Category) ? "" : $" — {b.Category}";
                    sb.AppendLine($"- **{b.Name}**{cat}");
                }
            }

            return sb.ToString();
        }
    }
}
