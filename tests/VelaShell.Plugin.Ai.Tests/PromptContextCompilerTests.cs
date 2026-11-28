using VelaShell.Plugin.Ai.Chat;

namespace VelaShell.Plugin.Ai.Tests;

[TestClass]
[TestCategory("Plugins")]
public sealed class PromptContextCompilerTests
{
    [TestMethod]
    public void PromptCompilation_IsDeterministic_AndReportsStableDiagnostics()
    {
        PromptCompilationInput input = new([
            new PromptSection("base", "A"),
            new PromptSection("empty", ""),
            new PromptSection("base", "B")
        ]);
        PromptCompiler compiler = new();

        PromptCompilationResult first = compiler.Compile(input);
        PromptCompilationResult second = compiler.Compile(input);

        Assert.AreEqual("AB", first.Text);
        Assert.AreSequenceEqual(first.SectionIds, second.SectionIds);
        Assert.AreSequenceEqual(
            first.Diagnostics.Select(d => (d.Code, d.Message, d.Index)),
            second.Diagnostics.Select(d => (d.Code, d.Message, d.Index)));
        Assert.AreEqual("prompt.empty-section", first.Diagnostics[0].Code);
        Assert.AreEqual("prompt.duplicate-section", first.Diagnostics[1].Code);
    }

    [TestMethod]
    public void ContextCompilation_TrimsToBudget_AndPreservesDeterministicDiagnostics()
    {
        List<TestMessage> history = [];
        for (int i = 0; i < 10; i++)
        {
            history.Add(new("user", $"u{i}", 10));
            history.Add(new("assistant", $"a{i}", 10));
        }

        ContextCompilationResult<TestMessage> result = PromptContextCompiler.CompileContext(
            new ContextCompilationInput<TestMessage>("system", history, 80, 10), TestMessageAdapter.Instance);

        Assert.IsGreaterThan(0, result.DroppedMessages);
        Assert.IsLessThanOrEqualTo(80, result.EstimatedTokens);
        Assert.AreEqual("context.history-trimmed", result.Diagnostics[0].Code);
    }

    [TestMethod]
    public void ContextCompilation_RemovesOrphanedToolParts_AndKeepsCompletePair()
    {
        List<TestMessage> history =
        [
            new("user", "question", 8),
            new("assistant", "call", 8, [new(ContextToolPartKind.Call, "complete")]),
            new("tool", "result", 8, [new(ContextToolPartKind.Result, "complete")]),
            new("assistant", "orphan", 8, [new(ContextToolPartKind.Call, "orphan")])
        ];

        ContextCompilationResult<TestMessage> result = PromptContextCompiler.CompileContext(
            new ContextCompilationInput<TestMessage>("system", history, 0, 0), TestMessageAdapter.Instance);

        Assert.HasCount(1, result.Messages.SelectMany(message => message.Parts)
            .Where(part => part.Kind == ContextToolPartKind.Call && part.CallId == "complete"));
        Assert.DoesNotContain(part => part.CallId == "orphan", result.Messages.SelectMany(message => message.Parts));
        Assert.Contains(d => d.Code == "context.orphan-tool-content", result.Diagnostics);
    }

    private sealed record TestMessage
    {
        public TestMessage(string role, string text, int tokens,
            IReadOnlyList<ContextToolPart>? parts = null)
        {
            Role = role;
            Text = text;
            Tokens = tokens;
            Parts = parts ?? [];
        }

        public string Role { get; }
        public string Text { get; }
        public int Tokens { get; }
        public IReadOnlyList<ContextToolPart> Parts { get; init; }
    }

    private sealed class TestMessageAdapter : IContextMessageAdapter<TestMessage>
    {
        public static TestMessageAdapter Instance { get; } = new();

        public ContextMessageInfo Describe(TestMessage message)
        {
            ContextMessageRole role = message.Role switch
            {
                "system" => ContextMessageRole.System,
                "user" => ContextMessageRole.User,
                "assistant" => ContextMessageRole.Assistant,
                "tool" => ContextMessageRole.Tool,
                _ => ContextMessageRole.Other
            };
            return new(role, message.Role, message.Tokens, message.Parts);
        }

        public TestMessage CreateSystem(string text) => new("system", text, 5);

        public TestMessage CreateUser(string text) => new("user", text, 5);

        public TestMessage Merge(TestMessage first, TestMessage second)
            => new(first.Role, first.Text + second.Text, first.Tokens + second.Tokens - 4, [.. first.Parts, .. second.Parts]);

        public ContextMessageNormalization<TestMessage> RemoveOrphanedToolParts(TestMessage message,
            IReadOnlySet<string> callIds, IReadOnlySet<string> resultIds)
        {
            ContextToolPart[] kept = [.. message.Parts.Where(part =>
                part.Kind == ContextToolPartKind.Call ? resultIds.Contains(part.CallId) : callIds.Contains(part.CallId))];
            int removed = message.Parts.Count - kept.Length;
            return removed == 0
                ? new(message, true, 0)
                : new(message with { Parts = kept }, kept.Length > 0, removed);
        }
    }
}
