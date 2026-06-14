using CityStoryMod.Storyteller;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CityStoryMod.Tests
{
    public class QuickstartConfigTests
    {
        public class BuildConfigBlock
        {
            // A representative payload mirroring the UI's foundCity() JSON
            // (QuickstartWizard.tsx submit()).
            const string FullJson = @"{
                ""region"": ""Europe"",
                ""name"": ""Selkirk Falls"",
                ""tone"": ""noir"",
                ""focus"": [""citizens"", ""civic""],
                ""player_role"": ""character"",
                ""player_character_name"": ""Mara Vance"",
                ""real_world_refs"": ""fictional"",
                ""cast_density"": ""balanced"",
                ""content_maturity"": ""pg-13"",
                ""secrets_visibility"": ""hidden"",
                ""levelup_storylines"": true,
                ""storyteller_proactivity"": ""on-request"",
                ""git_versioning"": false,
                ""integrations"": []
            }";

            [Fact]
            public void wraps_with_the_begin_and_end_markers()
            {
                string block = QuickstartConfig.BuildConfigBlock(FullJson);
                block.Should().StartWith("<<QUICKSTART_CONFIG>>\n");
                block.Should().EndWith("\n<<END_CONFIG>>");
            }

            [Fact]
            public void emits_every_field_as_a_key_value_line()
            {
                string block = QuickstartConfig.BuildConfigBlock(FullJson);
                block.Should().Contain("region: Europe");
                block.Should().Contain("name: Selkirk Falls");
                block.Should().Contain("tone: noir");
                block.Should().Contain("player_role: character");
                block.Should().Contain("player_character_name: Mara Vance");
                block.Should().Contain("real_world_refs: fictional");
                block.Should().Contain("cast_density: balanced");
                block.Should().Contain("content_maturity: pg-13");
                block.Should().Contain("secrets_visibility: hidden");
                block.Should().Contain("storyteller_proactivity: on-request");
            }

            [Fact]
            public void joins_array_fields_with_commas()
            {
                string block = QuickstartConfig.BuildConfigBlock(FullJson);
                block.Should().Contain("focus: citizens, civic");
            }

            [Fact]
            public void renders_booleans_lowercase()
            {
                string block = QuickstartConfig.BuildConfigBlock(FullJson);
                block.Should().Contain("levelup_storylines: true");
                block.Should().Contain("git_versioning: false");
            }

            [Fact]
            public void empty_arrays_render_as_an_empty_value()
            {
                string block = QuickstartConfig.BuildConfigBlock(FullJson);
                block.Should().Contain("integrations: \n").And.NotContain("integrations: [");
            }

            [Theory]
            [InlineData(@"{""name"": """"}")]
            [InlineData(@"{""name"": ""   ""}")]
            [InlineData(@"{}")]
            public void blank_or_missing_name_becomes_suggest(string json)
            {
                QuickstartConfig.BuildConfigBlock(json).Should().Contain("name: (suggest)");
            }

            [Fact]
            public void literal_name_is_trimmed_and_kept()
            {
                QuickstartConfig.BuildConfigBlock(@"{""name"": ""  Port Haldane  ""}")
                    .Should().Contain("name: Port Haldane");
            }

            [Theory]
            [InlineData(null)]
            [InlineData("")]
            [InlineData("not json {{{")]
            public void unparseable_input_yields_an_all_blank_block(string json)
            {
                string block = QuickstartConfig.BuildConfigBlock(json);
                // Still a well-formed block, name defaults to suggest, others blank.
                block.Should().StartWith("<<QUICKSTART_CONFIG>>");
                block.Should().EndWith("<<END_CONFIG>>");
                block.Should().Contain("name: (suggest)");
                block.Should().Contain("region: \n");
            }

            [Fact]
            public void omitted_field_renders_as_blank_so_prose_can_ask_it()
            {
                // region present, tone absent → tone line is blank, region filled.
                string block = QuickstartConfig.BuildConfigBlock(@"{""region"": ""Asia""}");
                block.Should().Contain("region: Asia");
                block.Should().Contain("tone: \n");
            }
        }

        public class NormalizeWizardDone
        {
            [Fact]
            public void produces_the_binding_shape()
            {
                var input = JObject.Parse(@"{
                    ""city_name"": ""Selkirk Falls"",
                    ""region"": ""Europe"",
                    ""founded"": ""1887"",
                    ""premise"": ""An old mill town reinvents itself.""
                }");
                JObject result = JObject.Parse(QuickstartConfig.NormalizeWizardDone(input));
                ((string)result["city_name"]).Should().Be("Selkirk Falls");
                ((string)result["region"]).Should().Be("Europe");
                ((string)result["founded"]).Should().Be("1887");
                ((string)result["premise"]).Should().Be("An old mill town reinvents itself.");
            }

            [Fact]
            public void missing_optional_fields_become_empty_strings()
            {
                var input = JObject.Parse(@"{ ""city_name"": ""Aldermoor"", ""region"": ""Oceania"", ""premise"": ""x"" }");
                JObject result = JObject.Parse(QuickstartConfig.NormalizeWizardDone(input));
                result.Should().ContainKey("founded");
                ((string)result["founded"]).Should().Be("");
            }

            [Fact]
            public void null_input_does_not_throw()
            {
                JObject result = JObject.Parse(QuickstartConfig.NormalizeWizardDone(null));
                ((string)result["city_name"]).Should().Be("");
                ((string)result["premise"]).Should().Be("");
            }
        }
    }
}
