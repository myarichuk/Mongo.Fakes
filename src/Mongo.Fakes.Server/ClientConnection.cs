using System.Buffers;
using System.Net.Sockets;
using MongoDB.Bson;
using Mongo.Fakes.Server.Errors;
using Mongo.Fakes.Server.Wire;

namespace Mongo.Fakes.Server;

internal sealed class ClientConnection
{
    // A reply buffer that grew past this is dropped after use instead of being kept for the connection's lifetime.
    private const int MaxRetainedReplyBufferSize = 4 * 1024 * 1024;

    private readonly TcpClient _client;
    private readonly CommandRouter _router;
    private readonly ConnectionContext _context;
    private readonly byte[] _headerBuffer = new byte[WireMessageReader.HeaderSize];
    private MemoryStream _replyBuffer = new();
    private int _requestIdCounter;

    public ClientConnection(TcpClient client, CommandRouter router)
    {
        _client = client;
        _router = router;
        _context = new ConnectionContext(router.State.OpenConnection(), client.Client.RemoteEndPoint?.ToString() ?? "127.0.0.1:0");
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
                    var header = await WireMessageReader.ReadHeaderAsync(tcpStream, _headerBuffer, ct).ConfigureAwait(false);
                    if (header == null)
                        break;

                    int bodyLength = header.Value.MessageLength - WireMessageReader.HeaderSize;
                    byte[] body = ArrayPool<byte>.Shared.Rent(bodyLength);
                    try
                    {
                        await WireMessageReader.ReadBodyAsync(tcpStream, body, bodyLength, ct).ConfigureAwait(false);
                        _router.State.RecordRequest(header.Value.MessageLength);
                        await HandleMessageAsync(tcpStream, header.Value, body.AsMemory(0, bodyLength), ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(body);
                    }
                }
            }
        }
        catch (IOException)
        {
        }
        catch (OperationCanceledException)
        {
        }
        catch (CloseConnectionException)
        {
            // A failpoint asked for the connection to drop without a reply.
        }
        finally
        {
            _client.Close();
            _router.OnConnectionClosed(_context);
        }
    }

    private async Task HandleMessageAsync(NetworkStream tcpStream, MessageHeader header, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        bool isOpMsg = header.OpCode == OpCode.Msg;
        bool moreToCome = isOpMsg && OpMsgMessage.ReadMoreToCome(body.Span);
        BsonDocument? command = null;
        BsonDocument reply;

        try
        {
            string database;
            (command, database) = ParseCommand(header, body);
            reply = await _router.RouteCommandAsync(database, command, _context, ct).ConfigureAwait(false);

            // A real server puts ok last in a success reply.
            reply.Remove("ok");
            reply.Add("ok", 1.0);
        }
        catch (MongoCommandException ex)
        {
            reply = BuildErrorReply(ex.Message, ex.Code, ex.CodeName, ex.Details,
                ErrorLabels.For(ex.Code, command ?? new BsonDocument(), ex.ErrorLabels));
        }
        catch (Exception ex) when (ex is not IOException and not CloseConnectionException
            && !(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            reply = BuildErrorReply(ex.Message, ErrorCodes.UnknownError, "UnknownError", null, null);
        }

        if (moreToCome)
            return;

        if (isOpMsg)
            OpMsgMessage.WriteReply(_replyBuffer, reply, header.RequestId, _requestIdCounter++);
        else
            OpQueryMessage.WriteReply(_replyBuffer, reply, header.RequestId, _requestIdCounter++);

        await tcpStream.WriteAsync(_replyBuffer.GetBuffer().AsMemory(0, (int)_replyBuffer.Length), ct).ConfigureAwait(false);
        _router.State.RecordReply((int)_replyBuffer.Length);

        if (_replyBuffer.Capacity > MaxRetainedReplyBufferSize)
            _replyBuffer = new MemoryStream();
    }

    private static (BsonDocument Command, string Database) ParseCommand(MessageHeader header, ReadOnlyMemory<byte> body)
    {
        switch (header.OpCode)
        {
            case OpCode.Msg:
            {
                var msg = OpMsgMessage.Parse(body);
                if (msg.Body.ElementCount == 0)
                    throw new MongoCommandException(ErrorCodes.BadValue, "BadValue", "Empty command document.");

                string database = msg.Body.TryGetValue("$db", out var dbValue) && dbValue.IsString
                    ? dbValue.AsString
                    : "admin";
                return (msg.Body, database);
            }
            case OpCode.Query:
            {
                var msg = OpQueryMessage.Parse(body);
                return (msg.Query, msg.FullCollectionName.Split('.')[0]);
            }
            case OpCode.Compressed:
                throw new MongoCommandException(ErrorCodes.UnknownError, "UnknownError", "Compression not supported.");
            default:
                throw new MongoCommandException(ErrorCodes.UnknownError, "UnknownError", $"Unknown opcode: {header.OpCode}");
        }
    }

    // Unlike a success reply, a real server's error reply starts with ok.
    private static BsonDocument BuildErrorReply(string message, int code, string codeName, BsonDocument? details, BsonArray? errorLabels)
    {
        var reply = new BsonDocument
        {
            { "ok", 0.0 },
            { "errmsg", message },
            { "code", code },
            { "codeName", codeName }
        };

        if (details != null)
        {
            foreach (var element in details)
                reply[element.Name] = element.Value;
        }

        if (errorLabels != null)
            reply["errorLabels"] = errorLabels;

        return reply;
    }
}
