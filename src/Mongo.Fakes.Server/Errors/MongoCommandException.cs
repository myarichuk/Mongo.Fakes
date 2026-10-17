using MongoDB.Bson;

namespace Mongo.Fakes.Server.Errors;

internal sealed class MongoCommandException : Exception
{
    public int Code { get; }
    public string CodeName { get; }

    /// <summary>Extra reply fields, such as <c>keyPattern</c> and <c>keyValue</c> for duplicate keys.</summary>
    public BsonDocument? Details { get; }

    /// <summary>
    /// Labels to send exactly as given (a failpoint's <c>errorLabels</c>). When null, the labels
    /// are derived from the code and the command by <see cref="Errors.ErrorLabels"/>.
    /// </summary>
    public IReadOnlyList<string>? ErrorLabels { get; }

    public MongoCommandException(int code, string codeName, string message, BsonDocument? details = null, IReadOnlyList<string>? errorLabels = null)
        : base(message)
    {
        Code = code;
        CodeName = codeName;
        Details = details;
        ErrorLabels = errorLabels;
    }
}
