using Anchor.Core;

namespace Anchor.Tests;

/// <summary>Gives the same answer to every request and records what was asked. <paramref name="meanwhile"/> runs while a
/// request waits, as something the user does before answering.</summary>
sealed class FakeApprover(Answer answer, Action<ApprovalRequest>? meanwhile = null) : IApprover
{
    public List<ApprovalRequest> Requests { get; } = [];

    public Task<Answer> ApproveAsync(ApprovalRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        meanwhile?.Invoke(request);
        return Task.FromResult(answer);
    }
}
