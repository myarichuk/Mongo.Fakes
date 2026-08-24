namespace Mongo.Fakes.Server.Wire;

internal readonly record struct MessageHeader(int MessageLength, int RequestId, int ResponseTo, OpCode OpCode);
