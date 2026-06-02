using System.Linq;
using CityStoryMod.Storyteller;
using FluentAssertions;
using Xunit;

namespace CityStoryMod.Tests
{
    public class CartoProcessorTests
    {
        // Test fixtures use Carto's coordinate space (WGS84 degrees), since
        // that's what the parser receives. The processor scales internally to
        // meters via the equator approximation (1° ≈ 111,320 m), so the
        // assertions below check the scaled values.
        // 0.0001° ≈ 11.13 m at the equator — pick fixture coordinates so the
        // squares are large enough to clear the 20 m adjacency tolerance.
        // Two adjacent 0.001° × 0.001° squares ≈ 111 m × 111 m, sharing the
        // edge at lon = 0.001°. A third feature with Object="MapTile" tests
        // that the District filter works.
        const string SampleTwoAdjacentDistricts = @"{
          ""type"": ""FeatureCollection"",
          ""features"": [
            {
              ""type"": ""Feature"",
              ""geometry"": { ""type"": ""Polygon"", ""coordinates"": [[
                [0.000, 0.000], [0.001, 0.000], [0.001, 0.001], [0.000, 0.001], [0.000, 0.000]
              ]] },
              ""properties"": { ""Name"": ""Old Halverson"", ""Object"": ""District"", ""Resident"": 1500, ""Employee"": 320 }
            },
            {
              ""type"": ""Feature"",
              ""geometry"": { ""type"": ""Polygon"", ""coordinates"": [[
                [0.001, 0.000], [0.002, 0.000], [0.002, 0.001], [0.001, 0.001], [0.001, 0.000]
              ]] },
              ""properties"": { ""Name"": ""Riverside"", ""Object"": ""District"", ""Resident"": 2200 }
            },
            {
              ""type"": ""Feature"",
              ""geometry"": { ""type"": ""Polygon"", ""coordinates"": [[
                [0.100, 0.100], [0.101, 0.100], [0.101, 0.101], [0.100, 0.101], [0.100, 0.100]
              ]] },
              ""properties"": { ""Name"": ""Tile Junk"", ""Object"": ""MapTile"" }
            }
          ]
        }";

        // One degree converts to this many meters in CartoProcessor — keep
        // tests in sync if the constant ever changes.
        const double DegToM = 111320.0;

        [Fact]
        public void ParseDistricts_filters_to_District_object_type()
        {
            var districts = CartoProcessor.ParseDistricts(SampleTwoAdjacentDistricts);
            districts.Should().HaveCount(2);
            districts.Select(d => d.Name).Should().BeEquivalentTo("Old Halverson", "Riverside");
        }

        [Fact]
        public void ParseDistricts_slugifies_names()
        {
            var districts = CartoProcessor.ParseDistricts(SampleTwoAdjacentDistricts);
            districts.Single(d => d.Name == "Old Halverson").Slug.Should().Be("old-halverson");
        }

        [Fact]
        public void ParseDistricts_computes_centroid_and_bbox_and_area_in_meters()
        {
            var districts = CartoProcessor.ParseDistricts(SampleTwoAdjacentDistricts);
            var d = districts.Single(x => x.Name == "Old Halverson");

            // 0.001° × 0.001° square → ~111 m × 111 m at the equator.
            d.MinX.Should().BeApproximately(0, 1e-6);
            d.MinY.Should().BeApproximately(0, 1e-6);
            d.MaxX.Should().BeApproximately(0.001 * DegToM, 1e-6);
            d.MaxY.Should().BeApproximately(0.001 * DegToM, 1e-6);

            double expectedSideM = 0.001 * DegToM;
            d.AreaM2.Should().BeApproximately(expectedSideM * expectedSideM, 1.0);

            // Centroid pulled toward (0,0) slightly because the closing vertex
            // duplicates [0,0,0]. The test pins behavior — important is that
            // it's a number of meters, not a fraction of a degree.
            d.CentroidX.Should().BeApproximately(expectedSideM * 0.4, expectedSideM * 0.01);
            d.CentroidY.Should().BeApproximately(expectedSideM * 0.4, expectedSideM * 0.01);
        }

        [Fact]
        public void RecenterCoordinates_shifts_centroids_to_city_origin()
        {
            var districts = CartoProcessor.ParseDistricts(SampleTwoAdjacentDistricts);
            var preCentroidA = (districts[0].CentroidX, districts[0].CentroidY);
            var preCentroidB = (districts[1].CentroidX, districts[1].CentroidY);
            double meanX = (preCentroidA.CentroidX + preCentroidB.CentroidX) / 2;
            double meanY = (preCentroidA.CentroidY + preCentroidB.CentroidY) / 2;

            CartoProcessor.RecenterCoordinates(districts, new System.Collections.Generic.List<CartoProcessor.Building>());

            // After recentering, sum of centroids should be ~ zero (symmetry around the new origin).
            (districts[0].CentroidX + districts[1].CentroidX).Should().BeApproximately(0, 1e-6);
            (districts[0].CentroidY + districts[1].CentroidY).Should().BeApproximately(0, 1e-6);

            // The shift equals the mean.
            districts[0].CentroidX.Should().BeApproximately(preCentroidA.CentroidX - meanX, 1e-6);
            districts[1].CentroidY.Should().BeApproximately(preCentroidB.CentroidY - meanY, 1e-6);
        }

        [Fact]
        public void RecenterCoordinates_also_shifts_buildings_and_polygons()
        {
            var districts = CartoProcessor.ParseDistricts(SampleTwoAdjacentDistricts);
            var buildings = CartoProcessor.ParseNamedBuildings(SampleBuildings);
            double preBuildingX = buildings[0].CentroidX;
            double prePolygonX = districts[0].Polygon[0][0];

            CartoProcessor.RecenterCoordinates(districts, buildings);

            buildings[0].CentroidX.Should().NotBe(preBuildingX);
            districts[0].Polygon[0][0].Should().NotBe(prePolygonX);
        }

        [Fact]
        public void ParseDistricts_pulls_numeric_Carto_properties()
        {
            var districts = CartoProcessor.ParseDistricts(SampleTwoAdjacentDistricts);
            var d = districts.Single(x => x.Name == "Old Halverson");
            d.CartoProperties.Should().ContainKey("Resident").WhoseValue.Should().Be(1500);
            d.CartoProperties.Should().ContainKey("Employee").WhoseValue.Should().Be(320);
            d.CartoProperties.Should().NotContainKey("Name");
            d.CartoProperties.Should().NotContainKey("Object");
        }

        [Fact]
        public void ComputeAdjacency_detects_shared_edge()
        {
            var districts = CartoProcessor.ParseDistricts(SampleTwoAdjacentDistricts);
            CartoProcessor.ComputeAdjacency(districts);

            var halverson = districts.Single(d => d.Name == "Old Halverson");
            var riverside = districts.Single(d => d.Name == "Riverside");

            halverson.Borders.Should().ContainSingle(b => b.Name == "Riverside");
            riverside.Borders.Should().ContainSingle(b => b.Name == "Old Halverson");
        }

        [Fact]
        public void ComputeAdjacency_assigns_directional_bearing_label()
        {
            var districts = CartoProcessor.ParseDistricts(SampleTwoAdjacentDistricts);
            CartoProcessor.ComputeAdjacency(districts);

            // Riverside centroid (15,5) is east of Old Halverson centroid (5,5)
            // → border from Old Halverson's perspective should be "east".
            var halverson = districts.Single(d => d.Name == "Old Halverson");
            halverson.Borders.Single().Direction.Should().Be("east");

            // Symmetric — Riverside sees Old Halverson to the west.
            var riverside = districts.Single(d => d.Name == "Riverside");
            riverside.Borders.Single().Direction.Should().Be("west");
        }

        [Fact]
        public void ComputeAdjacency_skips_pairs_that_only_touch_at_a_single_vertex()
        {
            const string twoDistrictsCornerTouching = @"{
              ""type"": ""FeatureCollection"",
              ""features"": [
                {
                  ""type"": ""Feature"",
                  ""geometry"": { ""type"": ""Polygon"", ""coordinates"": [[
                    [0, 0], [10, 0], [10, 10], [0, 10], [0, 0]
                  ]] },
                  ""properties"": { ""Name"": ""A"", ""Object"": ""District"" }
                },
                {
                  ""type"": ""Feature"",
                  ""geometry"": { ""type"": ""Polygon"", ""coordinates"": [[
                    [10, 10], [20, 10], [20, 20], [10, 20], [10, 10]
                  ]] },
                  ""properties"": { ""Name"": ""B"", ""Object"": ""District"" }
                }
              ]
            }";

            var districts = CartoProcessor.ParseDistricts(twoDistrictsCornerTouching);
            CartoProcessor.ComputeAdjacency(districts);

            // Only one shared vertex (10,10) → below MinSharedVerticesForAdjacency (2).
            districts.Single(d => d.Name == "A").Borders.Should().BeEmpty();
            districts.Single(d => d.Name == "B").Borders.Should().BeEmpty();
        }

        [Theory]
        [InlineData(0, 0, 10, 0, "east")]
        [InlineData(0, 0, 0, 10, "north")]
        [InlineData(0, 0, -10, 0, "west")]
        [InlineData(0, 0, 0, -10, "south")]
        [InlineData(0, 0, 10, 10, "north-east")]
        [InlineData(0, 0, -10, 10, "north-west")]
        [InlineData(0, 0, -10, -10, "south-west")]
        [InlineData(0, 0, 10, -10, "south-east")]
        [InlineData(5, 5, 5, 5, "adjacent")]
        public void BearingLabel_returns_compass_octant(double fx, double fy, double tx, double ty, string expected)
        {
            CartoProcessor.BearingLabel(fx, fy, tx, ty).Should().Be(expected);
        }

        [Fact]
        public void RenderIndexMarkdown_lists_districts_and_adjacency()
        {
            var districts = CartoProcessor.ParseDistricts(SampleTwoAdjacentDistricts);
            CartoProcessor.ComputeAdjacency(districts);
            string md = CartoProcessor.RenderIndexMarkdown(districts, new System.Collections.Generic.List<CartoProcessor.Building>());

            md.Should().Contain("# City spatial index");
            md.Should().Contain("Old Halverson");
            md.Should().Contain("Riverside");
            md.Should().Contain("## Adjacency");
            md.Should().Contain("Old Halverson");
            md.Should().Contain("Riverside (east)");
            md.Should().Contain("## Named buildings");
            md.Should().Contain("0 named building(s) total");
        }

        [Fact]
        public void RenderDistrictMarkdown_includes_stats_borders_and_Carto_properties()
        {
            var districts = CartoProcessor.ParseDistricts(SampleTwoAdjacentDistricts);
            CartoProcessor.ComputeAdjacency(districts);
            var halverson = districts.Single(d => d.Name == "Old Halverson");
            string md = CartoProcessor.RenderDistrictMarkdown(halverson);

            md.Should().Contain("# Old Halverson");
            md.Should().Contain("`old-halverson`");
            md.Should().Contain("Bounding box");
            md.Should().Contain("Carto Resident");   // case preserved from input
            md.Should().Contain("## Borders");
            md.Should().Contain("**Riverside**");
            md.Should().Contain("to the east");
            md.Should().Contain("## Named buildings");
        }

        // -- Building parsing + assignment --

        // One named civic building inside Old Halverson, one auto-named
        // landmark inside Riverside, one anonymous (empty Name) building
        // that must be dropped, and one building with a centroid outside
        // both districts (un-assigned).
        const string SampleBuildings = @"{
          ""type"": ""FeatureCollection"",
          ""features"": [
            {
              ""type"": ""Feature"",
              ""geometry"": { ""type"": ""Polygon"", ""coordinates"": [[
                [0.0002, 0.0002], [0.0003, 0.0002], [0.0003, 0.0003], [0.0002, 0.0003], [0.0002, 0.0002]
              ]] },
              ""properties"": { ""Name"": ""Halverson Crossing High School"", ""Object"": ""Building"", ""Category"": ""Education"", ""Employee"": 45, ""Resident"": 0 }
            },
            {
              ""type"": ""Feature"",
              ""geometry"": { ""type"": ""Polygon"", ""coordinates"": [[
                [0.0014, 0.0005], [0.0015, 0.0005], [0.0015, 0.0006], [0.0014, 0.0006], [0.0014, 0.0005]
              ]] },
              ""properties"": { ""Name"": ""Riverside Water Tower"", ""Object"": ""Building"", ""Category"": ""Water"", ""Employee"": 2 }
            },
            {
              ""type"": ""Feature"",
              ""geometry"": { ""type"": ""Polygon"", ""coordinates"": [[
                [0.0005, 0.0005], [0.00055, 0.0005], [0.00055, 0.00055], [0.0005, 0.00055], [0.0005, 0.0005]
              ]] },
              ""properties"": { ""Name"": """", ""Object"": ""Building"" }
            },
            {
              ""type"": ""Feature"",
              ""geometry"": { ""type"": ""Polygon"", ""coordinates"": [[
                [0.050, 0.050], [0.051, 0.050], [0.051, 0.051], [0.050, 0.051], [0.050, 0.050]
              ]] },
              ""properties"": { ""Name"": ""Lonely Lighthouse"", ""Object"": ""Building"", ""Category"": ""Park"" }
            }
          ]
        }";

        [Fact]
        public void ParseNamedBuildings_drops_anonymous_buildings()
        {
            var buildings = CartoProcessor.ParseNamedBuildings(SampleBuildings);
            buildings.Should().HaveCount(3);
            buildings.Select(b => b.Name).Should().NotContain("");
        }

        [Fact]
        public void ParseNamedBuildings_pulls_name_category_and_numeric_properties()
        {
            var buildings = CartoProcessor.ParseNamedBuildings(SampleBuildings);
            var school = buildings.Single(b => b.Name == "Halverson Crossing High School");
            school.Category.Should().Be("Education");
            school.CartoProperties.Should().ContainKey("Employee").WhoseValue.Should().Be(45);
            school.CartoProperties.Should().NotContainKey("Name");
            school.CartoProperties.Should().NotContainKey("Object");
            school.CartoProperties.Should().NotContainKey("Category");
        }

        [Fact]
        public void AssignBuildingsToDistricts_uses_point_in_polygon_centroid_match()
        {
            var districts = CartoProcessor.ParseDistricts(SampleTwoAdjacentDistricts);
            var buildings = CartoProcessor.ParseNamedBuildings(SampleBuildings);
            CartoProcessor.AssignBuildingsToDistricts(buildings, districts);

            var halverson = districts.Single(d => d.Name == "Old Halverson");
            var riverside = districts.Single(d => d.Name == "Riverside");

            halverson.Buildings.Should().ContainSingle(b => b.Name == "Halverson Crossing High School");
            riverside.Buildings.Should().ContainSingle(b => b.Name == "Riverside Water Tower");

            // Lonely Lighthouse is well outside both squares — stays unassigned.
            var lonely = buildings.Single(b => b.Name == "Lonely Lighthouse");
            lonely.DistrictSlug.Should().BeNull();
        }

        [Fact]
        public void RenderDistrictMarkdown_lists_assigned_buildings()
        {
            var districts = CartoProcessor.ParseDistricts(SampleTwoAdjacentDistricts);
            var buildings = CartoProcessor.ParseNamedBuildings(SampleBuildings);
            CartoProcessor.AssignBuildingsToDistricts(buildings, districts);
            CartoProcessor.ComputeAdjacency(districts);

            string md = CartoProcessor.RenderDistrictMarkdown(districts.Single(d => d.Name == "Old Halverson"));
            md.Should().Contain("## Named buildings");
            md.Should().Contain("**Halverson Crossing High School**");
            md.Should().Contain("Education");
            md.Should().Contain("45 employees");
        }

        [Fact]
        public void RenderDistrictMarkdown_dedupes_duplicate_building_names_and_aggregates_stats()
        {
            // Three generic "Low Density Offices" in the same district, plus
            // one uniquely-named school. Dedupe should collapse the offices
            // into one bullet with " (× 3)" and an aggregate employee count.
            string fixture = @"{
              ""type"": ""FeatureCollection"",
              ""features"": [
                { ""type"": ""Feature"",
                  ""geometry"": { ""type"": ""Polygon"", ""coordinates"": [[
                    [0.0001, 0.0001], [0.0002, 0.0001], [0.0002, 0.0002], [0.0001, 0.0002], [0.0001, 0.0001]
                  ]] },
                  ""properties"": { ""Name"": ""Halverson Crossing High School"", ""Object"": ""Building"", ""Category"": ""Education"", ""Employee"": 45 } },
                { ""type"": ""Feature"",
                  ""geometry"": { ""type"": ""Polygon"", ""coordinates"": [[
                    [0.00025, 0.00025], [0.0003, 0.00025], [0.0003, 0.0003], [0.00025, 0.0003], [0.00025, 0.00025]
                  ]] },
                  ""properties"": { ""Name"": ""Low Density Offices"", ""Object"": ""Building"", ""Category"": ""Property"", ""Employee"": 12 } },
                { ""type"": ""Feature"",
                  ""geometry"": { ""type"": ""Polygon"", ""coordinates"": [[
                    [0.00035, 0.00035], [0.0004, 0.00035], [0.0004, 0.0004], [0.00035, 0.0004], [0.00035, 0.00035]
                  ]] },
                  ""properties"": { ""Name"": ""Low Density Offices"", ""Object"": ""Building"", ""Category"": ""Property"", ""Employee"": 16 } },
                { ""type"": ""Feature"",
                  ""geometry"": { ""type"": ""Polygon"", ""coordinates"": [[
                    [0.00045, 0.00045], [0.0005, 0.00045], [0.0005, 0.0005], [0.00045, 0.0005], [0.00045, 0.00045]
                  ]] },
                  ""properties"": { ""Name"": ""Low Density Offices"", ""Object"": ""Building"", ""Category"": ""Property"", ""Employee"": 20 } }
              ]
            }";

            var districts = CartoProcessor.ParseDistricts(SampleTwoAdjacentDistricts);
            var buildings = CartoProcessor.ParseNamedBuildings(fixture);
            CartoProcessor.AssignBuildingsToDistricts(buildings, districts);

            string md = CartoProcessor.RenderDistrictMarkdown(districts.Single(d => d.Name == "Old Halverson"));

            // Three "Low Density Offices" → one bullet with × 3 and 48 employees total.
            md.Should().Contain("**Low Density Offices** (× 3)");
            md.Should().Contain("48 employees");

            // Halverson Crossing High School stays as its own bullet, no × annotation.
            md.Should().Contain("**Halverson Crossing High School**");
            md.Should().NotContain("Halverson Crossing High School** (×");
        }

        [Fact]
        public void RenderIndexMarkdown_summarizes_buildings_and_lists_unassigned()
        {
            var districts = CartoProcessor.ParseDistricts(SampleTwoAdjacentDistricts);
            var buildings = CartoProcessor.ParseNamedBuildings(SampleBuildings);
            CartoProcessor.AssignBuildingsToDistricts(buildings, districts);
            CartoProcessor.ComputeAdjacency(districts);
            string md = CartoProcessor.RenderIndexMarkdown(districts, buildings);

            md.Should().Contain("3 named building(s) total");
            md.Should().Contain("2 in a district");
            md.Should().Contain("1 on un-districted land");
            md.Should().Contain("### Outside any district");
            md.Should().Contain("**Lonely Lighthouse**");
        }

        [Theory]
        [InlineData(5, 5, true)]
        [InlineData(10, 5, false)]
        [InlineData(-1, 5, false)]
        [InlineData(5, -1, false)]
        public void PointInPolygon_classifies_unit_square(double x, double y, bool expected)
        {
            double[][] square = { new[] { 0d, 0d }, new[] { 10d, 0d }, new[] { 10d, 10d }, new[] { 0d, 10d }, new[] { 0d, 0d } };
            CartoProcessor.PointInPolygon(x, y, square).Should().Be(expected);
        }

        // -- MapTile parsing + footprint --

        // Same Area_Boundary.json shape but with MapTile features alongside
        // District. Carto emits these together; the processor must split them.
        const string SampleAreaWithMapTiles = @"{
          ""type"": ""FeatureCollection"",
          ""features"": [
            {
              ""type"": ""Feature"",
              ""geometry"": { ""type"": ""Polygon"", ""coordinates"": [[
                [0.000, 0.000], [0.005, 0.000], [0.005, 0.005], [0.000, 0.005], [0.000, 0.000]
              ]] },
              ""properties"": { ""Object"": ""MapTile"", ""Unlocked"": true }
            },
            {
              ""type"": ""Feature"",
              ""geometry"": { ""type"": ""Polygon"", ""coordinates"": [[
                [0.005, 0.000], [0.010, 0.000], [0.010, 0.005], [0.005, 0.005], [0.005, 0.000]
              ]] },
              ""properties"": { ""Object"": ""MapTile"", ""Unlocked"": false }
            },
            {
              ""type"": ""Feature"",
              ""geometry"": { ""type"": ""Polygon"", ""coordinates"": [[
                [0.001, 0.001], [0.002, 0.001], [0.002, 0.002], [0.001, 0.002], [0.001, 0.001]
              ]] },
              ""properties"": { ""Name"": ""Old Halverson"", ""Object"": ""District"" }
            }
          ]
        }";

        [Fact]
        public void ParseMapTiles_filters_to_MapTile_objects()
        {
            var tiles = CartoProcessor.ParseMapTiles(SampleAreaWithMapTiles);
            tiles.Should().HaveCount(2);
        }

        [Fact]
        public void ParseMapTiles_reads_unlocked_property()
        {
            var tiles = CartoProcessor.ParseMapTiles(SampleAreaWithMapTiles);
            tiles.Count(t => t.Unlocked).Should().Be(1);
            tiles.Count(t => !t.Unlocked).Should().Be(1);
        }

        [Fact]
        public void ComputeFootprint_uses_MapTile_bounds_when_present()
        {
            var districts = CartoProcessor.ParseDistricts(SampleAreaWithMapTiles);
            var tiles = CartoProcessor.ParseMapTiles(SampleAreaWithMapTiles);
            var fp = CartoProcessor.ComputeFootprint(tiles, districts);

            fp.HasGeometry.Should().BeTrue();
            fp.TileCount.Should().Be(2);
            fp.UnlockedCount.Should().Be(1);
            // 0.010° × 0.005° span → ~1113 m × ~556 m.
            fp.WidthM.Should().BeApproximately(0.010 * DegToM, 1.0);
            fp.HeightM.Should().BeApproximately(0.005 * DegToM, 1.0);
        }

        [Fact]
        public void ComputeFootprint_falls_back_to_district_bounds_when_no_tiles()
        {
            var districts = CartoProcessor.ParseDistricts(SampleTwoAdjacentDistricts);
            var fp = CartoProcessor.ComputeFootprint(new System.Collections.Generic.List<CartoProcessor.MapTile>(), districts);

            fp.HasGeometry.Should().BeTrue();
            fp.TileCount.Should().Be(0);
            // Two 0.001° squares side-by-side → 0.002° × 0.001° envelope.
            fp.WidthM.Should().BeApproximately(0.002 * DegToM, 1.0);
            fp.HeightM.Should().BeApproximately(0.001 * DegToM, 1.0);
        }

        [Fact]
        public void ComputeFootprint_returns_empty_when_no_tiles_and_no_districts()
        {
            var fp = CartoProcessor.ComputeFootprint(
                new System.Collections.Generic.List<CartoProcessor.MapTile>(),
                new System.Collections.Generic.List<CartoProcessor.District>());
            fp.HasGeometry.Should().BeFalse();
        }

        [Fact]
        public void RenderIndexMarkdown_includes_footprint_section_when_present()
        {
            var districts = CartoProcessor.ParseDistricts(SampleAreaWithMapTiles);
            var tiles = CartoProcessor.ParseMapTiles(SampleAreaWithMapTiles);
            CartoProcessor.RecenterCoordinates(districts, new System.Collections.Generic.List<CartoProcessor.Building>(),
                tiles, new System.Collections.Generic.List<CartoProcessor.Road>());
            var fp = CartoProcessor.ComputeFootprint(tiles, districts);

            string md = CartoProcessor.RenderIndexMarkdown(districts,
                new System.Collections.Generic.List<CartoProcessor.Building>(),
                new System.Collections.Generic.List<CartoProcessor.Road>(),
                fp);

            md.Should().Contain("## Map footprint");
            md.Should().Contain("2 total, 1 unlocked");
        }

        [Fact]
        public void RenderIndexMarkdown_omits_footprint_section_when_no_geometry()
        {
            string md = CartoProcessor.RenderIndexMarkdown(
                new System.Collections.Generic.List<CartoProcessor.District>(),
                new System.Collections.Generic.List<CartoProcessor.Building>(),
                new System.Collections.Generic.List<CartoProcessor.Road>(),
                new CartoProcessor.Footprint { HasGeometry = false });
            md.Should().NotContain("## Map footprint");
        }

        // -- Road parsing + rendering --

        // Two segments of a named highway, one anonymous side street.
        const string SampleNetworkCenterline = @"{
          ""type"": ""FeatureCollection"",
          ""features"": [
            {
              ""type"": ""Feature"",
              ""geometry"": { ""type"": ""LineString"", ""coordinates"": [
                [0.000, 0.000], [0.001, 0.000]
              ] },
              ""properties"": { ""Name"": ""Riverside Highway"", ""Object"": ""Road"", ""Category"": ""Car, Large"", ""Form"": ""Highway"", ""Length"": 250.0, ""Lane"": 4, ""Limit"": 100 }
            },
            {
              ""type"": ""Feature"",
              ""geometry"": { ""type"": ""LineString"", ""coordinates"": [
                [0.001, 0.000], [0.002, 0.000]
              ] },
              ""properties"": { ""Name"": ""Riverside Highway"", ""Object"": ""Road"", ""Category"": ""Car, Large"", ""Form"": ""Highway"", ""Length"": 250.0, ""Lane"": 4, ""Limit"": 100 }
            },
            {
              ""type"": ""Feature"",
              ""geometry"": { ""type"": ""LineString"", ""coordinates"": [
                [0.000, 0.001], [0.001, 0.001]
              ] },
              ""properties"": { ""Name"": """", ""Object"": ""Road"", ""Category"": ""Car, Small"", ""Form"": ""Street"", ""Length"": 110.0, ""Lane"": 2, ""Limit"": 30 }
            }
          ]
        }";

        [Fact]
        public void ParseRoads_reads_properties_and_geometry()
        {
            var roads = CartoProcessor.ParseRoads(SampleNetworkCenterline);
            roads.Should().HaveCount(3);

            var highway = roads.First(r => r.Name == "Riverside Highway");
            highway.Object.Should().Be("Road");
            highway.Category.Should().Be("Car, Large");
            highway.Form.Should().Be("Highway");
            highway.Length.Should().Be(250.0);
            highway.Lane.Should().Be(4);
            highway.Limit.Should().Be(100);
            highway.Centerline.Should().NotBeNull();
            highway.Centerline.Length.Should().Be(2);
        }

        [Fact]
        public void RenderRoadsMarkdown_groups_named_roads_and_summarizes_by_object()
        {
            var roads = CartoProcessor.ParseRoads(SampleNetworkCenterline);
            string md = CartoProcessor.RenderRoadsMarkdown(roads);

            md.Should().Contain("# Road network");
            md.Should().Contain("3 segment(s)");
            md.Should().Contain("Road:");                  // Object summary line
            md.Should().Contain("**Riverside Highway**");  // grouped, not duplicated
            md.Should().Contain("2 segments");             // segment-group annotation
            // Combined length 250 + 250 = 500 m → 0.50 km.
            md.Should().Contain("0.50 km");
        }

        [Fact]
        public void RenderRoadsMarkdown_reports_no_named_roads_when_all_anonymous()
        {
            string anonOnly = @"{
              ""type"": ""FeatureCollection"",
              ""features"": [
                { ""type"": ""Feature"",
                  ""geometry"": { ""type"": ""LineString"", ""coordinates"": [[0,0],[0.001,0]] },
                  ""properties"": { ""Name"": """", ""Object"": ""Road"", ""Length"": 111.0 } }
              ]
            }";
            var roads = CartoProcessor.ParseRoads(anonOnly);
            string md = CartoProcessor.RenderRoadsMarkdown(roads);
            md.Should().Contain("(none — the city has no player-named roads yet)");
        }

        [Fact]
        public void RenderIndexMarkdown_includes_road_network_summary_when_roads_present()
        {
            var roads = CartoProcessor.ParseRoads(SampleNetworkCenterline);
            string md = CartoProcessor.RenderIndexMarkdown(
                new System.Collections.Generic.List<CartoProcessor.District>(),
                new System.Collections.Generic.List<CartoProcessor.Building>(),
                roads,
                null);

            md.Should().Contain("## Road network");
            md.Should().Contain("3 segment(s) total, 2 named");
            md.Should().Contain("processed/roads.md");
        }

        [Fact]
        public void RenderRoadsMarkdown_emits_centroid_and_quadrant_for_named_groups()
        {
            // Recenter so the projected coordinates are zeroed around the
            // group centroid — that's the frame the agent sees in production.
            var roads = CartoProcessor.ParseRoads(SampleNetworkCenterline);
            CartoProcessor.RecenterCoordinates(
                new System.Collections.Generic.List<CartoProcessor.District>(),
                new System.Collections.Generic.List<CartoProcessor.Building>(),
                new System.Collections.Generic.List<CartoProcessor.MapTile>(),
                roads);
            string md = CartoProcessor.RenderRoadsMarkdown(roads);

            // The contract: each named road row carries a "centered (X, Y) in
            // the QQ" suffix so the agent can answer "where is X" without
            // touching raw GeoJSON.
            md.Should().Contain("centered (");
            md.Should().MatchRegex(@"in the (NE|NW|SE|SW)");
        }

        // Two distinct named roads whose centerlines share an endpoint at
        // (0.001, 0.001). That endpoint should land in a single intersection
        // bucket and surface in the rendered chunk. Lengths are bumped well
        // above the SVG label threshold (500 m) so the smoke tests can also
        // verify label rendering against this fixture; the actual segment
        // geometry stays short (single 0.001° edges).
        const string SampleNetworkWithIntersection = @"{
          ""type"": ""FeatureCollection"",
          ""features"": [
            { ""type"": ""Feature"",
              ""geometry"": { ""type"": ""LineString"", ""coordinates"": [
                [0.000, 0.000], [0.001, 0.001]
              ] },
              ""properties"": { ""Name"": ""Riverside Highway"", ""Object"": ""Road"", ""Category"": ""Highway"", ""Length"": 1500.0 }
            },
            { ""type"": ""Feature"",
              ""geometry"": { ""type"": ""LineString"", ""coordinates"": [
                [0.001, 0.001], [0.002, 0.000]
              ] },
              ""properties"": { ""Name"": ""Bridge Boulevard"", ""Object"": ""Road"", ""Category"": ""Car, Large"", ""Length"": 1500.0 }
            }
          ]
        }";

        [Fact]
        public void ComputeIntersections_finds_shared_endpoint_between_two_named_roads()
        {
            var roads = CartoProcessor.ParseRoads(SampleNetworkWithIntersection);
            var ix = CartoProcessor.ComputeIntersections(roads);

            ix.Should().HaveCount(1);
            ix[0].RoadNames.Should().BeEquivalentTo(new[] { "Bridge Boulevard", "Riverside Highway" });
            ix[0].Quadrant.Should().MatchRegex("^(NE|NW|SE|SW)$");
        }

        [Fact]
        public void ComputeIntersections_returns_empty_when_no_named_roads_share_endpoints()
        {
            // SampleNetworkCenterline has two segments of one named road
            // (Riverside Highway) plus an anonymous segment. The shared
            // endpoint is between two segments of the SAME name — that's
            // not an intersection.
            var roads = CartoProcessor.ParseRoads(SampleNetworkCenterline);
            var ix = CartoProcessor.ComputeIntersections(roads);
            ix.Should().BeEmpty();
        }

        [Fact]
        public void RenderRoadsMarkdown_lists_intersections_when_present()
        {
            var roads = CartoProcessor.ParseRoads(SampleNetworkWithIntersection);
            string md = CartoProcessor.RenderRoadsMarkdown(roads);

            md.Should().Contain("## Intersections of named roads");
            md.Should().Contain("Bridge Boulevard");
            md.Should().Contain("Riverside Highway");
            md.Should().Contain(" × ");  // separator between road names
        }

        [Fact]
        public void RenderRoadsMarkdown_omits_intersections_section_when_none()
        {
            var roads = CartoProcessor.ParseRoads(SampleNetworkCenterline);
            string md = CartoProcessor.RenderRoadsMarkdown(roads);
            md.Should().NotContain("## Intersections of named roads");
        }

        [Fact]
        public void RenderRoadsSvg_returns_null_for_empty_road_list()
        {
            string svg = CartoProcessor.RenderRoadsSvg(
                new System.Collections.Generic.List<CartoProcessor.Road>(),
                new System.Collections.Generic.List<CartoProcessor.District>(),
                new CartoProcessor.Footprint { HasGeometry = false });
            svg.Should().BeNull();
        }

        [Fact]
        public void RenderRoadsSvg_includes_named_road_label_and_intersection_dot()
        {
            var roads = CartoProcessor.ParseRoads(SampleNetworkWithIntersection);
            var districts = new System.Collections.Generic.List<CartoProcessor.District>();
            var fp = new CartoProcessor.Footprint
            {
                HasGeometry = true,
                MinX = -200, MaxX = 300, MinY = -200, MaxY = 300,
                WidthM = 500, HeightM = 500,
            };
            string svg = CartoProcessor.RenderRoadsSvg(roads, districts, fp);

            svg.Should().NotBeNull();
            svg.Should().StartWith("<?xml");
            svg.Should().Contain("<svg ");
            svg.Should().Contain("Riverside Highway");
            svg.Should().Contain("Bridge Boulevard");
            // Intersection dot layer present when the input has one.
            svg.Should().Contain("id=\"intersections\"");
            // North arrow always rendered.
            svg.Should().Contain("id=\"north\"");
        }

        [Fact]
        public void RenderRoadsSvg_falls_back_to_geometry_envelope_when_footprint_missing()
        {
            var roads = CartoProcessor.ParseRoads(SampleNetworkWithIntersection);
            string svg = CartoProcessor.RenderRoadsSvg(roads, null, null);
            svg.Should().NotBeNull();
            svg.Should().Contain("<svg ");
        }

        // -- Combined map (map.png) --

        // Synthetic elevation grid for the combined-map tests: 32×32 cells,
        // values ramp from 0 (NW corner) to 1000 (SE corner). NoData = -32768.
        static CityStoryMod.Storyteller.GeoTiffReader.Grid BuildSyntheticElevationGrid()
        {
            const int n = 32;
            var grid = new CityStoryMod.Storyteller.GeoTiffReader.Grid
            {
                Width = n,
                Height = n,
                Pixels = new int[n * n],
                NoData = -32768,
                ScaleX = 100,
                ScaleY = 100,
            };
            for (int r = 0; r < n; r++)
                for (int c = 0; c < n; c++)
                    grid.Pixels[r * n + c] = (r + c) * 16;  // 0 .. 992
            return grid;
        }

        // Synthetic depth grid matching the elevation grid dimensions, with
        // a 4×4 water patch in the NE corner (rows 2-5, cols 25-28).
        static CityStoryMod.Storyteller.GeoTiffReader.Grid BuildSyntheticDepthGrid()
        {
            const int n = 32;
            var grid = new CityStoryMod.Storyteller.GeoTiffReader.Grid
            {
                Width = n,
                Height = n,
                Pixels = new int[n * n],
                NoData = -32768,
                ScaleX = 100,
                ScaleY = 100,
            };
            for (int i = 0; i < grid.Pixels.Length; i++) grid.Pixels[i] = -32768;
            for (int r = 2; r < 6; r++)
                for (int c = 25; c < 29; c++)
                    grid.Pixels[r * n + c] = 10;  // 10 m deep
            return grid;
        }

        // Reads the PNG signature + IHDR width/height out of an encoded buffer.
        // Just enough to assert the renderer produced a valid, correctly-sized
        // image without pulling in a full PNG decoder.
        static (int width, int height) ReadPngHeader(byte[] png)
        {
            byte[] sig = { 137, 80, 78, 71, 13, 10, 26, 10 };
            png.Should().HaveCountGreaterThan(24);
            for (int i = 0; i < 8; i++) png[i].Should().Be(sig[i]);
            // First chunk after the signature must be IHDR.
            ((char)png[12]).Should().Be('I');
            ((char)png[13]).Should().Be('H');
            ((char)png[14]).Should().Be('D');
            ((char)png[15]).Should().Be('R');
            int w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
            int h = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
            return (w, h);
        }

        [Fact]
        public void RenderCombinedMapPng_returns_null_when_no_inputs()
        {
            byte[] png = CartoProcessor.RenderCombinedMapPng(
                new System.Collections.Generic.List<CartoProcessor.Road>(),
                new System.Collections.Generic.List<CartoProcessor.District>(),
                new System.Collections.Generic.List<CartoProcessor.Building>(),
                new CartoProcessor.Footprint { HasGeometry = false },
                null, null, null, null);
            png.Should().BeNull();
        }

        [Fact]
        public void RenderCombinedMapPng_emits_valid_sized_image_when_elevation_present()
        {
            var elev = BuildSyntheticElevationGrid();
            var elevSummary = CartoProcessor.ComputeElevationSummary(elev);
            var fp = new CartoProcessor.Footprint
            {
                HasGeometry = true,
                MinX = 0, MaxX = 3200, MinY = 0, MaxY = 3200,
                WidthM = 3200, HeightM = 3200,
            };
            byte[] png = CartoProcessor.RenderCombinedMapPng(
                new System.Collections.Generic.List<CartoProcessor.Road>(),
                new System.Collections.Generic.List<CartoProcessor.District>(),
                new System.Collections.Generic.List<CartoProcessor.Building>(),
                fp, elev, null, elevSummary, null);

            png.Should().NotBeNull();
            // worldW=3200, scale=1200/3200=0.375 → drawW=1200, +2*24 padding = 1248.
            var (w, h) = ReadPngHeader(png);
            w.Should().Be(1248);
            h.Should().Be(1248);
        }

        [Fact]
        public void RenderCombinedMapPng_renders_with_water_and_roads()
        {
            var roads = CartoProcessor.ParseRoads(SampleNetworkWithIntersection);
            var elev = BuildSyntheticElevationGrid();
            var depth = BuildSyntheticDepthGrid();
            var elevSummary = CartoProcessor.ComputeElevationSummary(elev);
            var waterSummary = CartoProcessor.ComputeWaterSummary(depth);
            var fp = new CartoProcessor.Footprint
            {
                HasGeometry = true,
                MinX = -200, MaxX = 300, MinY = -200, MaxY = 300,
                WidthM = 500, HeightM = 500,
            };
            byte[] png = CartoProcessor.RenderCombinedMapPng(
                roads,
                new System.Collections.Generic.List<CartoProcessor.District>(),
                new System.Collections.Generic.List<CartoProcessor.Building>(),
                fp, elev, depth, elevSummary, waterSummary);

            png.Should().NotBeNull();
            var (w, h) = ReadPngHeader(png);
            w.Should().BeGreaterThan(0);
            h.Should().BeGreaterThan(0);
        }

        [Fact]
        public void TerrainCellRgba_shades_water_blue_and_skips_nodata()
        {
            // Water cell (positive depth) → blue ramp: B channel dominates.
            CartoProcessor.TerrainCellRgba(0, 5, 0, 1000, 10, out var water).Should().BeTrue();
            water.B.Should().BeGreaterThan(water.R);
            water.B.Should().BeGreaterThan(water.G);

            // NoData land sentinel → skipped.
            CartoProcessor.TerrainCellRgba(int.MinValue, 0, 0, 1000, 10, out _).Should().BeFalse();

            // High land → light bare-rock end of the ramp (all channels high).
            CartoProcessor.TerrainCellRgba(1000, 0, 0, 1000, 10, out var peak).Should().BeTrue();
            peak.R.Should().BeGreaterThan(200);
            peak.G.Should().BeGreaterThan(200);
            peak.B.Should().BeGreaterThan(200);
        }

        [Theory]
        // Service categories classify straight off "Public, <Type>".
        [InlineData("Halverson Fire & Rescue", "Public, Fire", "Building", 0, 12, CartoProcessor.BuildingClass.ServiceFire)]
        [InlineData("Small Police Station", "Public, Police", "Building", 0, 8, CartoProcessor.BuildingClass.ServicePolice)]
        [InlineData("Medical Clinic", "Public, Health", "Building", 0, 26, CartoProcessor.BuildingClass.ServiceHealth)]
        [InlineData("High School", "Public, Education", "Building", 0, 90, CartoProcessor.BuildingClass.ServiceEducation)]
        [InlineData("Wind Turbine", "Public, Power", "Building", 0, 0, CartoProcessor.BuildingClass.ServicePower)]
        [InlineData("Water Tower", "Public, Water", "Building", 0, 0, CartoProcessor.BuildingClass.ServiceWater)]
        [InlineData("Sewage Outlet", "Public, Sewage", "Building", 0, 0, CartoProcessor.BuildingClass.ServiceWater)]
        [InlineData("Tiny City Park", "Public, Park", "Building", 0, 0, CartoProcessor.BuildingClass.ServicePark)]
        [InlineData("Cargo Terminal", "Public, Transportation", "Building", 0, 32, CartoProcessor.BuildingClass.ServiceTransport)]
        [InlineData("Radio Mast", "Public, Communication", "Building", 0, 10, CartoProcessor.BuildingClass.ServiceOther)]
        // Decoration is its own (skipped on the map).
        [InlineData("Cairn 03", "Decoration", "Building", 0, 0, CartoProcessor.BuildingClass.Decoration)]
        // Zoned "Property" buildings infer from the name first.
        [InlineData("NA Low Density Housing", "Property", "Building", 3, 0, CartoProcessor.BuildingClass.Residential)]
        [InlineData("NA Mixed Housing", "Property", "Building", 36, 13, CartoProcessor.BuildingClass.Residential)]
        [InlineData("NA Low Density Business", "Property", "Building", 0, 15, CartoProcessor.BuildingClass.Commercial)]
        [InlineData("Low Density Offices", "Property", "Building", 0, 16, CartoProcessor.BuildingClass.Office)]
        [InlineData("Crossing Mill 1", "Property", "Building", 0, 9, CartoProcessor.BuildingClass.Industrial)]
        [InlineData("Cascade Composite Products", "Property", "Building", 0, 112, CartoProcessor.BuildingClass.Industrial)]
        // No use keyword → occupancy fallback.
        [InlineData("Hayloft Steakhouse", "Property", "Building", 0, 14, CartoProcessor.BuildingClass.Commercial)]
        [InlineData("Mystery Renamed Home", "Property", "Building", 5, 0, CartoProcessor.BuildingClass.Residential)]
        [InlineData("Brennan Antiques", "Property", "Building", 0, 0, CartoProcessor.BuildingClass.Other)]
        // Extractor objects are landscape-significant industry.
        [InlineData("Oil Field", "Property", "Extractor", 0, 20, CartoProcessor.BuildingClass.Industrial)]
        public void ClassifyBuildingForMap_classifies(
            string name, string category, string objectType, int resident, int employee,
            CartoProcessor.BuildingClass expected)
        {
            CartoProcessor.ClassifyBuildingForMap(name, category, objectType, resident, employee)
                .Should().Be(expected);
        }

        [Fact]
        public void ParseBuildingsForMap_keeps_all_buildings_with_footprints()
        {
            // Two buildings: a generic house (Property) and a fire station
            // (Public, Fire). Coordinates in Carto's degree frame.
            const string json = @"{
              ""type"": ""FeatureCollection"",
              ""features"": [
                { ""type"": ""Feature"",
                  ""properties"": { ""Object"": ""Building"", ""Name"": ""NA Low Density Housing"", ""Category"": ""Property"", ""Resident"": 3, ""Employee"": 0 },
                  ""geometry"": { ""type"": ""Polygon"", ""coordinates"": [[[0.0010,0.0010],[0.0011,0.0010],[0.0011,0.0011],[0.0010,0.0011],[0.0010,0.0010]]] } },
                { ""type"": ""Feature"",
                  ""properties"": { ""Object"": ""Building"", ""Name"": ""Halverson Fire & Rescue"", ""Category"": ""Public, Fire"", ""Resident"": 0, ""Employee"": 12 },
                  ""geometry"": { ""type"": ""Polygon"", ""coordinates"": [[[0.0020,0.0020],[0.0021,0.0020],[0.0021,0.0021],[0.0020,0.0021],[0.0020,0.0020]]] } }
              ]
            }";

            var buildings = CartoProcessor.ParseBuildingsForMap(json);
            buildings.Should().HaveCount(2);
            buildings.Should().OnlyContain(b => b.Polygon != null && b.Polygon.Length >= 4);
            buildings.Should().Contain(b => b.MapClass == CartoProcessor.BuildingClass.Residential);
            buildings.Should().Contain(b => b.MapClass == CartoProcessor.BuildingClass.ServiceFire);
        }

        [Fact]
        public void DownsampleGrid_block_averages_correctly()
        {
            // 4×4 grid → 2×2 downsample. Each 2×2 block averages to a single cell.
            // Block 0 (rows 0-1, cols 0-1): values 0, 1, 4, 5 → mean 2.
            // Block 1 (rows 0-1, cols 2-3): values 2, 3, 6, 7 → mean 4 (integer division).
            // Block 2 (rows 2-3, cols 0-1): values 8, 9, 12, 13 → mean 10.
            // Block 3 (rows 2-3, cols 2-3): values 10, 11, 14, 15 → mean 12.
            var grid = new CityStoryMod.Storyteller.GeoTiffReader.Grid
            {
                Width = 4,
                Height = 4,
                NoData = -32768,
                Pixels = new int[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 },
            };
            var ds = CartoProcessor.DownsampleGrid(grid, 2, 2);
            ds.Should().Equal(new[] { 2, 4, 10, 12 });
        }

        [Fact]
        public void DownsampleGrid_treats_full_nodata_block_as_sentinel()
        {
            var grid = new CityStoryMod.Storyteller.GeoTiffReader.Grid
            {
                Width = 2, Height = 2, NoData = -32768,
                Pixels = new[] { -32768, -32768, -32768, -32768 },
            };
            var ds = CartoProcessor.DownsampleGrid(grid, 1, 1);
            ds[0].Should().Be(int.MinValue);
        }

        [Fact]
        public void RenderRoadsSvg_drops_short_non_highway_labels()
        {
            // A short non-highway named road (under 500 m) should still get
            // a stroke in the SVG but no text label, to keep the diagram
            // readable on cities full of CS2-default decorations.
            string fixture = @"{
              ""type"": ""FeatureCollection"",
              ""features"": [
                { ""type"": ""Feature"",
                  ""geometry"": { ""type"": ""LineString"", ""coordinates"": [[0,0],[0.001,0.001]] },
                  ""properties"": { ""Name"": ""Tiny Roundabout"", ""Object"": ""Road"", ""Category"": ""Car, Small"", ""Length"": 80.0 } },
                { ""type"": ""Feature"",
                  ""geometry"": { ""type"": ""LineString"", ""coordinates"": [[0.001,0.001],[0.002,0.002]] },
                  ""properties"": { ""Name"": ""Long Highway"", ""Object"": ""Road"", ""Category"": ""Highway"", ""Length"": 5000.0 } }
              ]
            }";
            var roads = CartoProcessor.ParseRoads(fixture);
            string svg = CartoProcessor.RenderRoadsSvg(roads, null, null);

            svg.Should().Contain("Long Highway");
            svg.Should().NotContain(">Tiny Roundabout<");  // no text label
        }

        [Fact]
        public void RenderRoadsSvg_de_collides_overlapping_labels()
        {
            // Two long named roads whose centroids fall at nearly the same
            // point. The label de-collision step should keep only the first
            // (highway → wins the priority sort) and drop the second.
            string fixture = @"{
              ""type"": ""FeatureCollection"",
              ""features"": [
                { ""type"": ""Feature"",
                  ""geometry"": { ""type"": ""LineString"", ""coordinates"": [[-0.001,0.000],[0.001,0.000]] },
                  ""properties"": { ""Name"": ""North Pike"", ""Object"": ""Road"", ""Category"": ""Highway"", ""Length"": 2000.0 } },
                { ""type"": ""Feature"",
                  ""geometry"": { ""type"": ""LineString"", ""coordinates"": [[0.000,-0.001],[0.000,0.001]] },
                  ""properties"": { ""Name"": ""North Pike East"", ""Object"": ""Road"", ""Category"": ""Highway"", ""Length"": 2000.0 } }
              ]
            }";
            var roads = CartoProcessor.ParseRoads(fixture);
            string svg = CartoProcessor.RenderRoadsSvg(roads, null, null);

            // Both roads still draw their strokes; only labels get filtered.
            // The first (highway, sorted alphabetically by length tiebreak)
            // should keep its label; the second drops to avoid overlap.
            int northPikeCount = System.Text.RegularExpressions.Regex.Matches(svg, ">North Pike<").Count;
            int northPikeEastCount = System.Text.RegularExpressions.Regex.Matches(svg, ">North Pike East<").Count;
            (northPikeCount + northPikeEastCount).Should().Be(1, "exactly one of the two overlapping labels should survive de-collision");
        }

        [Fact]
        public void RenderRoadsSvg_escapes_road_names_with_xml_specials()
        {
            // Carto can in principle pass through any string the player typed
            // (CustomName flows up from CS2). The label layer must HTML-escape
            // to keep the SVG well-formed.
            // Geometry needs spread on both axes — a single horizontal segment
            // yields worldH = 0 and the renderer correctly bails to null.
            // Length above the 500 m label threshold so it actually renders.
            string fixture = @"{
              ""type"": ""FeatureCollection"",
              ""features"": [
                { ""type"": ""Feature"",
                  ""geometry"": { ""type"": ""LineString"", ""coordinates"": [[0,0],[0.001,0.001]] },
                  ""properties"": { ""Name"": ""A & B <Road>"", ""Object"": ""Road"", ""Category"": ""Highway"", ""Length"": 1500.0 } }
              ]
            }";
            var roads = CartoProcessor.ParseRoads(fixture);
            string svg = CartoProcessor.RenderRoadsSvg(roads, null, null);
            svg.Should().Contain("A &amp; B &lt;Road&gt;");
            // The literal raw "<Road>" sequence must not survive — it would
            // be a stray tag inside an SVG text node.
            svg.Should().NotContain(">A & B <Road><");
        }

        [Fact]
        public void RenderIndexMarkdown_tags_unique_unassigned_buildings_with_quadrant()
        {
            var buildings = new System.Collections.Generic.List<CartoProcessor.Building>
            {
                // One unique landmark in the SW (negative x, negative y).
                new CartoProcessor.Building { Name = "Lonely Lighthouse", Category = "Landmark", CentroidX = -500, CentroidY = -300 },
                // Two cairns in the same quadrant (NE) — should collapse to "(× 2) ... in the NE".
                new CartoProcessor.Building { Name = "Cairn", Category = "Decoration", CentroidX = 100, CentroidY = 200 },
                new CartoProcessor.Building { Name = "Cairn", Category = "Decoration", CentroidX = 150, CentroidY = 250 },
                // Two ruins scattered across NW and SE — should report "scattered".
                new CartoProcessor.Building { Name = "Ruin", Category = "Decoration", CentroidX = -100, CentroidY = 200 },
                new CartoProcessor.Building { Name = "Ruin", Category = "Decoration", CentroidX = 100, CentroidY = -200 },
            };

            string md = CartoProcessor.RenderIndexMarkdown(
                new System.Collections.Generic.List<CartoProcessor.District>(),
                buildings);

            md.Should().Contain("**Lonely Lighthouse** — Landmark in the SW");
            md.Should().Contain("**Cairn** (× 2) — Decoration in the NE");
            md.Should().Contain("**Ruin** (× 2) — Decoration scattered (");
        }

        // -- Cycle 2b: terrain / water classifiers + quadrant logic --

        [Theory]
        // Glenville (the real city) — stdev 28 on a 191 m range, mean 96.
        // Relief / stdev = 6.8× → triggers the "localized high point" tag
        // because the base label is flatish.
        [InlineData(28, 191, 96, "Mostly flat, with a localized high point.")]
        // Same ratio but a "gently rolling" base — still flatish, still triggers.
        [InlineData(50, 300, 100, "Gently rolling, with a localized high point.")]
        // Uniform flat plain — small relief, no outlier.
        [InlineData(15, 60, 30, "Mostly flat.")]
        // Gently rolling farmland with proportional relief.
        [InlineData(50, 200, 100, "Gently rolling.")]
        // Hilly: even with a 6× relief/stdev ratio, suppress the suffix —
        // "Hilly with a localized high point" reads as redundant because
        // hills imply high points.
        [InlineData(100, 600, 200, "Hilly.")]
        [InlineData(100, 350, 200, "Hilly.")]
        // Rugged / mountainous: same suppression rule.
        [InlineData(200, 1500, 500, "Rugged / mountainous.")]
        [InlineData(200, 900, 500, "Rugged / mountainous.")]
        public void ClassifyTerrain_returns_human_reading(double stdev, double relief, double mean, string expected)
        {
            CartoProcessor.ClassifyTerrain(stdev, relief, mean).Should().Be(expected);
        }

        [Fact]
        public void ClassifyWater_landlocked_when_essentially_no_water()
        {
            CartoProcessor.ClassifyWater(0.5, new[] { 0d, 0d, 0d, 0d }, 0).Should().Be("Essentially landlocked.");
        }

        [Fact]
        public void ClassifyWater_archipelago_requires_dominant_water_and_fragmentation()
        {
            // Comptche (Archipelago map): 60% water, complexity 11.4, quadrants
            // 55-69%. All three signals say "archipelago." Uniform distribution
            // (max-min = 14) reads as evenly across the map.
            string r = CartoProcessor.ClassifyWater(60, new[] { 61d, 55d, 69d, 56d }, 11.4);
            r.Should().Contain("Water-dominated");
            r.Should().Contain("archipelago");
            r.Should().Contain("evenly across the map");
        }

        [Fact]
        public void ClassifyWater_river_system_when_heavy_water_fragmented_but_uneven()
        {
            // Mayworth (Verdant Vale map): 31% water, complexity 11.2, quadrants
            // 24/21/35/46. Fragmented shoreline but only mid-coverage water —
            // a river network with a southern coast, NOT an archipelago. SE
            // is the dominant quadrant (46%); spread 25 > 15 triggers weighting.
            string r = CartoProcessor.ClassifyWater(31, new[] { 24d, 21d, 35d, 46d }, 11.2);
            r.Should().Contain("Heavy water");
            r.Should().Contain("river system threaded with lakes");
            r.Should().NotContain("archipelago");
            r.Should().Contain("weighted to the SE");
        }

        [Fact]
        public void ClassifyWater_major_lake_when_heavy_water_low_complexity()
        {
            // Heavy water (30%) but a smooth-edge shoreline (complexity 1.5)
            // is a single large body, not a river network. Distributed across
            // 4 quadrants and uniform → "evenly across the map."
            string r = CartoProcessor.ClassifyWater(30, new[] { 28d, 30d, 32d, 30d }, 1.5);
            r.Should().Contain("Heavy water");
            r.Should().Contain("major lake or coastline");
            r.Should().NotContain("river system");
        }

        [Fact]
        public void ClassifyWater_concentrated_coast_when_water_in_one_quadrant()
        {
            // 30% water all in the SE quadrant (a corner coast), low complexity.
            string r = CartoProcessor.ClassifyWater(30, new[] { 0d, 0d, 0d, 60d }, 2.0);
            r.Should().Contain("Heavy water");
            r.Should().Contain("major lake or coastline");
            r.Should().Contain("concentrated in the SE");
        }

        [Fact]
        public void ClassifyWater_open_sea_when_water_dominant_but_smooth()
        {
            // 70% water, low complexity — a sea / ocean rather than islands.
            string r = CartoProcessor.ClassifyWater(70, new[] { 68d, 72d, 70d, 70d }, 1.5);
            r.Should().Contain("Water-dominated");
            r.Should().Contain("open sea or vast single body");
            r.Should().NotContain("archipelago");
        }

        [Theory]
        // Width 4, Height 4 → halfW=2, halfH=2. Quadrants (using row-major idx):
        //   NW = idx { 0,1, 4,5 }, NE = { 2,3, 6,7 }, SW = { 8,9, 12,13 }, SE = { 10,11, 14,15 }.
        [InlineData(0, 4, 4, "NW")]
        [InlineData(3, 4, 4, "NE")]
        [InlineData(12, 4, 4, "SW")]
        [InlineData(15, 4, 4, "SE")]
        public void QuadrantOf_maps_pixel_index_to_compass_label(long idx, int w, int h, string expected)
        {
            CartoProcessor.QuadrantOf(idx, w, h).Should().Be(expected);
        }

        [Fact]
        public void QuadrantOf_returns_null_for_invalid_index()
        {
            CartoProcessor.QuadrantOf(-1, 4, 4).Should().BeNull();
        }

        // -- Cycle 3c: coastline extraction --
        //
        // We exercise TryProcessWater end-to-end by writing a synthetic
        // Depth.tif into a temp dir, then asserting the returned
        // RasterSummary carries the right coastline counts. The TIFF format
        // is the same one GeoTiffReaderTests builds; we re-use that shape
        // here via a small helper.

        static byte[] BuildDepthTiff(int width, int height, short[] pixels, double scaleM)
        {
            int bps = width * 2;
            int stripsStart = 8;
            int offsetsTableStart = stripsStart + bps * height;
            int byteCountsTableStart = offsetsTableStart + 4 * height;
            int scaleStart = byteCountsTableStart + 4 * height;
            int ifdStart = scaleStart + 8 * 3;

            using var ms = new System.IO.MemoryStream();
            using var bw = new System.IO.BinaryWriter(ms);

            bw.Write((byte)'I'); bw.Write((byte)'I');
            bw.Write((short)42);
            bw.Write(ifdStart);

            for (int i = 0; i < pixels.Length; i++) bw.Write(pixels[i]);
            for (int i = 0; i < height; i++) bw.Write(stripsStart + i * bps);
            for (int i = 0; i < height; i++) bw.Write(bps);
            bw.Write(scaleM); bw.Write(scaleM); bw.Write(0d);

            void Entry(ushort tag, ushort type, uint count, uint v) { bw.Write(tag); bw.Write(type); bw.Write(count); bw.Write(v); }
            bw.Write((ushort)8);
            Entry(256, 3, 1, (uint)width);
            Entry(257, 3, 1, (uint)height);
            Entry(258, 3, 1, 16);
            Entry(259, 3, 1, 1);
            Entry(273, 4, (uint)height, (uint)offsetsTableStart);
            Entry(277, 3, 1, 1);
            Entry(339, 3, 1, 2);
            Entry(33550, 12, 3, (uint)scaleStart);
            bw.Write(0u);
            return ms.ToArray();
        }

        // Scratch directory mirroring Carto's layout: tempRoot/GeoTIFF/Depth.tif.
        // Caller deletes tempRoot after the test.
        static string WriteSyntheticDepthRaster(int w, int h, short[] pixels, double scaleM)
        {
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "citystorymod-test-" + System.Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "GeoTIFF"));
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(root, "GeoTIFF", "Depth.tif"), BuildDepthTiff(w, h, pixels, scaleM));
            return root;
        }

        [Fact]
        public void TryProcessWater_counts_coastline_cells_around_an_inland_lake()
        {
            // 6×6 grid. 2×2 inner block of water surrounded by land. Land = -32768.
            // Water cells (4 of them) all sit on the boundary, so all 4 are coastline.
            const short L = -32768;
            short[] pixels = {
                L, L, L, L, L, L,
                L, L, L, L, L, L,
                L, L, 5, 5, L, L,
                L, L, 5, 5, L, L,
                L, L, L, L, L, L,
                L, L, L, L, L, L,
            };
            string root = WriteSyntheticDepthRaster(6, 6, pixels, 10.0 /* m/pixel */);
            try
            {
                string processed = System.IO.Path.Combine(root, "processed");
                System.IO.Directory.CreateDirectory(processed);
                var summary = CartoProcessor.TryProcessWater(root, processed);

                summary.Should().NotBeNull();
                summary.WaterCells.Should().Be(4);
                summary.CoastlineCells.Should().Be(4);
                // 4 cells × 10 m = 40 m approximate shoreline.
                summary.CoastlineLengthM.Should().BeApproximately(40, 0.001);
            }
            finally
            {
                System.IO.Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void TryProcessWater_treats_map_edge_water_as_coastline()
        {
            // 4×4 grid, all water. Every cell is on the map edge or one step
            // inside it; all map-edge water cells count as coastline because
            // the map edge functions as "out of bounds" (treated like land).
            short[] pixels = new short[16];
            for (int i = 0; i < 16; i++) pixels[i] = 3;
            string root = WriteSyntheticDepthRaster(4, 4, pixels, 5.0);
            try
            {
                string processed = System.IO.Path.Combine(root, "processed");
                System.IO.Directory.CreateDirectory(processed);
                var summary = CartoProcessor.TryProcessWater(root, processed);

                summary.WaterCells.Should().Be(16);
                // Map edge = 12 cells (perimeter of 4×4); inner 2×2 has no
                // out-of-bounds neighbors so it's not coastline.
                summary.CoastlineCells.Should().Be(12);
            }
            finally
            {
                System.IO.Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void TryProcessWater_shoreline_ratio_near_1_for_compact_basin()
        {
            // 10×10 grid with a 6×6 block of water in the middle, no land
            // touching the map edge — a near-round basin. Shoreline ratio
            // should be in the "compact basin" range (~ 1.3 for a square,
            // higher than 1.0 because square is less efficient than circle).
            const short L = -32768;
            short[] pixels = new short[100];
            for (int r = 0; r < 10; r++)
                for (int c = 0; c < 10; c++)
                    pixels[r * 10 + c] = (r >= 2 && r < 8 && c >= 2 && c < 8) ? (short)2 : L;

            string root = WriteSyntheticDepthRaster(10, 10, pixels, 1.0);
            try
            {
                string processed = System.IO.Path.Combine(root, "processed");
                System.IO.Directory.CreateDirectory(processed);
                var summary = CartoProcessor.TryProcessWater(root, processed);

                summary.WaterCells.Should().Be(36);
                // Perimeter of a 6×6 inner block = 20 cells (4 corners + 16 edges).
                summary.CoastlineCells.Should().Be(20);
                // Ratio ≈ 20 / sqrt(36π) ≈ 20 / 10.63 ≈ 1.88. Round basin band.
                summary.ShorelineRatio.Should().BeInRange(1.5, 2.5);
            }
            finally
            {
                System.IO.Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void RenderIndexMarkdown_dedupes_outside_district_decorations()
        {
            // Simulate CS2's pre-populated decoration pattern: many cairns
            // and stone monuments, all with empty district assignment, all
            // sharing the same Name. The new render should collapse repeats.
            var buildings = new System.Collections.Generic.List<CartoProcessor.Building>();
            for (int i = 0; i < 7; i++)
                buildings.Add(new CartoProcessor.Building { Name = "Cairn 01", Category = "Decoration" });
            for (int i = 0; i < 4; i++)
                buildings.Add(new CartoProcessor.Building { Name = "Old Mill Ruins", Category = "Decoration" });
            buildings.Add(new CartoProcessor.Building { Name = "Castle Ruins", Category = "Decoration" });

            string md = CartoProcessor.RenderIndexMarkdown(
                new System.Collections.Generic.List<CartoProcessor.District>(),
                buildings);

            // Each unique name should appear exactly once.
            int cairnCount = (md.Length - md.Replace("**Cairn 01**", "").Length) / "**Cairn 01**".Length;
            cairnCount.Should().Be(1);
            md.Should().Contain("**Cairn 01** (× 7)");
            md.Should().Contain("**Old Mill Ruins** (× 4)");
            // Castle Ruins is unique — no × annotation.
            md.Should().Contain("**Castle Ruins**");
            md.Should().NotContain("Castle Ruins** (×");
        }
    }
}
