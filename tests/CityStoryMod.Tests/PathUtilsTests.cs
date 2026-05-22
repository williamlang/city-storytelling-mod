using System;
using System.IO;
using FluentAssertions;
using Xunit;

namespace CityStoryMod.Tests
{
    public class PathUtilsTests
    {
        // Builds a throwaway PATH with two temp dirs and a known executable name
        // so the test doesn't depend on what's actually installed on the machine.
        public class FindExecutable : IDisposable
        {
            readonly string _dirA;
            readonly string _dirB;

            public FindExecutable()
            {
                _dirA = Path.Combine(Path.GetTempPath(), "csm-test-a-" + Guid.NewGuid().ToString("N"));
                _dirB = Path.Combine(Path.GetTempPath(), "csm-test-b-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_dirA);
                Directory.CreateDirectory(_dirB);
            }

            public void Dispose()
            {
                try { Directory.Delete(_dirA, recursive: true); } catch { }
                try { Directory.Delete(_dirB, recursive: true); } catch { }
            }

            string BuildPath(params string[] dirs) => string.Join(Path.PathSeparator.ToString(), dirs);

            [Fact]
            public void returns_path_when_executable_found_in_first_dir()
            {
                string target = Path.Combine(_dirA, "claude.cmd");
                File.WriteAllText(target, "");
                string result = PathUtils.FindExecutable(BuildPath(_dirA, _dirB), new[] { "claude.cmd" });
                result.Should().Be(target);
            }

            [Fact]
            public void returns_path_when_executable_only_in_second_dir()
            {
                string target = Path.Combine(_dirB, "claude.cmd");
                File.WriteAllText(target, "");
                string result = PathUtils.FindExecutable(BuildPath(_dirA, _dirB), new[] { "claude.cmd" });
                result.Should().Be(target);
            }

            [Fact]
            public void searches_names_in_order_per_directory()
            {
                // .cmd in dirA, .exe in dirB. Caller's name order = .cmd first.
                // Hit should be dirA's .cmd, not dirB's .exe (which is later in PATH).
                File.WriteAllText(Path.Combine(_dirA, "claude.cmd"), "");
                File.WriteAllText(Path.Combine(_dirB, "claude.exe"), "");
                string result = PathUtils.FindExecutable(
                    BuildPath(_dirA, _dirB),
                    new[] { "claude.cmd", "claude.exe" });
                result.Should().Be(Path.Combine(_dirA, "claude.cmd"));
            }

            [Fact]
            public void prefers_earlier_directory_even_with_later_name_match()
            {
                // dirA has only .exe; dirB has .cmd. Name order = .cmd, .exe.
                // The dirA .exe should win because the directory comes first —
                // we walk dirs as the outer loop, names as the inner.
                File.WriteAllText(Path.Combine(_dirA, "claude.exe"), "");
                File.WriteAllText(Path.Combine(_dirB, "claude.cmd"), "");
                string result = PathUtils.FindExecutable(
                    BuildPath(_dirA, _dirB),
                    new[] { "claude.cmd", "claude.exe" });
                result.Should().Be(Path.Combine(_dirA, "claude.exe"));
            }

            [Fact]
            public void returns_null_when_not_found()
            {
                string result = PathUtils.FindExecutable(BuildPath(_dirA, _dirB), new[] { "claude.cmd" });
                result.Should().BeNull();
            }

            [Fact]
            public void skips_empty_path_segments()
            {
                string target = Path.Combine(_dirA, "claude.cmd");
                File.WriteAllText(target, "");
                // ;; in PATH (an empty segment) is common when PATH ends with ; or has typos.
                string path = "" + Path.PathSeparator + _dirA + Path.PathSeparator + "";
                string result = PathUtils.FindExecutable(path, new[] { "claude.cmd" });
                result.Should().Be(target);
            }

            [Fact]
            public void returns_null_for_null_or_empty_inputs()
            {
                PathUtils.FindExecutable(null, new[] { "claude" }).Should().BeNull();
                PathUtils.FindExecutable("", new[] { "claude" }).Should().BeNull();
                PathUtils.FindExecutable(_dirA, null).Should().BeNull();
            }
        }
    }
}
