using System.Net;
using System.Threading;
using LiteNetLib.Utils;

namespace LiteNetLib
{
    internal enum ConnectionRequestResult
    {
        None,
        Accept,
        Reject,
        RejectForce
    }

    public class ConnectionRequest
    {
        // 回调通知的对象
        private readonly LiteNetManager _listener;
        // 一次性开关，防止重复处理
        private int _used;

        // 自定义验证数据
        public NetDataReader Data => InternalPacket.Data;

        internal ConnectionRequestResult Result { get; private set; }
        // 原始的连接请求数据包对象
        internal NetConnectRequestPacket InternalPacket;

        // 请求者的 IP 地址和端口
        public readonly IPEndPoint RemoteEndPoint;

        //
        internal void UpdateRequest(NetConnectRequestPacket connectRequest)
        {
            //old request
            // 拦截延迟到达的旧请求包
            if (connectRequest.ConnectionTime < InternalPacket.ConnectionTime)
                return;

            // 忽略冗余包
            if (connectRequest.ConnectionTime == InternalPacket.ConnectionTime &&
                connectRequest.ConnectionNumber == InternalPacket.ConnectionNumber)
                return;
            // 有效包
            InternalPacket = connectRequest;
        }

        // 是否执行过操作，线程安全
        // 试把 _used 从 0 改为 1。如果原本就是 0就返回true
        private bool TryActivate() =>
            Interlocked.CompareExchange(ref _used, 1, 0) == 0;

        internal ConnectionRequest(IPEndPoint remoteEndPoint, NetConnectRequestPacket requestPacket, LiteNetManager listener)
        {
            InternalPacket = requestPacket;
            RemoteEndPoint = remoteEndPoint;
            _listener = listener;
        }

        public LiteNetPeer AcceptIfKey(string key)
        {
            if (!TryActivate())
                return null;
            try
            {
                if (Data.GetString() == key)
                    Result = ConnectionRequestResult.Accept;
            }
            catch
            {
                NetDebug.WriteError("[AC] Invalid incoming data");
            }
            if (Result == ConnectionRequestResult.Accept)
                return _listener.OnConnectionSolved(this, null, 0, 0);

            Result = ConnectionRequestResult.Reject;
            _listener.OnConnectionSolved(this, null, 0, 0);
            return null;
        }

        /// <summary>
        /// Accept connection and get new NetPeer as result
        /// 和上面相比不关心数据内容，直接接收，下面reject也是
        /// </summary>
        /// <returns>Connected NetPeer</returns>
        public LiteNetPeer Accept()
        {
            if (!TryActivate())
                return null;
            Result = ConnectionRequestResult.Accept;
            return _listener.OnConnectionSolved(this, null, 0, 0);
        }

        public void Reject(byte[] rejectData, int start, int length, bool force)
        {
            if (!TryActivate())
                return;
            Result = force ? ConnectionRequestResult.RejectForce : ConnectionRequestResult.Reject;
            _listener.OnConnectionSolved(this, rejectData, start, length);
        }

        public void Reject(byte[] rejectData, int start, int length) =>
            Reject(rejectData, start, length, false);

        public void RejectForce(byte[] rejectData, int start, int length) =>
            Reject(rejectData, start, length, true);

        public void RejectForce() =>
            Reject(null, 0, 0, true);

        public void RejectForce(byte[] rejectData) =>
            Reject(rejectData, 0, rejectData.Length, true);

        public void RejectForce(NetDataWriter rejectData) =>
            Reject(rejectData.Data, 0, rejectData.Length, true);

        public void Reject() =>
            Reject(null, 0, 0, false);

        public void Reject(byte[] rejectData) =>
            Reject(rejectData, 0, rejectData.Length, false);

        public void Reject(NetDataWriter rejectData) =>
            Reject(rejectData.Data, 0, rejectData.Length, false);
    }
}
