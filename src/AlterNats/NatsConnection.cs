﻿using AlterNats.Commands;
using AlterNats.Internal;
using Microsoft.Extensions.Logging;
using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace AlterNats;

public enum NatsConnectionState
{
    Closed,
    Open,
    Connecting,
    Reconnecting,
}

// This writer state is reused when reconnecting.
internal sealed class WriterState
{
    public FixedArrayBufferWriter BufferWriter { get; }
    public Channel<ICommand> CommandBuffer { get; }
    public NatsOptions Options { get; }
    public List<ICommand> PriorityCommands { get; }

    public WriterState(NatsOptions options)
    {
        Options = options;
        BufferWriter = new FixedArrayBufferWriter();
        CommandBuffer = Channel.CreateUnbounded<ICommand>(new UnboundedChannelOptions
        {
            AllowSynchronousContinuations = false, // always should be in async loop.
            SingleWriter = false,
            SingleReader = true,
        });
        PriorityCommands = new List<ICommand>();
    }
}

public class NatsConnection : IAsyncDisposable
{
    readonly object gate = new object();
    readonly WriterState writerState;
    readonly ChannelWriter<ICommand> commandWriter;
    readonly SubscriptionManager subscriptionManager;
    readonly RequestResponseManager requestResponseManager;
    readonly ILogger<NatsConnection> logger;
    readonly ObjectPool pool;
    internal readonly ReadOnlyMemory<byte> indBoxPrefix;

    int pongCount;
    bool isDisposed;

    // when reconnect, make new instance.
    TcpConnection? socket;
    CancellationTokenSource? pingTimerCancellationTokenSource;
    NatsUri? currentConnectUri;
    NatsReadProtocolProcessor? socketReader;
    NatsPipeliningWriteProtocolProcessor? socketWriter;
    TaskCompletionSource waitForOpenConnection;

    public NatsOptions Options { get; }
    public NatsConnectionState ConnectionState { get; private set; }
    public ServerInfo? ServerInfo { get; internal set; } // server info is set when received INFO

    public NatsConnection()
        : this(NatsOptions.Default)
    {
    }

    public NatsConnection(NatsOptions options)
    {
        this.Options = options;
        this.ConnectionState = NatsConnectionState.Closed;
        this.waitForOpenConnection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        this.pool = new ObjectPool(options.CommandPoolSize);
        this.writerState = new WriterState(options);
        this.commandWriter = writerState.CommandBuffer.Writer;
        this.subscriptionManager = new SubscriptionManager(this);
        this.requestResponseManager = new RequestResponseManager(this, pool);
        this.indBoxPrefix = Encoding.ASCII.GetBytes($"{options.InboxPrefix}{Guid.NewGuid()}.");
        this.logger = options.LoggerFactory.CreateLogger<NatsConnection>();
    }

    /// <summary>
    /// Connect socket and write CONNECT command to nats server.
    /// </summary>
    public async ValueTask ConnectAsync()
    {
        if (this.ConnectionState == NatsConnectionState.Open) return;

        TaskCompletionSource? waiter = null;
        lock (gate)
        {
            ThrowIfDisposed();
            if (ConnectionState != NatsConnectionState.Closed)
            {
                waiter = waitForOpenConnection;
            }
            else
            {
                // when closed, change state to connecting and only first connection try-to-connect.
                ConnectionState = NatsConnectionState.Connecting;
            }
        }

        if (waiter != null)
        {
            await waiter.Task.ConfigureAwait(false);
            return;
        }
        else
        {
            // Only Closed(initial) state, can run initial connect.
            await InitialConnectAsync().ConfigureAwait(false);
        }
    }

    async ValueTask InitialConnectAsync()
    {
        Debug.Assert(ConnectionState == NatsConnectionState.Connecting);

        var uris = Options.GetSeedUris();
        foreach (var uri in uris)
        {
            try
            {
                logger.LogInformation("Try to connect NATS {0}:{1}", uri.Host, uri.Port);
                var conn = new TcpConnection();
                await conn.ConnectAsync(uri.Host, uri.Port, Options.ConnectTimeout).ConfigureAwait(false);
                this.socket = conn;
                this.currentConnectUri = uri;
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Fail to connect NATS {0}:{1}.", uri.Host, uri.Port);
            }
        }
        if (this.socket == null)
        {
            var exception = new NatsException("can not connect uris: " + String.Join(",", uris.Select(x => x.ToString())));
            lock (gate)
            {
                this.ConnectionState = NatsConnectionState.Closed; // allow retry connect
                waitForOpenConnection.TrySetException(exception); // throw for waiter
                this.waitForOpenConnection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            throw exception;
        }

        // Connected completely but still ConnnectionState is Connecting(require after receive INFO).

        // add CONNECT and PING command to priority lane
        var connectCommand = AsyncConnectCommand.Create(pool, Options.ConnectOptions);
        writerState.PriorityCommands.Add(connectCommand);
        writerState.PriorityCommands.Add(PingCommand.Create(pool));

        // Run Reader/Writer LOOP start
        var waitForInfoSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitForPongOrErrorSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        this.socketWriter = new NatsPipeliningWriteProtocolProcessor(socket, writerState, pool);
        this.socketReader = new NatsReadProtocolProcessor(socket, this, waitForInfoSignal, waitForPongOrErrorSignal);

        try
        {
            // before send connect, wait INFO.
            await waitForInfoSignal.Task.ConfigureAwait(false);
            // send COMMAND and PING
            await connectCommand.AsValueTask().ConfigureAwait(false);
            // receive COMMAND response(PONG or ERROR)
            await waitForPongOrErrorSignal.Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // can not start reader/writer
            var uri = currentConnectUri;

            await socketWriter!.DisposeAsync();
            await socketReader!.DisposeAsync();
            await socket!.DisposeAsync();
            socket = null;
            socketWriter = null;
            socketReader = null;
            currentConnectUri = null;

            var exception = new NatsException("can not start to connect nats server: " + uri, ex);
            lock (gate)
            {
                this.ConnectionState = NatsConnectionState.Closed; // allow retry connect
                waitForOpenConnection.TrySetException(exception); // throw for waiter
                this.waitForOpenConnection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            throw exception;
        }

        lock (gate)
        {
            var url = currentConnectUri;
            logger.LogInformation("Connect succeed, NATS {0}:{1}", url?.Host, url?.Port);
            this.ConnectionState = NatsConnectionState.Open;
            this.pingTimerCancellationTokenSource = new CancellationTokenSource();
            StartPingTimerAsync(pingTimerCancellationTokenSource.Token);
            this.waitForOpenConnection.TrySetResult();
            Task.Run(ReconnectLoopAsync);
        }
    }

    async void ReconnectLoopAsync()
    {
        try
        {
            // If dispose this client, WaitForClosed throws OpeationCanceledException so stop reconnect-loop correctly.
            await socket!.WaitForClosed.ConfigureAwait(false);

            logger.LogTrace("Detect connection closed, start to cleanup current connection and start to reconnect.");

            lock (gate)
            {
                this.ConnectionState = NatsConnectionState.Reconnecting;
                this.waitForOpenConnection.TrySetCanceled();
                this.waitForOpenConnection = new TaskCompletionSource();
                this.pingTimerCancellationTokenSource?.Cancel();
                this.requestResponseManager.Reset();
            }

            // Cleanup current reader/writer
            {
                // reader is not share state, can dispose asynchronously.
                var reader = socketReader!;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await reader.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error occured when disposing socket reader loop.");
                    }
                });

                // writer's internal buffer/channel is not thread-safe, must wait complete.
                await socketWriter!.DisposeAsync();
            }

            // Dispose current and create new
            await socket.DisposeAsync();

            NatsUri[] urls = Array.Empty<NatsUri>();
            if (Options.NoRandomize)
            {
                urls = this.ServerInfo?.ClientConnectUrls?.Select(x => new NatsUri(x)).Distinct().ToArray() ?? Array.Empty<NatsUri>();
                if (urls.Length == 0)
                {
                    urls = Options.GetSeedUris();
                }
            }
            else
            {
                urls = this.ServerInfo?.ClientConnectUrls?.Select(x => new NatsUri(x)).OrderBy(_ => Guid.NewGuid()).Distinct().ToArray() ?? Array.Empty<NatsUri>();
                if (urls.Length == 0)
                {
                    urls = Options.GetSeedUris();
                }
            }

            if (this.currentConnectUri != null)
            {
                // add last.
                urls = urls.Where(x => x != currentConnectUri).Append(currentConnectUri).ToArray();
            }

            currentConnectUri = null;
            var urlEnumerator = urls.AsEnumerable().GetEnumerator();
            NatsUri? url = null;
        CONNECT_AGAIN:
            try
            {
                if (urlEnumerator.MoveNext())
                {
                    url = urlEnumerator.Current;
                    logger.LogInformation("Try to connect NATS {0}:{1}", url.Host, url.Port);
                    var conn = new TcpConnection();
                    await conn.ConnectAsync(url.Host, url.Port, Options.ConnectTimeout).ConfigureAwait(false);
                    this.socket = conn;
                    this.currentConnectUri = url;
                }
                else
                {
                    urlEnumerator = urls.AsEnumerable().GetEnumerator();
                    goto CONNECT_AGAIN;
                }

                // add CONNECT and PING command to priority lane
                var connectCommand = AsyncConnectCommand.Create(pool, Options.ConnectOptions);
                writerState.PriorityCommands.Add(connectCommand);
                writerState.PriorityCommands.Add(PingCommand.Create(pool));

                // Add SUBSCRIBE command to priority lane
                var subscribeCommand = AsyncSubscribeBatchCommand.Create(pool, subscriptionManager.GetExistingSubscriptions());
                writerState.PriorityCommands.Add(subscribeCommand);

                // Run Reader/Writer LOOP start
                var waitForInfoSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var waitForPongOrErrorSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                this.socketWriter = new NatsPipeliningWriteProtocolProcessor(socket, writerState, pool);
                this.socketReader = new NatsReadProtocolProcessor(socket, this, waitForInfoSignal, waitForPongOrErrorSignal);

                await waitForInfoSignal.Task.ConfigureAwait(false);
                await connectCommand.AsValueTask().ConfigureAwait(false);
                await waitForPongOrErrorSignal.Task.ConfigureAwait(false);
                await subscribeCommand.AsValueTask().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (url != null)
                {
                    logger.LogError(ex, "Fail to connect NATS {0}:{1}.", url.Host, url.Port);
                }

                if (socketWriter != null)
                {
                    await socketWriter.DisposeAsync().ConfigureAwait(false);
                }
                if (socketReader != null)
                {
                    await socketReader.DisposeAsync().ConfigureAwait(false);
                }
                if (socket != null)
                {
                    await socket.DisposeAsync().ConfigureAwait(false);
                }
                socket = null;
                socketWriter = null;
                socketReader = null;

                await WaitWithJitterAsync().ConfigureAwait(false);
                goto CONNECT_AGAIN;
            }

            lock (gate)
            {
                logger.LogInformation("Connect succeed, NATS {0}:{1}", url.Host, url.Port);
                this.ConnectionState = NatsConnectionState.Open;
                this.pingTimerCancellationTokenSource = new CancellationTokenSource();
                StartPingTimerAsync(pingTimerCancellationTokenSource.Token);
                this.waitForOpenConnection.TrySetResult();
                Task.Run(ReconnectLoopAsync);
            }
        }
        catch
        {
        }
    }

    async Task WaitWithJitterAsync()
    {
        var jitter = Random.Shared.NextDouble() * Options.ReconnectJitter.TotalMilliseconds;
        var waitTime = Options.ReconnectWait + TimeSpan.FromMilliseconds(jitter);
        logger.LogTrace("Wait {0}ms to reconnect.", waitTime.TotalMilliseconds);
        await Task.Delay(waitTime).ConfigureAwait(false);
    }

    async void StartPingTimerAsync(CancellationToken cancellationToken)
    {
        if (Options.PingInterval == TimeSpan.Zero) return;

        var periodicTimer = new PeriodicTimer(Options.PingInterval);
        ResetPongCount();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (Interlocked.Increment(ref pongCount) > Options.MaxPingOut)
                {
                    logger.LogInformation("Detect MaxPingOut, try to connection abort.");
                    if (socket != null)
                    {
                        await socket.AbortConnectionAsync(cancellationToken).ConfigureAwait(false);
                        return;
                    }
                }

                PostPing();
                await periodicTimer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch { }
    }

    internal void EnqueuePing(AsyncPingCommand pingCommand)
    {
        // Enqueue Ping Command to current working reader.
        var reader = socketReader;
        if (reader != null)
        {
            if (reader.TryEnqueuePing(pingCommand))
            {
                return;
            }
        }
        // Can not add PING, set fail.
        pingCommand.SetCanceled(CancellationToken.None);
    }

    // Public APIs
    // ***Async or Post***Async(fire-and-forget)

    public void PostPing()
    {
        if (ConnectionState == NatsConnectionState.Open)
        {
            commandWriter.TryWrite(PingCommand.Create(pool));
        }
        else
        {
            WithConnect(static self => self.commandWriter.TryWrite(PingCommand.Create(self.pool)));
        }
    }

    /// <summary>
    /// Send PING command and await PONG. Return value is similar as Round trip time.
    /// </summary>
    public ValueTask<TimeSpan> PingAsync()
    {
        if (ConnectionState == NatsConnectionState.Open)
        {
            var command = AsyncPingCommand.Create(this, pool);
            commandWriter.TryWrite(command);
            return command.AsValueTask();
        }
        else
        {
            return WithConnectAsync(static self =>
            {
                var command = AsyncPingCommand.Create(self, self.pool);
                self.commandWriter.TryWrite(command);
                return command.AsValueTask();
            });
        }
    }

    public ValueTask PublishAsync<T>(in NatsKey key, T value)
    {
        if (ConnectionState == NatsConnectionState.Open)
        {
            var command = AsyncPublishCommand<T>.Create(pool, key, value, Options.Serializer);
            commandWriter.TryWrite(command);
            return command.AsValueTask();
        }
        else
        {
            return WithConnectAsync(key, value, static (self, k, v) =>
            {
                var command = AsyncPublishCommand<T>.Create(self.pool, k, v, self.Options.Serializer);
                self.commandWriter.TryWrite(command);
                return command.AsValueTask();
            });
        }
    }

    public ValueTask PublishAsync<T>(string key, T value)
    {
        return PublishAsync<T>(new NatsKey(key, true), value);
    }

    public ValueTask PublishAsync(in NatsKey key, byte[] value)
    {
        return PublishAsync(key, new ReadOnlyMemory<byte>(value));
    }

    public ValueTask PublishAsync(string key, byte[] value)
    {
        return PublishAsync(new NatsKey(key, true), value);
    }

    public ValueTask PublishAsync(in NatsKey key, ReadOnlyMemory<byte> value)
    {
        if (ConnectionState == NatsConnectionState.Open)
        {
            var command = AsyncPublishBytesCommand.Create(pool, key, value);
            commandWriter.TryWrite(command);
            return command.AsValueTask();
        }
        else
        {
            return WithConnectAsync(key, value, static (self, k, v) =>
            {
                var command = AsyncPublishBytesCommand.Create(self.pool, k, v);
                self.commandWriter.TryWrite(command);
                return command.AsValueTask();
            });
        }
    }

    public ValueTask PublishAsync(string key, ReadOnlyMemory<byte> value)
    {
        return PublishAsync(new NatsKey(key, true), value);
    }

    public void PostPublish<T>(in NatsKey key, T value)
    {
        if (ConnectionState == NatsConnectionState.Open)
        {
            var command = PublishCommand<T>.Create(pool, key, value, Options.Serializer);
            commandWriter.TryWrite(command);
        }
        else
        {
            WithConnect(key, value, static (self, k, v) =>
            {
                var command = PublishCommand<T>.Create(self.pool, k, v, self.Options.Serializer);
                self.commandWriter.TryWrite(command);
            });
        }
    }

    public void PostPublish<T>(string key, T value)
    {
        PostPublish<T>(new NatsKey(key, true), value);
    }

    public void PostPublish(in NatsKey key, byte[] value)
    {
        PostPublish(key, new ReadOnlyMemory<byte>(value));
    }

    public void PostPublish(string key, byte[] value)
    {
        PostPublish(new NatsKey(key, true), value);
    }

    public void PostPublish(in NatsKey key, ReadOnlyMemory<byte> value)
    {
        if (ConnectionState == NatsConnectionState.Open)
        {
            var command = PublishBytesCommand.Create(pool, key, value);
            commandWriter.TryWrite(command);
        }
        else
        {
            WithConnect(key, value, static (self, k, v) =>
            {
                var command = PublishBytesCommand.Create(self.pool, k, v);
                self.commandWriter.TryWrite(command);
            });
        }
    }

    public void PostPublish(string key, ReadOnlyMemory<byte> value)
    {
        PostPublish(new NatsKey(key, true), value);
    }

    public ValueTask PublishBatchAsync<T>(IEnumerable<(NatsKey, T?)> values)
    {
        if (ConnectionState == NatsConnectionState.Open)
        {
            var command = AsyncPublishBatchCommand<T>.Create(pool, values, Options.Serializer);
            commandWriter.TryWrite(command);
            return command.AsValueTask();
        }
        else
        {
            return WithConnectAsync(values, static (self, v) =>
            {
                var command = AsyncPublishBatchCommand<T>.Create(self.pool, v, self.Options.Serializer);
                self.commandWriter.TryWrite(command);
                return command.AsValueTask();
            });
        }
    }

    public ValueTask PublishBatchAsync<T>(IEnumerable<(string, T?)> values)
    {
        if (ConnectionState == NatsConnectionState.Open)
        {
            var command = AsyncPublishBatchCommand<T>.Create(pool, values, Options.Serializer);
            commandWriter.TryWrite(command);
            return command.AsValueTask();
        }
        else
        {
            var command = AsyncPublishBatchCommand<T>.Create(pool, values, Options.Serializer);
            commandWriter.TryWrite(command);
            return command.AsValueTask();
        }
    }

    public void PostPublishBatch<T>(IEnumerable<(NatsKey, T?)> values)
    {
        if (ConnectionState == NatsConnectionState.Open)
        {
            var command = PublishBatchCommand<T>.Create(pool, values, Options.Serializer);
            commandWriter.TryWrite(command);
        }
        else
        {
            WithConnect(values, static (self, v) =>
            {
                var command = PublishBatchCommand<T>.Create(self.pool, v, self.Options.Serializer);
                self.commandWriter.TryWrite(command);
            });
        }
    }

    public void PostPublishBatch<T>(IEnumerable<(string, T?)> values)
    {
        if (ConnectionState == NatsConnectionState.Open)
        {
            var command = PublishBatchCommand<T>.Create(pool, values, Options.Serializer);
            commandWriter.TryWrite(command);
        }
        else
        {
            WithConnect(values, static (self, v) =>
            {
                var command = PublishBatchCommand<T>.Create(self.pool, v, self.Options.Serializer);
                self.commandWriter.TryWrite(command);
            });
        }
    }

    public void PostDirectWrite(string protocol, int repeatCount = 1)
    {
        if (ConnectionState == NatsConnectionState.Open)
        {
            commandWriter.TryWrite(new DirectWriteCommand(protocol, repeatCount));
        }
        else
        {
            WithConnect(protocol, repeatCount, static (self, protocol, repeatCount) =>
            {
                self.commandWriter.TryWrite(new DirectWriteCommand(protocol, repeatCount));
            });
        }
    }

    public void PostDirectWrite(byte[] protocol)
    {
        if (ConnectionState == NatsConnectionState.Open)
        {
            commandWriter.TryWrite(new DirectWriteCommand(protocol));
        }
        else
        {
            WithConnect(protocol, static (self, protocol) =>
            {
                self.commandWriter.TryWrite(new DirectWriteCommand(protocol));
            });
        }
    }

    public void PostDirectWrite(DirectWriteCommand command)
    {
        if (ConnectionState == NatsConnectionState.Open)
        {
            commandWriter.TryWrite(command);
        }
        else
        {
            WithConnect(command, static (self, command) =>
            {
                self.commandWriter.TryWrite(command);
            });
        }
    }

    public ValueTask<TResponse?> RequestAsync<TRequest, TResponse>(in NatsKey key, TRequest request)
    {
        if (ConnectionState == NatsConnectionState.Open)
        {
            return requestResponseManager.AddAsync<TRequest, TResponse>(key, indBoxPrefix, request);
        }
        else
        {
            return WithConnectAsync(key, request, static (self, key, request) =>
            {
                return self.requestResponseManager.AddAsync<TRequest, TResponse>(key, self.indBoxPrefix, request);
            });
        }
    }

    public ValueTask<TResponse?> RequestAsync<TRequest, TResponse>(string key, TRequest request)
    {
        return RequestAsync<TRequest, TResponse>(new NatsKey(key, true), request);
    }

    public ValueTask<IDisposable> SubscribeRequestAsync<TRequest, TResponse>(in NatsKey key, Func<TRequest, TResponse> requestHandler)
    {
        return SubscribeRequestAsync(key.Key, requestHandler);
    }

    public ValueTask<IDisposable> SubscribeRequestAsync<TRequest, TResponse>(string key, Func<TRequest, TResponse> requestHandler)
    {
        if (ConnectionState == NatsConnectionState.Open)
        {
            return subscriptionManager.AddRequestHandlerAsync(key, requestHandler);
        }
        else
        {
            return WithConnectAsync(key, requestHandler, static (self, key, requestHandler) =>
            {
                return self.subscriptionManager.AddRequestHandlerAsync(key, requestHandler);
            });
        }
    }

    public ValueTask<IDisposable> SubscribeRequestAsync<TRequest, TResponse>(in NatsKey key, Func<TRequest, Task<TResponse>> requestHandler)
    {
        return SubscribeRequestAsync(key.Key, requestHandler);
    }

    public ValueTask<IDisposable> SubscribeRequestAsync<TRequest, TResponse>(string key, Func<TRequest, Task<TResponse>> requestHandler)
    {
        if (ConnectionState == NatsConnectionState.Open)
        {
            return subscriptionManager.AddRequestHandlerAsync(key, requestHandler);
        }
        else
        {
            return WithConnectAsync(key, requestHandler, static (self, key, requestHandler) =>
            {
                return self.subscriptionManager.AddRequestHandlerAsync(key, requestHandler);
            });
        }
    }

    public ValueTask<IDisposable> SubscribeAsync<T>(in NatsKey key, Action<T> handler)
    {
        return SubscribeAsync(key.Key, handler);
    }

    public ValueTask<IDisposable> SubscribeAsync<T>(string key, Action<T> handler)
    {
        if (ConnectionState == NatsConnectionState.Open)
        {
            return subscriptionManager.AddAsync(key, null, handler);
        }
        else
        {
            return WithConnectAsync(key, handler, static (self, key, handler) =>
            {
                return self.subscriptionManager.AddAsync(key, null, handler);
            });
        }
    }

    public ValueTask<IDisposable> SubscribeAsync<T>(in NatsKey key, Func<T, Task> asyncHandler)
    {
        return SubscribeAsync(key.Key, asyncHandler);
    }

    public ValueTask<IDisposable> SubscribeAsync<T>(string key, Func<T, Task> asyncHandler)
    {
        if (ConnectionState == NatsConnectionState.Open)
        {
            return subscriptionManager.AddAsync<T>(key, null, async x =>
            {
                try
                {
                    await asyncHandler(x).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error occured during subscribe message.");
                }
            });
        }
        else
        {
            return WithConnectAsync(key, asyncHandler, static (self, key, asyncHandler) =>
            {
                return self.subscriptionManager.AddAsync<T>(key, null, async x =>
                {
                    try
                    {
                        await asyncHandler(x).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        self.logger.LogError(ex, "Error occured during subscribe message.");
                    }
                });
            });
        }
    }

    public ValueTask<IDisposable> SubscribeAsync<T>(in NatsKey key, in NatsKey queueGroup, Action<T> handler)
    {
        if (ConnectionState == NatsConnectionState.Open)
        {
            return subscriptionManager.AddAsync(key.Key, queueGroup, handler);
        }
        else
        {
            return WithConnectAsync(key, queueGroup, handler, static (self, key, queueGroup, handler) =>
            {
                return self.subscriptionManager.AddAsync(key.Key, queueGroup, handler);
            });
        }
    }

    public ValueTask<IDisposable> SubscribeAsync<T>(string key, string queueGroup, Action<T> handler)
    {
        if (ConnectionState == NatsConnectionState.Open)
        {
            return subscriptionManager.AddAsync(key, new NatsKey(queueGroup, true), handler);
        }
        else
        {
            return WithConnectAsync(key, queueGroup, handler, static (self, key, queueGroup, handler) =>
            {
                return self.subscriptionManager.AddAsync(key, new NatsKey(queueGroup, true), handler);
            });
        }
    }

    public ValueTask<IDisposable> SubscribeAsync<T>(in NatsKey key, in NatsKey queueGroup, Func<T, Task> asyncHandler)
    {
        if (ConnectionState == NatsConnectionState.Open)
        {
            return subscriptionManager.AddAsync<T>(key.Key, queueGroup, async x =>
            {
                try
                {
                    await asyncHandler(x).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error occured during subscribe message.");
                }
            });
        }
        else
        {
            return WithConnectAsync(key, queueGroup, asyncHandler, static (self, key, queueGroup, asyncHandler) =>
            {
                return self.subscriptionManager.AddAsync<T>(key.Key, queueGroup, async x =>
                {
                    try
                    {
                        await asyncHandler(x).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        self.logger.LogError(ex, "Error occured during subscribe message.");
                    }
                });
            });
        }
    }

    public ValueTask<IDisposable> SubscribeAsync<T>(string key, string queueGroup, Func<T, Task> asyncHandler)
    {
        if (ConnectionState == NatsConnectionState.Open)
        {
            return subscriptionManager.AddAsync<T>(key, new NatsKey(queueGroup, true), async x =>
            {
                try
                {
                    await asyncHandler(x).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error occured during subscribe message.");
                }
            });
        }
        else
        {
            return WithConnectAsync(key, queueGroup, asyncHandler, static (self, key, queueGroup, asyncHandler) =>
            {
                return self.subscriptionManager.AddAsync<T>(key, new NatsKey(queueGroup, true), async x =>
                {
                    try
                    {
                        await asyncHandler(x).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        self.logger.LogError(ex, "Error occured during subscribe message.");
                    }
                });
            });
        }
    }

    public IObservable<T> AsObservable<T>(string key)
    {
        return AsObservable<T>(new NatsKey(key, true));
    }

    public IObservable<T> AsObservable<T>(in NatsKey key)
    {
        return new NatsObservable<T>(this, key);
    }

    // internal commands.

    internal void PostPong()
    {
        commandWriter.TryWrite(PongCommand.Create(pool));
    }

    internal ValueTask SubscribeAsync(int subscriptionId, string subject, in NatsKey? queueGroup)
    {
        var command = AsyncSubscribeCommand.Create(pool, subscriptionId, new NatsKey(subject, true), queueGroup);
        commandWriter.TryWrite(command);
        return command.AsValueTask();
    }

    internal void PostUnsubscribe(int subscriptionId)
    {
        commandWriter.TryWrite(UnsubscribeCommand.Create(pool, subscriptionId));
    }

    internal void PostCommand(ICommand command)
    {
        commandWriter.TryWrite(command);
    }

    internal void PublishToClientHandlers(int subscriptionId, in ReadOnlySequence<byte> buffer)
    {
        subscriptionManager.PublishToClientHandlers(subscriptionId, buffer);
    }

    internal void PublishToRequestHandler(int subscriptionId, in NatsKey replyTo, in ReadOnlySequence<byte> buffer)
    {
        subscriptionManager.PublishToRequestHandler(subscriptionId, replyTo, buffer);
    }

    internal void PublishToResponseHandler(int requestId, in ReadOnlySequence<byte> buffer)
    {
        requestResponseManager.PublishToResponseHandler(requestId, buffer);
    }

    internal void ResetPongCount()
    {
        Interlocked.Exchange(ref pongCount, 0);
    }

    public async ValueTask DisposeAsync()
    {
        if (!isDisposed)
        {
            isDisposed = true;
            // Dispose Writer(Drain prepared queues -> write to socket)
            // Close Socket
            // Dispose Reader(Drain read buffers)
            if (socketWriter != null)
            {
                await socketWriter.DisposeAsync().ConfigureAwait(false);
            }
            if (socket != null)
            {
                await socket.DisposeAsync();
            }
            if (socketReader != null)
            {
                await socketReader.DisposeAsync().ConfigureAwait(false);
            }
            if (pingTimerCancellationTokenSource != null)
            {
                pingTimerCancellationTokenSource.Cancel();
            }
            subscriptionManager.Dispose();
            requestResponseManager.Dispose();
            waitForOpenConnection.TrySetCanceled();
        }
    }

    void ThrowIfDisposed()
    {
        if (isDisposed) throw new ObjectDisposedException(null);
    }

    async void WithConnect(Action<NatsConnection> core)
    {
        try
        {
            await ConnectAsync().ConfigureAwait(false);
        }
        catch
        {
            // log will shown on ConnectAsync failed
            return;
        }
        core(this);
    }

    async void WithConnect<T1>(T1 item1, Action<NatsConnection, T1> core)
    {
        try
        {
            await ConnectAsync().ConfigureAwait(false);
        }
        catch
        {
            // log will shown on ConnectAsync failed
            return;
        }
        core(this, item1);
    }

    async void WithConnect<T1, T2>(T1 item1, T2 item2, Action<NatsConnection, T1, T2> core)
    {
        try
        {
            await ConnectAsync().ConfigureAwait(false);
        }
        catch
        {
            // log will shown on ConnectAsync failed
            return;
        }
        core(this, item1, item2);
    }

    async ValueTask WithConnectAsync<T1>(T1 item1, Func<NatsConnection, T1, ValueTask> coreAsync)
    {
        await ConnectAsync().ConfigureAwait(false);
        await coreAsync(this, item1).ConfigureAwait(false);
    }

    async ValueTask WithConnectAsync<T1, T2>(T1 item1, T2 item2, Func<NatsConnection, T1, T2, ValueTask> coreAsync)
    {
        await ConnectAsync().ConfigureAwait(false);
        await coreAsync(this, item1, item2).ConfigureAwait(false);
    }

    async ValueTask<T> WithConnectAsync<T>(Func<NatsConnection, ValueTask<T>> coreAsync)
    {
        await ConnectAsync().ConfigureAwait(false);
        return await coreAsync(this).ConfigureAwait(false);
    }

    async ValueTask<TResult> WithConnectAsync<T1, T2, TResult>(T1 item1, T2 item2, Func<NatsConnection, T1, T2, ValueTask<TResult>> coreAsync)
    {
        await ConnectAsync().ConfigureAwait(false);
        return await coreAsync(this, item1, item2).ConfigureAwait(false);
    }

    async ValueTask<TResult> WithConnectAsync<T1, T2, T3, TResult>(T1 item1, T2 item2, T3 item3, Func<NatsConnection, T1, T2, T3, ValueTask<TResult>> coreAsync)
    {
        await ConnectAsync().ConfigureAwait(false);
        return await coreAsync(this, item1, item2, item3).ConfigureAwait(false);
    }
}
