using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Timers;
using Fleck;
using MusicBeeRemote.Core.Events;
using MusicBeeRemote.Core.Events.Status.Internal;
using MusicBeeRemote.Core.Model.Entities;
using MusicBeeRemote.Core.Settings;
using MusicBeeRemote.Core.Threading;
using MusicBeeRemote.Core.Utilities;
using Newtonsoft.Json;
using NLog;
using TinyMessenger;
using Timer = System.Timers.Timer;

namespace MusicBeeRemote.Core.Network
{
    /// <summary>
    /// The socket server.
    /// </summary>
    public sealed class SocketServer : IDisposable
    {
        private const string NewLine = "\n";

        private readonly Logger _logger = LogManager.GetCurrentClassLogger();
        private readonly ProtocolHandler _handler;
        private readonly ConcurrentDictionary<string, IWebSocketConnection> _availableWorkerSockets;
        private readonly TaskFactory _factory;
        private readonly ITinyMessengerHub _hub;
        private readonly Authenticator _auth;
        private readonly PersistenceManager _settings;

        /// <summary>
        /// The web socket server.
        /// </summary>
        private WebSocketServer _mainWsServer;

        private bool _isDisposed;
        private Timer _pingTimer;

        public SocketServer(
            ProtocolHandler handler,
            ITinyMessengerHub hub,
            Authenticator auth,
            PersistenceManager settings)
        {
            _handler = handler;
            _hub = hub ?? throw new ArgumentNullException(nameof(hub));
            _auth = auth;
            _settings = settings;
            _availableWorkerSockets = new ConcurrentDictionary<string, IWebSocketConnection>();
            TaskScheduler scheduler = new LimitedTaskScheduler(2);
            _factory = new TaskFactory(scheduler);

            _hub.Subscribe<RestartSocketEvent>(eEvent => RestartSocket());
            _hub.Subscribe<ForceClientDisconnect>(eEvent => DisconnectSocket(eEvent.ConnectionId));
            _hub.Subscribe<BroadcastEventAvailable>(eEvent => Broadcast(eEvent.BroadcastEvent));
            _hub.Subscribe<PluginResponseAvailableEvent>(eEvent => Send(eEvent.Message, eEvent.ConnectionId));
        }

        ~SocketServer()
        {
            Dispose(false);
        }

        /// <summary>
        /// It starts the websocket server.
        /// </summary>
        public void Start()
        {
            _logger.Debug($"Socket starts listening on port: {_settings.UserSettingsModel.ListeningPort}");
            try
            {
                // Create the websocket server
                _mainWsServer = new WebSocketServer($"ws://0.0.0.0:{_settings.UserSettingsModel.ListeningPort}");
                _mainWsServer.Start(OnClientConnect);

                _hub.Publish(new SocketStatusChanged(true));

                _pingTimer = new Timer(15000);
                _pingTimer.Elapsed += PingTimerOnElapsed;
                _pingTimer.Enabled = true;
            }
            catch (SocketException se)
            {
                _logger.Error(se, "While starting the socket service");
            }
        }

        /// <summary>
        /// It stops the SocketServer.
        /// </summary>
        public void Terminate()
        {
            _logger.Debug("Stopping socket service");
            try
            {
                _mainWsServer?.Dispose();

                foreach (var wSocket in _availableWorkerSockets.Values)
                {
                    if (wSocket == null)
                    {
                        continue;
                    }

                    wSocket.Close();
                }

                _mainWsServer = null;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "While stopping the socket service");
            }
            finally
            {
                _hub.Publish(new SocketStatusChanged(false));
            }
        }

        /// <summary>
        /// Disposes anything Related to the socket server at the end of life of the Object.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (_isDisposed)
            {
                return;
            }

            if (disposing)
            {
                _mainWsServer?.Dispose();
                _pingTimer.Dispose();
            }

            _isDisposed = true;
        }

        /// <summary>
        /// Finds an active connection with that matches the specified connection id, closes
        /// the connection and disposes the worker socket.
        /// </summary>
        /// <param name="connectionId">The id of the connection we want to disconnect.</param>
        private void DisconnectSocket(string connectionId)
        {
            try
            {
                if (!_availableWorkerSockets.TryRemove(connectionId, out var workerSocket))
                {
                    return;
                }

                workerSocket.Close();
                _hub.Publish(new ClientDisconnectedEvent(connectionId));
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "While disconnecting a socket");
            }
        }

        private void PingTimerOnElapsed(object sender, ElapsedEventArgs e)
        {
            Send(new SocketMessage("ping", string.Empty).ToJsonString());
            _logger.Debug($"Ping: {DateTime.UtcNow}");
        }

        /// <summary>
        /// Restarts the main socket that is listening for new clients.
        /// Useful when the user wants to change the listening port.
        /// </summary>
        private void RestartSocket()
        {
            Terminate();
            Start();
        }

        // this is the call back function,
        private void OnClientConnect(IWebSocketConnection ws)
        {
            try
            {
                // Validate If client should connect.
                string ipString = ws.ConnectionInfo.ClientIpAddress;
                IPAddress ipAddress = IPAddress.Parse(ipString);

                bool isAllowed = false;
                switch (_settings.UserSettingsModel.FilterSelection)
                {
                    case FilteringSelection.Specific:
                        foreach (var source in _settings.UserSettingsModel.IpAddressList)
                        {
                            if (string.Compare(ipString, source, StringComparison.Ordinal) == 0)
                            {
                                isAllowed = true;
                            }
                        }

                        break;

                    case FilteringSelection.Range:
                        var settings = _settings.UserSettingsModel;
                        isAllowed = RangeChecker.AddressInRange(ipString, settings.BaseIp, settings.LastOctetMax);
                        break;
                    default:
                        isAllowed = true;
                        break;
                }

                if (Equals(ipAddress == IPAddress.Loopback))
                {
                    isAllowed = true;
                }

                if (!isAllowed)
                {
                    ws.Send(
                        Encoding.UTF8.GetBytes(new SocketMessage(Constants.NotAllowed, string.Empty).ToJsonString()));
                    ws.Close();
                    _logger.Debug($"Client {ipString} was force disconnected IP was not in the allowed addresses");
                    return;
                }

                var connectionId = IdGenerator.GetUniqueKey();

                if (!_availableWorkerSockets.TryAdd(connectionId, ws))
                {
                    return;
                }

                // Inform the the Protocol Handler that a new Client has been connected, prepare for handshake.
                _hub.Publish(new ClientConnectedEvent(ipAddress, connectionId));

                // Let the worker Socket do the further processing
                // for the just connected client.
                WaitForData(ws, connectionId);
            }
            catch (ObjectDisposedException)
            {
                _logger.Debug("OnClientConnection: Socket has been closed");
            }
            catch (SocketException se)
            {
                _logger.Debug(se, "On client connect");
            }
            catch (Exception ex)
            {
                _logger.Debug($"OnClientConnect Exception : {ex.Message}");
            }
        }

        // Start waiting for data from the client
        private void WaitForData(IWebSocketConnection ws, string connectionId)
        {
            ws.OnMessage = message => _handler.ProcessIncomingMessage(message, connectionId);
            ws.OnClose = () =>
            {
                _availableWorkerSockets.TryRemove(connectionId, out _);
                _hub.Publish(new ClientDisconnectedEvent(connectionId));
                _logger.Debug($"Websocket has been closed: {connectionId}");
            };
            ws.OnError = e =>
            {
                _logger.Error(e, "Error in client");
            };
        }

        /// <summary>
        /// Sends a message to a specific client with a specific connection id.
        /// </summary>
        /// <param name="message">The message to send.</param>
        /// <param name="connectionId">The id of the connection that will receive the message.</param>
        private void Send(SocketMessage message, string connectionId)
        {
            var serializedMessage = JsonConvert.SerializeObject(message);

            if (message.NewLineTerminated)
            {
                serializedMessage += NewLine;
            }

            if (connectionId.Equals("all", StringComparison.InvariantCultureIgnoreCase))
            {
                Send(serializedMessage);
                return;
            }

            _logger.Debug($"sending-{connectionId}:{serializedMessage}");

            try
            {
                if (_availableWorkerSockets.TryGetValue(connectionId, out var wSocket))
                {
                    wSocket.Send(serializedMessage).Wait();
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "While sending message to specific client");
            }
        }

        /// <summary>
        /// Sends a message to all the connections that are in broadcast mode.
        /// The connections that are not in broadcast mode will be skipped.
        /// </summary>
        /// <param name="message">The message that will be send through the socket connection.</param>
        private void Send(string message)
        {
            _logger.Debug($"sending-all: {message}");

            try
            {
                foreach (var key in _availableWorkerSockets.Keys)
                {
                    if (!_availableWorkerSockets.TryGetValue(key, out var worker))
                    {
                        continue;
                    }

                    var isConnected = worker != null && worker.IsAvailable;
                    if (!isConnected)
                    {
                        RemoveDeadSocket(key);
                        _hub.Publish(new ClientDisconnectedEvent(key));
                    }

                    if (isConnected && _auth.CanConnectionReceive(key) && _auth.IsConnectionBroadcastEnabled(key))
                    {
                        worker.Send(message).Wait();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "While sending message to all available clients");
            }
        }

        private void RemoveDeadSocket(string connectionId)
        {
            _availableWorkerSockets.TryRemove(connectionId, out var worker);
            worker?.Close();
            _hub.Publish(new ClientDisconnectedEvent(connectionId));
        }

        private void Broadcast(BroadcastEvent broadcastEvent)
        {
            _logger.Debug($"broadcasting message {broadcastEvent}");

            try
            {
                foreach (var key in _availableWorkerSockets.Keys)
                {
                    if (!_availableWorkerSockets.TryGetValue(key, out var worker))
                    {
                        continue;
                    }

                    var isConnected = worker != null && worker.IsAvailable;
                    if (!isConnected)
                    {
                        RemoveDeadSocket(key);
                        _hub.Publish(new ClientDisconnectedEvent(key));
                    }

                    if (!isConnected || !_auth.CanConnectionReceive(key) ||
                        !_auth.IsConnectionBroadcastEnabled(key))
                    {
                        continue;
                    }

                    var clientProtocol = _auth.ClientProtocolVersion(key);
                    var message = broadcastEvent.GetMessage(clientProtocol);
                    worker.Send(message).Wait();
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "While sending message to all available clients");
            }
        }
    }
}
