using MongoDB.Bson;

namespace Mongo.Fakes.Server.Errors;

/// <summary>
/// Derives the error labels a real server attaches to an error reply. From wire version 9 (4.4) on,
/// drivers retry writes only when the server sends <c>RetryableWriteError</c>, so without these labels
/// the driver's retry logic can't be exercised.
/// </summary>
internal static class ErrorLabels
{
    public const string RetryableWriteError = "RetryableWriteError";
    public const string TransientTransactionError = "TransientTransactionError";

    // The server's retryable-error list (see the retryable writes spec).
    private static readonly HashSet<int> RetryableCodes =
    [
        6,     // HostUnreachable
        7,     // HostNotFound
        89,    // NetworkTimeout
        91,    // ShutdownInProgress
        189,   // PrimarySteppedDown
        262,   // ExceededTimeLimit
        9001,  // SocketException
        10107, // NotWritablePrimary
        11600, // InterruptedAtShutdown
        11602, // InterruptedDueToReplStateChange
        13435, // NotPrimaryNoSecondaryOk
        13436  // NotPrimaryOrSecondary
    ];

    private static readonly HashSet<int> TransientTransactionCodes =
    [
        24,  // LockTimeout
        112, // WriteConflict
        251  // NoSuchTransaction
    ];

    private static readonly HashSet<string> RetryableWriteCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "insert", "update", "delete", "findAndModify", "commitTransaction", "abortTransaction"
    };

    /// <param name="explicitLabels">Labels to use instead of deriving them, as a failpoint's <c>errorLabels</c> does.</param>
    public static BsonArray? For(int code, BsonDocument command, IReadOnlyList<string>? explicitLabels)
    {
        if (explicitLabels != null)
            return explicitLabels.Count == 0 ? null : new BsonArray(explicitLabels.Distinct());

        var labels = new List<string>();
        string commandName = command.ElementCount > 0 ? command.GetElement(0).Name : string.Empty;
        bool inTransaction = command.TryGetValue("autocommit", out var autocommit) && !autocommit.ToBoolean();
        bool isTransactionEnd = commandName.Equals("commitTransaction", StringComparison.OrdinalIgnoreCase)
            || commandName.Equals("abortTransaction", StringComparison.OrdinalIgnoreCase);

        if (RetryableCodes.Contains(code)
            && command.Contains("txnNumber")
            && RetryableWriteCommands.Contains(commandName)
            && (!inTransaction || isTransactionEnd))
        {
            labels.Add(RetryableWriteError);
        }

        if (inTransaction && !isTransactionEnd && (TransientTransactionCodes.Contains(code) || RetryableCodes.Contains(code)))
            labels.Add(TransientTransactionError);

        return labels.Count == 0 ? null : new BsonArray(labels);
    }
}
