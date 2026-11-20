namespace Ignixa.Federation.Core.Execution.Counts;

/// <summary>
/// The exact federated total exceeds 2147483647, the largest R4 <c>Bundle.total</c> (an <c>unsignedInt</c>). The count
/// fails rather than report a truncated or partial total.
/// </summary>
public sealed class TotalOverflowException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="TotalOverflowException"/> class.</summary>
    public TotalOverflowException()
        : base("The exact total exceeds the largest Bundle.total, 2147483647.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TotalOverflowException"/> class.</summary>
    /// <param name="message">The message.</param>
    public TotalOverflowException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TotalOverflowException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public TotalOverflowException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
