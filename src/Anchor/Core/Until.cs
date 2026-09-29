namespace Anchor.Core;

public enum CheckEnd { Passed, NoChanges, OutOfRounds }

/// <summary>
/// --until and /until: after each turn, run the user's check command and keep going until it exits 0. Done is decided
/// by the command, never by a model. A round that changes no files ends the loop, since the check would fail the
/// same way again; that includes the model stopping to ask the user something.
/// </summary>
public static class Until
{
    public const int MaxRounds = 5;
    const int MaxOutputChars = 8_000;

    /// <summary>Check is null when a turn itself didn't complete (cancelled, stopped, failed).</summary>
    public static async Task<(TurnEnd Turn, CheckEnd? Check)> RunAsync(Agent agent, Gate gate, string check, string input, CancellationToken ct)
    {
        for (var round = 1; ; round++)
        {
            var writes = gate.Writes;
            var end = await agent.RunTurnAsync(input, ct);
            if (end != TurnEnd.Completed)
                return (end, null);

            var (passed, output) = await gate.CheckAsync(check, round, ct);
            if (passed)
                return (end, CheckEnd.Passed);
            if (gate.Writes == writes)
                return (end, CheckEnd.NoChanges);
            if (round == MaxRounds)
                return (end, CheckEnd.OutOfRounds);
            input = FollowUp(check, output);
        }
    }

    public static string Describe(CheckEnd end, string check) => end switch
    {
        CheckEnd.Passed => $"check passed: {check}",
        CheckEnd.NoChanges => $"check still failing and the last round changed no files: {check}",
        _ => $"check still failing after {MaxRounds} rounds: {check}",
    };

    // The tail of a test run holds the failures and the summary.
    static string FollowUp(string check, string output) =>
        $"[anchor] The check `{check}` still fails. Keep working until it passes. anchor runs the check for you after " +
        $"each turn, so you don't need to run it yourself. If you can't make progress without the user, say so and stop.\n\n" +
        (output.Length > MaxOutputChars ? "[...]\n" + output[^MaxOutputChars..] : output);
}
