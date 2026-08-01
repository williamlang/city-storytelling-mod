using System;
using System.IO;
using CityStoryMod.Storyteller;
using FluentAssertions;
using Xunit;

namespace CityStoryMod.Tests
{
    // Tests for the clock.json instance pointers (#45). Each test lays out a
    // temp city dir on disk and asserts what the resolver publishes, since the
    // whole point of these helpers is to answer questions about real directory
    // contents that the agent would otherwise glob for.
    public class CityPointersTests : IDisposable
    {
        readonly string _cityDir;

        public CityPointersTests()
        {
            _cityDir = Path.Combine(Path.GetTempPath(), "csm-pointers-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_cityDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_cityDir, recursive: true); } catch { }
        }

        // ---- helpers ----

        void Write(string rel, string content)
        {
            string full = Path.Combine(_cityDir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, content);
        }

        static string OpenSessionBody(int n) =>
            "---\nsession: " + n + "\nreal_date: 2026-07-12\nin_world_window: TBD\n---\n\n(Session in progress.)\n";

        static string ClosedSessionBody(int n) =>
            "---\nsession: " + n + "\nreal_date: 2026-07-12\nin_world_window: 2026-03 -> 2026-06\n"
            + "ended_real_date: 2026-07-12\n---\n\n## What I built in-game\n";

        public class ResolveLatestSnapshot : CityPointersTests
        {
            [Fact]
            public void returns_null_when_there_is_no_snapshots_dir()
            {
                CityPointers.ResolveLatestSnapshot(_cityDir).Should().BeNull();
            }

            [Fact]
            public void returns_null_when_the_snapshots_dir_is_empty()
            {
                Directory.CreateDirectory(Path.Combine(_cityDir, "snapshots"));
                CityPointers.ResolveLatestSnapshot(_cityDir).Should().BeNull();
            }

            [Fact]
            public void picks_the_highest_timestamp_not_the_lexicographically_last()
            {
                // 1779300000 sorts after 999999999 numerically but before it
                // as a string — a plain name sort would pick the wrong file.
                Write("snapshots/snapshot-999999999.json", "{}");
                Write("snapshots/snapshot-1779300000.json", "{}");
                Write("snapshots/snapshot-1779200000.json", "{}");

                CityPointers.ResolveLatestSnapshot(_cityDir)
                    .Should().Be("snapshots/snapshot-1779300000.json");
            }

            [Fact]
            public void uses_forward_slashes_so_the_path_is_agent_readable()
            {
                Write("snapshots/snapshot-1779300000.json", "{}");
                CityPointers.ResolveLatestSnapshot(_cityDir).Should().NotContain("\\");
            }

            [Fact]
            public void ignores_files_that_are_not_snapshots()
            {
                Write("snapshots/snapshot-1779300000.json", "{}");
                Write("snapshots/notes.txt", "hi");
                Write("snapshots/snapshot-backup.json.bak", "{}");

                CityPointers.ResolveLatestSnapshot(_cityDir)
                    .Should().Be("snapshots/snapshot-1779300000.json");
            }

            [Fact]
            public void falls_back_to_mtime_for_a_name_whose_timestamp_does_not_parse()
            {
                // A hand-copied file still has to sort somewhere rather than
                // throwing the whole resolution away.
                Write("snapshots/snapshot-copy.json", "{}");
                CityPointers.ResolveLatestSnapshot(_cityDir)
                    .Should().Be("snapshots/snapshot-copy.json");
            }

            [Fact]
            public void returns_null_for_a_null_or_empty_city_dir()
            {
                CityPointers.ResolveLatestSnapshot(null).Should().BeNull();
                CityPointers.ResolveLatestSnapshot("").Should().BeNull();
            }
        }

        public class ResolveOpenSession : CityPointersTests
        {
            [Fact]
            public void returns_null_when_there_is_no_sessions_dir()
            {
                CityPointers.ResolveOpenSession(_cityDir).Should().BeNull();
            }

            [Fact]
            public void returns_the_stub_when_the_latest_session_lacks_ended_real_date()
            {
                Write("sessions/S07-2026-07-12-open.md", OpenSessionBody(7));
                CityPointers.ResolveOpenSession(_cityDir)
                    .Should().Be("sessions/S07-2026-07-12-open.md");
            }

            [Fact]
            public void returns_null_when_the_latest_session_is_closed()
            {
                Write("sessions/S07-2026-07-12-stadium-vote.md", ClosedSessionBody(7));
                CityPointers.ResolveOpenSession(_cityDir).Should().BeNull();
            }

            [Fact]
            public void judges_only_the_most_recent_session_not_any_older_open_one()
            {
                // An older session left open (the player force-quit long ago)
                // must not make the mod claim a live session — the agent's
                // own rule looks at the most recent file only.
                Write("sessions/S05-2026-06-01-open.md", OpenSessionBody(5));
                Write("sessions/S07-2026-07-12-stadium-vote.md", ClosedSessionBody(7));

                CityPointers.ResolveOpenSession(_cityDir).Should().BeNull();
            }

            [Fact]
            public void picks_the_highest_session_number_across_double_digits()
            {
                // S10 beats S09 numerically but loses a string sort.
                Write("sessions/S09-2026-07-01-transit-fight.md", ClosedSessionBody(9));
                Write("sessions/S10-2026-07-12-open.md", OpenSessionBody(10));

                CityPointers.ResolveOpenSession(_cityDir)
                    .Should().Be("sessions/S10-2026-07-12-open.md");
            }

            [Fact]
            public void ignores_files_that_do_not_carry_a_session_number()
            {
                Write("sessions/S07-2026-07-12-open.md", OpenSessionBody(7));
                Write("sessions/Some-notes.md", "not a session");

                CityPointers.ResolveOpenSession(_cityDir)
                    .Should().Be("sessions/S07-2026-07-12-open.md");
            }

            [Fact]
            public void ignores_the_archive_subdirectory()
            {
                Write("sessions/archive/S99-2026-01.md", OpenSessionBody(99));
                Write("sessions/S07-2026-07-12-stadium-vote.md", ClosedSessionBody(7));

                CityPointers.ResolveOpenSession(_cityDir).Should().BeNull();
            }

            [Fact]
            public void treats_a_blank_ended_real_date_as_still_open()
            {
                Write("sessions/S07-2026-07-12-open.md",
                    "---\nsession: 7\nreal_date: 2026-07-12\nended_real_date:\n---\n\nbody\n");

                CityPointers.ResolveOpenSession(_cityDir)
                    .Should().Be("sessions/S07-2026-07-12-open.md");
            }
        }

        public class ResolveBootstrapped : CityPointersTests
        {
            [Fact]
            public void is_false_when_settings_json_is_missing()
            {
                CityPointers.ResolveBootstrapped(_cityDir).Should().BeFalse();
            }

            [Fact]
            public void is_false_when_the_flag_is_absent()
            {
                Write("settings.json", @"{ ""secrets_visibility"": ""hidden"" }");
                CityPointers.ResolveBootstrapped(_cityDir).Should().BeFalse();
            }

            [Fact]
            public void is_true_when_the_flag_is_set()
            {
                Write("settings.json", @"{ ""bootstrapped"": true }");
                CityPointers.ResolveBootstrapped(_cityDir).Should().BeTrue();
            }

            [Fact]
            public void is_false_when_the_flag_is_explicitly_false()
            {
                Write("settings.json", @"{ ""bootstrapped"": false }");
                CityPointers.ResolveBootstrapped(_cityDir).Should().BeFalse();
            }

            [Fact]
            public void is_false_for_unparseable_json_rather_than_throwing()
            {
                Write("settings.json", "{ not json");
                CityPointers.ResolveBootstrapped(_cityDir).Should().BeFalse();
            }
        }
    }
}
