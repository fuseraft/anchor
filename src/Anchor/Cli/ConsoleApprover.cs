using Anchor.Core;

namespace Anchor.Cli;

/// <summary>Asks on the terminal with a single keypress; anything but y or a means no.</summary>
public sealed class ConsoleApprover(Renderer renderer) : IApprover
{
    public async Task<Answer> ApproveAsync(ApprovalRequest request, CancellationToken ct)
    {
        renderer.Line(renderer.Warning($"  ? {request.Title}"));
        if (!string.IsNullOrEmpty(request.Detail))
            renderer.Diff(request.Detail);

        Console.Write($"  {renderer.AllowPrompt(request.AlwaysLabel)} › ");

        var answer = char.ToLowerInvariant(await ReadKeyAsync(ct)) switch
        {
            'y' => Answer.Yes,
            'a' when request.AlwaysLabel is not null => Answer.Always,
            _ => Answer.No,
        };
        Console.WriteLine(answer == Answer.No ? renderer.Error("no") : renderer.Success(answer.ToString().ToLowerInvariant()));
        return answer;
    }

    public bool CanAsk => true;

    public Task<string?> AskAsync(Question question, CancellationToken ct)
    {
        renderer.Line(renderer.Warning($"  ? {question.Text}"));
        const string other = "Something else (type an answer)";
        var answer = Picker.Choose(null, question.AllowOther ? [.. question.Options, other] : question.Options);
        if (answer == other)
        {
            Console.Write("  Your answer: ");
            answer = Console.ReadLine()?.Trim() is { Length: > 0 } typed ? typed : null;
        }
        return Task.FromResult(answer);
    }

    static async Task<char> ReadKeyAsync(CancellationToken ct)
    {
        if (Console.IsInputRedirected)
            return (await Task.Run(Console.ReadLine, ct))?.Trim().FirstOrDefault() ?? 'n';

        while (Console.KeyAvailable)
            Console.ReadKey(intercept: true);
        while (!Console.KeyAvailable)
            await Task.Delay(30, ct);
        return Console.ReadKey(intercept: true).KeyChar;
    }
}
