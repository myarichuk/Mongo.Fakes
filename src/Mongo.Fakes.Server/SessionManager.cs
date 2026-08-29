using MongoDB.Bson;

namespace Mongo.Fakes.Server;

internal sealed class SessionManager
{
    private long _sessionIdCounter;
    private readonly Dictionary<BsonBinaryData, SessionState> _sessions = new();

    private class SessionState
    {
        public required long SessionId { get; init; }
        public int? TransactionNumber { get; set; }
        public bool InTransaction { get; set; }
    }

    public BsonDocument StartSession()
    {
        var sessionId = Interlocked.Increment(ref _sessionIdCounter);
        var binarySessionId = new BsonBinaryData(BitConverter.GetBytes(sessionId), BsonBinarySubType.UuidStandard);

        _sessions[binarySessionId] = new SessionState { SessionId = sessionId, TransactionNumber = 0 };

        return new BsonDocument
        {
            { "ok", 1.0 },
            { "sessionId", new BsonDocument { { "id", binarySessionId } } }
        };
    }

    public BsonDocument BeginTransaction(BsonBinaryData sessionId, int? txnNumber)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.InTransaction = true;
            if (txnNumber.HasValue)
                session.TransactionNumber = txnNumber.Value;
        }

        return new BsonDocument { { "ok", 1.0 } };
    }

    public BsonDocument CommitTransaction(BsonBinaryData sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.InTransaction = false;
        }

        return new BsonDocument { { "ok", 1.0 } };
    }

    public BsonDocument AbortTransaction(BsonBinaryData sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.InTransaction = false;
        }

        return new BsonDocument { { "ok", 1.0 } };
    }

    public void EndSession(BsonBinaryData sessionId)
    {
        _sessions.Remove(sessionId);
    }
}
