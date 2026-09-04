namespace Mongo.Fakes.Server.Errors;

internal static class ErrorCodes
{
    public const int BadValue = 2;
    public const int UnknownError = 8;
    public const int FailedToParse = 9;
    public const int Unauthorized = 13;
    public const int AuthenticationFailed = 18;
    public const int IndexNotFound = 27;
    public const int InvalidOptions = 72;
    public const int CursorNotFound = 43;
    public const int CommandNotFound = 59;
    public const int UnrecognizedPipelineStage = 40324;
    public const int DuplicateKey = 11000;
}
