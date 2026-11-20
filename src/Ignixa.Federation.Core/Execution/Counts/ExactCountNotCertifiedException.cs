namespace Ignixa.Federation.Core.Execution.Counts;

/// <summary>
/// The plan's count is not certified exact on every registered shard, so no exact total can be given; this maps to
/// <c>400</c> not-supported, as the planner's refusal does (design spec §7.2, §7.5, §9). It arises when the plan and the
/// executor's capabilities disagree, for example after the registry changed: a shard does not certify the count shape,
/// or a chain's hop was certified for other shards or, for a reference rewrite, another registry version.
/// </summary>
/// <remarks>The message never holds query text or the count shape.</remarks>
public sealed class ExactCountNotCertifiedException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ExactCountNotCertifiedException"/> class.</summary>
    public ExactCountNotCertifiedException()
        : base("The search's exact count is not certified on every shard.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ExactCountNotCertifiedException"/> class.</summary>
    /// <param name="message">The message.</param>
    public ExactCountNotCertifiedException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ExactCountNotCertifiedException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public ExactCountNotCertifiedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
