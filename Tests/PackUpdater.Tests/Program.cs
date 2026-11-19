using Newtonsoft.Json.Linq;
using PCL.Modpack;
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
        TestArchiveLinks();
        Test("publisher credentials can be reopened before publishing and cleared separately", root => {
            var publisher = new PackPublisher(root);
            publisher.SaveCredentials("", "");
            publisher.SaveCredentials("r2-access", "");
            publisher.SaveCredentials("", "mirror-secret", mirror: true);
            var reopened = new PackPublisher(root);
            Assert((string?)reopened.ReadCredentials()["access"] == "r2-access", "R2 edit was lost");
            Assert((string?)reopened.ReadCredentials(mirror: true)["secret"] == "mirror-secret", "Mirror edit was lost");
            string unfinished = publisher.ProjectFile("credentials.json") + ".tmp";
            File.Copy(publisher.ProjectFile("credentials.json"), unfinished);
            reopened.SaveCredentials("", "");
            Assert(!File.Exists(unfinished), "Unfinished credential write was not removed");
            Assert(!publisher.ReadCredentials().HasValues && publisher.ReadCredentials(mirror: true).HasValues, "Credential stores must be independent");
            reopened.SaveCredentials("", "", mirror: true);
            Assert(!publisher.ReadCredentials(mirror: true).HasValues, "Mirror credential was not removed");
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
                file.Urls.Add("https://example.com/platform");
            }
            var release = publisher.Build(Details(), files, 1, zip, "https://example.com");
            var updater = new PackUpdater(player);
            var preview = updater.Plan(release, Array.Empty<string>());
            Assert(preview.BaselineMatches == 1 && preview.Changes.Count == 3, "Reused file should not be downloaded");
            int platform = 0, ranges = 0; long bytes = 0;
            updater.Apply(preview, () => new PackRuntime(), _ => { },
                (_, _, _) => { platform++; throw new IOException("platform offline"); },
                new PackDownloadOptions { DownloadRange = LocalRanges(zip, size => { ranges++; bytes += size; }) });
            Assert(platform == 4 && ranges == 2 && bytes < new FileInfo(zip).Length, "Fallback downloaded more than the two ZIP slices");
            Assert(System.IO.File.ReadAllText(Path.Combine(player, "mods/new1.jar")) == "first" &&
                System.IO.File.ReadAllText(Path.Combine(player, "mods/new2.jar")) == "second", "Archive data was not installed");
            Assert(System.IO.File.Exists(Path.Combine(player, "config/empty.toml")), "Empty ZIP member was not installed");
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
        Test("renamed mod is previewed together and only a verified duplicate is removed", root => {
            string author = Path.Combine(root, "author"), player = Path.Combine(root, "player");
            Write(author, "mods/a-1.jar", "old");
            var publisher = new PackPublisher(author);
            var baseline = publisher.Scan(PackPublisher.DefaultFolders);
            baseline.Single().ModId = "example";
            publisher.SaveBaseline(baseline);
            System.IO.File.Delete(Path.Combine(author, "mods/a-1.jar"));
            Write(author, "mods/a-2.jar", "new");
            var files = publisher.Scan(PackPublisher.DefaultFolders);
            var newer = files.Single(f => f.Path == "mods/a-2.jar");
            newer.ModId = "example";
            newer.EmbedMod = true;
            var release = publisher.Build(Details(), files, 1, Path.Combine(root, "pack.zip"), "https://example.com");
            var updater = new PackUpdater(player);
            Write(player, "mods/a-1.jar", "old");
            var upgrade = updater.Plan(release, Array.Empty<string>());
            var preview = upgrade.PreviewChanges().Single();
            Assert(upgrade.Changes.Count == 2 && preview.Change.Path == "mods/a-2.jar" &&
                preview.PreviousPaths.SequenceEqual(new[] { "mods/a-1.jar" }), "Renamed upgrade should be previewed as one Mod row");
            Write(player, "mods/a-2.jar", "new");
            Write(player, "mods/a-1.jar", "player");
            Throws(() => updater.Plan(release, Array.Empty<string>()));
            Write(player, "mods/a-1.jar", "old");
            var plan = updater.Plan(release, Array.Empty<string>());
            Assert(plan.Changes.Single().Path == "mods/a-1.jar" && plan.Bytes == 0, "Incorrect duplicate cleanup preview");
            Assert(plan.PreviewChanges().Single().Change.Kind == "删除旧版本", "Standalone duplicate cleanup should remain visible");
            updater.Apply(plan, () => new PackRuntime(), _ => { });
            Assert(!System.IO.File.Exists(Path.Combine(player, "mods/a-1.jar")) && System.IO.File.ReadAllText(Path.Combine(player, "mods/a-2.jar")) == "new", "Duplicate mod survived");
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
            archive.Extract(entry, output, Path.Combine(root, "scratch"));
            Assert(System.IO.File.ReadAllText(output) == "first", "Complete ZIP extraction failed");
            archive.Extract((JObject)manifest["files"]![1]!, output, Path.Combine(root, "scratch"));
            Assert(System.IO.File.ReadAllText(output) == "second", "Second indexed ZIP entry extraction failed");
            entry["entry"]!["offset"] = (long)manifest["archive"]!["size"]!;
            Throws(() => PackProtocol.Validate(manifest, "release"));
        });
        Console.WriteLine($"{passed} tests passed");
    }
}
