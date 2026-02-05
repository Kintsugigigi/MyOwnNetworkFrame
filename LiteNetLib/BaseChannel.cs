using System.Collections.Generic;
using System.Threading;

namespace LiteNetLib
{
    internal abstract class BaseChannel
    {
        // 指向拥有该通道的 LiteNetPeer（即连接的另一端）。通道通过它来执行具体的 Socket 发送操作
        protected readonly LiteNetPeer Peer;
        // 发送缓冲区
        protected readonly Queue<NetPacket> OutgoingQueue = new Queue<NetPacket>(NetConstants.DefaultWindowSize);

        private int _isAddedToPeerChannelSendQueue;

        public int PacketsInQueue => OutgoingQueue.Count;

        protected BaseChannel(LiteNetPeer peer) =>
            Peer = peer;

        public void AddToQueue(NetPacket packet)
        {
            lock (OutgoingQueue)
            {
                OutgoingQueue.Enqueue(packet);
            }
            AddToPeerChannelSendQueue();
        }

        // 查看 _isAddedToPeerChannelSendQueue 的值。
        // 如果值是 0（表示不在发送队列中）：将其改为 1，并返回 0。
        // 如果值是 1（表示已经在队列中了）：不做任何修改，直接返回 1。
        protected void AddToPeerChannelSendQueue()
        {
            if (Interlocked.CompareExchange(ref _isAddedToPeerChannelSendQueue, 1, 0) == 0)
                Peer.AddToReliableChannelSendQueue(this);
        }

        public bool SendAndCheckQueue()
        {
            bool hasPacketsToSend = SendNextPackets();
            // 如果包发完了，重置
            if (!hasPacketsToSend)
                Interlocked.Exchange(ref _isAddedToPeerChannelSendQueue, 0);

            return hasPacketsToSend;
        }

        public abstract bool SendNextPackets();

        public abstract bool ProcessPacket(NetPacket packet);
    }
}
