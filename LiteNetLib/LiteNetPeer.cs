#if DEBUG
#define STATS_ENABLED
#endif
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using LiteNetLib.Utils;

namespace LiteNetLib
{
    /// <summary>
    /// Peer connection state
    /// </summary>
    [Flags]
    public enum ConnectionState : byte
    {
        // 主动发起的连接请求（正在尝试连接对方，但握手尚未完成）
        Outgoing         = 1 << 1,
        // 握手成功，连接已建立
        Connected         = 1 << 2,
        // 逻辑层已经调用了断开连接的方法，但协议栈可能还在处理最后的残留数据包
        ShutdownRequested = 1 << 3,
        // 连接已彻底关闭
        Disconnected      = 1 << 4,
        // 表示连接的物理地址（IP 或端口）正在发生变化
        EndPointChange    = 1 << 5,
        Any = Outgoing | Connected | ShutdownRequested | EndPointChange
    }

    internal enum ConnectRequestResult
    {
        None,
        P2PLose, //when peer connecting
        Reconnection,  //when peer was connected
        NewConnection  //when peer was disconnected
    }

    internal enum DisconnectResult
    {
        None,
        Reject,
        Disconnect
    }

    internal enum ShutdownResult
    {
        None,
        Success,
        WasConnected
    }

    /// <summary>
    /// Network peer. Main purpose is sending messages to specific peer.
    /// </summary>
    public class LiteNetPeer : IPEndPoint
    {
        //Ping and RTT
        private int _rtt;
        private int _avgRtt;
        private int _rttCount;
        private float _resendDelay = 27.0f;
        private float _pingSendTimer;
        private float _rttResetTimer;
        private readonly Stopwatch _pingTimer = new Stopwatch();
        private float _timeSinceLastPacket;
        private long _remoteDelta;

        //Common
        private readonly object _shutdownLock = new object();

        internal volatile LiteNetPeer NextPeer;
        internal LiteNetPeer PrevPeer;


        // 当一个 LiteNetPeer 被创建或重新激活时，它会被分配一个 ConnectionNum
        // 对于同一个远程端点（IP:Port），每一代新的连接，这个数字都会递增
        // 如果收到的包里的数字和本地记录的对不上，说明这是上一代连接遗留下来的僵尸包，会直接被丢弃
        internal byte ConnectionNum
        {
            get => _connectNum;
            private set
            {
                _connectNum = value;
                _mergeData.ConnectionNumber = value;
                _pingPacket.ConnectionNumber = value;
                _pongPacket.ConnectionNumber = value;
            }
        }

        // Channels
        // 无序的通道没有自己的类
        private NetPacket[] _unreliableSecondQueue;
        private NetPacket[] _unreliableChannel;
        private int _unreliablePendingCount;
        private readonly object _unreliableChannelLock = new object();

        //MTU
        private int _mtu;
        private int _mtuIdx;
        private bool _finishMtu;
        private float _mtuCheckTimer;
        private int _mtuCheckAttempts;
        private const int MtuCheckDelay = 1000;
        private const int MaxMtuCheckAttempts = 4;
        private readonly object _mtuMutex = new object();

        //Fragment
        private class IncomingFragments
        {
            public NetPacket[] Fragments;
            public int ReceivedCount;
            public int TotalSize;
            public byte ChannelId;
        }
        private int _fragmentId;
        // Fragment总序号，Fragment集合
        private readonly Dictionary<ushort, IncomingFragments> _holdedFragments;
        private readonly Dictionary<ushort, ushort> _deliveredFragments;

        //Merging
        private readonly NetPacket _mergeData;
        private int _mergePos;
        private int _mergeCount;

        //Connection
        private int _connectAttempts;
        private float _connectTimer;
        private long _connectTime;
        private byte _connectNum;
        private ConnectionState _connectionState;
        private NetPacket _shutdownPacket;
        private const int ShutdownDelay = 300;
        private float _shutdownTimer;
        private readonly NetPacket _pingPacket;
        private readonly NetPacket _pongPacket;
        private readonly NetPacket _connectRequestPacket;
        private readonly NetPacket _connectAcceptPacket;

        /// <summary>
        /// Peer parent NetManager
        /// </summary>
        public readonly LiteNetManager NetManager;

        /// <summary>
        /// Current connection state
        /// </summary>
        public ConnectionState ConnectionState => _connectionState;

        /// <summary>
        /// Connection time for internal purposes
        /// </summary>
        internal long ConnectTime => _connectTime;

        /// <summary>
        /// Peer id can be used as key in your dictionary of peers
        /// </summary>
        public readonly int Id;

        /// <summary>
        /// Id assigned from server
        /// </summary>
        public int RemoteId { get; private set; }

        /// <summary>
        /// Current one-way ping (RTT/2) in milliseconds
        /// </summary>
        public int Ping => _avgRtt/2;

        /// <summary>
        /// Round trip time in milliseconds
        /// </summary>
        public int RoundTripTime => _avgRtt;

        /// <summary>
        /// Current MTU - Maximum Transfer Unit ( maximum udp packet size without fragmentation )
        /// </summary>
        public int Mtu => _mtu;

        /// <summary>
        /// Delta with remote time in ticks (not accurate)
        /// positive - remote time > our time
        /// 时钟时差
        /// </summary>
        public long RemoteTimeDelta => _remoteDelta;

        /// <summary>
        /// Remote UTC time (not accurate)
        /// </summary>
        public DateTime RemoteUtcTime => new DateTime(DateTime.UtcNow.Ticks + _remoteDelta);

        /// <summary>
        /// Time since last packet received (including internal library packets) in milliseconds
        /// </summary>
        public float TimeSinceLastPacket => _timeSinceLastPacket;

        /// <summary>
        /// Fixed part of the resend delay
        /// </summary>
        public float ResendFixedDelay = 25.0f;

        /// <summary>
        /// Multiplication factor of Rtt in the resend delay calculation
        /// </summary>
        public float ResendRttMultiplier = 2.1f;

        internal float ResendDelay => _resendDelay;

        /// <summary>
        /// Application defined object containing data about the connection
        /// </summary>
        public object Tag;

        /// <summary>
        /// Statistics of peer connection
        /// </summary>
        public readonly NetStatistics Statistics;

        // sockaddr 结构体的包装
        private SocketAddress _cachedSocketAddr;
        private int _cachedHashCode;
        private ReliableChannel _reliableChannel;
        private ReliableChannel _reliableUnorderedChannel;
        private SequencedChannel _sequencedChannel;

        internal byte[] NativeAddress;

        protected virtual int ChannelsCount => 1;

        /// <summary>
        /// IPEndPoint serialize
        /// </summary>
        /// <returns>SocketAddress</returns>
        public override SocketAddress Serialize() =>
            _cachedSocketAddr;

        public override int GetHashCode() =>
            //uses SocketAddress hash in NET8 and IPEndPoint hash for NativeSockets and previous NET versions
            // 用于HashSet查找
            _cachedHashCode;

        //incoming connection constructor
        // 首先把传入的 remoteEndPoint 的 IP 和端口交给父类 IPEndPoint
        internal LiteNetPeer(LiteNetManager netManager, IPEndPoint remoteEndPoint, int id) : base(remoteEndPoint.Address, remoteEndPoint.Port)
        {
            Id = id;
            Statistics = new NetStatistics();
            NetManager = netManager;
            _cachedSocketAddr = base.Serialize();

            // 把序列化后的地址（sockaddr）拷进 NativeAddress 数组
            if (NetManager.UseNativeSockets)
            {
                NativeAddress = new byte[_cachedSocketAddr.Size];
                for (int i = 0; i < _cachedSocketAddr.Size; i++)
                    NativeAddress[i] = _cachedSocketAddr[i];
            }
#if NET8_0_OR_GREATER
            // _address.GetHashCode() ^ _port;
            // 1.拿 C# 的 IPAddress 对象（这本身就是一个复杂的托管对象）算一个哈希
            // 2.内核的 sockaddr 二进制数据进行哈希
            _cachedHashCode = NetManager.UseNativeSockets ? base.GetHashCode() : _cachedSocketAddr.GetHashCode();
#else
            _cachedHashCode = base.GetHashCode();
#endif

            ResetMtu();

            _connectionState = ConnectionState.Connected;
            _mergeData = new NetPacket(PacketProperty.Merged, NetConstants.MaxPacketSize);
            _pongPacket = new NetPacket(PacketProperty.Pong, 0);
            _pingPacket = new NetPacket(PacketProperty.Ping, 0) {Sequence = 1};

            _unreliableSecondQueue = new NetPacket[8];
            _unreliableChannel = new NetPacket[8];
            _holdedFragments = new Dictionary<ushort, IncomingFragments>();
            _deliveredFragments = new Dictionary<ushort, ushort>();
        }

        // 创建IP改动事件时调用
        internal void InitiateEndPointChange()
        {
            // 重设MTU
            ResetMtu();
            _connectionState = ConnectionState.EndPointChange;
        }

        // PoolEvents时调用
        internal void FinishEndPointChange(IPEndPoint newEndPoint)
        {
            if (_connectionState != ConnectionState.EndPointChange)
                return;
            _connectionState = ConnectionState.Connected;

            Address = newEndPoint.Address;
            Port = newEndPoint.Port;

            // new SocketAddress(Address, Port)
            _cachedSocketAddr = base.Serialize();
            if (NetManager.UseNativeSockets)
            {
                NativeAddress = new byte[_cachedSocketAddr.Size];
                for (int i = 0; i < _cachedSocketAddr.Size; i++)
                    NativeAddress[i] = _cachedSocketAddr[i];
            }
#if NET8_0_OR_GREATER
            _cachedHashCode = NetManager.UseNativeSockets ? base.GetHashCode() : _cachedSocketAddr.GetHashCode();
#else
            _cachedHashCode = base.GetHashCode();
#endif
        }

        internal void ResetMtu()
        {
            //finish if discovery disabled
            _finishMtu = !NetManager.MtuDiscovery;
            // 如果手动指定了大小
            if (NetManager.MtuOverride > 0)
                OverrideMtu(NetManager.MtuOverride);
            // 否则，从预设的最小宽度开始尝试
            else
                SetMtu(0);
        }

        private void SetMtu(int mtuIdx)
        {
            _mtuIdx = mtuIdx;
            _mtu = NetConstants.PossibleMtu[mtuIdx] - NetManager.ExtraPacketSizeForLayer;
        }

        private void OverrideMtu(int mtuValue)
        {
            _mtu = mtuValue;
            _finishMtu = true;
        }

        /// <summary>
        /// Returns packets count in queue for reliable channel 0
        /// </summary>
        /// <param name="ordered">type of channel ReliableOrdered or ReliableUnordered</param>
        /// <returns>packets count in channel queue</returns>
        public int GetPacketsCountInReliableQueue(bool ordered) =>
            _reliableChannel?.PacketsInQueue ?? 0;

        /// <summary>
        /// Create temporary packet (maximum size MTU - headerSize) to send later without additional copies
        /// </summary>
        /// <param name="deliveryMethod">Delivery method (reliable, unreliable, etc.)</param>
        /// <returns>PooledPacket that you can use to write data starting from UserDataOffset</returns>
        /// 这方法啥意思？
        public PooledPacket CreatePacketFromPool(DeliveryMethod deliveryMethod)
        {
            // multithreaded variable
            int mtu = _mtu;
            var packet = NetManager.PoolGetPacket(mtu);
            if (deliveryMethod == DeliveryMethod.Unreliable)
            {
                packet.Property = PacketProperty.Unreliable;
                return new PooledPacket(packet, mtu, 0);
            }
            else
            {
                packet.Property = PacketProperty.Channeled;
                return new PooledPacket(packet, mtu, (byte)deliveryMethod);
            }
        }

        /// <summary>
        /// Sends pooled packet without data copy
        /// </summary>
        /// <param name="packet">packet to send</param>
        /// <param name="userDataSize">size of user data you want to send</param>
        public void SendPooledPacket(PooledPacket packet, int userDataSize)
        {
            if (_connectionState != ConnectionState.Connected)
                return;
            packet._packet.Size = packet.UserDataOffset + userDataSize;
            if (packet._packet.Property == PacketProperty.Channeled)
            {
                CreateChannel(packet._channelNumber).AddToQueue(packet._packet);
            }
            else
            {
                lock (_unreliableChannelLock)
                {
                    if (_unreliablePendingCount == _unreliableChannel.Length)
                        Array.Resize(ref _unreliableChannel, _unreliablePendingCount*2);
                    _unreliableChannel[_unreliablePendingCount++] = packet._packet;
                }
            }
        }

        // 这里的实现只有三条
        internal virtual BaseChannel CreateChannel(byte channelNumber)
        {
            switch ((DeliveryMethod)channelNumber)
            {
                case DeliveryMethod.ReliableOrdered:
                case DeliveryMethod.ReliableSequenced:
                    return _reliableChannel ??= new ReliableChannel(this, true, (int)DeliveryMethod.ReliableOrdered);

                case DeliveryMethod.ReliableUnordered:
                    return _reliableUnorderedChannel ??= new ReliableChannel(this, false, channelNumber);

                case DeliveryMethod.Sequenced:
                    return _sequencedChannel ??= new SequencedChannel(this, true, channelNumber);

                default:
                    throw new Exception("Invalid channel type");
            }
        }

        //"Connect to" constructor
        // 客户端发起时对应的构造函数，这里会直接发送请求握手包
        // connectData一般是密钥
        internal LiteNetPeer(LiteNetManager netManager, IPEndPoint remoteEndPoint, int id, byte connectNum, ReadOnlySpan<byte> connectData)
            : this(netManager, remoteEndPoint, id)
        {
            _connectTime = DateTime.UtcNow.Ticks;
            _connectionState = ConnectionState.Outgoing;
            ConnectionNum = connectNum;

            //Make initial packet
            // Serialize后返回SocketAddress类型实例
            _connectRequestPacket = NetConnectRequestPacket.Make(connectData, remoteEndPoint.Serialize(), _connectTime, id);
            _connectRequestPacket.ConnectionNumber = connectNum;

            //Send request
            NetManager.SendRaw(_connectRequestPacket, this);

            NetDebug.Write(NetLogLevel.Trace, $"[CC] ConnectId: {_connectTime}, ConnectNum: {connectNum}");
        }

        //"Accept" incoming constructor
        // 这个ID是本地分配的（不是远端传过来的，现在是要发ACK）
        internal LiteNetPeer(LiteNetManager netManager, ConnectionRequest request, int id)
            : this(netManager, request.RemoteEndPoint, id)
        {
            _connectTime = request.InternalPacket.ConnectionTime;
            ConnectionNum = request.InternalPacket.ConnectionNumber;
            RemoteId = request.InternalPacket.PeerId;

            //Make initial packet
            _connectAcceptPacket = NetConnectAcceptPacket.Make(_connectTime, ConnectionNum, id);

            //Make Connected
            _connectionState = ConnectionState.Connected;

            //Send
            NetManager.SendRaw(_connectAcceptPacket, this);

            NetDebug.Write(NetLogLevel.Trace, $"[CC] ConnectId: {_connectTime}");
        }

        //Reject？？？
        internal void Reject(NetConnectRequestPacket requestData, byte[] data, int start, int length)
        {
            _connectTime = requestData.ConnectionTime;
            _connectNum = requestData.ConnectionNumber;
            Shutdown(data, start, length, false);
        }

        internal bool ProcessConnectAccept(NetConnectAcceptPacket packet)
        {
            // 如果并非正在申请而受到链接ACK，不要
            if (_connectionState != ConnectionState.Outgoing)
                return false;

            //check connection id
            if (packet.ConnectionTime != _connectTime)
            {
                NetDebug.Write(NetLogLevel.Trace, $"[NC] Invalid connectId: {packet.ConnectionTime} != our({_connectTime})");
                return false;
            }
            //check connect num
            ConnectionNum = packet.ConnectionNumber;
            RemoteId = packet.PeerId;

            NetDebug.Write(NetLogLevel.Trace, "[NC] Received connection accept");
            Interlocked.Exchange(ref _timeSinceLastPacket, 0);
            _connectionState = ConnectionState.Connected;
            return true;
        }

        /// <summary>
        /// Gets maximum size of packet that will be not fragmented.
        /// </summary>
        /// <param name="options">Type of packet that you want send</param>
        /// <returns>size in bytes</returns>
        public int GetMaxSinglePacketSize(DeliveryMethod options) =>
            _mtu - NetPacket.GetHeaderSize(options == DeliveryMethod.Unreliable ? PacketProperty.Unreliable : PacketProperty.Channeled);

        /// <summary>
        /// Send data to peer with delivery event called
        /// </summary>
        /// <param name="data">Data</param>
        /// <param name="deliveryMethod">Delivery method (reliable, unreliable, etc.)</param>
        /// <param name="userData">User data that will be received in DeliveryEvent</param>
        /// <exception cref="ArgumentException">
        ///     If you trying to send unreliable packet type<para/>
        /// </exception>
        public void SendWithDeliveryEvent(byte[] data, DeliveryMethod deliveryMethod, object userData)
        {
            if (deliveryMethod != DeliveryMethod.ReliableOrdered && deliveryMethod != DeliveryMethod.ReliableUnordered)
                throw new ArgumentException("Delivery event will work only for ReliableOrdered/Unordered packets");
            SendInternal(new ReadOnlySpan<byte>(data, 0, data.Length), 0, deliveryMethod, userData);
        }

        /// <summary>
        /// Send data to peer with delivery event called
        /// </summary>
        /// <param name="data">Data</param>
        /// <param name="start">Start of data</param>
        /// <param name="length">Length of data</param>
        /// <param name="deliveryMethod">Delivery method (reliable, unreliable, etc.)</param>
        /// <param name="userData">User data that will be received in DeliveryEvent</param>
        /// <exception cref="ArgumentException">
        ///     If you trying to send unreliable packet type<para/>
        /// </exception>
        public void SendWithDeliveryEvent(byte[] data, int start, int length, DeliveryMethod deliveryMethod, object userData)
        {
            if (deliveryMethod != DeliveryMethod.ReliableOrdered && deliveryMethod != DeliveryMethod.ReliableUnordered)
                throw new ArgumentException("Delivery event will work only for ReliableOrdered/Unordered packets");
            SendInternal(new ReadOnlySpan<byte>(data, start, length), 0, deliveryMethod, userData);
        }

        /// <summary>
        /// Send data to peer with delivery event called
        /// </summary>
        /// <param name="dataWriter">Data</param>
        /// <param name="deliveryMethod">Delivery method (reliable, unreliable, etc.)</param>
        /// <param name="userData">User data that will be received in DeliveryEvent</param>
        /// <exception cref="ArgumentException">
        ///     If you trying to send unreliable packet type<para/>
        /// </exception>
        public void SendWithDeliveryEvent(NetDataWriter dataWriter, DeliveryMethod deliveryMethod, object userData)
        {
            if (deliveryMethod != DeliveryMethod.ReliableOrdered && deliveryMethod != DeliveryMethod.ReliableUnordered)
                throw new ArgumentException("Delivery event will work only for ReliableOrdered/Unordered packets");
            SendInternal(dataWriter.AsReadOnlySpan(), 0, deliveryMethod, userData);
        }

        /// <summary>
        /// Send data to peer (channel - 0)
        /// </summary>
        /// <param name="data">Data</param>
        /// <param name="deliveryMethod">Send options (reliable, unreliable, etc.)</param>
        /// <exception cref="TooBigPacketException">
        ///     If size exceeds maximum limit:<para/>
        ///     MTU - headerSize bytes for Unreliable<para/>
        ///     Fragment count exceeded ushort.MaxValue<para/>
        /// </exception>
        public void Send(byte[] data, DeliveryMethod deliveryMethod) =>
            SendInternal(new ReadOnlySpan<byte>(data, 0, data.Length), 0, deliveryMethod, null);

        /// <summary>
        /// Send data to peer (channel - 0)
        /// </summary>
        /// <param name="dataWriter">DataWriter with data</param>
        /// <param name="deliveryMethod">Send options (reliable, unreliable, etc.)</param>
        /// <exception cref="TooBigPacketException">
        ///     If size exceeds maximum limit:<para/>
        ///     MTU - headerSize bytes for Unreliable<para/>
        ///     Fragment count exceeded ushort.MaxValue<para/>
        /// </exception>
        public void Send(NetDataWriter dataWriter, DeliveryMethod deliveryMethod) =>
            SendInternal(dataWriter.AsReadOnlySpan(), 0, deliveryMethod, null);

        /// <summary>
        /// Send data to peer (channel - 0)
        /// </summary>
        /// <param name="data">Data</param>
        /// <param name="start">Start of data</param>
        /// <param name="length">Length of data</param>
        /// <param name="options">Send options (reliable, unreliable, etc.)</param>
        /// <exception cref="TooBigPacketException">
        ///     If size exceeds maximum limit:<para/>
        ///     MTU - headerSize bytes for Unreliable<para/>
        ///     Fragment count exceeded ushort.MaxValue<para/>
        /// </exception>
        public void Send(byte[] data, int start, int length, DeliveryMethod options) =>
            SendInternal(new ReadOnlySpan<byte>(data, start, length), 0, options, null);

        /// <summary>
        /// Send data to peer
        /// </summary>
        /// <param name="data">Data</param>
        /// <param name="start">Start of data</param>
        /// <param name="length">Length of data</param>
        /// <param name="channelNumber">Number of channel (from 0 to channelsCount - 1)</param>
        /// <param name="deliveryMethod">Delivery method (reliable, unreliable, etc.)</param>
        /// <exception cref="TooBigPacketException">
        ///     If size exceeds maximum limit:<para/>
        ///     MTU - headerSize bytes for Unreliable<para/>
        ///     Fragment count exceeded ushort.MaxValue<para/>
        /// </exception>
        public void Send(byte[] data, int start, int length, byte channelNumber, DeliveryMethod deliveryMethod) =>
            SendInternal(new ReadOnlySpan<byte>(data, start, length), channelNumber, deliveryMethod, null);

        /// <summary>
        /// Send data to peer with delivery event called
        /// </summary>
        /// <param name="data">Data</param>
        /// <param name="deliveryMethod">Delivery method (reliable, unreliable, etc.)</param>
        /// <param name="userData">User data that will be received in DeliveryEvent</param>
        /// <exception cref="ArgumentException">
        ///     If you trying to send unreliable packet type<para/>
        /// </exception>
        public void SendWithDeliveryEvent(ReadOnlySpan<byte> data, DeliveryMethod deliveryMethod, object userData)
        {
            if (deliveryMethod != DeliveryMethod.ReliableOrdered && deliveryMethod != DeliveryMethod.ReliableUnordered)
                throw new ArgumentException("Delivery event will work only for ReliableOrdered/Unordered packets");
            SendInternal(data, 0, deliveryMethod, userData);
        }

        /// <summary>
        /// Send data to peer (channel - 0)
        /// </summary>
        /// <param name="data">Data（这是没有报头的数据）</param>
        /// <param name="deliveryMethod">Send options (reliable, unreliable, etc.)</param>
        /// <exception cref="TooBigPacketException">
        ///     If size exceeds maximum limit:<para/>
        ///     MTU - headerSize bytes for Unreliable<para/>
        ///     Fragment count exceeded ushort.MaxValue<para/>
        /// </exception>
        public void Send(ReadOnlySpan<byte> data, DeliveryMethod deliveryMethod) =>
            SendInternal(data, 0, deliveryMethod, null);

        // 专门给群发准备的
        protected void SendInternal(
            ReadOnlySpan<byte> data,
            byte channelNumber,
            DeliveryMethod deliveryMethod,
            object userData)
        {
            if (_connectionState != ConnectionState.Connected || channelNumber >= ChannelsCount)
                return;

            //Select channel
            PacketProperty property;
            BaseChannel channel = null;

            if (deliveryMethod == DeliveryMethod.Unreliable)
            {
                property = PacketProperty.Unreliable;
            }
            else
            {
                property = PacketProperty.Channeled;
                // 这里不走本类，而是覆写版本的CreateChannel
                channel = CreateChannel((byte)(channelNumber * NetConstants.ChannelTypeCount + (byte)deliveryMethod));
            }

            //Prepare
            NetDebug.Write("[RS]Packet: " + property);

            //Check fragmentation
            int headerSize = NetPacket.GetHeaderSize(property);
            //Save mtu for multithread
            int mtu = _mtu;
            // 这里的data没有报头，纯数据
            int length = data.Length;
            if (length + headerSize > mtu && channel != null)
            {
                //if cannot be fragmented
                // 只有可靠包被允许进行分片
                if (deliveryMethod != DeliveryMethod.ReliableOrdered && deliveryMethod != DeliveryMethod.ReliableUnordered)
                    throw new TooBigPacketException("Unreliable or ReliableSequenced packet size exceeded maximum of " + (mtu - headerSize) + " bytes, Check allowed size by GetMaxSinglePacketSize()");

                int packetFullSize = mtu - headerSize;
                // 单片最大
                int packetDataSize = packetFullSize - NetConstants.FragmentHeaderSize;
                int totalPackets = length / packetDataSize + (length % packetDataSize == 0 ? 0 : 1);

                if (totalPackets > NetManager.MaxFragmentsCount)
                    throw new TooBigPacketException("Data was split in " + totalPackets + " fragments, which exceeds " + NetManager.MaxFragmentsCount);

                ushort currentFragmentId = (ushort)Interlocked.Increment(ref _fragmentId);

                for (ushort partIdx = 0; partIdx < totalPackets; partIdx++)
                {
                    // sendLength是纯data的长度
                    int sendLength = length > packetDataSize ? packetDataSize : length;

                    NetPacket p = NetManager.PoolGetPacket(headerSize + sendLength + NetConstants.FragmentHeaderSize);
                    p.Property = property;
                    // 这是啥？反正不传出去
                    p.UserData = userData;
                    p.FragmentId = currentFragmentId;
                    p.FragmentPart = partIdx;
                    p.FragmentsTotal = (ushort)totalPackets;
                    p.MarkFragmented();

                    // 起始点为源数据的序号*单片大小，长度为sendLength，跳过Property + Sequence + FragmentID + PartIndex + TotalCount
                    data.Slice(partIdx * packetDataSize, sendLength).CopyTo(new Span<byte>(p.RawData, NetConstants.FragmentedHeaderTotalSize, sendLength));
                    // 并没有发送，而交给Channel管理
                    channel.AddToQueue(p);

                    length -= sendLength;
                }
                return;
            }

            //Else just send
            NetPacket packet = NetManager.PoolGetPacket(headerSize + length);
            packet.Property = property;
            data.CopyTo(new Span<byte>(packet.RawData, headerSize, length));
            packet.UserData = userData;

            if (channel == null) //unreliable
            {
                lock (_unreliableChannelLock)
                {
                    if (_unreliablePendingCount == _unreliableChannel.Length)
                        // 如果当前待发送的不可靠包太多了（超过了默认的 8 个），会以 2 倍的速度扩容数组
                        Array.Resize(ref _unreliableChannel, _unreliablePendingCount*2);
                    _unreliableChannel[_unreliablePendingCount++] = packet;
                }
            }
            else
            {
                channel.AddToQueue(packet);
            }
        }

        public void Disconnect(byte[] data) =>
            NetManager.DisconnectPeer(this, data);

        public void Disconnect(NetDataWriter writer) =>
            NetManager.DisconnectPeer(this, writer);

        public void Disconnect(byte[] data, int start, int count) =>
            NetManager.DisconnectPeer(this, data, start, count);

        public void Disconnect() =>
            NetManager.DisconnectPeer(this);

        internal DisconnectResult ProcessDisconnect(NetPacket packet)
        {
            if ((_connectionState == ConnectionState.Connected || _connectionState == ConnectionState.Outgoing) &&
                // 确认是DisConnect包
                packet.Size >= 9 &&
                // 时间戳（ConnectTime）匹配
                BitConverter.ToInt64(packet.RawData, 1) == _connectTime &&
                // 连接序号（ConnectionNumber）匹配
                packet.ConnectionNumber == _connectNum)
            {
                return _connectionState == ConnectionState.Connected
                    ? DisconnectResult.Disconnect
                    : DisconnectResult.Reject;
            }
            return DisconnectResult.None;
        }

        internal virtual void AddToReliableChannelSendQueue(BaseChannel channel)
        {

        }

        // DIsconnet和Shutdown时被调用
        internal ShutdownResult Shutdown(byte[] data, int start, int length, bool force)
        {
            lock (_shutdownLock)
            {
                //trying to shutdown already disconnected
                if (_connectionState == ConnectionState.Disconnected ||
                    _connectionState == ConnectionState.ShutdownRequested)
                {
                    return ShutdownResult.None;
                }

                var result = _connectionState == ConnectionState.Connected
                    // 活跃链接的断开
                    ? ShutdownResult.WasConnected
                    // 还在outgoing，直接断开
                    : ShutdownResult.Success;

                //don't send anything
                if (force)
                {
                    _connectionState = ConnectionState.Disconnected;
                    return result;
                }

                //reset time for reconnect protection
                Interlocked.Exchange(ref _timeSinceLastPacket, 0);

                //send shutdown packet（挥手包）
                _shutdownPacket = new NetPacket(PacketProperty.Disconnect, length) {ConnectionNumber = _connectNum};
                // 8个字节的_connectTime
                FastBitConverter.GetBytes(_shutdownPacket.RawData, 1, _connectTime);
                if (_shutdownPacket.Size >= _mtu)
                {
                    //Drop additional data
                    NetDebug.WriteError("[Peer] Disconnect additional data size more than MTU - 8!");
                }
                else if (data != null && length > 0)
                {
                    // 信息写在第九字节
                    Buffer.BlockCopy(data, start, _shutdownPacket.RawData, 9, length);
                }
                // 这里是唯一被设定关闭请求的地方
                _connectionState = ConnectionState.ShutdownRequested;
                NetDebug.Write("[Peer] Send disconnect");
                // 交由管理器发送
                NetManager.SendRaw(_shutdownPacket, this);
                return result;
            }
        }

        private void UpdateRoundTripTime(int roundTripTime)
        {
            // 记录了所有收到的 ACK 包带来的时延总和
            _rtt += roundTripTime;
            _rttCount++;
            // 平均往返时间
            _avgRtt = _rtt/_rttCount;
            _resendDelay = ResendFixedDelay + _avgRtt * ResendRttMultiplier;
        }

        // 在RealiableChannel的ProcessIncomingPacket被调用
        internal void AddReliablePacket(DeliveryMethod method, NetPacket p)
        {
            if (p.IsFragmented)
            {
                if (p.FragmentsTotal > NetManager.MaxFragmentsCount)
                {
                    NetManager.PoolRecycle(p);
                    return;
                }
                NetDebug.Write($"Fragment. Id: {p.FragmentId}, Part: {p.FragmentPart}, Total: {p.FragmentsTotal}");
                //Get needed array from dictionary
                ushort packetFragId = p.FragmentId;
                byte packetChannelId = p.ChannelId;
                // 还没有注册过这个分片ID
                if (!_holdedFragments.TryGetValue(packetFragId, out var incomingFragments))
                {
                    //Holded fragments limit reached
                    if (_holdedFragments.Count >= NetConstants.MaxFragmentsInWindow * ChannelsCount * NetConstants.FragmentedChannelsCount)
                    {
                        NetManager.PoolRecycle(p);
                        //NetDebug.WriteError($"Holded fragments limit reached ({_holdedFragments.Count}/{(NetConstants.DefaultWindowSize / 2) * ChannelsCount * NetConstants.FragmentedChannelsCount}). Dropping fragment id: {packetFragId}");
                        return;
                    }

                    incomingFragments = new IncomingFragments
                    {
                        Fragments = new NetPacket[p.FragmentsTotal],
                        ChannelId = p.ChannelId
                    };
                    _holdedFragments.Add(packetFragId, incomingFragments);
                }

                //Cache
                var fragments = incomingFragments.Fragments;

                //Error check
                // 1.当前序号超过声明数量
                // 2.分片重复
                // 3.当前包的ID与累计分片对应的ID不符
                // 则丢弃当前包
                if (p.FragmentPart >= fragments.Length ||
                    fragments[p.FragmentPart] != null ||
                    p.ChannelId != incomingFragments.ChannelId)
                {
                    NetManager.PoolRecycle(p);
                    NetDebug.WriteError("Invalid fragment packet");
                    return;
                }

                //Fill array
                fragments[p.FragmentPart] = p;

                //Increase received fragments count
                incomingFragments.ReceivedCount++;

                //Increase total size（纯Data）
                incomingFragments.TotalSize += p.Size - NetConstants.FragmentedHeaderTotalSize;

                //Check for finish（还没到）
                if (incomingFragments.ReceivedCount != fragments.Length)
                    return;

                //just simple packet
                NetPacket resultingPacket = NetManager.PoolGetPacket(incomingFragments.TotalSize);

                int pos = 0;
                for (int i = 0; i < incomingFragments.ReceivedCount; i++)
                {
                    var fragment = fragments[i];
                    // 内容长度
                    int writtenSize = fragment.Size - NetConstants.FragmentedHeaderTotalSize;

                    // 碎片加起来的总长度超过了最初预分配的大包长度
                    if (pos+writtenSize > resultingPacket.RawData.Length)
                    {
                        _holdedFragments.Remove(packetFragId);
                        NetDebug.WriteError($"Fragment error pos: {pos + writtenSize} >= resultPacketSize: {resultingPacket.RawData.Length} , totalSize: {incomingFragments.TotalSize}");
                        return;
                    }
                    // Size由于Rawdata在池内的扩充，一般size小于RawData
                    if (fragment.Size > fragment.RawData.Length)
                    {
                        _holdedFragments.Remove(packetFragId);
                        NetDebug.WriteError($"Fragment error size: {fragment.Size} > fragment.RawData.Length: {fragment.RawData.Length}");
                        return;
                    }

                    //Create resulting big packet
                    Buffer.BlockCopy(
                        fragment.RawData,
                        NetConstants.FragmentedHeaderTotalSize,
                        resultingPacket.RawData,
                        pos,
                        writtenSize);
                    pos += writtenSize;

                    //Free memory
                    NetManager.PoolRecycle(fragment);
                    fragments[i] = null;
                }

                //Clear memory
                _holdedFragments.Remove(packetFragId);

                //Send to process
                NetManager.CreateReceiveEvent(resultingPacket, method, (byte)(packetChannelId / NetConstants.ChannelTypeCount), 0, this);
            }
            else //Just simple packet
            {
                NetManager.CreateReceiveEvent(p, method, (byte)(p.ChannelId / NetConstants.ChannelTypeCount), NetConstants.ChanneledHeaderSize, this);
            }
        }

        // 在ProcessPacket中的MtuOk Case中被调用
        private void ProcessMtuPacket(NetPacket packet)
        {
            //header + int
            if (packet.Size < NetConstants.PossibleMtu[0])
                return;

            //first stage check (mtu check and mtu ok)
            int receivedMtu = BitConverter.ToInt32(packet.RawData, 1);
            int endMtuCheck = BitConverter.ToInt32(packet.RawData, packet.Size - 4);
            if (receivedMtu != packet.Size || receivedMtu != endMtuCheck || receivedMtu > NetConstants.MaxPacketSize)
            {
                NetDebug.WriteError($"[MTU] Broken packet. RMTU {receivedMtu}, EMTU {endMtuCheck}, PSIZE {packet.Size}");
                return;
            }

            // 直接把这个包改一下发回去
            if (packet.Property == PacketProperty.MtuCheck)
            {
                _mtuCheckAttempts = 0;
                NetDebug.Write("[MTU] check. send back: " + receivedMtu);
                packet.Property = PacketProperty.MtuOk;
                NetManager.SendRawAndRecycle(packet, this);
            }
            // 提高MTU
            else if(receivedMtu > _mtu && !_finishMtu) //MtuOk
            {
                //invalid packet
                // 如果不是期望的MTU就否决
                if (receivedMtu != NetConstants.PossibleMtu[_mtuIdx + 1] - NetManager.ExtraPacketSizeForLayer)
                    return;

                lock (_mtuMutex)
                {
                    SetMtu(_mtuIdx+1);
                }
                //if maxed - finish.
                if (_mtuIdx == NetConstants.PossibleMtu.Length - 1)
                    _finishMtu = true;
                NetManager.PoolRecycle(packet);
                NetDebug.Write("[MTU] ok. Increase to: " + _mtu);
            }
        }

        // 传一个长度为newMtu，收尾包含MTU（4字节）长度信息的报文
        private void UpdateMtuLogic(float deltaTime)
        {
            if (_finishMtu)
                return;

            _mtuCheckTimer += deltaTime;
            if (_mtuCheckTimer < MtuCheckDelay)
                return;

            _mtuCheckTimer = 0;
            _mtuCheckAttempts++;
            if (_mtuCheckAttempts >= MaxMtuCheckAttempts)
            {
                _finishMtu = true;
                return;
            }

            lock (_mtuMutex)
            {
                if (_mtuIdx >= NetConstants.PossibleMtu.Length - 1)
                    return;

                //Send increased packet
                int newMtu = NetConstants.PossibleMtu[_mtuIdx + 1] - NetManager.ExtraPacketSizeForLayer;
                var p = NetManager.PoolGetPacket(newMtu);
                p.Property = PacketProperty.MtuCheck;
                FastBitConverter.GetBytes(p.RawData, 1, newMtu);         //place into start
                FastBitConverter.GetBytes(p.RawData, p.Size - 4, newMtu);//and end of packet

                //Must check result for MTU fix（发送失败就结束）
                if (NetManager.SendRawAndRecycle(p, this) <= 0)
                    _finishMtu = true;
            }
        }

        //  LM.HandleMessageReceived-》LM.ProcessConnectRequest
        internal ConnectRequestResult ProcessConnectRequest(NetConnectRequestPacket connRequest)
        {
            //current or new request
            switch (_connectionState)
            {
                // 两个Peer同时向对方发起连接
                //P2P case
                case ConnectionState.Outgoing:
                    //fast check，时间戳小的赢
                    if (connRequest.ConnectionTime < _connectTime)
                    {
                        return ConnectRequestResult.P2PLose;
                    }
                    //slow rare case check，时间一致，比较地址
                    if (connRequest.ConnectionTime == _connectTime)
                    {
                        var localBytes = connRequest.TargetAddress;
                        for (int i = _cachedSocketAddr.Size-1; i >= 0; i--)
                        {
                            byte rb = _cachedSocketAddr[i];
                            if (rb == localBytes[i])
                                continue;
                            if (rb < localBytes[i])
                                return ConnectRequestResult.P2PLose;
                        }
                    }
                    break;
                case ConnectionState.Connected:
                    //Old connect request
                    if (connRequest.ConnectionTime == _connectTime)
                    {
                        //just reply accept
                        NetManager.SendRaw(_connectAcceptPacket, this);
                    }
                    //New connect request
                    else if (connRequest.ConnectionTime > _connectTime)
                    {
                        return ConnectRequestResult.Reconnection;
                    }
                    break;

                case ConnectionState.Disconnected:
                case ConnectionState.ShutdownRequested:
                    // 只要请求的时间戳不比旧的小
                    if (connRequest.ConnectionTime >= _connectTime)
                        return ConnectRequestResult.NewConnection;
                    break;
            }
            return ConnectRequestResult.None;
        }

        // ？？
        // 在HandleReceived-》本类的ProcessPacket-》ProcessChannel中被调用
        internal virtual void ProcessChanneled(NetPacket packet)
        {
            if (packet.ChannelId >= NetConstants.ChannelTypeCount ||
                packet.ChannelId == (int)DeliveryMethod.ReliableSequenced)
            {
                NetManager.PoolRecycle(packet);
                return;
            }

            if (!CreateChannel(packet.ChannelId).ProcessPacket(packet))
                NetManager.PoolRecycle(packet);
        }

        //Process incoming packet
        // HandleMessageReceived中被调用
        internal void ProcessPacket(NetPacket packet)
        {
            //not initialized，Peer当前还未开启
            if (_connectionState == ConnectionState.Outgoing || _connectionState == ConnectionState.Disconnected)
            {
                NetManager.PoolRecycle(packet);
                return;
            }
            // 已经发出过断线请求，确认断线
            if (packet.Property == PacketProperty.ShutdownOk)
            {
                if (_connectionState == ConnectionState.ShutdownRequested)
                    _connectionState = ConnectionState.Disconnected;
                NetManager.PoolRecycle(packet);
                return;
            }
            // 上一次链接的旧包
            if (packet.ConnectionNumber != _connectNum)
            {
                NetDebug.Write(NetLogLevel.Trace, "[RR]Old packet");
                NetManager.PoolRecycle(packet);
                return;
            }
            Interlocked.Exchange(ref _timeSinceLastPacket, 0);

            NetDebug.Write($"[RR]PacketProperty: {packet.Property}");
            switch (packet.Property)
            {
                // ？？？？？？？？？？
                case PacketProperty.Merged:
                    int pos = NetConstants.HeaderSize;
                    while (pos < packet.Size)
                    {
                        ushort size = BitConverter.ToUInt16(packet.RawData, pos);
                        if (size == 0)
                            break;

                        pos += 2;
                        if (packet.RawData.Length - pos < size)
                            break;

                        NetPacket mergedPacket = NetManager.PoolGetPacket(size);
                        Buffer.BlockCopy(packet.RawData, pos, mergedPacket.RawData, 0, size);
                        mergedPacket.Size = size;

                        if (!mergedPacket.Verify())
                            break;

                        pos += size;
                        ProcessPacket(mergedPacket);
                    }
                    NetManager.PoolRecycle(packet);
                    break;
                //If we get ping, send pong
                case PacketProperty.Ping:
                    // pong的seq一开始是0
                    if (NetUtils.RelativeSequenceNumber(packet.Sequence, _pongPacket.Sequence) > 0)
                    {
                        NetDebug.Write("[PP]Ping receive, send pong");
                        FastBitConverter.GetBytes(_pongPacket.RawData, 3, DateTime.UtcNow.Ticks);
                        _pongPacket.Sequence = packet.Sequence;
                        NetManager.SendRaw(_pongPacket, this);
                    }
                    NetManager.PoolRecycle(packet);
                    break;

                //If we get pong, calculate ping time and rtt
                case PacketProperty.Pong:
                    // 要检查是不是一对
                    if (packet.Sequence == _pingPacket.Sequence)
                    {
                        _pingTimer.Stop();
                        int elapsedMs = (int)_pingTimer.ElapsedMilliseconds;
                        _remoteDelta = BitConverter.ToInt64(packet.RawData, 3) + (elapsedMs * TimeSpan.TicksPerMillisecond ) / 2 - DateTime.UtcNow.Ticks;
                        UpdateRoundTripTime(elapsedMs);
                        NetManager.ConnectionLatencyUpdated(this, elapsedMs / 2);
                        NetDebug.Write($"[PP]Ping: {packet.Sequence} - {elapsedMs} - {_remoteDelta}");
                    }
                    NetManager.PoolRecycle(packet);
                    break;

                case PacketProperty.Ack:
                case PacketProperty.Channeled:
                case PacketProperty.ReliableMerged:
                    ProcessChanneled(packet);
                    break;

                //Simple packet without acks
                case PacketProperty.Unreliable:
                    NetManager.CreateReceiveEvent(packet, DeliveryMethod.Unreliable, 0, NetConstants.HeaderSize, this);
                    return;

                case PacketProperty.MtuCheck:
                case PacketProperty.MtuOk:
                    ProcessMtuPacket(packet);
                    break;

                default:
                    NetDebug.WriteError("Error! Unexpected packet type: " + packet.Property);
                    break;
            }
        }

        // 把来自不同频道、不同属性（比如一个可靠包 + 一个 Ping 包 + 一个不可靠包）的东西塞进同一个 UDP 数据包发走
        private void SendMerged()
        {
            if (_mergeCount == 0)
                return;
            int bytesSent;
            if (_mergeCount > 1)
            {
                NetDebug.Write("[P]Send merged: " + _mergePos + ", count: " + _mergeCount);
                bytesSent = NetManager.SendRaw(_mergeData.RawData, 0, NetConstants.HeaderSize + _mergePos, this);
            }
            else
            {
                //Send without length information and merging
                bytesSent = NetManager.SendRaw(_mergeData.RawData, NetConstants.HeaderSize + 2, _mergePos - 2, this);
            }

            if (NetManager.EnableStatistics)
            {
                Statistics.IncrementPacketsSent();
                Statistics.AddBytesSent(bytesSent);
            }

            _mergePos = 0;
            _mergeCount = 0;
        }


        // 在Reliable层以上更细致的合并
        // 在Channel中的SendNextPacket被调用
        // 在Peer的非可靠通道中发送
        internal void SendUserData(NetPacket packet)
        {
            packet.ConnectionNumber = _connectNum;
            int mergedPacketSize = NetConstants.HeaderSize + packet.Size + 2;
            const int sizeTreshold = 20;
            if (mergedPacketSize + sizeTreshold >= _mtu)
            {
                NetDebug.Write(NetLogLevel.Trace, "[P]SendingPacket: " + packet.Property);
                int bytesSent = NetManager.SendRaw(packet, this);

                if (NetManager.EnableStatistics)
                {
                    Statistics.IncrementPacketsSent();
                    Statistics.AddBytesSent(bytesSent);
                }

                return;
            }
            if (_mergePos + mergedPacketSize > _mtu)
                SendMerged();

            FastBitConverter.GetBytes(_mergeData.RawData, _mergePos + NetConstants.HeaderSize, (ushort)packet.Size);
            Buffer.BlockCopy(packet.RawData, 0, _mergeData.RawData, _mergePos + NetConstants.HeaderSize + 2, packet.Size);
            _mergePos += packet.Size + 2;
            // 唯一++的地方
            _mergeCount++;
            //DebugWriteForce("Merged: " + _mergePos + "/" + (_mtu - 2) + ", count: " + _mergeCount);
        }

        // 更新频道
        protected virtual void UpdateChannels()
        {
            _reliableChannel?.SendNextPackets();
            _reliableUnorderedChannel?.SendNextPackets();
            _sequencedChannel?.SendNextPackets();
        }

        // 在Mgr的Update中调用
        internal void Update(float deltaTime)
        {
            Interlocked.Exchange(ref _timeSinceLastPacket, _timeSinceLastPacket + deltaTime);
            switch (_connectionState)
            {
                case ConnectionState.Connected:
                    // 超过一定时间没有收到任何包
                    if (_timeSinceLastPacket > NetManager.DisconnectTimeout)
                    {
                        NetDebug.Write($"[UPDATE] Disconnect by timeout: {_timeSinceLastPacket} > {NetManager.DisconnectTimeout}");
                        NetManager.DisconnectPeerForce(this, DisconnectReason.Timeout, 0, null);
                        return;
                    }
                    break;
                    // 超过一定时间没有收到任何包
                    // 这个状态在shutdown中被调用
                case ConnectionState.ShutdownRequested:
                    if (_timeSinceLastPacket > NetManager.DisconnectTimeout)
                    {
                        _connectionState = ConnectionState.Disconnected;
                    }
                    // shutdown计时，超出计时发出shutdown请求包（这里是补偿性挥手，shutdown发出第一次，这里补偿性重传）
                    else
                    {
                        _shutdownTimer += deltaTime;
                        if (_shutdownTimer >= ShutdownDelay)
                        {
                            _shutdownTimer = 0;
                            NetManager.SendRaw(_shutdownPacket, this);
                        }
                    }
                    return;

                case ConnectionState.Outgoing:
                    _connectTimer += deltaTime;
                    // 超过等待时间尝试次数+1，发从握手包，超出尝试限制直接关闭自己的连接
                    if (_connectTimer > NetManager.ReconnectDelay)
                    {
                        _connectTimer = 0;
                        _connectAttempts++;
                        if (_connectAttempts > NetManager.MaxConnectAttempts)
                        {
                            NetManager.DisconnectPeerForce(this, DisconnectReason.ConnectionFailed, 0, null);
                            return;
                        }

                        //else send connect again
                        NetManager.SendRaw(_connectRequestPacket, this);
                    }
                    return;

                case ConnectionState.Disconnected:
                    return;
            }

            //Send ping，定期发送Ping包
            _pingSendTimer += deltaTime;
            if (_pingSendTimer >= NetManager.PingInterval)
            {
                NetDebug.Write("[PP] Send ping...");
                //reset timer
                _pingSendTimer = 0;
                //send ping
                _pingPacket.Sequence++;
                //ping timeout（ping的超时补偿，pong没收到，直接填入当前的_pingTimer）
                if (_pingTimer.IsRunning)
                    UpdateRoundTripTime((int)_pingTimer.ElapsedMilliseconds);
                _pingTimer.Restart();
                NetManager.SendRaw(_pingPacket, this);
            }

            //RTT - round trip time
            _rttResetTimer += deltaTime;
            // 每隔 3 个 Ping 周期，强制将当前的“历史平均值”变成“唯一的样本”
            if (_rttResetTimer >= NetManager.PingInterval * 3)
            {
                _rttResetTimer = 0;
                _rtt = _avgRtt;
                _rttCount = 1;
            }

            UpdateMtuLogic(deltaTime);

            UpdateChannels();

            if (_unreliablePendingCount > 0)
            {
                int unreliableCount;
                lock (_unreliableChannelLock)
                {
                    (_unreliableChannel, _unreliableSecondQueue) = (_unreliableSecondQueue, _unreliableChannel);
                    unreliableCount = _unreliablePendingCount;
                    _unreliablePendingCount = 0;
                }
                for (int i = 0; i < unreliableCount; i++)
                {
                    var packet = _unreliableSecondQueue[i];
                    SendUserData(packet);
                    NetManager.PoolRecycle(packet);
                }
            }

            // 可能还未达到mtu限制而没发出去的包，也要发出去
            SendMerged();
        }

        //For reliable channel
        // 在RChannel.ProcessAck中调用
        // 这里处理的是本地的PendingPac，ACK以后要执行的内容，算是回调
        internal void RecycleAndDeliver(NetPacket packet)
        {
            // 如果是合并包，循环读取包内的内容
            if (packet.UserData is MergedPacketUserData mergedUserData)
            {
                for (int i = 0; i < mergedUserData.Items.Length; i++)
                    NetManager.MessageDelivered(this, mergedUserData.Items[i]);
                packet.UserData = null;
            }
            else if (packet.UserData != null)
            {
                if (packet.IsFragmented)
                {
                    // 将对应的碎片组的数量+1
                    _deliveredFragments.TryGetValue(packet.FragmentId, out ushort fragCount);
                    fragCount++;
                    if (fragCount == packet.FragmentsTotal)
                    {
                        NetManager.MessageDelivered(this, packet.UserData);
                        _deliveredFragments.Remove(packet.FragmentId);
                    }
                    else
                    {
                        _deliveredFragments[packet.FragmentId] = fragCount;
                    }
                }
                else
                {
                    NetManager.MessageDelivered(this, packet.UserData);
                }
                packet.UserData = null;
            }
            // 资源处理
            NetManager.PoolRecycle(packet);
        }
    }
}
