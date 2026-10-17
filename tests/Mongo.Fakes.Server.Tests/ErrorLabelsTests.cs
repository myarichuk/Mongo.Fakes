using MongoDB.Bson;
using Mongo.Fakes.Server.Errors;

namespace Mongo.Fakes.Server.Tests;

public class ErrorLabelsTests
{
    private const int NotWritablePrimary = 10107;
    private const int WriteConflict = 112;

    private static BsonDocument Insert(bool retryable = true, bool inTransaction = false)
    {
        var command = new BsonDocument { { "insert", "c" }, { "documents", new BsonArray() } };
        if (retryable || inTransaction)
            command["txnNumber"] = 1L;
        if (inTransaction)
            command["autocommit"] = false;
        return command;
    }

    [Fact]
    public void RetryableCode_OnRetryableWrite_GetsRetryableWriteError()
    {
        var labels = ErrorLabels.For(NotWritablePrimary, Insert(), null);

        Assert.Equal(new BsonArray { ErrorLabels.RetryableWriteError }, labels);
    }

    [Fact]
    public void RetryableCode_WithoutTxnNumber_GetsNoLabel()
    {
        Assert.Null(ErrorLabels.For(NotWritablePrimary, Insert(retryable: false), null));
    }

    [Fact]
    public void RetryableCode_OnRead_GetsNoLabel()
    {
        Assert.Null(ErrorLabels.For(NotWritablePrimary, new BsonDocument { { "find", "c" }, { "txnNumber", 1L } }, null));
    }

    [Fact]
    public void NonRetryableCode_GetsNoLabel()
    {
        Assert.Null(ErrorLabels.For(ErrorCodes.DuplicateKey, Insert(), null));
    }

    [Fact]
    public void ErrorInsideTransaction_GetsTransientTransactionError()
    {
        var labels = ErrorLabels.For(WriteConflict, Insert(inTransaction: true), null);

        Assert.Equal(new BsonArray { ErrorLabels.TransientTransactionError }, labels);
    }

    [Fact]
    public void RetryableCode_OnCommitTransaction_GetsRetryableWriteError()
    {
        var commit = new BsonDocument { { "commitTransaction", 1 }, { "txnNumber", 1L }, { "autocommit", false } };

        Assert.Equal(new BsonArray { ErrorLabels.RetryableWriteError }, ErrorLabels.For(NotWritablePrimary, commit, null));
    }

    [Fact]
    public void ExplicitLabels_ReplaceDerivedOnes()
    {
        Assert.Equal(new BsonArray { "Custom" }, ErrorLabels.For(NotWritablePrimary, Insert(), ["Custom", "Custom"]));
        Assert.Null(ErrorLabels.For(NotWritablePrimary, Insert(), []));
    }
}
