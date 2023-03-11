using System;
using System.Net;
using System.Net.Sockets;
using System.Collections;
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace Netick.Transport
{

    [CreateAssetMenu(fileName = "LiteNetTransport", menuName = "Netick/Transport/LiteNetTransport", order = 1)]
    public class LiteNetTransport : NetworkTransport, INetEventListener
    {
        public class LNLConnection : NetickConnection
        {
            public NetPeer              LNLPeer;
            public override IPEndPoint  EndPoint => LNLPeer.EndPoint;
            public override int         Mtu => LNLPeer.Mtu;

            public override void Send(byte[] data, int length)
            {
                LNLPeer.Send(data, 0, length, DeliveryMethod.Unreliable);
            }
        }

        private NetManager                         _netManager;
        private readonly byte[]                    _bytes              = new byte[2048];
        private readonly byte[]                    _connectionBytes    = new byte[200];

        private int                                _port;
        private bool                               _isServer           = false;
        private Dictionary<NetPeer, LNLConnection> _clients            = new Dictionary<NetPeer, LNLConnection>();
        private Queue<LNLConnection>               _freeClients        = new Queue<LNLConnection>();

        // LAN Matchmaking
        private List<Session>                      _sessions           = new List<Session>();
        private NetDataWriter                      _writer             = new NetDataWriter();
        private string                             _machineName;

        public override void Init()
        {
            _netManager  = new NetManager((INetEventListener)this) { AutoRecycle = true };
            _machineName = Environment.MachineName;

            for (int i = 0; i < Sandbox.Config.GetMaxPlayers; i++)
                _freeClients.Enqueue(new LNLConnection());
        }

        public override void PollEvents()
        {
            _netManager.PollEvents();
        }

        public override void ForceUpdate()
        {
            _netManager.TriggerUpdate();
        }

        public override void Run(RunMode mode, int port)
        {
            if (mode == RunMode.Client)
            {
                _netManager.UnconnectedMessagesEnabled = true;
                _netManager.Start();
                _isServer = false;
            }

            else
            {
                _netManager.BroadcastReceiveEnabled = true;
                _netManager.Start(port);
                _isServer = true;
            }

            _port = port;
        }

        public override void Shutdown()
        {
            _netManager.Stop();
        }

        public override void Connect(string address, int port, byte[] connectionData, int connectionDataLen)
        {
            if (!_netManager.IsRunning)
                _netManager.Start();

            if (connectionData == null)
            {
                _netManager.Connect(address, port, "");
            }
            else
            {
                _writer.Reset();
                _writer.Put(connectionData, 0, connectionDataLen);
                _netManager.Connect(address, port, _writer);
            }

         
        }

        public override void Disconnect(NetickConnection connection)
        {
            _netManager.DisconnectPeer(((LNLConnection)connection).LNLPeer);
        }

        public override void HostMatch(string name) 
        {

        }

        public override void UpdateMatchList()
        {
            if (!_netManager.IsRunning)
                _netManager.Start();

            _sessions.Clear();
            _writer.Reset();
            _writer.Put(NetickConfig.LAN_DISCOVERY);
            _netManager.SendBroadcast(_writer, _port);
        }

        /// ////////////////////////////////////////////

        void INetEventListener.OnConnectionRequest(ConnectionRequest request)
        {
            int len = request.Data.AvailableBytes;
            request.Data.GetBytes(_connectionBytes, 0, len);
            bool accepted = NetworkPeer.OnConnectRequest(_connectionBytes, len, request.RemoteEndPoint);

            if (accepted)
                request.Accept();
            else
                request.Reject();
        }

        void INetEventListener.OnPeerConnected(NetPeer peer)
        {
            var connection      = _freeClients.Dequeue();
            connection.LNLPeer  = peer;

            _clients.   Add(peer, connection);
            NetworkPeer.OnConnected(connection);
        }

        void INetEventListener.OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            if (!_isServer)
            {
                if (disconnectInfo.Reason == DisconnectReason.ConnectionRejected || disconnectInfo.Reason == DisconnectReason.ConnectionFailed)
                {
                    NetworkPeer.OnConnectFailed(ConnectionFailedReason.Refused);
                    return;
                }

                if (peer == null)
                {
                    Debug.Log($"ERROR: {disconnectInfo.Reason}");
                    NetworkPeer.OnConnectFailed(ConnectionFailedReason.Refused);
                    return;
                }

            }

            if (peer == null)
            {
                return;
            }

            if (_clients.ContainsKey(peer))
            {
                NetworkPeer.OnDisconnected(_clients[peer]);
                _freeClients.Enqueue(_clients[peer]);
                _clients.Remove(peer);
            }
        }

        void INetEventListener.OnNetworkReceive(NetPeer peer, NetPacketReader reader, DeliveryMethod deliveryMethod)
        {
            if (_clients.TryGetValue(peer, out var client))
            {
                var len = reader.AvailableBytes;
                reader.GetBytes(_bytes, 0, reader.AvailableBytes);
                NetworkPeer.Receive(_clients[peer], _bytes, len);
            }
        }

        void INetEventListener.OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
        {
            ulong msgType = reader.GetULong();

            if (msgType == NetickConfig.LAN_DISCOVERY_RESPONSE)
            {
                string name = reader.GetString();
                int    port = reader.GetInt();

                var newSession = new Session() 
                { 
                    Name = name, 
                    IP   = remoteEndPoint.Address.ToString() ,
                    Port = port   
                };

                if (!_sessions.Contains(newSession))
                    _sessions.Add(newSession);

                OnMatchListUpdate(_sessions);
            }

            else if (_isServer && msgType == NetickConfig.LAN_DISCOVERY)
            {
                _writer.Reset();
                _writer.Put(NetickConfig.LAN_DISCOVERY_RESPONSE);
                _writer.Put(_machineName);
                _writer.Put(_port);

                _netManager.SendUnconnectedMessage(_writer, remoteEndPoint);
            }
        }


        void INetEventListener.OnNetworkError(IPEndPoint endPoint, SocketError socketError)
        {
            Debug.Log("[S] NetworkError: " + socketError);
            NetworkPeer.OnConnectFailed(ConnectionFailedReason.Refused);
        }

        void INetEventListener.OnNetworkLatencyUpdate(NetPeer peer, int latency)
        {
        }



    }


}

