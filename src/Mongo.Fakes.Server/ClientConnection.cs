using System.Net.Sockets;
using MongoDB.Bson;
using Mongo.Fakes.Server.Errors;
using Mongo.Fakes.Server.Wire;

namespace Mongo.Fakes.Server;

internal static class TcpClientExtensions
{
    public static void SetNoDelay(this TcpClient client)
    {
        client.NoDelay = true;
    }
}


internal sealed class ClientConnection
{
    private readonly TcpClient _client;
    private readonly CommandRouter _router;
    private int _requestIdCounter;

    public ClientConnection(TcpClient client, CommandRouter router)
    {
        _client = client;
        _router = router;
    }

    public async Task ProcessAsync(CancellationToken ct)
    {
        try
        {
            _client.NoDelay = true;
            var tcpStream = _client.GetStream();

            using (tcpStream)
            {
                while (!ct.IsCancellationRequested)
                {
                    var header = await WireMessageReader.ReadHeaderAsync(tcpStream, ct).ConfigureAwait(false);
                    if (header == null)
                        break;

                    int bodyLength = header.Value.MessageLength - 16;
                    byte[] body = await WireMessageReader.ReadBodyAsync(tcpStream, bodyLength, ct).ConfigureAwait(false);

                byte[] reply;
                try
                {
                    reply = await HandleMessageAsync(header.Value, body, ct).ConfigureAwait(false);
                }
                catch (MongoCommandException ex)
                {
                    var errorDoc = new BsonDocument
                    {
                        { "ok", 0.0 },
                        { "errmsg", ex.Message },
                        { "code", ex.Code },
                        { "codeName", ex.CodeName }
                    };
                    reply = OpMsgMessage.BuildReply(errorDoc, header.Value.RequestId, _requestIdCounter++);
                }
                catch (Exception ex)
                {
                    var errorDoc = new BsonDocument
                    {
                        { "ok", 0.0 },
                        { "errmsg", ex.Message },
                        { "code", ErrorCodes.UnknownError },
                        { "codeName", "UnknownError" }
                    };
                    reply = OpMsgMessage.BuildReply(errorDoc, header.Value.RequestId, _requestIdCounter++);
                }

                    if (reply.Length > 0)
                        await tcpStream.WriteAsync(reply, 0, reply.Length, ct).ConfigureAwait(false);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _client.Close();
        }
    }

    private async Task<byte[]> HandleMessageAsync(MessageHeader header, byte[] body, CancellationToken ct)
    {
        return header.OpCode switch
        {
            OpCode.Msg => await HandleOpMsgAsync(header, body, ct).ConfigureAwait(false),
            OpCode.Query => HandleOpQuery(header, body),
            OpCode.Compressed => throw new MongoCommandException(ErrorCodes.UnknownError, "UnknownError", "Compression not supported."),
            _ => throw new MongoCommandException(ErrorCodes.UnknownError, "UnknownError", $"Unknown opcode: {header.OpCode}")
        };
    }

    private async Task<byte[]> HandleOpMsgAsync(MessageHeader header, byte[] body, CancellationToken ct)
    {
        var msg = OpMsgMessage.Parse(body);

        if (msg.Body.ElementCount == 0)
            throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Empty command document.");

        string commandName = msg.Body.GetElement(0).Name.ToLowerInvariant();
        string database = msg.Body.TryGetValue("$db", out var dbValue) && dbValue.IsString
            ? dbValue.AsString
            : "admin";

        var result = await _router.RouteCommandAsync(database, msg.Body, ct).ConfigureAwait(false);
        result["ok"] = 1.0;

        if (msg.MoreToCome)
            return Array.Empty<byte>();

        return OpMsgMessage.BuildReply(result, header.RequestId, _requestIdCounter++);
    }

    private byte[] HandleOpQuery(MessageHeader header, byte[] body)
    {
        var msg = OpQueryMessage.Parse(body);

        string database = msg.FullCollectionName.Split('.')[0];
        var result = _router.RouteCommandAsync(database, msg.Query, CancellationToken.None).Result;
        result["ok"] = 1.0;

        return OpQueryMessage.BuildReply(result, header.RequestId, _requestIdCounter++);
    }
}
