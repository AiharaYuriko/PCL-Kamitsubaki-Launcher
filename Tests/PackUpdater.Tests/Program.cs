using Newtonsoft.Json.Linq;
using PCL.Modpack;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

internal static partial class Program {
    static int passed;
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Test(string name, Action<string> action) {
        string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { Console.Error.WriteLine("FAIL " + name + ": " + ex.Message); Environment.ExitCode = 1; }
        finally { Directory.Delete(root, true); }
    }
    static void Write(string root, string path, string content) { string target = Path.Combine(root, path); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllText(target, content, new UTF8Encoding(false)); }
    static JObject FileEntry(string path, string body, string policy = "replace", string? component = null) {
        var value = new JObject { ["path"] = path, ["sha256"] = PackProtocol.Hash(Encoding.UTF8.GetBytes(body)), ["size"] = Encoding.UTF8.GetByteCount(body), ["urls"] = new JArray("https://example.com/" + Uri.EscapeDataString(body)), ["category"] = "file", ["write_policy"] = policy };
        if (component != null) value["component"] = component;
        return value;
    }
    static JObject Release(int sequence, params JObject[] files) => new() {
        ["schema"] = "pcl.modpack.release.v1", ["pack"] = new JObject { ["id"] = "test", ["name"] = "Test" },
        ["release"] = new JObject { ["id"] = "v" + sequence, ["sequence"] = sequence, ["channel"] = "stable", ["notes"] = "" },
        ["runtime"] = new JObject { ["minecraft"] = "1.21.1", ["loader"] = new JObject { ["type"] = "neoforge", ["version"] = "21.1.219" }, ["minecraft_change"] = "new-instance", ["allow_loader_downgrade"] = false },
        ["components"] = new JArray(new JObject { ["id"] = "extra", ["name"] = "Extra", ["required"] = false, ["default_selected"] = true }),
        ["files"] = new JArray(files), ["actions"] = new JArray(), ["cleanup"] = "previously-managed-unmodified"
    };
    static void Download(string url, string target, long size) => System.IO.File.WriteAllText(target, Uri.UnescapeDataString(new Uri(url).AbsolutePath.Substring(1)), new UTF8Encoding(false));
    static void Apply(PackUpdater updater, JObject release, Func<PackRuntime>? runtime = null, string[]? selected = null) => updater.Apply(updater.Plan(release, selected ?? new string[0]), runtime ?? (() => new PackRuntime()), _ => { }, Download);
    static void Throws(Action action) { bool failed = false; try { action(); } catch { failed = true; } Assert(failed, "Expected failure"); }
    static PackPublishDetails Details(string id = "archive1") => new() {
        PackId = "test", Name = "Archive Test", ReleaseId = id, Minecraft = "1.21.1", NeoForge = "21.1.219"
    };
    static Action<string, Stream, long, long, long> LocalRanges(string zip, Action<long>? onRange = null) =>
        (_, output, offset, length, total) => {
            using var input = System.IO.File.OpenRead(zip);
            Assert(total == input.Length && offset + length <= total, "Invalid byte range");
            input.Position = offset;
            byte[] data = new byte[checked((int)length)];
            Assert(input.Read(data, 0, data.Length) == data.Length, "Short test archive");
            output.Write(data, 0, data.Length);
            onRange?.Invoke(length);
        };
    static void Main() {
        Test("read local mod IDs without relying on filenames or online projects", root => {
            void Check(string name, string[] expected, params (string Entry, string Text)[] entries) {
                string path = Path.Combine(root, name);
                using (var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create)) {
                    foreach (var entry in entries) {
                        using var writer = new StreamWriter(zip.CreateEntry(entry.Entry).Open());
                        writer.Write(entry.Text);
                    }
                }
                Assert(expected.SequenceEqual(PackPublisher.ReadDeclaredModIds(path).OrderBy(id => id, StringComparer.Ordinal)), name);
            }

            const string creativeCore = "[[mods]]\nmodId=\"creativecore\"\n[[dependencies.creativecore]]\nmodId=\"neoforge\"";
            Check("renamed.jar", new[] { "creativecore" }, ("META-INF/neoforge.mods.toml", creativeCore));
            Check("original.jar.disabled", new[] { "creativecore", "second_mod" },
                ("META-INF/mods.toml", creativeCore + "\n[[mods]]\nmodId='second_mod'"),
                ("fabric.mod.json", """{"id":"creativecore"}"""));
            Check("other.jar", new[] { "quilt-mod" }, ("fabric.mod.json", "{invalid"), ("quilt.mod.json", """{"quilt_loader":{"id":"quilt-mod"}}"""));
            Write(root, "not-an-archive.jar", "invalid");
            Assert(PackPublisher.ReadDeclaredModIds(Path.Combine(root, "not-an-archive.jar")).Length == 0, "bad archive should not block the list");
        });
        TestArchiveLinks();
        TestMultipartUpload();
        Test("official update source has its own default mirror and explicit disable is saved", root => {
            foreach (string source in new[] { "https://r2.kaf.mom", "https://R2.KAF.MOM/", "https://r2.kaf.mom/pcl/index.json" })
                Assert(PackMirror.DefaultAddressFor(source) == PackMirror.DefaultAddress, "Official source should have the official mirror");
            foreach (string source in new[] { "https://other.example", "https://r2.kaf.mom/custom", "https://r2.kaf.mom/pcl/index.json?foo=bar",
                         "https://user@r2.kaf.mom", "http://r2.kaf.mom", "https://r2.kaf.mom:444", "" })
                Assert(PackMirror.DefaultAddressFor(source) == "", "Custom sources should not get an unrelated mirror");
            var updater = new PackUpdater(root);
            var mirror = new PackMirror(PackMirror.DefaultAddress);
            Assert(!mirror.PreferMirror, "The primary source should still come first by default");
            updater.Bind(PackMirror.DefaultPrimaryAddress, "test", "stable", mirror);
            Assert(PackUpdater.GetBoundMirror(updater.Binding)?.PreferMirror == false, "A saved primary-first binding should remain primary-first");
            updater.Bind(PackMirror.DefaultPrimaryAddress, "test", "stable", new PackMirror(PackMirror.DefaultAddress, preferMirror: true));
            Assert(PackUpdater.GetBoundMirror(updater.Binding)?.PreferMirror == true, "A saved S3-first binding should remain S3-first");
            updater.Bind(PackMirror.DefaultPrimaryAddress, "test", "stable");
            Assert((string?)new PackUpdater(root).Binding["mirror_url"] == "", "Clearing the mirror should survive reopening");
            Assert(PackUpdater.GetBoundMirror(updater.Binding) == null, "An explicitly cleared mirror should stay disabled");
        });
        Test("startup check skips uninstalled packs and requires the installed channel", root => {
            var updater = new PackUpdater(root);
            updater.Bind("https://pack.example", "test", "preview");
            Assert(updater.CheckForUpdate() == null, "A binding without an installed release should not trigger a check");

            Write(root, ".pcl-update/state.json", """
                {"pack_id":"test","channel":"stable","release_id":"v1","sequence":1}
                """);
            try {
                updater.CheckForUpdate();
                throw new Exception("The startup check accepted a source bound to another channel");
            } catch (IOException ex) when (ex.Message == "此实例缺少匹配的整合包更新源。") { }
        });
        Test("first connection recognizes only the bundled legacy instance", root => {
            const string primary = ".minecraft/versions/神椿市搬砖中";
            const string legacy = ".minecraft/versions/1.21.1-NeoForge_21.1.219";
            const string config = "client-config.toml";
            var updater = new PackUpdater(Path.Combine(root, legacy));
            Directory.CreateDirectory(Path.Combine(root, legacy, "mods"));
            Assert(!updater.IsBundledLegacyInstance(root), "An ordinary NeoForge instance must not be offered migration");
            Write(root, config, "# manifest_url = 'https://r2.iris-mapper.top/minecraft/releases/current/release-manifest.json'\n" +
                "[client]\nmanifest_url = 'https://other.example/release-manifest.json'\n");
            Assert(!updater.IsBundledLegacyInstance(root), "A commented-out official URL must not override a custom source");
            Write(root, config, "[client]\nmanifest_url = \"https://r2.iris-mapper.top/minecraft/releases/current/release-manifest.json\" # old updater\n");
            Assert(updater.IsBundledLegacyInstance(root), "The older instance name must work before the old updater has run");
            Directory.CreateDirectory(Path.Combine(root, primary, "mods"));
            var canonical = new PackUpdater(Path.Combine(root, primary));
            Assert(!updater.IsBundledLegacyInstance(root) && canonical.IsBundledLegacyInstance(root),
                "Only the preferred legacy instance should get the first-connection offer");
            canonical.Bind("https://pack.example", "test", "stable");
            Assert(canonical.CheckLegacyStartupUpdate(root) == null, "An existing custom binding must take precedence");
        });
        Test("publisher credentials share across instances and migrate by storage target", root => {
            string store = Path.Combine(root, "shared");
            var publisher = new PackPublisher(Path.Combine(root, "first"), store);
            var reopened = new PackPublisher(Path.Combine(root, "second"), store);
            string account = new string('a', 32);
            string r2 = PackPublisher.CredentialTarget(account, "pack-bucket")!;
            string other = PackPublisher.CredentialTarget(account, "other-bucket")!;
            string mirror = PackPublisher.CredentialTarget("", "pack-bucket", mirror: true)!;

            void WriteLegacy(PackPublisher instance, string name, string access) {
                string path = instance.ProjectFile(name);
                var data = Encoding.UTF8.GetBytes(new JObject { ["access"] = access, ["secret"] = "legacy-secret" }.ToString());
                var encrypted = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
                PackUpdater.Atomic(path, new JObject { ["encrypted"] = Convert.ToBase64String(encrypted) });
            }

            WriteLegacy(publisher, "credentials.json", "legacy-r2");
            WriteLegacy(publisher, "mirror-credentials.json", "legacy-mirror");
            publisher.MigrateLegacyCredentials(r2);
            publisher.MigrateLegacyCredentials(mirror, mirror: true);
            Assert((string?)reopened.ReadCredentials(r2)["access"] == "legacy-r2", "R2 migration should be visible from another instance");
            Assert((string?)reopened.ReadCredentials(mirror)["access"] == "legacy-mirror", "Mirror migration should be visible from another instance");
            Assert(!File.Exists(publisher.ProjectFile("credentials.json")) && !File.Exists(publisher.ProjectFile("mirror-credentials.json")),
                "Migrated files should be removed");
            Assert(!reopened.ReadCredentials(other).HasValues, "Other buckets should not inherit credentials");
            Assert(!reopened.ReadCredentials(PackPublisher.CredentialTarget(new string('b', 32), "pack-bucket")).HasValues,
                "Other accounts should not inherit credentials");

            reopened.SaveCredentials(r2, "shared-r2", "");
            WriteLegacy(publisher, "credentials.json", "older-r2");
            publisher.MigrateLegacyCredentials(r2);
            Assert((string?)publisher.ReadCredentials(r2)["access"] == "shared-r2", "Legacy migration should not overwrite shared credentials");
            reopened.SaveCredentials(r2, "", "");
            Assert(!publisher.ReadCredentials(r2).HasValues && publisher.ReadCredentials(mirror).HasValues, "Clearing one target should keep the other");

            WriteLegacy(publisher, "credentials.json", "unknown-target");
            publisher.MigrateLegacyCredentials(null);
            publisher.MigrateLegacyCredentials(other);
            Assert(File.Exists(publisher.ProjectFile("credentials.json")) && !publisher.ReadCredentials(other).HasValues,
                "Legacy credentials with an unknown original target should not be assigned to a later target");
        });
        Test("publisher preview pairs renamed mods by ID, platform project, then path", root => {
            string old = Path.Combine(root, "before");
            string current = Path.Combine(root, "after");
            const string metadata = "[[mods]]\nmodId=\"paired_mod\"";
            void Jar(string instance, string path, string? toml = null) {
                string full = Path.Combine(instance, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                using var zip = new ZipArchive(File.Create(full), ZipArchiveMode.Create);
                using var output = new StreamWriter(zip.CreateEntry(toml == null ? "payload" : "META-INF/neoforge.mods.toml").Open());
                output.Write(toml ?? path);
            }
            Jar(old, "mods/identified-old.jar", metadata);
            Jar(current, "mods/identified-new.jar", metadata);
            Jar(old, "mods/platform-old.jar");
            Jar(current, "mods/platform-new.jar");
            Jar(old, "mods/platform-renamed-id-old.jar", "[[mods]]\nmodId=\"old_platform_id\"");
            Jar(current, "mods/platform-renamed-id-new.jar", "[[mods]]\nmodId=\"new_platform_id\"");
            Jar(old, "mods/same.jar");
            Jar(current, "mods/same.jar.disabled");
            Jar(old, "mods/duplicate-old.jar");
            Jar(current, "mods/duplicate-a.jar");
            Jar(current, "mods/duplicate-b.jar");
            Write(old, "config/options.json", "{\"old\":true}");
            Write(current, "config/options.json", "{\"old\":false}");
            var folders = new[] { "mods", "config" };
            var publisher = new PackPublisher(current);
            void Resolve(IEnumerable<PackPublishFile> files) {
                foreach (var file in files.Where(f => f.Category == "mod")) {
                    Assert(File.Exists(file.LocalPath), "Platform matching requires real old or new JAR bytes");
                    if (file.Path.StartsWith("mods/platform") || file.Path.StartsWith("mods/duplicate")) {
                        string project = file.Path.StartsWith("mods/platform-renamed-id") ? "RenamedId" :
                            file.Path.StartsWith("mods/platform") ? "Platform" : "Duplicate";
                        file.Sources.Add(new JObject { ["platform"] = "modrinth", ["project"] = project, ["version"] = "Version" });
                    }
                }
            }
            var legacy = publisher.ImportBaselineFolder("Legacy", old, folders, Array.Empty<string>());
            var folder = publisher.ImportBaselineFolder("Folder", old, folders, Array.Empty<string>(), Resolve);
            string archivePath = Path.Combine(root, "old.zip");
            ZipFile.CreateFromDirectory(old, archivePath);
            var archive = publisher.ImportBaselineZip("Archive", archivePath, folders, Array.Empty<string>(), Resolve);
            Assert(publisher.Scan(folders, baselineId: legacy.Id, resolveModSources: Resolve)
                .Single(f => f.Path == "mods/platform-new.jar").Change == "新增", "Old baselines should work without platform metadata");
            publisher.SetBaselinePreviewSource(legacy.Id, old);
            foreach (var baseline in new[] { folder, archive, legacy }) {
                var rows = publisher.Scan(folders, baselineId: baseline.Id, resolveModSources: Resolve).ToDictionary(f => f.Path);
                void CheckPair(string path, string previous) {
                    Assert(rows[path].Change == "更新" && rows[path].PreviousPath == previous && !rows.ContainsKey(previous), path);
                }
                CheckPair("mods/identified-new.jar", "mods/identified-old.jar");
                CheckPair("mods/platform-new.jar", "mods/platform-old.jar");
                CheckPair("mods/platform-renamed-id-new.jar", "mods/platform-renamed-id-old.jar");
                CheckPair("mods/same.jar.disabled", "mods/same.jar");
                Assert(rows["config/options.json"].Change == "更新", "Unrelated files should compare by path");
                Assert(rows["mods/duplicate-old.jar"].Change == "已移出" && rows["mods/duplicate-a.jar"].Change == "新增" &&
                    rows["mods/duplicate-b.jar"].Change == "新增", "Ambiguous projects should not be paired");
                var baselineDocument = publisher.BaselineDocument(baseline.Id);
                Assert((baselineDocument["mod_projects"] is JObject) == (baseline.Id != legacy.Id) &&
                    baselineDocument["files"]!.All(f => f["mod_projects"] == null),
                    "Preview metadata should stay outside the player baseline records");
            }
            Write(old, "mods/platform-old.jar", "wrong baseline bytes");
            Assert(publisher.Scan(folders, baselineId: legacy.Id, resolveModSources: Resolve)
                .Single(f => f.Path == "mods/platform-new.jar").Change == "新增",
                "The on-disk old JAR must still match the selected baseline hash");
        });
        Test("mirror signing preflight requires the configured private bucket", _ => {
            var settings = new PackS3MirrorSettings { Bucket = "custom-pack-bucket" };
            string root = "https://fgws3-ocloud.ihep.ac.cn/";
            string key = "/pcl-mirror/pcl/index.json";
            string signed = "?X-Amz-Signature=" + new string('a', 64);
            settings.VerifySigningTarget(new Uri(root + settings.Bucket + key + signed));
            Throws(() => settings.VerifySigningTarget(new Uri(root + PackS3MirrorSettings.DefaultBucket + key + signed)));
            Throws(() => settings.VerifySigningTarget(new Uri(root + settings.Bucket + key)));
        });
        Test("dual publication preserves other channels and rejects a stale mirror release", _ => {
            JObject Pointer(string channel, long sequence, string release) => new() {
                ["id"] = channel, ["release_id"] = release, ["sequence"] = sequence,
                ["manifest_url"] = $"https://primary.example/pcl/test/{channel}/releases/{release}.json",
                ["manifest_sha256"] = PackProtocol.Hash(Encoding.UTF8.GetBytes(release))
            };
            var stable = Pointer("stable", 1, "a");
            var test = Pointer("test", 1, "b");
            var next = Pointer("stable", 2, "c");
            byte[] first = PackR2Publisher.JsonBytes(PackR2Publisher.UpdateMirrorIndex(null, "Pack", "test", stable));
            byte[] second = PackR2Publisher.JsonBytes(PackR2Publisher.UpdateMirrorIndex(first, "Pack", "test", test));
            var updated = PackR2Publisher.UpdateMirrorIndex(second, "Pack", "test", next);
            var channels = (JArray)updated["packs"]![0]!["channels"]!;
            Assert(channels.Count == 2 && JToken.DeepEquals(channels[0], next) && JToken.DeepEquals(channels[1], test), "Other mirrored channel was lost");
            byte[] final = PackR2Publisher.JsonBytes(updated);
            Assert(final.SequenceEqual(PackR2Publisher.JsonBytes(PackR2Publisher.UpdateMirrorIndex(final, "Pack", "test", next))), "Same-release retry changed index bytes");
            Throws(() => PackR2Publisher.UpdateMirrorIndex(final, "Pack", "test", stable));
            Throws(() => PackR2Publisher.UpdateMirrorIndex(final, "Pack", "test", Pointer("stable", 2, "conflict")));
        });
        TestLegacyUpgrades();
        Test("shared R2 source resolves to the same object key", root => {
            var updater = new PackUpdater(root);
            foreach (string prefix in new[] { "", "modpacks/kamitsubaki/" }) {
                string source = "https://packs.example.com/" + prefix.TrimEnd('/');
                var settings = new PackR2Settings {
                    Account = new string('a', 32), Bucket = "packs", PublicRoot = source,
                    AccessKey = "key", SecretKey = "secret"
                };
                var address = settings.Validate();
                string key = PackR2Publisher.ObjectKey(address, PackProtocol.IndexPath);
                Assert(key == prefix + "pcl/index.json", "Incorrect bucket index key");
                string index = "https://packs.example.com/" + key;
                Assert(updater.ResolveIndexAddress(source) == index, "Shared address did not resolve to the uploaded index");
                Assert(updater.ResolveIndexAddress(index) == index, "Direct JSON address was rewritten");
                updater.Bind(source, "test", "stable");
                Assert((string?)updater.Binding["source"] == source && updater.ResolveIndexAddress(source) == index, "Binding lost the shared address or its index");

                string archiveKey = PackPublisher.ArchiveKey(new string('b', 64));
                Assert(PackR2Publisher.ObjectKey(address, archiveKey) == prefix + archiveKey, "Archive escaped the bucket directory");
                var manifest = new JObject { ["archive"] = new JObject {
                    ["download_url"] = PackPublisher.PublicUrl(source, archiveKey), ["sha256"] = new string('b', 64)
                } };
                Assert(PackPublisher.PublishedSource(manifest) == source.TrimEnd('/'), "Could not copy the last published address");
            }

            const string legacy = "https://packs.example.com/custom-index";
            Write(root, ".pcl-update/binding.json", new JObject { ["source"] = legacy, ["pack_id"] = "test", ["channel"] = "stable" }.ToString());
            Assert(updater.ResolveIndexAddress(legacy) == legacy, "Existing extensionless index binding was rewritten");
            updater.Bind(legacy, "test", "stable");
            Assert(updater.ResolveIndexAddress(legacy) == legacy, "Rebinding changed the legacy index address");
        });
        Test("update, preserve, cleanup and one backup", root => {
            var updater = new PackUpdater(root); Write(root, "options.txt", "player"); Write(root, "mods/personal.jar", "extra");
            Apply(updater, Release(1, FileEntry("config/a.toml", "one"), FileEntry("mods/old.jar", "old"), FileEntry("options.txt", "defaults", "preserve")));
            Apply(updater, Release(5, FileEntry("config/a.toml", "five")));
            Assert(!System.IO.File.Exists(Path.Combine(root, "mods/old.jar")), "Cross-version cleanup failed");
            Assert(System.IO.File.ReadAllText(Path.Combine(root, "options.txt")) == "player", "Settings overwritten");
            Assert(System.IO.File.Exists(Path.Combine(root, "mods/personal.jar")), "Extra file removed");
            Assert(updater.HasBackup && !updater.NeedsRecovery, "Backup not finalized");
            Apply(updater, Release(5, FileEntry("config/a.toml", "five")));
            updater.Rollback();
            Assert(System.IO.File.ReadAllText(Path.Combine(root, "config/a.toml")) == "one", "No-op replaced previous backup");
            Assert((string?)updater.State["release_id"] == "v1", "State rollback failed");
            Assert(!updater.HasBackup, "Rollback should consume retained backup");
        });
        Test("runtime failure restores environment and retains last successful backup", root => {
            var updater = new PackUpdater(root); Write(root, "instance.json", "old-env");
            Apply(updater, Release(1, FileEntry("config/a", "one")));
            var runtime = new PackRuntime { Commit = () => { Write(root, "instance.json", "broken"); throw new IOException("injected failure"); } };
            runtime.Paths.Add("instance.json");
            Throws(() => Apply(updater, Release(2, FileEntry("config/a", "two")), () => runtime));
            Assert(System.IO.File.ReadAllText(Path.Combine(root, "instance.json")) == "old-env", "Environment not restored");
            Assert((string?)updater.State["release_id"] == "v1" && !updater.NeedsRecovery && updater.HasBackup, "Failed transaction committed");
        });
        Test("post-commit verification failure restores files", root => {
            var updater = new PackUpdater(root); Apply(updater, Release(1, FileEntry("config/a", "one")));
            Throws(() => Apply(updater, Release(2, FileEntry("config/a", "two")), () => new PackRuntime { Verify = () => throw new IOException("verify") }));
            Assert(System.IO.File.ReadAllText(Path.Combine(root, "config/a")) == "one", "File rollback failed");
        });
        Test("modified obsolete file blocks cleanup", root => {
            var updater = new PackUpdater(root); Apply(updater, Release(1, FileEntry("config/a", "one")));
            Write(root, "config/a", "player edit"); Throws(() => updater.Plan(Release(2), new string[0]));
            Assert(System.IO.File.ReadAllText(Path.Combine(root, "config/a")) == "player edit", "Modified file removed");
        });
        Test("optional deselection preserves installed component", root => {
            var updater = new PackUpdater(root);
            Apply(updater, Release(1, FileEntry("mods/extra.jar", "extra", component: "extra")), selected: new[] { "extra" });
            Apply(updater, Release(2), selected: new string[0]);
            Assert(System.IO.File.Exists(Path.Combine(root, "mods/extra.jar")), "Deselect uninstalled component");
            Apply(updater, Release(3), selected: new[] { "extra" });
            Assert(!System.IO.File.Exists(Path.Combine(root, "mods/extra.jar")), "Selected component obsolete file not cleaned");
        });
        Test("disable operation and rollback", root => {
            Write(root, "mods/a.jar", "old"); var updater = new PackUpdater(root); var release = Release(1);
            release["actions"] = new JArray(new JObject { ["path"] = "mods/a.jar", ["action"] = "disable", ["expected_sha256"] = PackProtocol.Hash(Encoding.UTF8.GetBytes("old")), ["reason"] = "incompatible" });
            Apply(updater, release); Assert(System.IO.File.Exists(Path.Combine(root, "mods/a.jar.disabled")), "Disable failed");
            updater.Rollback(); Assert(System.IO.File.Exists(Path.Combine(root, "mods/a.jar")), "Disable rollback failed");
        });
        Test("publisher removed component is cleaned", root => {
            var updater = new PackUpdater(root);
            Apply(updater, Release(1, FileEntry("mods/extra.jar", "extra", component: "extra")), selected: new[] { "extra" });
            var next = Release(2); next["components"] = new JArray(); Apply(updater, next);
            Assert(!System.IO.File.Exists(Path.Combine(root, "mods/extra.jar")), "Removed component left obsolete file");
        });
        Test("interrupted transaction recovery", root => {
            Write(root, "config/a", "broken"); Write(root, ".pcl-update/pending/files/0", "before");
            Write(root, ".pcl-update/pending/journal.json", new JObject { ["status"] = "applying", ["files"] = new JArray(new JObject { ["path"] = "config/a", ["existed"] = true, ["backup"] = "files/0" }) }.ToString());
            var updater = new PackUpdater(root); Assert(updater.NeedsRecovery, "Journal undetected"); updater.Recover();
            Assert(!updater.NeedsRecovery && System.IO.File.ReadAllText(Path.Combine(root, "config/a")) == "before", "Recovery failed");
        });
        Test("hash mismatch leaves installed files untouched", root => {
            Write(root, "config/a", "before"); var updater = new PackUpdater(root); var release = Release(1, FileEntry("config/a", "target"));
            Throws(() => updater.Apply(updater.Plan(release, new string[0]), () => new PackRuntime(), _ => { }, (url, target, size) => Write(Path.GetDirectoryName(target)!, Path.GetFileName(target), "wrong")));
            Assert(System.IO.File.ReadAllText(Path.Combine(root, "config/a")) == "before" && !updater.NeedsRecovery, "Download failure changed files");
        });
        Test("path and protocol rejection", root => {
            foreach (string path in new[] { "../outside", "mods/CON.jar", "saves/world", "PCL/config.json", "mods/a:stream" }) Throws(() => PackProtocol.Validate(Release(1, FileEntry(path, "a")), "release"));
            var unknown = Release(1); unknown["commands"] = new JArray("run"); Throws(() => PackProtocol.Validate(unknown, "release"));
        });
        Test("changed preview cannot execute", root => {
            var updater = new PackUpdater(root); var plan = updater.Plan(Release(1, FileEntry("config/a", "a")), new string[0]);
            Write(root, "config/a", "changed"); Throws(() => updater.Apply(plan, () => new PackRuntime(), _ => { }, Download));
        });
        Test("complete published ZIP supplies only required fallback ranges", root => {
            string author = Path.Combine(root, "author"), player = Path.Combine(root, "player"), zip = Path.Combine(root, "pack.zip");
            Write(author, "mods/reused.jar", "same");
            Write(player, "mods/reused.jar", "same");
            var publisher = new PackPublisher(author);
            publisher.SaveBaseline(publisher.Scan(PackPublisher.DefaultFolders));
            Write(author, "mods/new1.jar", "first");
            Write(author, "mods/new2.jar", "second");
            Write(author, "config/empty.toml", "");
            var files = publisher.Scan(PackPublisher.DefaultFolders);
            foreach (var file in files.Where(f => f.Category == "mod")) {
                file.EmbedMod = true;
                file.Urls.Add(file.Path == "mods/new1.jar" ? "https://cdn.modrinth.com/data/test/first.jar" :
                    file.Path == "mods/new2.jar" ? "https://mediafilez.forgecdn.net/files/test/second.jar" : "https://example.com/platform");
            }
            var release = publisher.Build(Details(), files, 1, zip, "https://example.com");
            var updater = new PackUpdater(player);
            var preview = updater.Plan(release, Array.Empty<string>());
            Assert(preview.BaselineMatches == 1 && preview.Changes.Count == 3, "Reused file should not be downloaded");
            var rows = preview.Changes.ToDictionary(change => change.Path);
            Assert(rows["mods/new1.jar"].DownloadSources.SequenceEqual(new[] { "Modrinth", "整合包 ZIP" }) &&
                rows["mods/new2.jar"].DownloadSources.SequenceEqual(new[] { "CurseForge", "整合包 ZIP" }) &&
                rows["config/empty.toml"].DownloadSources.SequenceEqual(new[] { "整合包 ZIP" }), "Incorrect download sources or priority in the preview");
            Assert(PackChange.DownloadSourceFor("https://cdn.modrinth.com.example.org/file") == "cdn.modrinth.com.example.org" &&
                !new PackChange { Kind = "删除" }.DownloadSources.Any(), "Custom hosts or local-only changes should not be attributed to a platform");
            int platform = 0, ranges = 0; long bytes = 0;
            var messages = new List<string>();
            updater.Apply(preview, () => new PackRuntime(), messages.Add,
                (_, _, _) => { platform++; throw new IOException("platform offline"); },
                new PackDownloadOptions { DownloadRange = LocalRanges(zip, size => { ranges++; bytes += size; }) });
            Assert(platform == 2 && ranges == 2 && bytes < new FileInfo(zip).Length, "Each ZIP fallback should run before retrying a platform URL");
            Assert(messages.Any(m => m.StartsWith("正在从 Modrinth 下载", StringComparison.Ordinal)) &&
                messages.Any(m => m.StartsWith("正在从 CurseForge 下载", StringComparison.Ordinal)) &&
                messages.Any(m => m.StartsWith("正在读取整合包 ZIP", StringComparison.Ordinal)), "Progress did not show the attempted sources");
            Assert(System.IO.File.ReadAllText(Path.Combine(player, "mods/new1.jar")) == "first" &&
                System.IO.File.ReadAllText(Path.Combine(player, "mods/new2.jar")) == "second", "Archive data was not installed");
            Assert(System.IO.File.Exists(Path.Combine(player, "config/empty.toml")), "Empty ZIP member was not installed");
        });
        Test("mod downloads try every route before starting a second round", root => {
            string author = Path.Combine(root, "author"), player = Path.Combine(root, "player"), zip = Path.Combine(root, "pack.zip");
            Write(author, "config/base.toml", "base");
            Write(player, "config/base.toml", "base");
            var publisher = new PackPublisher(author);
            publisher.SaveBaseline(publisher.Scan(PackPublisher.DefaultFolders));
            Write(author, "mods/new.jar", "new");
            var files = publisher.Scan(PackPublisher.DefaultFolders);
            var mod = files.Single(file => file.Category == "mod");
            mod.EmbedMod = true;
            mod.Urls.Add("https://cdn.modrinth.com/new.jar");
            mod.Urls.Add("https://mediafilez.forgecdn.net/new.jar");
            var release = publisher.Build(Details(), files, 1, zip, "https://primary.example");
            var updater = new PackUpdater(player);
            var requests = new List<string>();
            int ranges = 0;
            var local = LocalRanges(zip);
            updater.Apply(updater.Plan(release, Array.Empty<string>()), () => new PackRuntime(), _ => { },
                (address, _, _) => {
                    requests.Add(new Uri(address).Host);
                    throw new IOException("Platform offline");
                }, new PackDownloadOptions {
                    Mirror = new PackMirror("https://mirror.example", preferMirror: true),
                    ApproveWholeArchive = _ => throw new Exception("Second round should run before asking for a full archive"),
                    DownloadRange = (address, output, offset, length, total) => {
                        requests.Add(new Uri(address).Host);
                        if (++ranges < 3) throw new IOException("ZIP route offline");
                        local(address, output, offset, length, total);
                    }
                });
            Assert(requests.SequenceEqual(new[] {
                "cdn.modrinth.com", "mediafilez.forgecdn.net", "mirror.example", "primary.example",
                "cdn.modrinth.com", "mediafilez.forgecdn.net", "mirror.example"
            }), "Download routes were retried out of order");
            Assert(System.IO.File.ReadAllText(Path.Combine(player, "mods/new.jar")) == "new", "Second round did not install the mod");
        });
        Test("canceling before writes preserves the instance and cancellation ends at commit", root => {
            Write(root, "mods/original.jar", "original");
            var updater = new PackUpdater(root);
            var control = new PackDownloadControl();
            var plan = updater.Plan(Release(1, FileEntry("mods/new.jar", "new")), Array.Empty<string>());
            try {
                updater.Apply(plan, () => {
                    Assert(control.TryCancel(), "Cancellation should be possible while preparing the runtime");
                    return new PackRuntime { Commit = () => throw new Exception("Canceled updates must not write files") };
                }, _ => { }, Download, new PackDownloadOptions { Control = control });
                throw new Exception("Canceled update completed");
            } catch (PackUpdateCanceledException) { }
            Assert(!updater.NeedsRecovery && !updater.State.HasValues &&
                !File.Exists(Path.Combine(root, "mods/new.jar")) &&
                File.ReadAllText(Path.Combine(root, "mods/original.jar")) == "original", "Cancellation modified the instance");

            control = new PackDownloadControl();
            updater.Apply(plan, () => new PackRuntime {
                Commit = () => Assert(!control.CanCancel && !control.TryCancel(), "Cancellation should be unavailable once writes start")
            }, _ => { }, Download, new PackDownloadOptions { Control = control });
            Assert(File.ReadAllText(Path.Combine(root, "mods/new.jar")) == "new", "The completed update did not install the file");
        });
        Test("S3 fallback retries corrupt ZIP ranges and stays on the working route", root => {
            string author = Path.Combine(root, "author"), player = Path.Combine(root, "player"), zip = Path.Combine(root, "pack.zip");
            Write(author, "config/one.toml", "old");
            Write(player, "config/one.toml", "old");
            var publisher = new PackPublisher(author);
            publisher.SaveBaseline(publisher.Scan(PackPublisher.DefaultFolders));
            Write(author, "config/one.toml", "new");
            Write(author, "config/two.toml", "second");
            var release = publisher.Build(Details(), publisher.Scan(PackPublisher.DefaultFolders), 1, zip, "https://primary.example");
            var updater = new PackUpdater(player);
            var mirror = new PackMirror("https://mirror.example/packs");
            updater.Bind("https://primary.example", "test", "stable", mirror);
            Assert((string?)updater.Binding["mirror_url"] == mirror.Address, "Mirror setting was not saved");
            var requests = new List<string>();
            var local = LocalRanges(zip);
            updater.Apply(updater.Plan(release, Array.Empty<string>()), () => new PackRuntime(), _ => { },
                options: new PackDownloadOptions {
                    Mirror = mirror,
                    ApproveWholeArchive = _ => throw new Exception("The mirror should supply the ranges"),
                    DownloadRange = (address, output, offset, length, total) => {
                        requests.Add(address);
                        if (address.StartsWith("https://primary.example/", StringComparison.Ordinal)) {
                            output.Write(new byte[(int)length], 0, (int)length);
                            return;
                        }
                        local(address, output, offset, length, total);
                    }
                });
            string key = "/pcl/archives/" + (string)release["archive"]!["sha256"]! + ".zip";
            Assert(requests.SequenceEqual(new[] { "https://primary.example" + key, "https://mirror.example/packs" + key,
                "https://mirror.example/packs" + key }), "Expected verification-driven failover to the same archive key");
            Assert(System.IO.File.ReadAllText(Path.Combine(player, "config/one.toml")) == "new" &&
                System.IO.File.ReadAllText(Path.Combine(player, "config/two.toml")) == "second", "Mirror data was not installed");
        });
        Test("legacy adoption and forced actions run once", root => {
            string author = Path.Combine(root, "author"), player = Path.Combine(root, "player"), zip = Path.Combine(root, "pack.zip");
            Write(author, "mods/toggle.jar.disabled", "toggle");
            Write(author, "mods/removed.jar", "before");
            Write(author, "config/private.toml", "initial");
            Write(author, "config/settings.json", """{"server":{"rule":1},"local":0}""");
            foreach (string path in new[] { "mods/toggle.jar.disabled", "mods/removed.jar", "config/private.toml", "config/settings.json" })
                Write(player, path, System.IO.File.ReadAllText(Path.Combine(author, path)));
            var publisher = new PackPublisher(author);
            publisher.SaveBaseline(publisher.Scan(PackPublisher.DefaultFolders));
            System.IO.File.Move(Path.Combine(player, "mods/toggle.jar.disabled"), Path.Combine(player, "mods/toggle.jar"));
            Write(player, "mods/removed.jar", "player edit");
            Write(player, "config/private.toml", "my view distance");
            Write(player, "config/settings.json", """{"server":{"rule":1},"local":30}""");
            Write(author, "config/private.toml", "new default");
            Write(author, "config/settings.json", """{"server":{"rule":2},"local":99}""");
            var files = publisher.Scan(PackPublisher.DefaultFolders);
            files.Single(f => f.Path == "mods/removed.jar").Operation = PackPublisher.Delete;
            files.Single(f => f.Path == "mods/toggle.jar.disabled").EmbedMod = true;
            var settings = files.Single(f => f.Path == "config/settings.json");
            settings.Operation = PackPublisher.Patch;
            settings.JsonKeys = "/server/rule";
            var updater = new PackUpdater(player);
            var release = publisher.Build(Details(), files, 1, zip, "https://example.com");
            var range = new PackDownloadOptions { DownloadRange = LocalRanges(zip) };
            updater.Apply(updater.Plan(release, Array.Empty<string>()), () => new PackRuntime(), _ => { }, options: range);
            Assert(System.IO.File.Exists(Path.Combine(player, "mods/toggle.jar")), "Player enabled state was overwritten");
            Assert(!System.IO.File.Exists(Path.Combine(player, "mods/removed.jar")), "Explicit delete was not applied");
            Assert(System.IO.File.ReadAllText(Path.Combine(player, "config/private.toml")) == "my view distance", "Player config was overwritten");
            var merged = JObject.Parse(System.IO.File.ReadAllText(Path.Combine(player, "config/settings.json")));
            Assert((int)merged["server"]!["rule"]! == 2 && (int)merged["local"]! == 30, "JSON operation changed an unrelated key");
            Write(player, "mods/removed.jar", "player reinstalled");
            Assert(updater.Plan(release, Array.Empty<string>()).IsApplied, "Rechecking reapplied a forced action");
            updater.Apply(updater.Plan(release, Array.Empty<string>()), () => throw new Exception("Runtime shouldn't run"), _ => { }, options: range);
            Assert(System.IO.File.Exists(Path.Combine(player, "mods/removed.jar")), "Same-version check undid player modification");
            Write(author, "config/settings.json", """{"server":{"rule":3},"local":100}""");
            files = publisher.Scan(PackPublisher.DefaultFolders);
            files.Single(f => f.Path == "mods/toggle.jar.disabled").EmbedMod = true;
            files.Single(f => f.Path == "mods/removed.jar").Operation = PackPublisher.Exclude;
            var wholeFile = publisher.Build(Details("whole"), files, 2, zip, "https://example.com");
            var currentState = updater.State;
            var legacyState = (JObject)currentState.DeepClone();
            var patchedRecord = legacyState["files"]!.Cast<JObject>().Single(f => (string?)f["path"] == settings.Path);
            Assert((string?)patchedRecord["json_patch"]?["keys"]?[0] == "/server/rule", "Patch ownership missing");
            patchedRecord.Remove("json_patch");
            PackUpdater.Atomic(Path.Combine(player, ".pcl-update/state.json"), legacyState);
            Assert(!updater.Plan(wholeFile, Array.Empty<string>()).Changes.Any(c => c.Path == settings.Path), "Legacy merged config overwritten");
            PackUpdater.Atomic(Path.Combine(player, ".pcl-update/state.json"), currentState);
            updater.Apply(updater.Plan(wholeFile, Array.Empty<string>()), () => new PackRuntime(), _ => { }, options: range);
            string settingsPath = Path.Combine(player, "config/settings.json");
            Assert(JToken.DeepEquals(JObject.Parse(System.IO.File.ReadAllText(settingsPath)), merged), "Whole-file update overwrote patched player config");
            files.Single(f => f.Path == "config/settings.json").Operation = PackPublisher.Exclude;
            var removed = publisher.Build(Details("removed"), files, 3, zip, "https://example.com");
            updater.Apply(updater.Plan(removed, Array.Empty<string>()), () => new PackRuntime(), _ => { }, options: range);
            Assert(JToken.DeepEquals(JObject.Parse(System.IO.File.ReadAllText(settingsPath)), merged), "Cleanup deleted patched player config");
        });
        Test("explicit files can build a baseline and a release without scanning siblings", root => {
            string author = Path.Combine(root, "author");
            var publisher = new PackPublisher(author);
            const string selected = "config/sub/rules;main.toml";
            const string sibling = "config/sub/local.toml";
            Write(author, selected, "old");
            Write(author, sibling, "local");
            Write(root, "config/outside.toml", "outside");
            Assert(publisher.GetImportPath(Path.Combine(author, selected)) == selected, "Selection must preserve its instance path");
            Throws(() => publisher.GetImportPath(Path.Combine(root, "config/outside.toml")));
            Throws(() => publisher.Scan(Array.Empty<string>(), files: new[] { "config/missing.toml" }));
            var baseline = publisher.Scan(Array.Empty<string>(), files: new[] { selected, selected });
            Assert(baseline.Single().Path == selected, "Selection included siblings or duplicates");
            publisher.SaveBaseline(baseline);

            Write(author, selected, "updated");
            var draft = publisher.Scan(Array.Empty<string>(), files: new[] { selected });
            Assert(draft.Single().Change == "更新", "Selected file was not compared with its baseline");
            Assert(publisher.Scan(new[] { "config" }, files: new[] { selected }).Count == 2, "Overlapping scan and selection duplicated a file");
            var release = publisher.Build(Details(), draft, 1, Path.Combine(root, "selected.zip"), "https://example.com");
            Assert((string?)release["baseline"]!.Single()["path"] == selected &&
                (string?)release["files"]!.Single()["path"] == selected, "Selection did not reach the release");
        });
        Test("saved and imported baselines stay separate when previewing and building releases", root => {
            string author = Path.Combine(root, "author"), old = Path.Combine(root, "old-instance");
            string sourceZip = Path.Combine(root, "source.zip"), releaseZip = Path.Combine(root, "release.zip");
            const string rules = "config/rules.toml", removed = "config/old.toml";
            Write(author, rules, "legacy");
            var publisher = new PackPublisher(author);
            var folders = new[] { "config" };
            publisher.SaveBaseline(publisher.Scan(folders));
            Write(old, rules, "folder");
            var folder = publisher.ImportBaselineFolder("测试 A", old, folders, Array.Empty<string>());
            using (var zip = new ZipArchive(File.Create(sourceZip), ZipArchiveMode.Create)) {
                foreach (string path in new[] { rules, removed, "saves/world/level.dat" }) {
                    using var writer = new StreamWriter(zip.CreateEntry("pack/.minecraft/versions/zipped/" + path).Open());
                    writer.Write("zip");
                }
            }
            var archive = publisher.ImportBaselineZip("测试 B", sourceZip, Array.Empty<string>(), new[] { rules, removed });
            Throws(() => publisher.ImportBaselineFolder(folder.Name, old, folders, Array.Empty<string>()));
            File.Delete(sourceZip);
            Write(author, rules, "folder");
            publisher = new PackPublisher(author);
            Assert(publisher.Baselines.Count == 3, "Legacy and both named baselines should survive reopening");
            var details = Details();
            details.BaselineId = folder.Id;
            var folderDraft = publisher.Scan(folders, baselineId: folder.Id);
            Assert(folderDraft.Single().Change == "相同", "Preview should use the selected folder baseline");
            var folderRelease = publisher.Build(details, folderDraft, 1, releaseZip, "https://example.com");
            publisher.Remember(folderRelease);
            details.BaselineId = archive.Id;
            var zipDraft = publisher.Scan(folders, baselineId: archive.Id);
            Assert(zipDraft.Single(f => f.Path == rules).Change == "更新" &&
                zipDraft.Single(f => f.Path == removed).Change == "已移出", "Preview should use the selected ZIP instead of the latest release");
            var zipRelease = publisher.Build(details, zipDraft, 2, releaseZip, "https://example.com");
            Assert((string?)zipRelease["baseline_instance"] == "zipped" && zipRelease["baseline"]!.Count() == 2 &&
                zipRelease["baseline"]!.All(f => (string?)f["sha256"] == PackProtocol.Hash(Encoding.UTF8.GetBytes("zip"))),
                "Release should carry only the selected ZIP snapshot");
        });
        Test("cloud baselines cache only verified records and stay scoped to the configured library", root => {
            const string path = "config/rules.toml";
            Write(root, path, "local");
            var publisher = new PackPublisher(root);
            var local = publisher.SaveBaseline("测试 A", publisher.Scan(new[] { "config" }));
            string scope = PackR2Publisher.BaselineScope("account", "private-baselines", "pack-a");
            string otherScope = PackR2Publisher.BaselineScope("account", "private-baselines", "pack-b");
            var document = publisher.BaselineDocument(local.Id);
            string Hash(JObject value) => PackProtocol.Hash(PackR2Publisher.JsonBytes(value));
            publisher.CacheCloudBaseline(document, scope, Hash(document));
            Assert(publisher.BaselinesFor(scope).Count == 1, "Identical snapshots should share one choice");
            document["files"]![0]!["sha256"] = PackProtocol.Hash(Encoding.UTF8.GetBytes("remote"));
            string hash = Hash(document);
            publisher.CacheCloudBaseline(document, scope, hash);
            publisher.CacheCloudBaseline(document, scope, hash);
            publisher = new PackPublisher(root);
            var remote = publisher.BaselinesFor(scope).Single(b => b.Id != local.Id);
            Assert(remote.Name == "测试 A（云端）" && publisher.BaselinesFor(otherScope).Count == 1,
                "Remote snapshots should remain distinct and scoped after reopening");
            var draft = publisher.Scan(new[] { "config" }, baselineId: remote.Id);
            var details = Details();
            details.BaselineId = remote.Id;
            var release = publisher.Build(details, draft, 1, Path.Combine(root, "release.zip"), "https://example.com");
            Assert(draft.Single().Change == "更新" && JToken.DeepEquals(release["baseline"], document["files"]),
                "The cloud choice should reach the release manifest");
            var record = new JObject { ["name"] = "测试 A", ["sha256"] = hash };
            var index = new JObject { ["baselines"] = new JArray(record) };
            Assert(PackR2Publisher.ReadBaselineIndex(PackR2Publisher.JsonBytes(index)).Count == 1, "Valid cloud catalog should load");
            ((JArray)index["baselines"]!).Add(record.DeepClone());
            Throws(() => PackR2Publisher.ReadBaselineIndex(PackR2Publisher.JsonBytes(index)));
            document["files"]![0]!["path"] = "saves/world";
            Throws(() => publisher.CacheCloudBaseline(document, scope, Hash(document)));
        });
        Test("updates a Mod by ID and keeps the exact published custom JAR", root => {
            string author = Path.Combine(root, "author"), older = Path.Combine(root, "older.jar"), zip = Path.Combine(root, "pack.zip");
            void ModJar(string path, string id, string version, string body) {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var jar = new ZipArchive(File.Create(path), ZipArchiveMode.Create);
                using (var metadata = new StreamWriter(jar.CreateEntry("META-INF/neoforge.mods.toml").Open()))
                    metadata.Write($"[[mods]]\nmodId=\"{id}\"\nversion=\"{version}\"\n");
                using var contents = new StreamWriter(jar.CreateEntry("custom.txt").Open());
                contents.Write(body);
            }
            const string first = "mods/a-1.jar", baselinePath = "mods/a-2.jar", target = "mods/a-3.jar";
            string baselineFile = Path.Combine(author, baselinePath);
            string baselineCopy = Path.Combine(root, "baseline.jar");
            ModJar(older, "example", "1", "old");
            ModJar(baselineFile, "example", "2", "baseline");
            File.Copy(baselineFile, baselineCopy);
            var publisher = new PackPublisher(author);
            publisher.SaveBaseline(publisher.Scan(PackPublisher.DefaultFolders));
            File.Delete(baselineFile);
            string published = Path.Combine(author, target);
            ModJar(published, "example", "3", "publisher modifications");
            var files = publisher.Scan(PackPublisher.DefaultFolders);
            files.Single(file => file.Path == target).EmbedMod = true;
            var release = publisher.Build(Details(), files, 1, zip, "https://example.com");
            Assert((string?)release["files"]![0]!["id"] == "example", "Published custom Mod ID was missing");
            foreach (var (name, hasBaseline, hasTarget, matchingTarget) in new[] {
                ("p1", false, false, false), ("p2", true, false, false),
                ("p3", true, true, true), ("p4", true, true, false)
            }) {
                string player = Path.Combine(root, name);
                Directory.CreateDirectory(Path.Combine(player, "mods"));
                File.Copy(older, Path.Combine(player, first));
                if (hasBaseline) File.Copy(baselineCopy, Path.Combine(player, baselinePath));
                if (matchingTarget) File.Copy(published, Path.Combine(player, target));
                else if (hasTarget) ModJar(Path.Combine(player, target), "example", "3", "different build with same version");
                Write(player, "mods/unknown.jar", "unrecognized file");
                var updater = new PackUpdater(player);
                var plan = updater.Plan(release, Array.Empty<string>());
                Assert((plan.Bytes == 0) == matchingTarget, "Plan should download only when the target hash differs");
                var deletionPaths = plan.Changes.Where(change => change.Kind == "删除旧版本").Select(change => change.Path).ToList();
                Assert(deletionPaths.Contains(first) && deletionPaths.Contains(baselinePath) == hasBaseline, "Wrong cleanup preview");
                updater.Apply(plan, () => new PackRuntime(), _ => { }, options: new PackDownloadOptions { DownloadRange = LocalRanges(zip) });
                Assert(!File.Exists(Path.Combine(player, first)) && !File.Exists(Path.Combine(player, baselinePath)), "Old Mod copy survived");
                Assert(PackProtocol.HashFile(Path.Combine(player, target)) == PackProtocol.HashFile(published), "Published custom JAR was not applied");
                Assert(File.Exists(Path.Combine(player, "mods/unknown.jar")), "File without a recognizable ID was removed");
            }
        });
        Test("uses only the outer primary ID to replace a bundled Mod", root => {
            const string target = "mods/bundle.jar", oldBundle = "mods/old-bundle.jar", standalone = "mods/example-extra.jar";
            string author = Path.Combine(root, "a"), player = Path.Combine(root, "p"), zip = Path.Combine(root, "pack.zip");
            void Jar(string folder, string path, string version, params string[] ids) {
                string full = Path.Combine(folder, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                using var jar = new ZipArchive(File.Create(full), ZipArchiveMode.Create);
                using var metadata = new StreamWriter(jar.CreateEntry("META-INF/neoforge.mods.toml").Open());
                foreach (string id in ids) metadata.Write($"[[mods]]\nmodId=\"{id}\"\nversion=\"{version}\"\n");
            }
            Jar(player, oldBundle, "2", "example", "old_extra");
            Jar(player, standalone, "4", "example_extra");
            Jar(player, "mods/unrelated.jar", "1", "unrelated", "example");
            var publisher = new PackPublisher(author);
            Directory.CreateDirectory(Path.Combine(author, "mods"));
            File.Copy(Path.Combine(player, oldBundle), Path.Combine(author, oldBundle));
            publisher.SaveBaseline(publisher.Scan(PackPublisher.DefaultFolders));
            File.Delete(Path.Combine(author, oldBundle));
            Jar(author, target, "3", "example", "example_extra");
            var files = publisher.Scan(PackPublisher.DefaultFolders);
            files.Single(file => file.Path == target).EmbedMod = true;
            var release = publisher.Build(Details(), files, 1, zip, "https://example.com");
            Assert((string?)release["files"]![0]!["id"] == "example" && release["files"]![0]!["ids"] == null, "Release should record only the outer primary ID");
            var updater = new PackUpdater(player);
            var plan = updater.Plan(release, Array.Empty<string>());
            Assert(plan.PreviewChanges().Single().PreviousPaths.SequenceEqual(new[] { oldBundle }), "Bundle preview should only remove the previous bundle");
            updater.Apply(plan, () => new PackRuntime(), _ => { }, options: new PackDownloadOptions { DownloadRange = LocalRanges(zip) });
            Assert(File.Exists(Path.Combine(player, target)) && !File.Exists(Path.Combine(player, oldBundle)) &&
                File.Exists(Path.Combine(player, standalone)) && File.Exists(Path.Combine(player, "mods/unrelated.jar")), "Wrong JARs after bundle installation");
            updater.Rollback();
            Assert(File.Exists(Path.Combine(player, oldBundle)) && File.Exists(Path.Combine(player, standalone)), "Rollback lost a replaced JAR");

            var legacyBundleRelease = (JObject)release.DeepClone();
            var targetFile = (JObject)legacyBundleRelease["files"]![0]!;
            targetFile.Remove("id");
            targetFile["ids"] = new JArray("example", "example_extra");
            Assert(updater.Plan(legacyBundleRelease, Array.Empty<string>()).PreviewChanges().Single().PreviousPaths.SequenceEqual(new[] { oldBundle }),
                "Legacy bundle records should use their first ID");
            ((JArray)release["actions"]!).Add(new JObject {
                ["path"] = "mods/requested-extra.jar", ["action"] = "delete", ["force"] = true, ["id"] = "example_extra",
                ["expected_sha256"] = PackProtocol.HashFile(Path.Combine(player, standalone)), ["reason"] = "Remove standalone copy"
            });
            Assert(updater.Plan(release, Array.Empty<string>()).Changes.Single(change => change.Kind == "强制删除").Path == standalone,
                "Explicit force deletion should resolve the standalone ID without claiming the bundle");
        });
        Test("distinguishes Fabric IDs and resolves metadata-free Mods by platform fingerprints", root => {
            const string fabric = "mods/fabric-2.jar.disabled", oldFabric = "mods/fabric-1.jar.disabled";
            const string platform = "mods/library-2.jar", oldPlatform = "mods/renamed-library.jar";
            string author = Path.Combine(root, "a"), player = Path.Combine(root, "p"), zip = Path.Combine(root, "pack.zip");
            void Jar(string folder, string path, string metadata, string content) {
                string full = Path.Combine(folder, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                using var jar = new ZipArchive(File.Create(full), ZipArchiveMode.Create);
                using var writer = new StreamWriter(jar.CreateEntry(metadata).Open());
                writer.Write(content);
            }
            Jar(author, oldFabric, "fabric.mod.json", """{"schemaVersion":1,"id":"shared_mod","version":"1"}""");
            Directory.CreateDirectory(Path.Combine(player, "mods"));
            File.Copy(Path.Combine(author, oldFabric), Path.Combine(player, oldFabric));
            Write(author, "mods/library-1.jar", "known baseline library");
            var publisher = new PackPublisher(author);
            publisher.SaveBaseline(publisher.Scan(PackPublisher.DefaultFolders));
            File.Delete(Path.Combine(author, oldFabric));
            File.Delete(Path.Combine(author, "mods/library-1.jar"));
            Jar(author, fabric, "fabric.mod.json", """{"schemaVersion":1,"id":"shared_mod","version":"2"}""");
            Jar(player, "mods/forge.jar", "META-INF/neoforge.mods.toml", "[[mods]]\nmodId=\"shared_mod\"");
            Write(author, platform, "published library");
            Write(player, oldPlatform, "another platform version");
            Write(player, "mods/unknown.jar", "unrecognized file");
            JObject Source() => new() { ["platform"] = "modrinth", ["project"] = "Library1", ["version"] = "Version1" };
            var files = publisher.Scan(PackPublisher.DefaultFolders);
            foreach (var file in files.Where(file => file.LocalPath.Length != 0)) file.EmbedMod = true;
            files.Single(file => file.Path == platform).Sources.Add(Source());
            var release = publisher.Build(Details(), files, 1, zip, "https://example.com");
            Assert(release["files"]!.Any(file => (string?)file["path"] == fabric && (string?)file["fabric_id"] == "shared_mod"), "Fabric ID missing from release");
            int lookups = 0;
            var updater = new PackUpdater(player, candidates => {
                lookups++;
                foreach (var candidate in candidates.Where(file => file.Path == oldPlatform)) candidate.Sources.Add(Source());
            });
            var plan = updater.Plan(release, Array.Empty<string>());
            var platformPreview = plan.PreviewChanges().Single(row => row.Change.Path == platform);
            Assert(platformPreview.PreviousPaths.SequenceEqual(new[] { oldPlatform }), "Platform fallback missed renamed file");
            Assert((string?)platformPreview.Change.ModSources?.Single()["project"] == "Library1", "The preview lost the project ID used to resolve its icon");
            updater.Apply(plan, () => new PackRuntime(), _ => { }, options: new PackDownloadOptions { DownloadRange = LocalRanges(zip) });
            Assert(lookups == 1 && File.Exists(Path.Combine(player, fabric)) && !File.Exists(Path.Combine(player, oldFabric)) &&
                !File.Exists(Path.Combine(player, oldPlatform)) && File.Exists(Path.Combine(player, "mods/forge.jar")) &&
                File.Exists(Path.Combine(player, "mods/unknown.jar")), "Incorrect layered identification or preview lookup reuse");
        });
        Test("reinstalls missing Mods and updates disabled copies on upgrade and repair", root => {
            string author = Path.Combine(root, "author"), player = Path.Combine(root, "player"), zip = Path.Combine(root, "pack.zip");
            const string upgraded = "mods/upgrade.jar.disabled", unchanged = "mods/unchanged.jar", config = "config/player.toml";
            const string playerMod = "mods/player.jar", disabled = playerMod + ".disabled";
            Write(author, upgraded, "old mod");
            Write(author, unchanged, "unchanged mod");
            Write(author, playerMod, "baseline mod");
            Write(author, config, "old config");
            var publisher = new PackPublisher(author);
            publisher.SaveBaseline(publisher.Scan(PackPublisher.DefaultFolders));
            Write(author, upgraded, "new mod");
            Write(author, playerMod, "published player mod");
            Write(author, config, "new config");
            Write(player, disabled, "locally installed version");
            var files = publisher.Scan(PackPublisher.DefaultFolders);
            foreach (var file in files.Where(file => file.Category == "mod")) file.EmbedMod = true;
            var release = publisher.Build(Details(), files, 1, zip, "https://example.com");
            var updater = new PackUpdater(player);
            var plan = updater.Plan(release, Array.Empty<string>());
            var changes = plan.PreviewChanges().Select(row => row.Change).ToDictionary(change => change.Path);
            Assert(changes[upgraded].Kind == "更新" && changes[unchanged].Kind == "补装", "Both missing Mods should be installed");
            Assert(changes[disabled].Kind == "更新" && !changes.ContainsKey(playerMod), "Existing disabled Mod should update in place");
            Assert(plan.Preserved.Single().StartsWith(config, StringComparison.Ordinal), "Missing player config should be preserved");
            void Install(PackPlan pending) => updater.Apply(pending, () => new PackRuntime(), _ => { },
                options: new PackDownloadOptions { DownloadRange = LocalRanges(zip) });
            Install(plan);
            Assert(File.ReadAllText(Path.Combine(player, upgraded)) == "new mod", "The new Mod should be installed at its published disabled path");
            Assert(File.ReadAllText(Path.Combine(player, disabled)) == "published player mod", "A local disabled Mod should stay disabled");

            File.Delete(Path.Combine(player, unchanged));
            Write(player, disabled, "locally installed version");
            Write(player, config, "player config created after the release");
            var repair = updater.Plan(release, Array.Empty<string>());
            Assert(repair.IsCurrentArchiveRelease && !repair.IsApplied &&
                new HashSet<string>(repair.Changes.Select(change => change.Path)).SetEquals(new[] { unchanged, disabled }),
                "Repair of the applied release should only restore missing or mismatched Mods");
            Install(repair);
            Assert(updater.Plan(release, Array.Empty<string>()).IsApplied &&
                File.ReadAllText(Path.Combine(player, config)) == "player config created after the release", "Repair should leave configs alone");
        });
        Test("skipped optional upgrade remains managed until next release", root => {
            string author = Path.Combine(root, "author"), player = Path.Combine(root, "player"), zip = Path.Combine(root, "pack.zip");
            Write(author, "mods/optional.jar", "old");
            Write(player, "mods/optional.jar", "old");
            var publisher = new PackPublisher(author);
            publisher.SaveBaseline(publisher.Scan(PackPublisher.DefaultFolders));
            Write(author, "mods/optional.jar", "updated");
            var files = publisher.Scan(PackPublisher.DefaultFolders);
            var optional = files.Single();
            optional.Optional = true;
            optional.Urls.Add("https://example.com/updated");
            var release = publisher.Build(Details(), files, 1, zip, "https://example.com");
            var updater = new PackUpdater(player);
            updater.Apply(updater.Plan(release, Array.Empty<string>()), () => new PackRuntime(), _ => { },
                (_, _, _) => throw new IOException("offline"), new PackDownloadOptions { SkipOptional = _ => true });
            Assert(System.IO.File.ReadAllText(Path.Combine(player, optional.Path)) == "old", "Skip changed installed file");
            Apply(updater, publisher.Build(Details("retry"), files, 2, zip, "https://example.com"));
            Assert(System.IO.File.ReadAllText(Path.Combine(player, optional.Path)) == "updated", "Later release did not upgrade skipped mod");
        });
        Test("rollback after interrupted backup preparation", root => {
            var updater = new PackUpdater(root);
            Apply(updater, Release(1, FileEntry("config/a", "one")));
            Apply(updater, Release(2, FileEntry("config/a", "two")));
            Write(root, ".pcl-update/stage/backup/files/0", "incomplete");
            Write(root, ".pcl-update/pending/files/0", "legacy incomplete");
            updater.Rollback();
            Assert(!updater.NeedsRecovery && System.IO.File.ReadAllText(Path.Combine(root, "config/a")) == "one", "Incomplete backup blocked rollback");
        });
        Test("failed archive update preserves instance and reuses verified downloads", root => {
            string author = Path.Combine(root, "author"), player = Path.Combine(root, "player"), zip = Path.Combine(root, "pack.zip");
            Write(author, "config/a.toml", "first");
            Write(player, "config/a.toml", "first");
            var publisher = new PackPublisher(author);
            publisher.SaveBaseline(publisher.Scan(PackPublisher.DefaultFolders));
            Write(author, "config/a.toml", "second");
            Write(author, "mods/optional.jar", "optional");
            Write(author, "mods/required.jar", "required");
            var files = publisher.Scan(PackPublisher.DefaultFolders);
            foreach (var file in files.Where(f => f.Category == "mod")) file.Urls.Add("https://example.com/" + System.IO.Path.GetFileName(file.Path));
            files.Single(f => f.Path == "mods/optional.jar").Optional = true;
            var release = publisher.Build(Details(), files, 1, zip, "https://example.com");
            var updater = new PackUpdater(player);
            string manual = Path.Combine(root, "manual.jar");
            System.IO.File.WriteAllText(manual, "required", new UTF8Encoding(false));
            int slices = 0;
            var options = new PackDownloadOptions { DownloadRange = LocalRanges(zip, _ => slices++), SkipOptional = _ => true };
            void unavailable(string _, string __, long ___) => throw new IOException("offline");
            Throws(() => updater.Apply(updater.Plan(release, Array.Empty<string>()), () => new PackRuntime(), _ => { }, unavailable, options));
            Assert(System.IO.File.ReadAllText(Path.Combine(player, "config/a.toml")) == "first" && !updater.NeedsRecovery, "Download failure modified the player instance");
            options.SelectLocalFile = (path, _) => path.EndsWith("required.jar", StringComparison.Ordinal) ? manual : null;
            updater.Apply(updater.Plan(release, Array.Empty<string>()), () => new PackRuntime(), _ => { }, unavailable, options);
            Assert(slices == 1, "Already verified file was downloaded again");
            Assert(System.IO.File.Exists(Path.Combine(player, "mods/required.jar")) && !System.IO.File.Exists(Path.Combine(player, "mods/optional.jar")), "Optional/manual handling failed");
        });
        Test("Xaero world map and public waypoints preserve player data", root => {
            string author = Path.Combine(root, "author"), player = Path.Combine(root, "player"), zip = Path.Combine(root, "pack.zip");
            const string source = "xaero/world-map/Multiplayer_test/null/cm$author";
            const string waypoint = "xaero/minimap/Multiplayer_test/dim%0/mw$1.txt";
            Write(author, "config/base.toml", "baseline");
            Write(player, "config/base.toml", "baseline");
            Write(author, source + "/0_0.zip", "map");
            Write(author, waypoint, "public");
            Write(player, "xaero/world-map/Multiplayer_test/null/cm$player/0_0.zip", "existing map");
            Write(player, waypoint, "player waypoint");
            var publisher = new PackPublisher(author);
            publisher.SaveBaseline(publisher.Scan(PackPublisher.DefaultFolders));
            string destination = publisher.ImportXaeroMap(Path.Combine(author, source), "season-2");
            publisher.ImportXaeroWaypoints(Path.Combine(author, waypoint));
            Write(author, source + "/0_0.zip", "modified after import");
            var release = publisher.Build(Details(), publisher.Scan(PackPublisher.DefaultFolders), 1, zip, "https://example.com");
            var updater = new PackUpdater(player);
            updater.Apply(updater.Plan(release, Array.Empty<string>()), () => new PackRuntime(), _ => { },
                options: new PackDownloadOptions { DownloadRange = LocalRanges(zip) });
            Assert(System.IO.File.ReadAllText(Path.Combine(player, destination, "0_0.zip")) == "map", "Published map was not frozen");
            Assert(System.IO.File.ReadAllText(Path.Combine(player, waypoint)) == "player waypoint", "Existing waypoints were overwritten");
            Assert(System.IO.File.Exists(Path.Combine(player, "xaero/world-map/Multiplayer_test/null/cm$player/0_0.zip")), "Player map was replaced");
        });
        Test("archive plan validates fallback bounds and can extract a complete ZIP", root => {
            Write(root, "config/base.toml", "first");
            Write(root, "config/second.toml", "second");
            var publisher = new PackPublisher(root);
            publisher.SaveBaseline(publisher.Scan(PackPublisher.DefaultFolders));
            string zip = Path.Combine(root, "pack.zip");
            var manifest = publisher.Build(Details(), publisher.Scan(PackPublisher.DefaultFolders), 1, zip, "https://example.com");
            var entry = (JObject)manifest["files"]![0]!;
            using var archive = new PackArchive((JObject)manifest["archive"]!, zip, _ => { }, _ => throw new Exception(),
                (_, _, _, _, _) => throw new Exception("A complete verified archive should be reused"));
            string output = Path.Combine(root, "extracted");
            archive.ExtractRange(entry, output, Path.Combine(root, "scratch"));
            Assert(System.IO.File.ReadAllText(output) == "first", "Complete ZIP extraction failed");
            archive.ExtractRange((JObject)manifest["files"]![1]!, output, Path.Combine(root, "scratch"));
            Assert(System.IO.File.ReadAllText(output) == "second", "Second indexed ZIP entry extraction failed");
            entry["entry"]!["offset"] = (long)manifest["archive"]!["size"]!;
            Throws(() => PackProtocol.Validate(manifest, "release"));
        });
        Console.WriteLine($"{passed} tests passed");
    }
}
