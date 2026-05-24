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
    }
}
