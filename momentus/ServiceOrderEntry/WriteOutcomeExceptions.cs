namespace ServiceOrderEntry;

internal sealed class UnknownWriteOutcomeException(string operation, string target, Exception cause)
    : Exception($"UNKNOWN WRITE OUTCOME: {operation} ({target}). Momentus may have accepted this mutation; it was dispatched once and must be reconciled before resubmission. {cause.Message}", cause)
{
    public string Operation { get; } = operation;
    public string Target { get; } = target;
}

internal sealed class WriteRejectedException(string operation, string target, Exception cause)
    : Exception($"WRITE REJECTED: {operation} ({target}). {cause.Message}", cause);
