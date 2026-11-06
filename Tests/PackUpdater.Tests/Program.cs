using Newtonsoft.Json.Linq;
using PCL.Modpack;
using System.Text;

internal static class Program {
    static int passed;
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Test(string name, Action<string> action) {
        string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root); passed++; Console.WriteLine("PASS " + name); }
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
    static void Main() {
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
        Console.WriteLine($"{passed} tests passed");
    }
}
