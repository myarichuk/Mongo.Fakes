namespace Mongo.Fakes.Server.Errors;

internal static class ErrorCodes
{
    public const int BadValue = 2;
    public const int UnknownError = 8;
    public const int FailedToParse = 9;
    public const int Unauthorized = 13;
    public const int TypeMismatch = 14;
    public const int AuthenticationFailed = 18;
    public const int IllegalOperation = 20;
    public const int NamespaceNotFound = 26;
    public const int IndexNotFound = 27;
    public const int CursorNotFound = 43;
    public const int NamespaceExists = 48;
    public const int MaxTimeMSExpired = 50;
    public const int CommandNotFound = 59;
    public const int ImmutableField = 66;
    public const int InvalidOptions = 72;
    public const int InvalidNamespace = 73;
    public const int NoSuchTransaction = 251;
    public const int TransactionTooOld = 225;
    public const int UnrecognizedPipelineStage = 40324;
    public const int DuplicateKey = 11000;

    private static readonly Dictionary<int, string> Names = new()
    {
        [1] = "InternalError",
        [BadValue] = "BadValue",
        [6] = "HostUnreachable",
        [7] = "HostNotFound",
        [UnknownError] = "UnknownError",
        [FailedToParse] = "FailedToParse",
        [11] = "UserNotFound",
        [Unauthorized] = "Unauthorized",
        [TypeMismatch] = "TypeMismatch",
        [AuthenticationFailed] = "AuthenticationFailed",
        [IllegalOperation] = "IllegalOperation",
        [24] = "LockTimeout",
        [NamespaceNotFound] = "NamespaceNotFound",
        [IndexNotFound] = "IndexNotFound",
        [CursorNotFound] = "CursorNotFound",
        [NamespaceExists] = "NamespaceExists",
        [MaxTimeMSExpired] = "MaxTimeMSExpired",
        [CommandNotFound] = "CommandNotFound",
        [ImmutableField] = "ImmutableField",
        [InvalidOptions] = "InvalidOptions",
        [InvalidNamespace] = "InvalidNamespace",
        [89] = "NetworkTimeout",
        [91] = "ShutdownInProgress",
        [112] = "WriteConflict",
        [189] = "PrimarySteppedDown",
        [TransactionTooOld] = "TransactionTooOld",
        [NoSuchTransaction] = "NoSuchTransaction",
        [262] = "ExceededTimeLimit",
        [9001] = "SocketException",
        [10107] = "NotWritablePrimary",
        [DuplicateKey] = "DuplicateKey",
        [11600] = "InterruptedAtShutdown",
        [11601] = "Interrupted",
        [11602] = "InterruptedDueToReplStateChange",
        [13435] = "NotPrimaryNoSecondaryOk",
        [13436] = "NotPrimaryOrSecondary",
        [UnrecognizedPipelineStage] = "UnrecognizedPipelineStage"
    };

    /// <summary>The server's codeName for <paramref name="code"/>, or <c>Location{code}</c> like the server uses for unnamed codes.</summary>
    public static string NameOf(int code) => Names.TryGetValue(code, out var name) ? name : $"Location{code}";
}
