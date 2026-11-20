namespace Ignixa.Federation.Core.Execution;

/// <summary>The page needs another shard call, but the request's live call budget is spent (design spec §7.8).</summary>
public sealed class FetchBudgetExhaustedException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="FetchBudgetExhaustedException"/> class.</summary>
    /// <param name="budget">The number of shard calls the request was allowed.</param>
    public FetchBudgetExhaustedException(int budget)
        : base($"The page needs more than the {budget} shard calls its budget allows.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="FetchBudgetExhaustedException"/> class.</summary>
    public FetchBudgetExhaustedException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="FetchBudgetExhaustedException"/> class.</summary>
    /// <param name="message">The message.</param>
    public FetchBudgetExhaustedException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="FetchBudgetExhaustedException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public FetchBudgetExhaustedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
