using System.IO.Compression;
using Newtonsoft.Json.Linq;
using PCL.Modpack;

internal static partial class Program {
    static void InstallLegacyV1(string player, params JObject[] records) {
        var updater = new PackUpdater(player);
        Apply(updater, Release(1, records));
        var state = updater.State;
        foreach (JObject record in state["files"]!) record.Remove("whole_file");
        PackUpdater.Atomic(Path.Combine(player, ".pcl-update/state.json"), state);
    }

    static void TestLegacyUpgrades() {
        Test("legacy v1 managed JSON follows subsequent whole-file releases", player => {
            const string path = "config/server.json";
            InstallLegacyV1(player, FileEntry(path, """{"rule":1}"""));
            var updater = new PackUpdater(player);
            for (int n = 2; n <= 3; n++) {
                string body = "{\"rule\":" + n + "}";
                Apply(updater, Release(n, FileEntry(path, body)));
                Assert(File.ReadAllText(Path.Combine(player, path)) == body, "Old v1 config did not advance to " + n);
            }
        });
        Test("legacy v1 Mod without ID upgrades under a new name", root => CheckRenamedMod(root, false));
        Test("preinstalled renamed Mod inherits disabled state", root => CheckRenamedMod(root, true));
        Test("withdrawn JSON patch survives later v1 reintroduction", root => {
            const string path = "config/settings.json";
            string author = Path.Combine(root, "author"), player = Path.Combine(root, "player"), zip = Path.Combine(root, "pack.zip");
            Write(author, path, """{"pack":1,"personal":0}""");
            Write(player, path, """{"pack":1,"personal":9}""");
            var publisher = new PackPublisher(author);
            publisher.SaveBaseline(publisher.Scan(PackPublisher.DefaultFolders));
            Write(author, path, """{"pack":2,"personal":0}""");
            var files = publisher.Scan(PackPublisher.DefaultFolders);
            files.Single().Operation = PackPublisher.Patch;
            files.Single().JsonKeys = "/pack";
            var updater = new PackUpdater(player);
            var release = publisher.Build(Details(), files, 1, zip, "https://example.com");
            updater.Apply(updater.Plan(release, Array.Empty<string>()), () => new PackRuntime(), _ => { },
                options: new PackDownloadOptions { DownloadRange = LocalRanges(zip) });
            files.Single().Operation = PackPublisher.Exclude;
            Apply(updater, publisher.Build(Details("removed"), files, 2, zip, "https://example.com"));
            Apply(updater, Release(3));
            Apply(updater, Release(4, FileEntry(path, """{"pack":4,"personal":0}""")));
            var json = JObject.Parse(File.ReadAllText(Path.Combine(player, path)));
            Assert((int)json["personal"]! == 9 && (int)json["pack"]! == 2, "Reintroduced config replaced player data");
        });
        Test("legacy missing optional ownership recovers only matching baseline", root => {
            string author = Path.Combine(root, "author"), player = Path.Combine(root, "player"), zip = Path.Combine(root, "pack.zip");
            foreach (string path in new[] { "mods/optional.jar", "mods/custom.jar" }) {
                Write(author, path, "old");
                Write(player, path, path.Contains("custom") ? "personal" : "old");
            }
            var publisher = new PackPublisher(author);
            publisher.SaveBaseline(publisher.Scan(PackPublisher.DefaultFolders));
            InstallLegacyV1(player);
            foreach (var f in publisher.Scan(PackPublisher.DefaultFolders)) Write(author, f.Path, "updated");
            var files = publisher.Scan(PackPublisher.DefaultFolders);
            foreach (var f in files) { f.Optional = true; f.Urls.Add("https://example.com/updated"); }
            var updater = new PackUpdater(player);
            Apply(updater, publisher.Build(Details(), files, 2, zip, "https://example.com"));
            Assert(File.ReadAllText(Path.Combine(player, "mods/optional.jar")) == "updated", "Lost optional record was not recovered");
            Assert(File.ReadAllText(Path.Combine(player, "mods/custom.jar")) == "personal", "Adopted a modified optional file");
        });
    }

    static void CheckRenamedMod(string root, bool preinstalled) {
        string author = Path.Combine(root, "author"), player = Path.Combine(root, "player"), zip = Path.Combine(root, "pack.zip");
        const string old = "mods/a-1.jar", newer = "mods/a-2.jar";
        void Jar(string path, int version) {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
            using var writer = new StreamWriter(archive.CreateEntry("META-INF/neoforge.mods.toml").Open());
            writer.WriteLine("[[mods]]\nmodId=\"example\"\nversion=\"" + version + "\"");
        }
        Jar(Path.Combine(author, old), 1);
        Directory.CreateDirectory(Path.Combine(player, "mods"));
        File.Copy(Path.Combine(author, old), Path.Combine(player, old));
        var prior = FileEntry(old, "");
        prior["sha256"] = PackProtocol.HashFile(Path.Combine(player, old));
        prior["size"] = new FileInfo(Path.Combine(player, old)).Length;
        if (!preinstalled) InstallLegacyV1(player, prior);
        var publisher = new PackPublisher(author);
        publisher.SaveBaseline(publisher.Scan(PackPublisher.DefaultFolders));
        File.Delete(Path.Combine(author, old));
        Jar(Path.Combine(author, newer), 2);
        if (preinstalled) {
            File.Move(Path.Combine(player, old), Path.Combine(player, old + ".disabled"));
            File.Copy(Path.Combine(author, newer), Path.Combine(player, newer));
        }
        var files = publisher.Scan(PackPublisher.DefaultFolders);
        files.Single(f => f.Path == newer).EmbedMod = true;
        var release = publisher.Build(Details(), files, 2, zip, "https://example.com");
        var updater = new PackUpdater(player);
        var plan = updater.Plan(release, Array.Empty<string>());
        if (preinstalled) Assert(plan.Bytes == 0, "Preinstalled Mod should be reused");
        updater.Apply(plan, () => new PackRuntime(), _ => { }, options: new PackDownloadOptions { DownloadRange = LocalRanges(zip) });
        string expected = Path.Combine(player, newer + (preinstalled ? ".disabled" : ""));
        Assert(Directory.GetFiles(Path.Combine(player, "mods")).Single() == Path.GetFullPath(expected) &&
            PackProtocol.HashFile(expected) == PackProtocol.HashFile(Path.Combine(author, newer)), "Incorrect upgraded Mod or enabled state");
        updater.Rollback();
        Assert(File.Exists(Path.Combine(player, old + (preinstalled ? ".disabled" : ""))), "Rollback lost old Mod");
    }
}
