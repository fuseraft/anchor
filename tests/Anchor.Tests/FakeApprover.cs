using Anchor.Core;

namespace Anchor.Tests;

/// <summary>Gives the same answer to every request and records what was asked.</summary>
sealed class FakeApprover(Answer answer) : IApprover
{
    public List<ApprovalRequest> Requests { get; } = [];

    public Task<Answer> ApproveAsync(ApprovalRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(answer);
    }
}
