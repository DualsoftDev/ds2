using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Llm.Shared;
using Llm.Shared.Abstractions;
using Llm.Shared.Instructions;
using Xunit;

namespace Promaker.Tests;

public sealed class InstructionPromptTests
{
    [Fact]
    public void LoadComposed_selected_0_legacy_base_byte_identical()
    {
        var root = NewTempRoot();
        try
        {
            var profile = new TestProfile(userPromptsDir: root);

            var text = SystemPromptText.Phase1c(profile);

            Assert.Equal("BASE PROMPT", text);
            Assert.DoesNotContain(InstructionPromptComposer.SectionHeader, text);
            Assert.DoesNotContain(InstructionPromptComposer.Guard, text);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LoadComposed_inserts_header_guard_when_selected()
    {
        var root = NewTempRoot();
        try
        {
            var profile = new TestProfile(
                userPromptsDir: root,
                instructionSources: new[] { BuiltInSource() },
                instructionSelection: new InstructionSelectionState(
                    new[] { "builtin:builtin-alpha" },
                    new[] { "builtin:builtin-default" }));

            var text = SystemPromptText.Phase1c(profile);

            Assert.Contains(InstructionPromptComposer.SectionHeader, text);
            Assert.Contains(InstructionPromptComposer.Guard, text);
            Assert.Contains("### BUILTIN INSTRUCTION: builtin-alpha", text);
            Assert.Contains("BUILTIN ALPHA BODY", text);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BuiltIn_defaultEnabled_enabled_by_default_and_disabled_override()
    {
        var catalog = InstructionCatalog.Discover(new[] { BuiltInSource() });

        var defaultSelection = InstructionSelection.Resolve(catalog, InstructionSelectionState.Empty);
        Assert.Contains(defaultSelection.EnabledInstructions, e => e.Key.Value == "builtin:builtin-default");

        var disabledSelection = InstructionSelection.Resolve(
            catalog,
            new InstructionSelectionState(
                Array.Empty<string>(),
                new[] { "builtin:builtin-default" }));
        Assert.DoesNotContain(disabledSelection.EnabledInstructions, e => e.Key.Value == "builtin:builtin-default");
    }

    [Fact]
    public void Custom_defaultEnabled_does_not_auto_enable()
    {
        var root = NewTempRoot();
        try
        {
            CreateCustomInstruction(root, "custom-default", "custom-default", defaultEnabled: true);

            var catalog = InstructionCatalog.Discover(new[] { InstructionSource.CustomFileSystem(root) });
            var selection = InstructionSelection.Resolve(catalog, InstructionSelectionState.Empty);

            Assert.Empty(selection.EnabledInstructions);
            Assert.Contains(selection.Warnings, w => w.Contains("defaultEnabled ignored", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Operator_key_reserved_always_disabled()
    {
        var catalog = InstructionCatalog.Discover(Array.Empty<InstructionSource>());
        var selection = InstructionSelection.Resolve(
            catalog,
            new InstructionSelectionState(new[] { "operator:future" }, Array.Empty<string>()));

        Assert.Empty(selection.EnabledInstructions);
        Assert.Contains(selection.Warnings, w => w.Contains("operator instruction key is reserved", StringComparison.Ordinal));
    }

    [Fact]
    public void Selection_enabled_disabled_conflict_fail_closed()
    {
        var catalog = InstructionCatalog.Discover(new[] { BuiltInSource() });
        var selection = InstructionSelection.Resolve(
            catalog,
            new InstructionSelectionState(
                new[] { "builtin:builtin-alpha" },
                new[] { "builtin:builtin-alpha", "builtin:builtin-default" }));

        Assert.DoesNotContain(selection.EnabledInstructions, e => e.Key.Value == "builtin:builtin-alpha");
        Assert.Contains(selection.Warnings, w => w.Contains("selection conflict", StringComparison.Ordinal));
    }

    [Fact]
    public void Catalog_custom_strict_utf8_fail_closed()
    {
        var root = NewTempRoot();
        try
        {
            var dir = Path.Combine(root, "bad-utf8");
            Directory.CreateDirectory(dir);
            WriteManifest(dir, "bad-utf8", "INSTRUCTION.md");
            File.WriteAllBytes(Path.Combine(dir, "INSTRUCTION.md"), new byte[] { 0xff, 0xfe, 0xfd });

            var catalog = InstructionCatalog.Discover(new[] { InstructionSource.CustomFileSystem(root) });

            Assert.Empty(catalog.Entries);
            Assert.Contains(catalog.Warnings, w => w.Contains("strict UTF-8", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Catalog_custom_path_traversal_fail_closed()
    {
        var root = NewTempRoot();
        try
        {
            var dir = Path.Combine(root, "escape");
            Directory.CreateDirectory(dir);
            WriteManifest(dir, "escape", "../escape.md");
            WriteUtf8(Path.Combine(root, "escape.md"), "ESCAPE");

            var catalog = InstructionCatalog.Discover(new[] { InstructionSource.CustomFileSystem(root) });

            Assert.Empty(catalog.Entries);
            Assert.Contains(catalog.Warnings, w => w.Contains("entry rejected", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Catalog_custom_allows_md_extension_only()
    {
        var root = NewTempRoot();
        try
        {
            var dir = Path.Combine(root, "txt");
            Directory.CreateDirectory(dir);
            WriteManifest(dir, "txt", "INSTRUCTION.txt");
            WriteUtf8(Path.Combine(dir, "INSTRUCTION.txt"), "TXT");

            var catalog = InstructionCatalog.Discover(new[] { InstructionSource.CustomFileSystem(root) });

            Assert.Empty(catalog.Entries);
            Assert.Contains(catalog.Warnings, w => w.Contains("entry rejected", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Catalog_custom_entry_size_cap_fail_closed()
    {
        var root = NewTempRoot();
        try
        {
            CreateCustomInstruction(root, "large", "large", content: "1234567890");

            var catalog = InstructionCatalog.Discover(
                new[] { InstructionSource.CustomFileSystem(root) },
                new InstructionCatalogOptions(MaxEntryBytes: 5));

            Assert.Empty(catalog.Entries);
            Assert.Contains(catalog.Warnings, w => w.Contains("size cap", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Catalog_custom_invalid_root_path_fail_closed()
    {
        var catalog = InstructionCatalog.Discover(new[] { InstructionSource.CustomFileSystem("\0") });

        Assert.Empty(catalog.Entries);
        Assert.Contains(catalog.Warnings, w => w.Contains("custom instruction root path invalid", StringComparison.Ordinal));
    }

    [SkippableFact]
    public void Catalog_custom_reparse_point_fail_closed()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows reparse point validation.");

        var root = NewTempRoot();
        try
        {
            var targetDir = Path.Combine(root, "target");
            Directory.CreateDirectory(targetDir);
            var targetFile = Path.Combine(targetDir, "INSTRUCTION.md");
            WriteUtf8(targetFile, "TARGET");

            var packageDir = Path.Combine(root, "linked");
            Directory.CreateDirectory(packageDir);
            WriteManifest(packageDir, "linked", "INSTRUCTION.md");
            var linkPath = Path.Combine(packageDir, "INSTRUCTION.md");
            try
            {
                File.CreateSymbolicLink(linkPath, targetFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                Skip.If(true, $"symlink creation is not permitted: {ex.Message}");
            }

            var catalog = InstructionCatalog.Discover(new[] { InstructionSource.CustomFileSystem(root) });

            Assert.Empty(catalog.Entries);
            Assert.Contains(catalog.Warnings, w => w.Contains("reparse point", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Catalog_id_collision_rejects_all()
    {
        var root = NewTempRoot();
        try
        {
            CreateCustomInstruction(root, "custom-collision", "collision");

            var catalog = InstructionCatalog.Discover(new[]
            {
                BuiltInSource(),
                InstructionSource.CustomFileSystem(root),
            });

            Assert.DoesNotContain(catalog.Entries, e => e.Id == "collision");
            Assert.Contains(catalog.Warnings, w => w.Contains("id collision rejected: collision", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Selection_order_sourcePriority_order_id_ordinal()
    {
        var root = NewTempRoot();
        try
        {
            CreateCustomInstruction(root, "custom-upper", "A", order: 0, content: "CUSTOM A BODY");
            CreateCustomInstruction(root, "custom-lower", "a", order: 0, content: "CUSTOM a BODY");

            var catalog = InstructionCatalog.Discover(new[]
            {
                BuiltInSource(),
                InstructionSource.CustomFileSystem(root),
            });
            var selection = InstructionSelection.Resolve(
                catalog,
                new InstructionSelectionState(
                    new[] { "custom:a", "builtin:builtin-late", "custom:A" },
                    new[] { "builtin:builtin-default" }));

            Assert.Equal(
                new[] { "builtin:builtin-late", "custom:A", "custom:a" },
                selection.EnabledInstructions.Select(e => e.Key.Value).ToArray());

            var prompt = InstructionPromptComposer.Compose(selection.EnabledInstructions).Text;
            Assert.True(
                prompt.IndexOf("### BUILTIN INSTRUCTION: builtin-late", StringComparison.Ordinal) <
                prompt.IndexOf("### CUSTOM INSTRUCTION: A", StringComparison.Ordinal));
            Assert.True(
                prompt.IndexOf("### CUSTOM INSTRUCTION: A", StringComparison.Ordinal) <
                prompt.IndexOf("### CUSTOM INSTRUCTION: a", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static InstructionSource BuiltInSource() =>
        InstructionSource.BuiltInEmbedded(TestAssembly, BuiltInInstructionPrefix());

    private static void CreateCustomInstruction(
        string root,
        string folder,
        string id,
        bool defaultEnabled = false,
        int order = 100,
        string content = "CUSTOM BODY")
    {
        var dir = Path.Combine(root, folder);
        Directory.CreateDirectory(dir);
        WriteManifest(dir, id, "INSTRUCTION.md", defaultEnabled, order);
        WriteUtf8(Path.Combine(dir, "INSTRUCTION.md"), content);
    }

    private static void WriteManifest(
        string dir,
        string id,
        string entry,
        bool defaultEnabled = false,
        int order = 100)
    {
        var json = JsonSerializer.Serialize(new
        {
            id,
            displayName = id,
            entry,
            defaultEnabled,
            order,
        });
        WriteUtf8(Path.Combine(dir, "instruction.json"), json);
    }

    private static void WriteUtf8(string path, string text) =>
        File.WriteAllText(path, text, Utf8NoBom);

    private static string NewTempRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "promaker-instructions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string BasePromptPrefix() =>
        PrefixBefore("0.base.md");

    private static string BuiltInInstructionPrefix() =>
        PrefixBefore("builtin-alpha.instruction.json");

    private static string PrefixBefore(string suffix)
    {
        var resourceName = TestAssembly
            .GetManifestResourceNames()
            .Single(n => n.EndsWith(suffix, StringComparison.Ordinal));
        return resourceName[..^suffix.Length];
    }

    private static readonly Assembly TestAssembly = typeof(InstructionPromptTests).Assembly;
    private static readonly UTF8Encoding Utf8NoBom = new(false, true);

    private sealed class TestProfile : ILlmAppProfile
    {
        public TestProfile(
            string userPromptsDir,
            IReadOnlyList<InstructionSource>? instructionSources = null,
            InstructionSelectionState? instructionSelection = null)
        {
            UserPromptsDir = userPromptsDir;
            EmbeddedPromptsSources = new[]
            {
                new PromptSource(TestAssembly, BasePromptPrefix()),
            };
            InstructionSources = instructionSources ?? Array.Empty<InstructionSource>();
            InstructionSelection = instructionSelection ?? InstructionSelectionState.Empty;
        }

        public IReadOnlyList<PromptSource> EmbeddedPromptsSources { get; }
        public string UserPromptsDir { get; }
        public string? LegacyUserPromptsDir => null;
        public IReadOnlyList<InstructionSource> InstructionSources { get; }
        public InstructionSelectionState InstructionSelection { get; }
        public string LoggerName => "Promaker.Tests.Instructions";
    }
}
