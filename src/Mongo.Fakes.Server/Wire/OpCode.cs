namespace Mongo.Fakes.Server.Wire;

internal enum OpCode
{
    Reply = 1,
    Query = 2004,
    Compressed = 2012,
    Msg = 2013
}
