using System.ComponentModel;
using Anchor.Core;
using Microsoft.Extensions.AI;

namespace Anchor.Tools;

/// <summary>ask_user: a multiple-choice question for the user, answered with the same picker as anchor setup.</summary>
public sealed class AskTool(IApprover person)
{
    public const int MaxOptions = 8;

    public IEnumerable<AIFunction> All() => [AIFunctionFactory.Create(AskUser, "ask_user")];

    [Description("Ask the user a multiple-choice question and wait for the answer. Use it only when you're blocked on a decision " +
                 "that is genuinely the user's (a preference, or intent the request and the code don't settle), not for facts you can " +
                 "look up or for permission, which the tools ask for themselves. Offer 2 to 8 short, distinct options, the one you " +
                 "recommend first. The user can also type their own answer unless allow_other is false.")]
    public async Task<string> AskUser(
        [Description("The question, as one clear sentence.")] string question,
        [Description("The answers to choose from, 2 to 8, most recommended first.")] string[] options,
        [Description("Let the user type an answer that isn't in the options.")] bool allow_other = true,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(question))
            throw new ToolException("question is empty.");
        var choices = options.Select(o => o.Trim()).Where(o => o.Length > 0).Distinct().ToList();
        if (choices.Count is < 2 or > MaxOptions)
            throw new ToolException($"Give between 2 and {MaxOptions} distinct options; got {choices.Count}.");

        var answer = await person.AskAsync(new Question(question.Trim(), choices, allow_other), ct);
        if (!allow_other && answer is not null && !choices.Contains(answer))
            answer = null;
        return answer is null
            ? "The user dismissed the question without answering. Don't ask it again; continue with your best judgment and say what you assumed, or stop and explain what you need."
            : choices.Contains(answer) ? $"The user chose: {answer}" : $"The user answered: {answer}";
    }
}
