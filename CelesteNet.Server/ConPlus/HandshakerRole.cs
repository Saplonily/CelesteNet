using Celeste.Mod.CelesteNet.DataTypes;
using Celeste.Mod.CelesteNet.Server.Utils;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Celeste.Mod.CelesteNet.DataTypes;

namespace Celeste.Mod.CelesteNet.Server {
    /*
    This role creates a task scheduler, whose tasks are run on all threads
    assigned to the role. The connection acceptor role creates new tasks for
    that scheduler, which perform the handshake, before passing control of the
    connection to the regular send/recv roles.
    -Popax21
    */
    public partial class HandshakerRole : NetPlusThreadRole {

        public const int TeapotTimeout = 10000;

        private class TaskWorkerScheduler : TaskScheduler, IDisposable {

            private readonly BlockingCollection<Task> TaskQueue = new();
            private readonly ThreadLocal<bool> ExecutingTasks = new();

            public void Dispose() {
                ExecutingTasks.Dispose();
                TaskQueue.Dispose();
            }

            public void ExecuteTasks(Worker worker, CancellationToken token) {
                ExecutingTasks.Value = true;
                foreach (Task t in TaskQueue.GetConsumingEnumerable(token)) {
                    worker.EnterActiveZone();
                    try {
                        TryExecuteTask(t);
                    } finally {
                        worker.ExitActiveZone();
                    }
                }
                ExecutingTasks.Value = false;
            }

            protected override IEnumerable<Task> GetScheduledTasks() => TaskQueue;
            protected override void QueueTask(Task task) => TaskQueue.Add(task);

            protected override bool TryExecuteTaskInline(Task task, bool prevQueued) {
                if (prevQueued || !ExecutingTasks.Value)
                    return false;
                return TryExecuteTask(task);
            }

        }

        private class Worker : RoleWorker {

            public Worker(HandshakerRole role, NetPlusThread thread) : base(role, thread) {}

            protected internal override void StartWorker(CancellationToken token) => Role.Scheduler.ExecuteTasks(this, token);

            public new void EnterActiveZone() => base.EnterActiveZone();
            public new void ExitActiveZone() => base.ExitActiveZone();

            public new HandshakerRole Role => (HandshakerRole) base.Role;

        }

        public override int MinThreads => 1;
        public override int MaxThreads => int.MaxValue;

        public CelesteNetServer Server { get; }
        public TaskFactory Factory { get; }

        private readonly TaskWorkerScheduler Scheduler;
        private readonly List<(string, IConnectionFeature)> ConFeatures;

        public HandshakerRole(NetPlusThreadPool pool, CelesteNetServer server) : base(pool) {
            Server = server;
            Scheduler = new();
            Factory = new(Scheduler);

            // Find connection features
            ConFeatures = new List<(string, IConnectionFeature)>();
            foreach (Type type in CelesteNetUtils.GetTypes()) {
                if (!typeof(IConnectionFeature).IsAssignableFrom(type) || type.IsAbstract || string.IsNullOrEmpty(type.FullName))
                    continue;

                IConnectionFeature? feature = (IConnectionFeature?) Activator.CreateInstance(type);
                if (feature == null)
                    throw new Exception($"Cannot create instance of connection feature {type.FullName}");
                Logger.Log(LogLevel.DBG, "handshake", $"Found connection feature: {type.FullName}");
                ConFeatures.Add((type.FullName, feature));
            }
        }

        public override void Dispose() {
            Scheduler.Dispose();
            base.Dispose();
        }

        public override RoleWorker CreateWorker(NetPlusThread thread) => new Worker(this, thread);

        public async Task DoTCPUDPHandshake(Socket sock, CelesteNetTCPUDPConnection.Settings settings, TCPReceiverRole tcpReceiver, UDPReceiverRole udpReceiver, TCPUDPSenderRole sender) {
            if (sock.RemoteEndPoint is not IPEndPoint remoteEP) {
                Logger.Log(LogLevel.WRN, "tcpudphs", $"Handshake for connection without valid remote IP endpoint???");
                sock.Dispose();
                return;
            }
            ConPlusTCPUDPConnection? con = null;
            try {
                // Obtain a connection token
                uint conToken = Server.ConTokenGenerator.GenerateToken();

                // Do the teapot handshake
                IConnectionFeature[]? conFeatures = null;
                string? playerUID = null, playerName = null, playerColor = null, avaterPhotoUrl = null, playerPrefix = null;
                CelesteNetClientOptions? clientOptions = null;
                using (CancellationTokenSource tokenSrc = new()) {
                    // .NET is completly stupid, you can't cancel async socket operations
                    // We literally have to kill the socket for the handshake to be able to timeout
                    tokenSrc.CancelAfter(TeapotTimeout);
                    tokenSrc.Token.Register(() => sock.Close());
                    try {
                        (IConnectionFeature[], string, string, CelesteNetClientOptions,string,string,string)? teapotRes =
                            await TeapotHandshake(
                                sock, conToken, settings,
                                ConPlusTCPUDPConnection.GetConnectionUID(remoteEP)
                            );
                        if (teapotRes != null)
                            (conFeatures, playerUID, playerName, clientOptions,playerColor,avaterPhotoUrl,playerPrefix) = teapotRes.Value;
                    } catch {
                        if (tokenSrc.IsCancellationRequested) {
                            Logger.Log(LogLevel.INF, "tcpudphs", $"Handshake for connection {remoteEP} timed out, maybe an old client?");
                            sock.Dispose();
                            return;
                        }
                        throw;
                    }
                }

                if (conFeatures == null ||
                    playerUID.IsNullOrEmpty() || 
                    playerName.IsNullOrEmpty() ||
                    clientOptions == null) {
                    Logger.Log(LogLevel.INF, "tcpudphs", $"Connection from {remoteEP} failed teapot handshake");
                    sock.ShutdownSafe(SocketShutdown.Both);
                    sock.Close();
                    return;
                }
                var features = conFeatures.Aggregate((string?)null, (a, f) => ((a == null) ? $"{f}" : $"{a}, {f}"));
                Logger.Log(LogLevel.VVV, "tcpudphs", $"Connection {remoteEP} teapot handshake success: connection features '{features}' player UID {playerUID} player name {playerName}");

                // Create the connection, do the generic connection handshake
                Server.HandleConnect(con = new(Server, conToken, settings, sock, tcpReceiver, udpReceiver, sender));
                await DoConnectionHandshake(con, conFeatures);

                // Create the session
                using (con.Utilize(out bool alive)) {
                    // Better safe than sorry
                    if (!alive || !con.IsConnected)
                        return;
                    Server.CreateSession(con, playerUID, playerName, clientOptions, playerColor, avaterPhotoUrl, playerPrefix);
                }
            } catch {
                con?.Dispose();
                sock.Dispose();
                throw;
            }
        }

        // Let's mess with web crawlers even more ;)
        // Also: I'm a Teapot
        private async Task<(IConnectionFeature[] conFeatures, string playerUID, string playerName, CelesteNetClientOptions clientOptions, string playerColor, string avaterPhotoUrl, string playerPrefix)?> TeapotHandshake<T>(Socket sock, uint conToken, T settings, string conUID) where T : new() {
            using NetworkStream netStream = new(sock, false);
            BufferedStream bufStream = new(netStream);
            try {
                using StreamWriter writer = new(bufStream, CelesteNetUtils.UTF8NoBOM, 1024, true);
                async Task<(IConnectionFeature[], string, string, CelesteNetClientOptions,string,string,string)?> Send500() {
                    await writer.WriteAsync(
@"HTTP/1.1 500 Internal Server Error
Connection: close

The server encountered an internal error while handling the request"
                        .Trim().Replace("\r\n", "\n").Replace("\n", "\r\n")
                    );
                    return null;
                }

                // Parse the "HTTP" request line
                string? reqLine = netStream.UnbufferedReadLine();
                if (reqLine == null)
                    return await Send500();

                string[] reqLineSegs = reqLine.Split(' ').Where(s => !string.IsNullOrEmpty(s)).ToArray();
                if (
                    reqLineSegs.Length != 3 ||
                    (reqLineSegs[0] != "CONNECT" && reqLineSegs[0] != "TEAREQ") ||
                    reqLineSegs[1] != "/teapot"
                )
                    return await Send500();

                // Parse the headers
                Dictionary<string, string> headers = new();
                for (string? line = netStream.UnbufferedReadLine(); !string.IsNullOrEmpty(line); line = netStream.UnbufferedReadLine()) {
                    int split = line.IndexOf(':');
                    if (split == -1)
                        return await Send500();
                    headers[line.Substring(0, split).Trim()] = line.Substring(split + 1).Trim();
                }
                bufStream.Flush();

                // Check teapot version
                if (!headers.TryGetValue("CelesteNet-TeapotVersion", out string? teapotVerHeader) || !int.TryParse(teapotVerHeader, out int teapotVer))
                    return await Send500();

                if (teapotVer != CelesteNetUtils.LoadedVersion) {
                    Logger.Log(LogLevel.DBG, "teapot", $"Teapot version mismatch for connection {sock.RemoteEndPoint}: {teapotVer} [client] != {CelesteNetUtils.LoadedVersion} [server]");
                    await writer.WriteAsync(
$@"HTTP/1.1 409 Version Mismatch
Connection: close

{string.Format(Server.Settings.MessageTeapotVersionMismatch, teapotVer, CelesteNetUtils.LoadedVersion)}"
                        .Trim().Replace("\r\n", "\n").Replace("\n", "\r\n")
                    );
                    return null;
                }

                headers.TryGetValue("CelesteNet-ClientVersion", out string? clientVersion);

                const string expectedVersion = "3.2.3";
                if (clientVersion != expectedVersion)
                {
                    await writer.WriteAsync(
$@"HTTP/1.1 403 Access Denied
Connection: close

{string.Format(Server.Settings.MessageOutdatedVersion, clientVersion, expectedVersion)}" 
.Trim().Replace("\r\n", "\n").Replace("\n", "\r\n")
);
                    return null;
                }
                
                // Get the list of supported connection features
                HashSet<string> conFeatures;
                if (headers.TryGetValue("CelesteNet-ConnectionFeatures", out string? conFeaturesRaw))
                    conFeatures = new(conFeaturesRaw.Split(',').Select(f => f.Trim().ToLower()));
                else
                    conFeatures = new();

                // Match connection features
                List<(string name, IConnectionFeature feature)> matchedFeats = new();
                foreach ((string name, IConnectionFeature feature) feat in ConFeatures) {
                    if (conFeatures.Contains(feat.name.ToLower()))
                        matchedFeats.Add((feat.name, feat.feature));
                }

                // Get the player name-key
                if (!headers.TryGetValue("CelesteNet-PlayerNameKey", out string? playerNameKey))
                    return await Send500();

                // Authenticate name-key
                string? errorReason = AuthenticatePlayerNameKey(playerNameKey, conUID, out string? playerUID, out string? playerName, out string? playerColor, out string? avaterPhotoUrl, out string? playerPrefix);
                if (playerUID == null)
                    errorReason ??= "No UID";
                if (playerName == null)
                    errorReason ??= "No name, please login first.";
                if (avaterPhotoUrl == null)
                    avaterPhotoUrl = "https://celeste.centralteam.cn/assets/uploads/profile/default.jpg";
                if (errorReason != null || playerUID == null || playerName == null) {
                    Logger.Log(LogLevel.INF, "teapot", $"Error authenticating name-key '{playerNameKey}' for connection {sock.RemoteEndPoint}: {errorReason}");
                    await writer.WriteAsync(
$@"HTTP/1.1 403 Access Denied
Connection: close

{errorReason}"
                        .Trim().Replace("\r\n", "\n").Replace("\n", "\r\n")
                    );
                    return null;
                }

                // Parse the client options
                CelesteNetClientOptions clientOptions = new();
                foreach (FieldInfo field in typeof(CelesteNetClientOptions).GetFields(BindingFlags.Public | BindingFlags.Instance)) {
                    string headerName = $"CelesteNet-ClientOptions-{field.Name}";
                    if (!headers.TryGetValue(headerName, out string? val))
                        continue;
#pragma warning disable IDE0049 // Simplify Names
                    switch (Type.GetTypeCode(field.FieldType)) {
                        case TypeCode.Boolean:
                            field.SetValue(clientOptions, Boolean.Parse(val));
                            break;
                        case TypeCode.Int16:
                            field.SetValue(clientOptions, Int16.Parse(val));
                            break;
                        case TypeCode.Int32:
                            field.SetValue(clientOptions, Int32.Parse(val));
                            break;
                        case TypeCode.Int64:
                            field.SetValue(clientOptions, Int64.Parse(val));
                            break;
                        case TypeCode.UInt16:
                            field.SetValue(clientOptions, UInt16.Parse(val));
                            break;
                        case TypeCode.UInt32:
                            field.SetValue(clientOptions, UInt32.Parse(val));
                            break;
                        case TypeCode.UInt64:
                            field.SetValue(clientOptions, UInt64.Parse(val));
                            break;
                        case TypeCode.Single:
                            field.SetValue(clientOptions, Single.Parse(val));
                            break;
                        case TypeCode.Double:
                            field.SetValue(clientOptions, Double.Parse(val));
                            break;
                    }
#pragma warning restore IDE0049
                }

                // Answer with the almighty teapot
                StringBuilder settingsBuilder = new();
                settings ??= new();
                foreach (FieldInfo field in typeof(T).GetFields(BindingFlags.Public | BindingFlags.Instance)) {
                    switch (Type.GetTypeCode(field.FieldType)) {
                        case TypeCode.Boolean:
                        case TypeCode.Int16:
                        case TypeCode.Int32:
                        case TypeCode.Int64:
                        case TypeCode.UInt16:
                        case TypeCode.UInt32:
                        case TypeCode.UInt64:
                        case TypeCode.Single:
                        case TypeCode.Double: {
                                settingsBuilder.AppendLine($"CelesteNet-Settings-{field.Name}: {field.GetValue(settings)}");
                            }
                            break;
                    }
                }

                await writer.WriteAsync(
$@"HTTP/4.2 418 I'm a teapot
Connection: keep-alive
CelesteNet-TeapotVersion: {CelesteNetUtils.LoadedVersion}
CelesteNet-ConnectionToken: {conToken:X}
CelesteNet-ConnectionFeatures: {matchedFeats.Aggregate((string?) null, (a, f) => ((a == null) ? f.name : $"{a}, {f.name}"))}
{settingsBuilder.ToString().Trim()}

Who wants some tea?"
                    .Trim().Replace("\r\n", "\n").Replace("\n", "\r\n") + "\r\n" + "\r\n"
                );

                return (matchedFeats.Select(f => f.feature).ToArray(), playerUID, playerName, clientOptions, playerColor, avaterPhotoUrl, playerPrefix);
            } finally {
                // We must try-catch buffered stream disposes as those will try to flush.
                // If a network stream was torn down out of our control, it will throw!
                try {
                    bufStream.Dispose();
                } catch {
                }
            }
            return null;
        }

        public async Task DoConnectionHandshake(CelesteNetConnection con, IConnectionFeature[] features) {
            // Handshake connection features
            foreach (IConnectionFeature feature in features)
                feature.Register(con, false);
            foreach (IConnectionFeature feature in features)
                await feature.DoHandshake(con, false);

            // Send the current tick rate
            con.Send(new DataTickRate {
                TickRate = Server.CurrentTickRate
            });
        }

        public string? AuthenticatePlayerNameKey(string nameKey, string conUID, out string? playerUID, out string? playerName, out string? playerColor, out string? avaterPhotoUrl, out string? playerPrefix) {
            // Get the player UID and name from the player name-key
            playerUID = playerName = avaterPhotoUrl = playerPrefix = playerColor = null;
            if (nameKey.Length > 1 && nameKey.StartsWith("#"))
            {
                
                string key = nameKey.Substring(1);
                Logger.Log(LogLevel.INF, "NetAuth", "Authing:" + key);
                dynamic? json = JsonConvert.DeserializeObject(HttpUtils.Get($"https://celeste.centralteam.cn/api/celeste/user?access_token={key}", 7000));
                Logger.Log(LogLevel.INF, "NetAuth", json.ToString());
                if (json == null)
                {
                    return string.Format(Server.Settings.MessageInvalidKey, nameKey);
                }
                playerUID = $"miaoNet-{conUID}";
                playerName = (string)json.username;
                avaterPhotoUrl = (string)json.avatar_url;
                playerColor = (string)json.color;
                playerPrefix = (string)json.prefix;
                return null;
            }
            return string.Format(Server.Settings.MessageAuthOnly, nameKey);
        }

    }
}
