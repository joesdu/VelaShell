using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Chat;
using VelaShell.PluginSdk.Testing;
using VelaShell.PluginSdk.TimeSeries;

namespace VelaShell.Plugin.Ai.Tests;

[TestClass]
[TestCategory("Plugins")]
public sealed class GeneratedImageStoreTests
{
    private static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3];

    [TestMethod]
    public void SaveAndLoad_RoundTripBinaryAndRejectUnsafeReferences()
    {
        using var context = new TestPluginContext();
        var store = new GeneratedImageStore(context.DataDirectory);
        ChatGeneratedImage image = store.Save(new DataContent(Png, "image/png"));
        Assert.AreSequenceEqual(Png, store.Load(image).Data.ToArray());
        Assert.AreEqual("image/png", store.Load(image).MediaType);
        Assert.ThrowsExactly<ArgumentException>(() => store.Load(new ChatGeneratedImage("../secret.png", "image/png")));
        Assert.ThrowsExactly<ArgumentException>(() => store.Load(image with { MediaType = "image/svg+xml" }));
        Assert.ThrowsExactly<ArgumentException>(() => store.Save(new DataContent(Png, "image/svg+xml")));
        Assert.ThrowsExactly<ArgumentException>(() => store.Save(new DataContent(Png, "image/jpeg")));
        Assert.ThrowsExactly<ArgumentException>(() => store.Save(new DataContent(new byte[GeneratedImageStore.MaxImageBytes + 1], "image/png")));
    }

    [TestMethod]
    public void SupportedFormats_RoundTrip()
    {
        using var context = new TestPluginContext();
        var store = new GeneratedImageStore(context.DataDirectory);
        (string type, byte[] bytes)[] formats =
        [
            ("image/jpeg", [255, 216, 255, 1]),
            ("image/gif", "GIF89a"u8.ToArray()),
            ("image/webp", "RIFF1234WEBP"u8.ToArray())
        ];
        foreach ((string type, byte[] bytes) in formats)
        {
            DataContent loaded = store.Load(store.Save(new DataContent(bytes, type)));
            Assert.AreEqual(type, loaded.MediaType);
            Assert.AreSequenceEqual(bytes, loaded.Data.ToArray());
        }
    }

    [TestMethod]
    public async Task ImagesOnlyMetadata_RoundTripsAndLimitsCount()
    {
        using var context = new TestPluginContext();
        var images = new GeneratedImageStore(context.DataDirectory);
        ChatGeneratedImage[] refs = [.. Enumerable.Range(0, 5).Select(_ => images.Save(new DataContent(Png, "image/png")))];
        var store = new ChatHistoryStore(context);
        await store.InitAsync();
        string id = ChatHistoryStore.NewConversationId();
        await store.AppendAsync(id, DateTimeOffset.UtcNow, 0, "assistant", "");
        await store.AppendMetaAsync(id, 0, new ChatTurnMeta(Images: refs));
        ChatTurnMeta meta = (await store.LoadAsync(id)).Single().Meta!;
        Assert.HasCount(4, meta.Images!);
        Assert.AreEqual(refs[0], meta.Images![0]);
        Assert.AreSequenceEqual(Png, images.Load(meta.Images[0]).Data.ToArray());
    }

    private static string PathOf(TestPluginContext context, ChatGeneratedImage image)
        => Path.Combine(context.DataDirectory, "generated-images", image.FileName);

    private static async Task WriteImageTurnAsync(ChatHistoryStore store, string conversation, int sequence, string role,
        params ChatGeneratedImage[] images)
    {
        await store.AppendAsync(conversation, DateTimeOffset.UtcNow, sequence, role, role);
        await store.AppendMetaAsync(conversation, sequence, new ChatTurnMeta(Images: images));
    }

    [TestMethod]
    public async Task DeletingConversation_ReleasesOnlyImagesNoOtherConversationOrLiveViewUses()
    {
        using var context = new TestPluginContext();
        var images = new GeneratedImageStore(context.DataDirectory);
        ChatGeneratedImage Save() => images.Save(new DataContent(Png, "image/png"));
        ChatGeneratedImage attached = Save(), generated = Save(), shared = Save(), live = Save(), other = Save(), orphan = Save();
        var store = new ChatHistoryStore(context);
        await store.InitAsync();
        string doomed = ChatHistoryStore.NewConversationId(), kept = ChatHistoryStore.NewConversationId();
        await WriteImageTurnAsync(store, doomed, 0, "user", attached, shared);
        await WriteImageTurnAsync(store, doomed, 1, "assistant", generated, live);
        await WriteImageTurnAsync(store, kept, 0, "assistant", shared, other);

        IReadOnlyList<string> owned = await store.ImageFilesAsync(doomed);
        Assert.AreSequenceEqual(new[] { attached, shared, generated, live }.Select(i => i.FileName).Order(),
            owned.Order());
        await store.DeleteAsync(doomed);
        int removed = await store.ReleaseImagesAsync(images, owned, new HashSet<string> { live.FileName },
            DateTime.UtcNow.AddMinutes(1));

        Assert.AreEqual(2, removed);
        Assert.IsFalse(File.Exists(PathOf(context, attached)));
        Assert.IsFalse(File.Exists(PathOf(context, generated)));
        Assert.IsTrue(File.Exists(PathOf(context, shared)), "另一个会话还引用着,不能删");
        Assert.IsTrue(File.Exists(PathOf(context, live)), "还活在界面上的对话要用,不能删");
        Assert.IsTrue(File.Exists(PathOf(context, other)));
        Assert.IsTrue(File.Exists(PathOf(context, orphan)), "删会话只回收它自己引用的图");
        Assert.AreSequenceEqual(Png, images.Load(shared).Data.ToArray());
    }

    [TestMethod]
    public async Task ClearAndStartupSweep_ReleaseUnreferencedImagesButKeepLiveRecentAndForeignFiles()
    {
        using var context = new TestPluginContext();
        var images = new GeneratedImageStore(context.DataDirectory);
        ChatGeneratedImage Save() => images.Save(new DataContent(Png, "image/png"));
        ChatGeneratedImage referenced = Save(), oldOrphan = Save(), freshOrphan = Save(), live = Save();
        string directory = Path.Combine(context.DataDirectory, "generated-images");
        DateTime old = DateTime.UtcNow.AddDays(-2);
        foreach (ChatGeneratedImage image in new[] { referenced, oldOrphan, live })
            File.SetLastWriteTimeUtc(PathOf(context, image), old);
        string foreign = Path.Combine(directory, "notes.txt");
        File.WriteAllText(foreign, "not ours");
        string nested = Path.Combine(directory, "sub");
        Directory.CreateDirectory(nested);
        string nestedImage = Path.Combine(nested, Path.GetFileName(PathOf(context, oldOrphan)));
        File.WriteAllBytes(nestedImage, Png);
        File.SetLastWriteTimeUtc(nestedImage, old);
        var store = new ChatHistoryStore(context);
        await store.InitAsync();
        await WriteImageTurnAsync(store, ChatHistoryStore.NewConversationId(), 0, "assistant", referenced);

        // 启动回收:只动一天前就写下、没有任何会话引用、也不在界面上的
        int swept = await store.ReleaseImagesAsync(images, null, new HashSet<string> { live.FileName },
            DateTime.UtcNow.AddDays(-1));
        Assert.AreEqual(1, swept);
        Assert.IsFalse(File.Exists(PathOf(context, oldOrphan)));
        Assert.IsTrue(File.Exists(PathOf(context, referenced)));
        Assert.IsTrue(File.Exists(PathOf(context, freshOrphan)), "刚写下的可能是在途一轮的图,元数据还没写");
        Assert.IsTrue(File.Exists(PathOf(context, live)));

        // 清空历史:没有会话再引用它们了,除了界面上还活着的,其余全回收
        await store.ClearAsync();
        int cleared = await store.ReleaseImagesAsync(images, null, new HashSet<string> { live.FileName },
            DateTime.UtcNow.AddMinutes(1));
        Assert.AreEqual(2, cleared);
        Assert.IsFalse(File.Exists(PathOf(context, referenced)));
        Assert.IsFalse(File.Exists(PathOf(context, freshOrphan)));
        Assert.IsTrue(File.Exists(PathOf(context, live)));
        Assert.IsTrue(File.Exists(foreign), "不是本插件命名规则下的文件一律不碰");
        Assert.IsTrue(File.Exists(nestedImage), "不进子目录");
    }

    [TestMethod]
    public async Task ReleaseImages_DeletesNothingWhenReferencesCannotBeReadCompletely()
    {
        using var context = new TestPluginContext();
        var images = new GeneratedImageStore(context.DataDirectory);
        ChatGeneratedImage image = images.Save(new DataContent(Png, "image/png"));
        var store = new ChatHistoryStore(context);
        await store.InitAsync();
        // 某个会话的附加信息撞上查询上限:后面可能还有没读到的引用,宁可一张都不删
        ITimeSeries meta = await context.TimeSeries.OpenAsync(new TimeSeriesDefinition("chat_meta",
        [
            TimeSeriesColumn.Tag("conv"),
            TimeSeriesColumn.Field("seq", TimeSeriesValueKind.Integer),
            TimeSeriesColumn.Field("payload", TimeSeriesValueKind.Text)
        ]));
        DateTimeOffset start = DateTimeOffset.UtcNow.AddDays(-1);
        for (int batch = 0; batch < TimeSeriesLimits.MaxQueryLimit / TimeSeriesLimits.MaxWriteBatch; batch++)
        {
            await meta.WriteManyAsync(Enumerable.Range(0, TimeSeriesLimits.MaxWriteBatch).Select(i => new TimeSeriesPoint(
                start.AddMilliseconds(batch * TimeSeriesLimits.MaxWriteBatch + i),
                new Dictionary<string, string> { ["conv"] = "busy" },
                new Dictionary<string, TimeSeriesValue>
                {
                    ["seq"] = TimeSeriesValue.FromInteger(batch * TimeSeriesLimits.MaxWriteBatch + i),
                    ["payload"] = TimeSeriesValue.FromText("{}")
                })));
        }

        Assert.AreEqual(0, await store.ReleaseImagesAsync(images, null, new HashSet<string>(), DateTime.UtcNow.AddMinutes(1)));
        Assert.IsTrue(File.Exists(PathOf(context, image)));
    }
}
