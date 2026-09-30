using Anchor.Core;
using Anchor.Tools;

namespace Anchor.Tests;

public class AskToolTests
{
    sealed class Person(string? answer) : IApprover
    {
        public Question? Asked { get; private set; }

        public bool CanAsk => true;

        public Task<Answer> ApproveAsync(ApprovalRequest request, CancellationToken ct) => Task.FromResult(Answer.No);

        public Task<string?> AskAsync(Question question, CancellationToken ct)
        {
            Asked = question;
            return Task.FromResult(answer);
        }
    }

    [Fact]
    public async Task PassesTheQuestion_AndReportsAChoice()
    {
        var person = new Person("SQLite");

        var result = await new AskTool(person).AskUser("Which database? ", [" Postgres", "SQLite", "", "SQLite"]);

        Assert.Equal("The user chose: SQLite", result);
        Assert.Equal("Which database?", person.Asked!.Text);
        Assert.Equal(["Postgres", "SQLite"], person.Asked.Options);
        Assert.True(person.Asked.AllowOther);
    }

    [Fact]
    public async Task ReportsATypedAnswer_AndADismissal()
    {
        Assert.Equal("The user answered: DuckDB", await new AskTool(new Person("DuckDB")).AskUser("Which?", ["A", "B"]));
        Assert.Contains("dismissed", await new AskTool(new Person(null)).AskUser("Which?", ["A", "B"]));
        Assert.Contains("dismissed", await new AskTool(new Person("C")).AskUser("Which?", ["A", "B"], allow_other: false));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    public async Task RejectsTooFewOrTooManyOptions(int count)
    {
        var options = Enumerable.Range(1, count).Select(i => $"o{i}").ToArray();

        await Assert.ThrowsAsync<ToolException>(() => new AskTool(new Person("o1")).AskUser("Which?", options));
    }

    [Fact]
    public void NoOneToAsk_ByDefault()
    {
        Assert.False(((IApprover)new FakeApprover(Answer.Yes)).CanAsk);
    }
}
