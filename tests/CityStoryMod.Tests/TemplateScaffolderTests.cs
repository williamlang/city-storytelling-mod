using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using FluentAssertions;
using Xunit;
using static CityStoryMod.TemplateScaffolder;

namespace CityStoryMod.Tests
{
    // Tests for the template-sync state machine. Each test sets up a temp
    // city dir, drives Sync through a sequence of (embedded set, on-disk
    // state) transitions, and verifies the outcome — files added,
    // updated, left divergent, or skipped.
    public class TemplateScaffolderTests : IDisposable
    {
        readonly string _cityDir;

        public TemplateScaffolderTests()
        {
            _cityDir = Path.Combine(Path.GetTempPath(), "csm-scaffold-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_cityDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_cityDir, recursive: true); } catch { }
        }

        // ---- helpers ----

        static TemplateFile File_(string rel, string content) =>
            new TemplateFile { RelativePath = rel, Content = Encoding.UTF8.GetBytes(content) };

        string Read(string rel)
        {
            string full = Path.Combine(_cityDir, rel.Replace('/', Path.DirectorySeparatorChar));
            return File.ReadAllText(full);
        }

        void WriteOnDisk(string rel, string content)
        {
            string full = Path.Combine(_cityDir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, content);
        }

        // ---- fresh-city cases ----

        [Fact]
        public void writes_all_files_on_fresh_city()
        {
            var result = Sync(_cityDir, new[]
            {
                File_("CLAUDE.md", "# template v1"),
                File_(".claude/commands/foo.md", "foo"),
                File_("canon/INDEX.md", "index"),
            });

            result.Added.Should().Be(3);
            result.Updated.Should().Be(0);
            result.Divergent.Should().BeEmpty();
            Read("CLAUDE.md").Should().Be("# template v1");
            Read(".claude/commands/foo.md").Should().Be("foo");
            File.Exists(Path.Combine(_cityDir, ".template-manifest.json")).Should().BeTrue();
        }

        // ---- update cases ----

        [Fact]
        public void updates_unmodified_template_file_when_embedded_changes()
        {
            Sync(_cityDir, new[] { File_("CLAUDE.md", "# v1") });
            var result = Sync(_cityDir, new[] { File_("CLAUDE.md", "# v2") });

            result.Updated.Should().Be(1);
            result.Divergent.Should().BeEmpty();
            Read("CLAUDE.md").Should().Be("# v2");
        }

        [Fact]
        public void does_not_rewrite_files_already_matching_embedded()
        {
            Sync(_cityDir, new[] { File_("CLAUDE.md", "# v1") });
            var result = Sync(_cityDir, new[] { File_("CLAUDE.md", "# v1") });

            result.Unchanged.Should().Be(1);
            result.Added.Should().Be(0);
            result.Updated.Should().Be(0);
        }

        // ---- divergent (player-edited) cases ----

        [Fact]
        public void leaves_player_modified_file_alone_when_template_evolves()
        {
            Sync(_cityDir, new[] { File_("CLAUDE.md", "# v1") });
            WriteOnDisk("CLAUDE.md", "# v1\n\n## My addition\n");

            var result = Sync(_cityDir, new[] { File_("CLAUDE.md", "# v2") });

            result.Divergent.Should().ContainSingle().Which.Should().Be("CLAUDE.md");
            result.Updated.Should().Be(0);
            Read("CLAUDE.md").Should().Be("# v1\n\n## My addition\n");
        }

        [Fact]
        public void keeps_reporting_divergence_until_player_reconciles()
        {
            // Divergence is a state, not an event. As long as disk doesn't
            // match what the mod wrote (and doesn't match the current
            // embedded), the file is reported as divergent on every sync.
            // The mod never auto-updates a divergent file. Callers that
            // want one-shot notification can dedupe themselves.
            Sync(_cityDir, new[] { File_("CLAUDE.md", "# v1") });
            WriteOnDisk("CLAUDE.md", "# v1\n\nplayer notes\n");

            var first = Sync(_cityDir, new[] { File_("CLAUDE.md", "# v2") });
            var second = Sync(_cityDir, new[] { File_("CLAUDE.md", "# v2") });

            first.Divergent.Should().Contain("CLAUDE.md");
            second.Divergent.Should().Contain("CLAUDE.md");
            // File never gets auto-overwritten.
            Read("CLAUDE.md").Should().Be("# v1\n\nplayer notes\n");
        }

        [Fact]
        public void player_reconciliation_to_embedded_clears_divergence()
        {
            Sync(_cityDir, new[] { File_("CLAUDE.md", "# v1") });
            WriteOnDisk("CLAUDE.md", "# my edits\n");
            Sync(_cityDir, new[] { File_("CLAUDE.md", "# v2") }).Divergent.Should().Contain("CLAUDE.md");

            // Player decides to take the new template version verbatim.
            WriteOnDisk("CLAUDE.md", "# v2");
            var result = Sync(_cityDir, new[] { File_("CLAUDE.md", "# v2") });

            result.Divergent.Should().BeEmpty();
            result.Unchanged.Should().Be(1);
        }

        // ---- restore-missing cases ----

        [Fact]
        public void rewrites_a_deleted_template_file()
        {
            Sync(_cityDir, new[] { File_("CLAUDE.md", "# v1") });
            File.Delete(Path.Combine(_cityDir, "CLAUDE.md"));

            var result = Sync(_cityDir, new[] { File_("CLAUDE.md", "# v1") });

            result.Added.Should().Be(1);
            Read("CLAUDE.md").Should().Be("# v1");
        }

        // ---- new-file cases (template grows) ----

        [Fact]
        public void writes_newly_added_template_files()
        {
            Sync(_cityDir, new[] { File_("CLAUDE.md", "# v1") });
            var result = Sync(_cityDir, new[]
            {
                File_("CLAUDE.md", "# v1"),
                File_(".claude/commands/new.md", "fresh command"),
            });

            result.Added.Should().Be(1);
            Read(".claude/commands/new.md").Should().Be("fresh command");
        }

        // ---- legacy-city bootstrap ----

        [Fact]
        public void legacy_untracked_file_stays_untracked_and_is_left_alone()
        {
            // Simulate a city scaffolded before TemplateScaffolder existed:
            // template files on disk, no manifest. The mod can't tell
            // legacy-untouched from legacy-edited, so the conservative
            // default is to never auto-update.
            WriteOnDisk("CLAUDE.md", "# legacy player content");

            var result = Sync(_cityDir, new[] { File_("CLAUDE.md", "# embedded v3") });

            result.Divergent.Should().Contain("CLAUDE.md");
            result.Updated.Should().Be(0);
            Read("CLAUDE.md").Should().Be("# legacy player content");
        }

        [Fact]
        public void legacy_untracked_file_matching_embedded_gets_adopted_into_manifest()
        {
            // Edge case: legacy file happens to match the current embedded
            // template byte-for-byte. Sync adopts it into the manifest so
            // future template updates can flow through normally.
            WriteOnDisk("CLAUDE.md", "# v1");

            // First sync: matches embedded, gets adopted.
            var first = Sync(_cityDir, new[] { File_("CLAUDE.md", "# v1") });
            first.Unchanged.Should().Be(1);
            first.Divergent.Should().BeEmpty();

            // Now template evolves. File is tracked → safe to update.
            var second = Sync(_cityDir, new[] { File_("CLAUDE.md", "# v2") });
            second.Updated.Should().Be(1);
            Read("CLAUDE.md").Should().Be("# v2");
        }

        // ---- manifest persistence ----

        [Fact]
        public void manifest_persists_hashes_across_calls()
        {
            Sync(_cityDir, new[] { File_("CLAUDE.md", "# v1") });
            string manifestPath = Path.Combine(_cityDir, ".template-manifest.json");
            string raw = File.ReadAllText(manifestPath);

            raw.Should().Contain("CLAUDE.md");
            // SHA256("# v1") hex string is 64 chars; just check the manifest
            // value isn't empty.
            raw.Length.Should().BeGreaterThan(40);
        }

        [Fact]
        public void malformed_manifest_is_treated_as_empty_and_does_not_crash()
        {
            WriteOnDisk(".template-manifest.json", "{ this is not json");
            WriteOnDisk("CLAUDE.md", "# v1");

            // Effective state: no tracked entries. CLAUDE.md is untracked
            // and disk differs from embedded → divergent, but mod doesn't
            // crash and doesn't overwrite player content.
            var act = () => Sync(_cityDir, new[] { File_("CLAUDE.md", "# v1 evolved") });
            act.Should().NotThrow();
            Read("CLAUDE.md").Should().Be("# v1");
        }
    }
}
