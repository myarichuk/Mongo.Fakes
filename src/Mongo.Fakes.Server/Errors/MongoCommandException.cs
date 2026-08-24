namespace Mongo.Fakes.Server.Errors;

internal sealed class MongoCommandException : Exception
{
    public int Code { get; }
    public string CodeName { get; }

    public MongoCommandException(int code, string codeName, string message)
        : base(message)
    {
        Code = code;
        CodeName = codeName;
    }
}
