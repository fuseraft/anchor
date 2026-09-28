using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Anchor.Core;

public sealed record Limits(int WarnIdentical = 3, int MaxIdentical = 5, int WarnFailures = 2, int MaxFailures = 3);

public enum GuardAction { None, Warn, Stop }

public readonly record struct GuardVerdict(GuardAction Action, string Message = "")
{
    public static readonly GuardVerdict None = new(GuardAction.None);
}

/// <summary>Stops a turn that repeats the same call or keeps failing; replaces a flat round cap.</summary>
public sealed class LoopGuard(Limits limits)
{
    string? _lastSignature;
    int _identical;
    int _failures;

    public GuardVerdict BeforeCall(FunctionCallContent call)
    {
        var signature = Signature(call);
        _identical = signature == _lastSignature ? _identical + 1 : 1;
        _lastSignature = signature;

        if (_identical >= limits.MaxIdentical)
            return new(GuardAction.Stop, $"'{call.Name}' was called {_identical} times in a row with identical arguments.");
        if (_identical == limits.WarnIdentical)
            return new(GuardAction.Warn, $"You have called '{call.Name}' {_identical} times in a row with identical arguments. The result will not change; use it or try a different approach.");
        return GuardVerdict.None;
    }

    public GuardVerdict AfterCall(bool ok)
    {
        _failures = ok ? 0 : _failures + 1;

        if (_failures >= limits.MaxFailures)
            return new(GuardAction.Stop, $"{_failures} tool calls failed in a row.");
        if (_failures == limits.WarnFailures)
            return new(GuardAction.Warn, $"{_failures} tool calls have failed in a row. Read the errors and change approach before trying again.");
        return GuardVerdict.None;
    }

    static string Signature(FunctionCallContent call)
    {
        var args = new SortedDictionary<string, object?>(call.Arguments ?? new Dictionary<string, object?>(), StringComparer.Ordinal);
        return call.Name + JsonSerializer.Serialize(args);
    }
}
