using FluentAssertions;
using Xunit;

namespace CityStoryMod.Tests
{
    public class TextUtilsTests
    {
        public class Slugify
        {
            [Theory]
            [InlineData("Halverson Crossing", "halverson-crossing")]
            [InlineData("Halverson Crossing E5", "halverson-crossing-e5")]
            [InlineData("HalversonCrossing", "halversoncrossing")]
            [InlineData("Halverson  Crossing", "halverson-crossing")]
            [InlineData("Halverson--Crossing", "halverson-crossing")]
            [InlineData("Halverson_Crossing!", "halverson-crossing")]
            [InlineData("Halverson Crossing ", "halverson-crossing")]
            [InlineData(" Halverson Crossing", "halverson-crossing")]
            [InlineData("PORTLAND", "portland")]
            [InlineData("123 Main St.", "123-main-st")]
            [InlineData("a", "a")]
            public void produces_lowercase_dash_separated_slug(string input, string expected)
            {
                TextUtils.Slugify(input).Should().Be(expected);
            }

            [Theory]
            [InlineData(null)]
            [InlineData("")]
            [InlineData("   ")]
            [InlineData("\t\n")]
            [InlineData("!!!")]
            [InlineData("   ---  ")]
            public void returns_null_when_input_yields_no_alphanumeric_chars(string input)
            {
                TextUtils.Slugify(input).Should().BeNull();
            }

            [Fact]
            public void preserves_non_ascii_letters_as_lowercase()
            {
                // char.IsLetterOrDigit considers most Unicode letters letters.
                TextUtils.Slugify("Café Münch").Should().Be("café-münch");
            }
        }

        public class FrontmatterHasEndedRealDate
        {
            [Fact]
            public void returns_true_when_key_present_with_value()
            {
                string md = "---\nsession: 4\nended_real_date: 2026-05-21\nin_world_window: foo\n---\n\nbody";
                TextUtils.FrontmatterHasEndedRealDate(md).Should().BeTrue();
            }

            [Fact]
            public void returns_false_when_key_present_but_empty()
            {
                string md = "---\nsession: 4\nended_real_date:\nin_world_window: foo\n---\n\nbody";
                TextUtils.FrontmatterHasEndedRealDate(md).Should().BeFalse();
            }

            [Fact]
            public void returns_false_when_key_present_but_value_is_whitespace()
            {
                string md = "---\nended_real_date:   \n---\n";
                TextUtils.FrontmatterHasEndedRealDate(md).Should().BeFalse();
            }

            [Fact]
            public void returns_false_when_key_missing_entirely()
            {
                string md = "---\nsession: 4\nin_world_window: foo\n---\n\nbody";
                TextUtils.FrontmatterHasEndedRealDate(md).Should().BeFalse();
            }

            [Fact]
            public void returns_false_when_only_one_fence_marker_present()
            {
                string md = "---\nended_real_date: 2026-05-21\nbody without closing fence";
                TextUtils.FrontmatterHasEndedRealDate(md).Should().BeFalse();
            }

            [Fact]
            public void returns_false_when_no_fence_markers()
            {
                string md = "session: 4\nended_real_date: 2026-05-21\n";
                TextUtils.FrontmatterHasEndedRealDate(md).Should().BeFalse();
            }

            [Fact]
            public void returns_false_for_null_or_empty()
            {
                TextUtils.FrontmatterHasEndedRealDate(null).Should().BeFalse();
                TextUtils.FrontmatterHasEndedRealDate("").Should().BeFalse();
            }

            [Fact]
            public void only_scans_first_frontmatter_block_not_body()
            {
                // ended_real_date in body after the closing fence should not count.
                string md = "---\nsession: 4\n---\n\nended_real_date: 2026-05-21\n";
                TextUtils.FrontmatterHasEndedRealDate(md).Should().BeFalse();
            }

            [Fact]
            public void tolerates_leading_whitespace_on_key_line()
            {
                string md = "---\n  ended_real_date: 2026-05-21\n---\n";
                TextUtils.FrontmatterHasEndedRealDate(md).Should().BeTrue();
            }
        }

        public class GetFrontmatterField
        {
            [Fact]
            public void returns_value_for_present_key()
            {
                string md = "---\ndescription: Generate story choices\n---\n\nbody";
                TextUtils.GetFrontmatterField(md, "description")
                    .Should().Be("Generate story choices");
            }

            [Fact]
            public void returns_null_for_missing_key()
            {
                string md = "---\ndescription: foo\n---\n";
                TextUtils.GetFrontmatterField(md, "nonexistent").Should().BeNull();
            }

            [Fact]
            public void returns_null_when_value_is_empty()
            {
                string md = "---\ndescription:\n---\n";
                TextUtils.GetFrontmatterField(md, "description").Should().BeNull();
            }

            [Fact]
            public void returns_null_when_no_frontmatter()
            {
                string md = "body without frontmatter";
                TextUtils.GetFrontmatterField(md, "description").Should().BeNull();
            }

            [Fact]
            public void returns_null_when_only_one_fence_marker()
            {
                string md = "---\ndescription: foo\nno closing fence";
                TextUtils.GetFrontmatterField(md, "description").Should().BeNull();
            }

            [Fact]
            public void tolerates_leading_whitespace_on_key_line()
            {
                string md = "---\n  description: indented value\n---\n";
                TextUtils.GetFrontmatterField(md, "description")
                    .Should().Be("indented value");
            }

            [Fact]
            public void does_not_scan_body_after_frontmatter()
            {
                // description: in the body shouldn't be picked up
                string md = "---\ntitle: foo\n---\n\ndescription: in the body\n";
                TextUtils.GetFrontmatterField(md, "description").Should().BeNull();
            }

            [Fact]
            public void returns_null_for_null_or_empty_content()
            {
                TextUtils.GetFrontmatterField(null, "key").Should().BeNull();
                TextUtils.GetFrontmatterField("", "key").Should().BeNull();
            }

            [Fact]
            public void returns_null_for_null_or_empty_key()
            {
                string md = "---\ndescription: foo\n---\n";
                TextUtils.GetFrontmatterField(md, null).Should().BeNull();
                TextUtils.GetFrontmatterField(md, "").Should().BeNull();
            }

            [Fact]
            public void key_match_is_exact_not_prefix()
            {
                // "desc" shouldn't match "description"
                string md = "---\ndescription: foo\n---\n";
                TextUtils.GetFrontmatterField(md, "desc").Should().BeNull();
            }
        }
    }
}
