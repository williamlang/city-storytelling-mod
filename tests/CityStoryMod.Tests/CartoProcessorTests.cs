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

        // -- Cycle 2b: terrain / water classifiers + quadrant logic --

        [Theory]
        // Glenville (the real city) — stdev 28 on a 191 m range, mean 96.
        // Relief / stdev = 6.8x → triggers the "localized high point" tag.
        [InlineData(28, 191, 96, "Mostly flat, with a localized high point.")]
        // Uniform flat plain — small stdev, small relief, no high point.
        [InlineData(15, 60, 30, "Mostly flat.")]
        // Gently rolling farmland.
        [InlineData(50, 200, 100, "Gently rolling.")]
        [InlineData(100, 350, 200, "Hilly.")]
        [InlineData(200, 900, 500, "Rugged / mountainous.")]
        public void ClassifyTerrain_returns_human_reading(double stdev, double relief, double mean, string expected)
        {
            CartoProcessor.ClassifyTerrain(stdev, relief, mean).Should().Be(expected);
        }

        [Fact]
        public void ClassifyWater_landlocked_when_essentially_no_water()
        {
            CartoProcessor.ClassifyWater(0.5, new[] { 0d, 0d, 0d, 0d }).Should().Be("Essentially landlocked.");
        }

        [Fact]
        public void ClassifyWater_flags_lake_district_when_spread_across_quadrants()
        {
            // 36% water, spread across NW + NE + SW quadrants (Glenville-ish).
            string r = CartoProcessor.ClassifyWater(36, new[] { 25d, 30d, 22d, 5d });
            r.Should().Contain("Heavy water");
            r.Should().Contain("spread across most of the map");
            r.Should().Contain("complex lake district");
        }

        [Fact]
        public void ClassifyWater_flags_coastline_when_water_is_concentrated()
        {
            // 30% water all in the SE quadrant (a corner coast).
            string r = CartoProcessor.ClassifyWater(30, new[] { 0d, 0d, 0d, 60d });
            r.Should().Contain("Heavy water");
            r.Should().Contain("concentrated on one side");
            r.Should().Contain("coastline or large concentrated lake");
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
