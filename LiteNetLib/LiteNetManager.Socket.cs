#if UNITY_2018_3_OR_NEWER
#define UNITY_SOCKET_FIX
#endif
using System.Runtime.InteropServices;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using LiteNetLib.Utils;

namespace LiteNetLib
{
    public partial class LiteNetManager
    {
        protected Socket _udpSocketv4;
        private Socket _udpSocketv6;
        private Thread _receiveThread;
        private IPEndPoint _bufferEndPointv4;
        private IPEndPoint _bufferEndPointv6;
#if UNITY_SOCKET_FIX
        private PausedSocketFix _pausedSocketFix;
        private bool _useSocketFix;
#endif

#if NET8_0_OR_GREATER
        // 通过预先分配好 SocketAddress 缓存，可以复用这块内存空间。
        private readonly SocketAddress _sockAddrCacheV4 = new SocketAddress(AddressFamily.InterNetwork);
        private readonly SocketAddress _sockAddrCacheV6 = new SocketAddress(AddressFamily.InterNetworkV6);
#endif

        private const int SioUdpConnreset = -1744830452; //SIO_UDP_CONNRESET = IOC_IN | IOC_VENDOR | 12
        private static readonly IPAddress MulticastAddressV6 = IPAddress.Parse("ff02::1");
        public static readonly bool IPv6Support;

        // special case in iOS (and possibly android that should be resolved in unity)
        internal bool NotConnected;

        /// <summary>
        /// Poll timeout in microseconds. Increasing can slightly increase performance in cost of slow NetManager.Stop(Socket.Close)
        /// </summary>
        public int ReceivePollingTime = 50000; //0.05 second

        public short Ttl
        {
            get
            {
#if UNITY_SWITCH
                return 0;
#else
                return _udpSocketv4.Ttl;
#endif
            }
            internal set
            {
#if !UNITY_SWITCH
                _udpSocketv4.Ttl = value;
#endif
            }
        }

        static LiteNetManager()
        {
#if DISABLE_IPV6
            IPv6Support = false;
#elif !UNITY_2019_1_OR_NEWER && !UNITY_2018_4_OR_NEWER && (!UNITY_EDITOR && ENABLE_IL2CPP)
            string version = UnityEngine.Application.unityVersion;
            IPv6Support = Socket.OSSupportsIPv6 && int.Parse(version.Remove(version.IndexOf('f')).Split('.')[2]) >= 6;
#else
            IPv6Support = Socket.OSSupportsIPv6;
#endif
        }

        // 过滤非重要异常
        private bool ProcessError(SocketException ex)
        {
            switch (ex.SocketErrorCode)
            {
                case SocketError.NotConnected:
                    NotConnected = true;
                    return true;
                case SocketError.Interrupted:
                case SocketError.NotSocket:
                case SocketError.OperationAborted:
                    return true;
                case SocketError.ConnectionReset:
                case SocketError.MessageSize:
                case SocketError.TimedOut:
                case SocketError.NetworkReset:
                case SocketError.WouldBlock:
                    //NetDebug.Write($"[R]Ignored error: {(int)ex.SocketErrorCode} - {ex}");
                    break;
                default:
                    NetDebug.WriteError($"[R]Error code: {(int)ex.SocketErrorCode} - {ex}");
                    CreateEvent(NetEvent.EType.Error, errorCode: ex.SocketErrorCode);
                    break;
            }
            return false;
        }

        // 在PollEvent中被调用（手动控制时序时）
        private void ManualReceive(Socket socket, EndPoint bufferEndPoint)
        {
            //Reading data
            try
            {
                // 现在排队等待处理的字节数
                int available = socket.Available;
                while (available > 0)
                    // 不停读直到读完
                    available -= ReceiveFrom(socket, ref bufferEndPoint);
            }
            catch (SocketException ex)
            {
                ProcessError(ex);
            }
            catch (ObjectDisposedException)
            {

            }
            catch (Exception e)
            {
                //protects socket receive thread
                NetDebug.WriteError("[NM] SocketReceiveThread error: " + e);
            }
        }

        private void NativeReceiveLogic()
        {
            // 获取句柄
            IntPtr socketHandle4 = _udpSocketv4.Handle;
            IntPtr socketHandle6 = _udpSocketv6?.Handle ?? IntPtr.Zero;
            byte[] addrBuffer4 = new byte[NativeSocket.IPv4AddrSize];
            byte[] addrBuffer6 = new byte[NativeSocket.IPv6AddrSize];
            var tempEndPoint = new IPEndPoint(IPAddress.Any, 0);
            var selectReadList = new List<Socket>(2);
            var socketv4 = _udpSocketv4;
            var socketV6 = _udpSocketv6;
            var packet = PoolGetPacket(NetConstants.MaxPacketSize);

            while (_isRunning)
            {
                try
                {
                    if (socketV6 == null)
                    {
                        if (NativeReceiveFrom(socketHandle4, addrBuffer4) == false)
                            return;
                        continue;
                    }
                    bool messageReceived = false;
                    if (socketv4.Available != 0 || selectReadList.Contains(socketv4))
                    {
                        if (NativeReceiveFrom(socketHandle4, addrBuffer4) == false)
                            return;
                        messageReceived = true;
                    }
                    if (socketV6.Available != 0 || selectReadList.Contains(socketV6))
                    {
                        if (NativeReceiveFrom(socketHandle6, addrBuffer6) == false)
                            return;
                        messageReceived = true;
                    }

                    // 重置状态，防止上一轮残留的 selectReadList 数据干扰下一轮的判断
                    selectReadList.Clear();

                    // 有收到数据，继续读网卡缓存
                    if (messageReceived)
                        continue;

                    selectReadList.Add(socketv4);
                    selectReadList.Add(socketV6);

                    // 系统调用：内核会检查名单里的 Socket 是否有数据可读。阻塞。
                    Socket.Select(selectReadList, null, null, ReceivePollingTime);
                }
                catch (SocketException ex)
                {
                    if (ProcessError(ex))
                        return;
                }
                catch (ObjectDisposedException)
                {
                    //socket closed
                    return;
                }
                catch (ThreadAbortException)
                {
                    //thread closed
                    return;
                }
                catch (Exception e)
                {
                    //protects socket receive thread
                    NetDebug.WriteError("[NM] SocketReceiveThread error: " + e);
                }
            }

            bool NativeReceiveFrom(IntPtr s, byte[] address)
            {
                int addrSize = address.Length;
                // 把数据直接填进 packet.RawData，把发送者的地址信息（二进制格式）填进 address 字节数组，
                packet.Size = NativeSocket.RecvFrom(s, packet.RawData, NetConstants.MaxPacketSize, address, ref addrSize);
                if (packet.Size == 0)
                    return true; //socket closed or empty packet

                if (packet.Size == -1)
                {
                    //Linux timeout EAGAIN
                    return ProcessError(new SocketException((int)NativeSocket.GetSocketError())) == false;
                }

                //NetDebug.WriteForce($"[R]Received data from {endPoint}, result: {packet.Size}");
                //refresh temp Addr/Port
                // 从 address 数组的第 2 和第 3 个字节里拼凑出来的。这是网络字节序（大端序）
                short family = (short)((address[1] << 8) | address[0]);
                tempEndPoint.Port = (ushort)((address[2] << 8) | address[3]);
                if ((NativeSocket.UnixMode && family == NativeSocket.AF_INET6) || (!NativeSocket.UnixMode && (AddressFamily)family == AddressFamily.InterNetworkV6))
                {
                    // 提取第 8 到 23 字节（共 16 字节），并处理了 scope（网卡索引）
                    uint scope = unchecked((uint)(
                        (address[27] << 24) +
                        (address[26] << 16) +
                        (address[25] << 8) +
                        (address[24])));
                    tempEndPoint.Address = new IPAddress(new ReadOnlySpan<byte>(address, 8, 16), scope);
                }
                else //IPv4
                {
                    // 提取第 4, 5, 6, 7 字节，通过位移拼成一个 32 位的长整型
                    long ipv4Addr = unchecked((uint)((address[4] & 0x000000FF) |
                                                     (address[5] << 8 & 0x0000FF00) |
                                                     (address[6] << 16 & 0x00FF0000) |
                                                     (address[7] << 24)));
                    tempEndPoint.Address = new IPAddress(ipv4Addr);
                }

                if (TryGetPeer(tempEndPoint, out var peer))
                {
                    //use cached native ep
                    OnMessageReceived(packet, peer);
                }
                else
                {
                    OnMessageReceived(packet, tempEndPoint);
                    tempEndPoint = new IPEndPoint(IPAddress.Any, 0);
                }
                // packet的return在OnMessageReceived内解决，现在重新从池中生成
                packet = PoolGetPacket(NetConstants.MaxPacketSize);
                return true;
            }
        }

        private int ReceiveFrom(Socket s, ref EndPoint bufferEndPoint)
        {
            var packet = PoolGetPacket(NetConstants.MaxPacketSize);
#if NET8_0_OR_GREATER
            var sockAddr = s.AddressFamily == AddressFamily.InterNetwork ? _sockAddrCacheV4 : _sockAddrCacheV6;
            // UDP不粘包，报文有长度约束
            packet.Size = s.ReceiveFrom(packet, SocketFlags.None, sockAddr);
            // 立刻处理
            OnMessageReceived(packet, TryGetPeer(sockAddr, out var peer) ? peer : (IPEndPoint)bufferEndPoint.Create(sockAddr));
#else
            packet.Size = s.ReceiveFrom(packet.RawData, 0, NetConstants.MaxPacketSize, SocketFlags.None, ref bufferEndPoint);
            OnMessageReceived(packet, (IPEndPoint)bufferEndPoint);
#endif
            return packet.Size;
        }

        private void ReceiveLogic()
        {
            EndPoint bufferEndPoint4 = new IPEndPoint(IPAddress.Any, 0);
            EndPoint bufferEndPoint6 = new IPEndPoint(IPAddress.IPv6Any, 0);
            var selectReadList = new List<Socket>(2);
            var socketv4 = _udpSocketv4;
            var socketV6 = _udpSocketv6;

            while (_isRunning)
            {
                //Reading data
                try
                {
                    if (socketV6 == null)
                    {
                        if (socketv4.Available == 0 && !socketv4.Poll(ReceivePollingTime, SelectMode.SelectRead))
                            continue;
                        ReceiveFrom(socketv4, ref bufferEndPoint4);
                    }
                    else
                    {
                        bool messageReceived = false;
                        if (socketv4.Available != 0 || selectReadList.Contains(socketv4))
                        {
                            ReceiveFrom(socketv4, ref bufferEndPoint4);
                            messageReceived = true;
                        }
                        if (socketV6.Available != 0 || selectReadList.Contains(socketV6))
                        {
                            ReceiveFrom(socketV6, ref bufferEndPoint6);
                            messageReceived = true;
                        }

                        selectReadList.Clear();

                        if (messageReceived)
                            continue;

                        selectReadList.Add(socketv4);
                        selectReadList.Add(socketV6);
                        Socket.Select(selectReadList, null, null, ReceivePollingTime);
                    }
                    //NetDebug.Write(NetLogLevel.Trace, $"[R]Received data from {bufferEndPoint}, result: {packet.Size}");
                }
                catch (SocketException ex)
                {
                    if (ProcessError(ex))
                        return;
                }
                catch (ObjectDisposedException)
                {
                    //socket closed
                    return;
                }
                catch (ThreadAbortException)
                {
                    //thread closed
                    return;
                }
                catch (Exception e)
                {
                    //protects socket receive thread
                    NetDebug.WriteError("[NM] SocketReceiveThread error: " + e);
                }
            }
        }

        /// <summary>
        /// Start logic thread and listening on selected port
        /// </summary>
        /// <param name="addressIPv4">bind to specific ipv4 address</param>
        /// <param name="addressIPv6">bind to specific ipv6 address</param>
        /// <param name="port">port to listen</param>
        /// <param name="manualMode">mode of library</param>
        public bool Start(IPAddress addressIPv4, IPAddress addressIPv6, int port, bool manualMode)
        {
            if (IsRunning && NotConnected == false)
                return false;

            NotConnected = false;
            _manualMode = manualMode;
            UseNativeSockets = UseNativeSockets && NativeSocket.IsSupported;
            // IPv4 地址族，数据报，UDP
            _udpSocketv4 = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            if (!BindSocket(_udpSocketv4, new IPEndPoint(addressIPv4, port)))
                return false;

            LocalPort = ((IPEndPoint)_udpSocketv4.LocalEndPoint).Port;

#if UNITY_SOCKET_FIX
            if (_useSocketFix && _pausedSocketFix == null)
                _pausedSocketFix = new PausedSocketFix(this, addressIPv4, addressIPv6, port, manualMode);
#endif

            _isRunning = true;
            if (_manualMode)
            {
                _bufferEndPointv4 = new IPEndPoint(IPAddress.Any, 0);
            }

            //Check IPv6 support
            if (IPv6Support && IPv6Enabled)
            {
                _udpSocketv6 = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
                //Use one port for two sockets
                if (BindSocket(_udpSocketv6, new IPEndPoint(addressIPv6, LocalPort)))
                {
                    if (_manualMode)
                        _bufferEndPointv6 = new IPEndPoint(IPAddress.IPv6Any, 0);
                }
                else
                {
                    _udpSocketv6 = null;
                }
            }

            if (!manualMode)
            {
                ThreadStart ts = ReceiveLogic;
                if (UseNativeSockets)
                    ts = NativeReceiveLogic;
                _receiveThread = new Thread(ts)
                {
                    Name = $"ReceiveThread({LocalPort})",
                    IsBackground = true
                };
                _receiveThread.Start();
                if (_logicThread == null)
                {
                    _logicThread = new Thread(UpdateLogic) { Name = "LogicThread", IsBackground = true };
                    _logicThread.Start();
                }
            }

            return true;
        }

        private bool BindSocket(Socket socket, IPEndPoint ep)
        {
            //Setup socket
            // 同步阻塞的超时保护
            socket.ReceiveTimeout = 500;
            socket.SendTimeout = 500;

            // 内核收发缓冲区大小
            socket.ReceiveBufferSize = NetConstants.SocketBufferSize;
            socket.SendBufferSize = NetConstants.SocketBufferSize;
            socket.Blocking = true;

            // 关掉 UDP 连接重置通知
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    socket.IOControl(SioUdpConnreset, new byte[] { 0 }, null);
                }
                catch
                {
                    //ignored
                }
            }

            try
            {
                // 独占模式
                socket.ExclusiveAddressUse = !ReuseAddress;
                // 不需要等待操作系统的 TIME_WAIT 回收时间
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, ReuseAddress);
                // 告诉系统包不需要经过网关
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.DontRoute, DontRoute);
            }
            catch
            {
                //Unity with IL2CPP throws an exception here, it doesn't matter in most cases so just ignore it
            }
            // IPV4
            if (ep.AddressFamily == AddressFamily.InterNetwork)
            {
                Ttl = NetConstants.SocketTTL;

                // 允许广播
                try { socket.EnableBroadcast = true; }
                catch (SocketException e)
                {
                    NetDebug.WriteError($"[B]Broadcast error: {e.SocketErrorCode}");
                }

                if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    // 不允许中间路由器切分
                    try { socket.DontFragment = true; }
                    catch (SocketException e)
                    {
                        NetDebug.WriteError($"[B]DontFragment error: {e.SocketErrorCode}");
                    }
                }
            }
            //Bind
            try
            {
                // 任何发送到这个端口的 UDP 数据包都会被放入 Socket 缓冲区
                socket.Bind(ep);
                NetDebug.Write(NetLogLevel.Trace, $"[B]Successfully binded to port: {((IPEndPoint)socket.LocalEndPoint).Port}, AF: {socket.AddressFamily}");

                //join multicast
                if (ep.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    try
                    {
#if !UNITY_SOCKET_FIX
                        // IPV6没有广播，只有组播
                        socket.SetSocketOption(
                            SocketOptionLevel.IPv6,
                            // 添加成员资格
                            SocketOptionName.AddMembership,
                            // 组播频道号
                            new IPv6MulticastOption(MulticastAddressV6));
#endif
                    }
                    catch (Exception)
                    {
                        // Unity3d throws exception - ignored
                    }
                }
            }
            catch (SocketException bindException)
            {
                switch (bindException.SocketErrorCode)
                {
                    //IPv6 bind fix
                    // IPv6 实现通常默认开启了 DualMode
                    // 同时接管该端口的 IPv4 和 IPv6 流量
                    case SocketError.AddressAlreadyInUse:
                        if (socket.AddressFamily == AddressFamily.InterNetworkV6)
                        {
                            try
                            {
                                //Set IPv6Only
                                socket.DualMode = false;
                                socket.Bind(ep);
                            }
                            catch (SocketException ex)
                            {
                                //because its fixed in 2018_3
                                NetDebug.WriteError($"[B]Bind exception: {ex}, errorCode: {ex.SocketErrorCode}");
                                return false;
                            }
                            return true;
                        }
                        break;
                    //hack for iOS (Unity3D)
                    case SocketError.AddressFamilyNotSupported:
                        return true;
                }
                NetDebug.WriteError($"[B]Bind exception: {bindException}, errorCode: {bindException.SocketErrorCode}");
                return false;
            }
            return true;
        }

        internal int SendRawAndRecycle(NetPacket packet, IPEndPoint remoteEndPoint)
        {
            int result = SendRaw(packet.RawData, 0, packet.Size, remoteEndPoint);
            PoolRecycle(packet);
            return result;
        }

        internal int SendRaw(NetPacket packet, IPEndPoint remoteEndPoint) =>
            SendRaw(packet.RawData, 0, packet.Size, remoteEndPoint);

        internal int SendRaw(byte[] message, int start, int length, IPEndPoint remoteEndPoint)
        {
            if (!_isRunning)
                return 0;

            NetPacket expandedPacket = null;
            if (_extraPacketLayer != null)
            {
                expandedPacket = PoolGetPacket(length + _extraPacketLayer.ExtraPacketSizeForLayer);
                Buffer.BlockCopy(message, start, expandedPacket.RawData, 0, length);
                start = 0;
                _extraPacketLayer.ProcessOutBoundPacket(ref remoteEndPoint, ref expandedPacket.RawData, ref start, ref length);
                message = expandedPacket.RawData;
            }

#if DEBUG || SIMULATE_NETWORK
            // 模拟丢包
            if (HandleSimulateOutboundPacketLoss())
            {
                if (expandedPacket != null)
                    PoolRecycle(expandedPacket);
                return 0; // Simulate successful send to avoid triggering error handling
            }

            // 这里被挂在列表中，需要传递全部信息进行复制
            if (HandleSimulateOutboundLatency(message, start, length, remoteEndPoint))
            {
                if (expandedPacket != null)
                    PoolRecycle(expandedPacket);
                return length; // Simulate successful send
            }
#endif

            return SendRawCoreWithCleanup(message, start, length, remoteEndPoint, expandedPacket);
        }

        private int SendRawCoreWithCleanup(byte[] message, int start, int length, IPEndPoint remoteEndPoint, NetPacket expandedPacket)
        {
            try
            {
                return SendRawCore(message, start, length, remoteEndPoint);
            }
            finally
            {
                if (expandedPacket != null)
                    PoolRecycle(expandedPacket);
            }
        }

        // Core socket sending logic without simulation - used by both SendRaw and delayed packet processing
        internal int SendRawCore(byte[] message, int start, int length, IPEndPoint remoteEndPoint)
        {
            if (!_isRunning)
                return 0;

            var socket = _udpSocketv4;
            if (remoteEndPoint.AddressFamily == AddressFamily.InterNetworkV6 && IPv6Support)
            {
                socket = _udpSocketv6;
                if (socket == null)
                    return 0;
            }

            int result;
            try
            {
                if (UseNativeSockets && remoteEndPoint is LiteNetPeer peer)
                {
                    unsafe
                    {
                        fixed (byte* dataWithOffset = &message[start])
                            result = NativeSocket.SendTo(socket.Handle, dataWithOffset, length, peer.NativeAddress, peer.NativeAddress.Length);
                    }
                    if (result == -1)
                        throw NativeSocket.GetSocketException();
                }
                else
                {
#if NET8_0_OR_GREATER
                    result = socket.SendTo(new ReadOnlySpan<byte>(message, start, length), SocketFlags.None, remoteEndPoint.Serialize());
#else
                    result = socket.SendTo(message, start, length, SocketFlags.None, remoteEndPoint);
#endif
                }
                //NetDebug.WriteForce("[S]Send packet to {0}, result: {1}", remoteEndPoint, result);
            }
            catch (SocketException ex)
            {
                switch (ex.SocketErrorCode)
                {
                    case SocketError.NoBufferSpaceAvailable:
                    case SocketError.Interrupted:
                        return 0;
                    case SocketError.MessageSize:
                        NetDebug.Write(NetLogLevel.Trace, $"[SRD] 10040, datalen: {length}");
                        return 0;

                    case SocketError.HostUnreachable:
                    case SocketError.NetworkUnreachable:
                        if (DisconnectOnUnreachable && remoteEndPoint is LiteNetPeer peer)
                        {
                            DisconnectPeerForce(
                                peer,
                                ex.SocketErrorCode == SocketError.HostUnreachable
                                    ? DisconnectReason.HostUnreachable
                                    : DisconnectReason.NetworkUnreachable,
                                ex.SocketErrorCode,
                                null);
                        }

                        CreateEvent(NetEvent.EType.Error, remoteEndPoint: remoteEndPoint, errorCode: ex.SocketErrorCode);
                        return -1;

                    case SocketError.Shutdown:
                        CreateEvent(NetEvent.EType.Error, remoteEndPoint: remoteEndPoint, errorCode: ex.SocketErrorCode);
                        return -1;

                    default:
                        NetDebug.WriteError($"[S] {ex}");
                        return -1;
                }
            }
            catch (Exception ex)
            {
                NetDebug.WriteError($"[S] {ex}");
                return 0;
            }

            if (result <= 0)
                return 0;

            if (EnableStatistics)
            {
                Statistics.IncrementPacketsSent();
                Statistics.AddBytesSent(length);
            }

            return result;
        }

        public bool SendBroadcast(NetDataWriter writer, int port) =>
            SendBroadcast(writer.Data, 0, writer.Length, port);

        public bool SendBroadcast(byte[] data, int port) =>
            SendBroadcast(data, 0, data.Length, port);

        // data不带报头
        public bool SendBroadcast(byte[] data, int start, int length, int port)
        {
            if (!IsRunning)
                return false;

            NetPacket packet;
            if (_extraPacketLayer != null)
            {
                // 如果是广播，要附上广播报头
                var headerSize = NetPacket.GetHeaderSize(PacketProperty.Broadcast);
                packet = PoolGetPacket(headerSize + length + _extraPacketLayer.ExtraPacketSizeForLayer);
                packet.Property = PacketProperty.Broadcast;
                Buffer.BlockCopy(data, start, packet.RawData, headerSize, length);
                var checksumComputeStart = 0;
                int preCrcLength = length + headerSize;
                IPEndPoint emptyEp = null;
                _extraPacketLayer.ProcessOutBoundPacket(ref emptyEp, ref packet.RawData, ref checksumComputeStart, ref preCrcLength);
            }
            else
            {
                packet = PoolGetWithData(PacketProperty.Broadcast, data, start, length);
            }

            bool broadcastSuccess = false;
            bool multicastSuccess = false;
            try
            {
                broadcastSuccess = _udpSocketv4.SendTo(
                    packet.RawData,
                    0,
                    packet.Size,
                    SocketFlags.None,
                    new IPEndPoint(IPAddress.Broadcast, port)) > 0;

                if (_udpSocketv6 != null)
                {
                    multicastSuccess = _udpSocketv6.SendTo(
                        packet.RawData,
                        0,
                        packet.Size,
                        SocketFlags.None,
                        new IPEndPoint(MulticastAddressV6, port)) > 0;
                }
            }
            catch (SocketException ex)
            {
                if (ex.SocketErrorCode == SocketError.HostUnreachable)
                    return broadcastSuccess;
                NetDebug.WriteError($"[S][MCAST] {ex}");
                return broadcastSuccess;
            }
            catch (Exception ex)
            {
                NetDebug.WriteError($"[S][MCAST] {ex}");
                return broadcastSuccess;
            }
            finally
            {
                PoolRecycle(packet);
            }

            return broadcastSuccess || multicastSuccess;
        }

        private void CloseSocket()
        {
            _isRunning = false;
            if (_receiveThread != null && _receiveThread != Thread.CurrentThread)
                _receiveThread.Join();
            _receiveThread = null;
            _udpSocketv4?.Close();
            _udpSocketv6?.Close();
            _udpSocketv4 = null;
            _udpSocketv6 = null;
        }
    }
}
