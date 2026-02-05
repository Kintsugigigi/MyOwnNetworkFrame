using System;

namespace LiteNetLib
{
    internal sealed class SequencedChannel : BaseChannel
    {
        private int _localSequence;
        private ushort _remoteSequence;
        private readonly bool _reliable;
        private NetPacket _lastPacket;
        private readonly NetPacket _ackPacket;
        private bool _mustSendAck;
        private readonly byte _id;
        private long _lastPacketSendTime;

        public SequencedChannel(LiteNetPeer peer, bool reliable, byte id) : base(peer)
        {
            _id = id;
            _reliable = reliable;
            if (_reliable)
                _ackPacket = new NetPacket(PacketProperty.Ack, 0) {ChannelId = id};
        }

        public override bool SendNextPackets()
        {
            // 只有当queue中没有待传pac，才执行RTO重传逻辑
            if (_reliable && OutgoingQueue.Count == 0)
            {
                long currentTime = DateTime.UtcNow.Ticks;
                long packetHoldTime = currentTime - _lastPacketSendTime;
                // RTO到达后发送之前缓存的最后一个包
                if (packetHoldTime >= Peer.ResendDelay * TimeSpan.TicksPerMillisecond)
                {
                    var packet = _lastPacket;
                    if (packet != null)
                    {
                        _lastPacketSendTime = currentTime;
                        Peer.SendUserData(packet);
                    }
                }
            }
            // 把queue发空
            else
            {
                lock (OutgoingQueue)
                {
                    while (OutgoingQueue.Count > 0)
                    {
                        NetPacket packet = OutgoingQueue.Dequeue();
                        _localSequence = (_localSequence + 1) % NetConstants.MaxSequence;
                        packet.Sequence = (ushort)_localSequence;
                        packet.ChannelId = _id;
                        Peer.SendUserData(packet);
                        // 如果是可靠模式，缓存最后发出的一个包
                        if (_reliable && OutgoingQueue.Count == 0)
                        {
                            _lastPacketSendTime = DateTime.UtcNow.Ticks;
                            _lastPacket = packet;
                        }
                        else
                        {
                            Peer.NetManager.PoolRecycle(packet);
                        }
                    }
                }
            }

            if (_reliable && _mustSendAck)
            {
                _mustSendAck = false;
                _ackPacket.Sequence = _remoteSequence;
                Peer.SendUserData(_ackPacket);
            }

            return _lastPacket != null;
        }

        public override bool ProcessPacket(NetPacket packet)
        {
            // Sequenced 通道不处理分片包
            if (packet.IsFragmented)
                return false;
            if (packet.Property == PacketProperty.Ack)
            {
                // 如果是可靠有序模式，且收到的 ACK 序号正好为存的最后一个包
                if (_reliable && _lastPacket != null && packet.Sequence == _lastPacket.Sequence)
                    _lastPacket = null;
                return false;
            }
            // 当前包的序号 VS 之前收到过的最大包序号
            int relative = NetUtils.RelativeSequenceNumber(packet.Sequence, _remoteSequence);
            bool packetProcessed = false;
            if (packet.Sequence < NetConstants.MaxSequence && relative > 0)
            {
                if (Peer.NetManager.EnableStatistics)
                {
                    Peer.Statistics.AddPacketLoss(relative - 1);
                    Peer.NetManager.Statistics.AddPacketLoss(relative - 1);
                }

                // 该通道只接受最新，不接受过时
                _remoteSequence = packet.Sequence;
                Peer.NetManager.CreateReceiveEvent(
                    packet,
                    _reliable ? DeliveryMethod.ReliableSequenced : DeliveryMethod.Sequenced,
                    (byte)(packet.ChannelId / NetConstants.ChannelTypeCount),
                    NetConstants.ChanneledHeaderSize,
                    Peer);
                packetProcessed = true;
            }

            if (_reliable)
            {
                _mustSendAck = true;
                AddToPeerChannelSendQueue();
            }

            return packetProcessed;
        }
    }
}
