using MongoDB.Bson;
using Mongo.Fakes.Server.Errors;

namespace Mongo.Fakes.Server;

/// <summary>
/// Logical sessions and their transaction state. Drivers start a transaction by sending
/// <c>startTransaction: true</c> with <c>autocommit: false</c> and a <c>txnNumber</c> on its first
/// operation, then end it with <c>commitTransaction</c> or <c>abortTransaction</c>. Writes are
/// applied as they happen: there is no isolation, and an abort does not roll them back.
/// </summary>
internal sealed class SessionManager
{
    private const int TransactionCommitted = 256;

    private long _sessionIdCounter;
    private readonly Dictionary<BsonBinaryData, SessionState> _sessions = new();
    private readonly object _lock = new();

    private enum TransactionState
    {
        None,
        InProgress,
        Committed,
        Aborted
    }

    private sealed class SessionState
    {
        public long TxnNumber { get; set; } = -1;
        public TransactionState State { get; set; }
    }

    public BsonDocument StartSession()
    {
        var sessionId = new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard);
        Interlocked.Increment(ref _sessionIdCounter);

        lock (_lock)
            _sessions[sessionId] = new SessionState();

        return new BsonDocument
        {
            { "id", new BsonDocument { { "id", sessionId } } },
            { "timeoutMinutes", 30 },
            { "ok", 1.0 }
        };
    }

    public void EndSession(BsonBinaryData sessionId)
    {
        lock (_lock)
            _sessions.Remove(sessionId);
    }

    public bool IsInTransaction(BsonDocument command) =>
        command.TryGetValue("autocommit", out var autocommit) && !autocommit.ToBoolean();

    /// <summary>
    /// Checks and advances the transaction state for a command that carries transaction fields.
    /// Throws what a real server would: NoSuchTransaction for an operation or commit outside a
    /// running transaction, TransactionTooOld for a stale <c>txnNumber</c>.
    /// </summary>
    public void Apply(string commandName, BsonDocument command)
    {
        if (!command.TryGetValue("autocommit", out var autocommit))
            return;

        if (autocommit.ToBoolean())
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "autocommit must be false");

        var sessionId = GetSessionId(command)
            ?? throw new MongoCommandException(ErrorCodes.InvalidOptions, "InvalidOptions", "Transaction requires a session (lsid)");

        if (!command.TryGetValue("txnNumber", out var txnValue) || !txnValue.IsNumeric)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "'autocommit' requires 'txnNumber'");
        long txnNumber = txnValue.ToInt64();

        bool start = command.TryGetValue("startTransaction", out var startValue) && startValue.ToBoolean();

        lock (_lock)
        {
            if (!_sessions.TryGetValue(sessionId, out var session))
                _sessions[sessionId] = session = new SessionState();

            if (txnNumber < session.TxnNumber)
                throw new MongoCommandException(ErrorCodes.TransactionTooOld, "TransactionTooOld",
                    $"Cannot start transaction {txnNumber} on session because a newer transaction {session.TxnNumber} has already started");

            if (start)
            {
                if (txnNumber == session.TxnNumber && session.State != TransactionState.None)
                    throw new MongoCommandException(ErrorCodes.BadValue, "ConflictingOperationInProgress",
                        $"Transaction {txnNumber} has already been started");

                session.TxnNumber = txnNumber;
                session.State = TransactionState.InProgress;
                return;
            }

            bool sameTransaction = txnNumber == session.TxnNumber;
            switch (commandName)
            {
                case "committransaction":
                    // A retried commit of a committed transaction succeeds again.
                    if (sameTransaction && session.State is TransactionState.InProgress or TransactionState.Committed)
                    {
                        session.State = TransactionState.Committed;
                        return;
                    }
                    throw NoSuchTransaction(txnNumber, session.State == TransactionState.Aborted && sameTransaction);

                case "aborttransaction":
                    if (sameTransaction && session.State == TransactionState.InProgress)
                    {
                        session.State = TransactionState.Aborted;
                        return;
                    }
                    if (sameTransaction && session.State == TransactionState.Committed)
                        throw new MongoCommandException(TransactionCommitted, "TransactionCommitted",
                            $"Transaction {txnNumber} has been committed.");
                    throw NoSuchTransaction(txnNumber, aborted: sameTransaction && session.State == TransactionState.Aborted);

                default:
                    if (sameTransaction && session.State == TransactionState.InProgress)
                        return;
                    throw NoSuchTransaction(txnNumber, aborted: sameTransaction && session.State == TransactionState.Aborted);
            }
        }
    }

    private static BsonBinaryData? GetSessionId(BsonDocument command)
    {
        if (command.TryGetValue("lsid", out var lsid) && lsid is BsonDocument lsidDoc
            && lsidDoc.TryGetValue("id", out var id) && id.IsBsonBinaryData)
            return id.AsBsonBinaryData;
        return null;
    }

    private static MongoCommandException NoSuchTransaction(long txnNumber, bool aborted) =>
        new(ErrorCodes.NoSuchTransaction, "NoSuchTransaction",
            aborted ? $"Transaction {txnNumber} has been aborted." : $"Transaction with txnNumber {txnNumber} is not in progress.");
}
