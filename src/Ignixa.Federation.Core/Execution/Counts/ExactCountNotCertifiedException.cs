namespace Ignixa.Federation.Core.Execution.Counts;

/// <summary>
/// The plan's count is not certified exact on every registered shard, so no exact total can be given; this maps to
/// <c>400</c> not-supported, as the planner's refusal does (design spec §7.2, §7.5, §9). It arises when the plan and the
/// executor's capabilities disagree, for example after the registry or a shard's certification changed: a shard does not
/// certify the count shape, or the executor's capabilities no longer certify a chain's hop as the plan did, namely the
/// required native or distributed operation on exactly the plan's shards and, for a reference rewrite, coverage of the
/// current registry version with the planned forms and count shapes.
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
