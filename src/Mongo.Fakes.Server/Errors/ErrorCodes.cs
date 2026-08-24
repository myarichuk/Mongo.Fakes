namespace Mongo.Fakes.Server.Errors;

internal static class ErrorCodes
{
    public const int BadValue = 2;
    public const int UnknownError = 8;
    public const int FailedToParse = 9;
    public const int CursorNotFound = 43;
    public const int CommandNotFound = 59;
    public const int UnrecognizedPipelineStage = 40324;
    public const int DuplicateKey = 11000;
}
