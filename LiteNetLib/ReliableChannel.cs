using System;
using System.Collections.Generic;
using LiteNetLib.Utils;

namespace LiteNetLib
{
    internal sealed class MergedPacketUserData
    {
        public readonly object[] Items;

        public MergedPacketUserData(object[] items)
        {
            Items = items;
        }
    }

    internal sealed class ReliableChannel : BaseChannel
    {
        [ThreadStatic]
        private static List<object> _mergedPacketUserDataList;
        // 合并中间的长度（下一子包的长度）
        private const int MergeHeaderSize = 2;
        // 合并的最小长度（防止合并后超出MTU）
        private const int MergeSizeThreshold = 20;

        private struct PendingPacket
        {
            private NetPacket _packet;
            private long _timeStamp;
            private bool _isSent;

            public override string ToString() => _packet == null ? "Empty" : _packet.Sequence.ToString();

            public void Init(NetPacket packet)
            {
                _packet = packet;
                _isSent = false;
            }

            //Returns true if there is a pending packet inside
            public bool TrySend(long currentTime, LiteNetPeer peer)
            {
                if (_packet == null)
                    return false;

                // 如果已经发送过，却没有收到ACK
                if (_isSent) //check send time
                {
                    // 计算RTO
                    double resendDelay = peer.ResendDelay * TimeSpan.TicksPerMillisecond;
                    double packetHoldTime = currentTime - _timeStamp;
                    // 时间不到RTO,不重传
                    if (packetHoldTime < resendDelay)
                        return true;
                    NetDebug.Write($"[RC]Resend: {packetHoldTime} > {resendDelay}");
                }
                _timeStamp = currentTime;
                _isSent = true;
                peer.SendUserData(_packet);
                return true;
            }

            public bool Clear(LiteNetPeer peer)
            {
                if (_packet != null)
                {
                    peer.RecycleAndDeliver(_packet);
                    _packet = null;
                    return true;
                }
                return false;
            }
        }

        // 特殊的 NetPacket，其 RawData 中存储着一个位图
        // 每一位（Bit）代表窗口中的一个位置
        // 如果收到序号为 X 的包，就在位图对应的位置打上 1
        // 发送端解析这个位图，通过 (acksData[currentByte] & (1 << currentBit)) 就能一次性知道哪些包对方收到了，哪些丢了
        private readonly NetPacket _outgoingAcks;            //for send acks
        // 已经发送但没有ACK的包（KCP的una）
        private readonly PendingPacket[] _pendingPackets;    //for unacked packets and duplicates
        // 接收窗口（KCP的recv_wnd）
        private readonly NetPacket[] _receivedPackets;       //for order
        // recv_wnd
        private readonly bool[] _earlyReceived;              //for unordered

        private int _localSeqence;
        private int _remoteSequence;
        // una
        private int _localWindowStart;
        private int _remoteWindowStart;

        private bool _mustSendAcks;

        private readonly DeliveryMethod _deliveryMethod;
        private readonly bool _ordered;
        private readonly int _windowSize;
        private const int BitsInByte = 8;
        private readonly byte _id;

        // 对象，是否有序，通道ID？
        public ReliableChannel(LiteNetPeer peer, bool ordered, byte id) : base(peer)
        {
            _id = id;
            // KCP的c_wnd
            _windowSize = NetConstants.DefaultWindowSize;
            _ordered = ordered;
            _pendingPackets = new PendingPacket[_windowSize];
            for (int i = 0; i < _pendingPackets.Length; i++)
                _pendingPackets[i] = new PendingPacket();

            if (_ordered)
            {
                _deliveryMethod = DeliveryMethod.ReliableOrdered;
                _receivedPackets = new NetPacket[_windowSize];
            }
            else
            {
                _deliveryMethod = DeliveryMethod.ReliableUnordered;
                _earlyReceived = new bool[_windowSize];
            }

            // 指向当前最老的一个尚未收到确认（ACK）的包序号，una
            _localWindowStart = 0;
            // 每发一个可靠包，这个值就加 1
            _localSeqence = 0;
            // 接收端的窗口起点
            _remoteSequence = 0;
            // 如果收到的包序号大于它，说明中间有包跳过了，这个包需要先存进缓冲区挂起
            _remoteWindowStart = 0;
            // 11字节（3+8）
            _outgoingAcks = new NetPacket(PacketProperty.Ack, (_windowSize - 1) / BitsInByte + 2) {ChannelId = id};
        }

        private NetPacket GetNextOutgoingPacket()
        {
            // mtu（最大传输单元） - 基础报头大小
            int maxPayloadSize = Peer.Mtu - NetConstants.ChanneledHeaderSize;
            NetPacket mergedPacket = null;
            int mergePos = 0;

            List<object> userDataList = null;

            while (OutgoingQueue.Count > 0)
            {
                var packet = OutgoingQueue.Peek();
                // 分片包不进行合并
                if (packet.IsFragmented)
                    break;
                // 该包实际Paylaod
                int payloadSize = packet.Size - NetConstants.ChanneledHeaderSize;
                int newSize = mergePos + MergeHeaderSize + payloadSize;
                // 如果当前已经合并了一些包（mergePos > 0），并且剩下的空间已经非常局促（不足以再塞下一个 20 字节左右的小包），那么 LiteNetLib 宁愿现在就“封箱”发货
                if (newSize + MergeSizeThreshold > maxPayloadSize && mergePos > 0)
                    break;
                if (newSize > maxPayloadSize)
                    break;

                if (mergedPacket == null)
                {
                    // 这里传入的MTU是动态变化的
                    mergedPacket = Peer.NetManager.PoolGetPacket(Peer.Mtu);
                    mergedPacket.Property = PacketProperty.ReliableMerged;

                    // 用于最后封装信息
                    userDataList = _mergedPacketUserDataList;
                    if (userDataList == null)
                    {
                        userDataList = new List<object>();
                        _mergedPacketUserDataList = userDataList;
                    }
                    else
                    {
                        userDataList.Clear();
                    }
                }

                // 在大包的载荷区域写入当前子包的实际数据长度
                // ChanneledHeaderSize 跳过大包自己的 4 字节报头
                // mergePos 是之前已经合并进去的数据长度
                // MergeHeaderSize
                FastBitConverter.GetBytes(mergedPacket.RawData, NetConstants.ChanneledHeaderSize + mergePos, (ushort)payloadSize);
                // 将子包的 Payload 拷贝进大包的缓冲区
                Buffer.BlockCopy(packet.RawData, NetConstants.ChanneledHeaderSize, mergedPacket.RawData, NetConstants.ChanneledHeaderSize + mergePos + MergeHeaderSize, payloadSize);
                mergePos += payloadSize + MergeHeaderSize;

                if (packet.UserData != null)
                {
                    userDataList.Add(packet.UserData);
                    packet.UserData = null;
                }

                Peer.NetManager.PoolRecycle(OutgoingQueue.Dequeue());
            }

            // 不满足合并条件（包太大），直接单个发出去
            if (mergedPacket == null)
                return OutgoingQueue.Dequeue();

            // 际合并进去的数据长度（mergePos）加上 4 字节的报头（ChanneledHeaderSize），重新修正了 mergedPacket.Size
            mergedPacket.Size = NetConstants.ChanneledHeaderSize + mergePos;
            if (userDataList.Count > 0)
                mergedPacket.UserData = new MergedPacketUserData(userDataList.ToArray());

            return mergedPacket;
        }

        // 拆分合并包或直接塞入Peer
        private void ProcessIncomingPacket(NetPacket packet)
        {
            // 接收的UDP包是否为合并包
            if (packet.Property == PacketProperty.ReliableMerged)
            {
                //ProcessMerged
                int pos = NetConstants.ChanneledHeaderSize;
                while (pos + MergeHeaderSize <= packet.Size)
                {
                    ushort size = BitConverter.ToUInt16(packet.RawData, pos);
                    pos += MergeHeaderSize;
                    if (size == 0 || pos + size > packet.Size)
                    {
                        NetDebug.Write("[RR]Merged packet corrupted");
                        break;
                    }

                    NetPacket mergedPacket = Peer.NetManager.PoolGetPacket(NetConstants.ChanneledHeaderSize + size);
                    mergedPacket.Property = PacketProperty.Channeled;
                    mergedPacket.ChannelId = packet.ChannelId;
                    Buffer.BlockCopy(packet.RawData, pos, mergedPacket.RawData, NetConstants.ChanneledHeaderSize, size);
                    pos += size;

                    Peer.AddReliablePacket(_deliveryMethod, mergedPacket);
                }
                Peer.NetManager.PoolRecycle(packet);
            }
            else
            {
                Peer.AddReliablePacket(_deliveryMethod, packet);
            }
        }

        //ProcessAck in packet
        private void ProcessAck(NetPacket packet)
        {
            if (packet.Size != _outgoingAcks.Size)
            {
                NetDebug.Write("[PA]Invalid acks packet size");
                return;
            }

            // ACK 包的第 2-3 字节存储的是接收端当前的窗口起点
            ushort ackWindowStart = packet.Sequence;
            // 从una与ACK反馈的锚点的插值判断ACK的信息是否陈旧
            int windowRel = NetUtils.RelativeSequenceNumber(_localWindowStart, ackWindowStart);
            // ushort的一半作为边界处理ushort溢出循环
            if (ackWindowStart >= NetConstants.MaxSequence || windowRel < 0)
            {
                NetDebug.Write("[PA]Bad window start");
                return;
            }

            //check relevance
            // 意味着对方反馈的这个包序号，比我目前正在等的那个最老的包还要早 64 个位次以上，ACK过期
            if (windowRel >= _windowSize)
            {
                NetDebug.Write("[PA]Old acks");
                return;
            }

            byte[] acksData = packet.RawData;
            lock (_pendingPackets)
            {
                // 从 _localWindowStart（目前最早没被确认的包）开始，一直遍历到 _localSeqence（下一个准备发的包序号）
                for (int pendingSeq = _localWindowStart;
                    pendingSeq != _localSeqence;
                    pendingSeq = (pendingSeq + 1) % NetConstants.MaxSequence)
                {
                    int rel = NetUtils.RelativeSequenceNumber(pendingSeq, ackWindowStart);
                    if (rel >= _windowSize)
                    {
                        NetDebug.Write("[PA]REL: " + rel);
                        break;
                    }

                    int pendingIdx = pendingSeq % _windowSize;
                    // 64 位位图被拆成了 8 个字节（64 / 8 = 8），这个 pendingIdx 落在第几个字节里
                    int currentByte = NetConstants.ChanneledHeaderSize + pendingIdx / BitsInByte;
                    // 在一个字节中，该包对应的是哪一位
                    int currentBit = pendingIdx % BitsInByte;
                    // 左移N位，判断bit上的字节是否为0
                    if ((acksData[currentByte] & (1 << currentBit)) == 0)
                    {
                        // 开启统计功能，系统增加丢包计数
                        if (Peer.NetManager.EnableStatistics)
                        {
                            Peer.Statistics.IncrementPacketLoss();
                            Peer.NetManager.Statistics.IncrementPacketLoss();
                        }

                        //Skip false ack
                        NetDebug.Write($"[PA]False ack: {pendingSeq}");
                        continue;
                    }

                    // 当目前最老的一个包被确认收到后，将发送窗口的起点向后移动
                    if (pendingSeq == _localWindowStart)
                    {
                        //Move window
                        _localWindowStart = (_localWindowStart + 1) % NetConstants.MaxSequence;
                    }

                    //clear packet
                    // 真正接受数据的函数
                    if (_pendingPackets[pendingIdx].Clear(Peer))
                        NetDebug.Write($"[PA]Removing reliableInOrder ack: {pendingSeq} - true");
                }
            }
        }

        public override bool SendNextPackets()
        {
            // 发任何数据前，先检查是否有ACK需要发送
            if (_mustSendAcks)
            {
                _mustSendAcks = false;
                NetDebug.Write("[RR]SendAcks");
                lock(_outgoingAcks)
                    Peer.SendUserData(_outgoingAcks);
            }

            long currentTime = DateTime.UtcNow.Ticks;
            bool hasPendingPackets = false;

            lock (_pendingPackets)
            {
                //get packets from queue
                lock (OutgoingQueue)
                {
                    while (OutgoingQueue.Count > 0)
                    {
                        // 下一个准备分配的序号，una
                        // 一旦单次从queue转移到buf的packet序号超出una+窗口，就不允许再转移（发送）了
                        // 一定要记住这里，这里是限制发包的关键，后面看到ACK跳变的时候会考虑产生死锁，其实这里已经限制住了
                        int relate = NetUtils.RelativeSequenceNumber(_localSeqence, _localWindowStart);
                        if (relate >= _windowSize)
                            break;
                        // 从queue内部取出合并包或普通包
                        var netPacket = GetNextOutgoingPacket();
                        netPacket.Sequence = (ushort) _localSeqence;
                        netPacket.ChannelId = _id;
                        // 将queue中的内容真正移入buf，像KCP一样
                        _pendingPackets[_localSeqence % _windowSize].Init(netPacket);
                        _localSeqence = (_localSeqence + 1) % NetConstants.MaxSequence;
                    }
                }

                //send
                for (int pendingSeq = _localWindowStart; pendingSeq != _localSeqence; pendingSeq = (pendingSeq + 1) % NetConstants.MaxSequence)
                {
                    // 这里的发送逻辑包括：1.从来没发送过的 2.超过RTO的 3.？？
                    // Please note: TrySend is invoked on a mutable struct, it's important to not extract it into a variable here
                    // 值类型，不要拿出来用，否则为深拷贝对原始值不造成影响
                    if (_pendingPackets[pendingSeq % _windowSize].TrySend(currentTime, Peer))
                        hasPendingPackets = true;
                }
            }

            return hasPendingPackets || _mustSendAcks || OutgoingQueue.Count > 0;
        }

        //ProcessChannel（Peer）中调用
        public override bool ProcessPacket(NetPacket packet)
        {
            if (packet.Property == PacketProperty.Ack)
            {
                ProcessAck(packet);
                return false;
            }
            int seq = packet.Sequence;
            if (seq >= NetConstants.MaxSequence)
            {
                NetDebug.Write("[RR]Bad sequence");
                return false;
            }

            // 当前序号到接收端窗口的起点(本地目前还没收到的、目前正在等待的最小序号（期望的下一个包）)的距离
            int relate = NetUtils.RelativeSequenceNumber(seq, _remoteWindowStart);
            // 当前序号到（从远端收到的、目前为止的最大序号）的距离
            int relateSeq = NetUtils.RelativeSequenceNumber(seq, _remoteSequence);

            //（10（已发送ACK），_remoteWindowStart，12,13，_remoteSequence）


            // 新来的包序号，比目前为止的最大序号还要超出一整个窗口
            if (relateSeq > _windowSize)
            {
                NetDebug.Write("[RR]Bad sequence");
                return false;
            }

            // 新来的包序号比本地目前还没收到的、目前正在等待的最小序号更小
            //Drop bad packets
            if (relate < 0)
            {
                //Too old packet doesn't ack
                NetDebug.Write("[RR]ReliableInOrder too old");
                return false;
            }
            // ？新来的包序号超出本地目前还没收到的、目前正在等待的最小序号整整两个窗口
            if (relate >= _windowSize * 2)
            {
                //Some very new packet
                NetDebug.Write("[RR]ReliableInOrder too new");
                return false;
            }

            //If very new - move window
            int ackIdx;
            int ackByte;
            int ackBit;
            lock (_outgoingAcks)
            {
                if (relate >= _windowSize)
                {
                    //New window position
                    // 位图移动，但是位图的最后一位必须是新的seq
                    int newWindowStart = (_remoteWindowStart + relate - _windowSize + 1) % NetConstants.MaxSequence;
                    _outgoingAcks.Sequence = (ushort) newWindowStart;

                    //Clean old data
                    // // 只要当前窗口起点还没追上新的起点
                    while (_remoteWindowStart != newWindowStart)
                    {
                        // 计算当前要清理的那个旧序号在位图里的位置
                        ackIdx = _remoteWindowStart % _windowSize;
                        ackByte = NetConstants.ChanneledHeaderSize + ackIdx / BitsInByte;
                        ackBit = ackIdx % BitsInByte;
                        // 清零
                        _outgoingAcks.RawData[ackByte] &= (byte) ~(1 << ackBit);
                        // 窗口起点向后挪
                        _remoteWindowStart = (_remoteWindowStart + 1) % NetConstants.MaxSequence;
                    }
                }

                //Final stage - process valid packet
                //trigger acks send
                _mustSendAcks = true;

                // 当一个可靠包到达时： 1.这个包以前是不是收过 2、在位图（Bitmask）里把这个包对应的位置标记为 1，告诉发送方
                // 注意一个假设的死锁：本地发的ACK对面没收到，但对面有发了新的序号，导致ACK更新；对面的旧序号RTO触发重传，结果本地已经没有旧序号的ACK缓存
                // 这个死锁的悖论是：如果对面收不到ACK，那他不可能发出比ACK内有效最大序号更大的包序号
                ackIdx = seq % _windowSize;
                ackByte = NetConstants.ChanneledHeaderSize + ackIdx / BitsInByte;
                ackBit = ackIdx % BitsInByte;
                // 收到重复确认的包
                if ((_outgoingAcks.RawData[ackByte] & (1 << ackBit)) != 0)
                {
                    NetDebug.Write("[RR]ReliableInOrder duplicate");
                    //because _mustSendAcks == true
                    // 强制在下一个 Tick 周期再次发送一份最新的 ACK 位图给对端
                    AddToPeerChannelSendQueue();
                    return false;
                }

                //save ack
                // 将对应Idx的byte设置为1，确认其收到
                _outgoingAcks.RawData[ackByte] |= (byte) (1 << ackBit);
            }

            AddToPeerChannelSendQueue();

            // 如果收到的包有序
            //detailed check
            if (seq == _remoteSequence)
            {
                NetDebug.Write("[RR]ReliableInOrder packet succes");
                ProcessIncomingPacket(packet);
                _remoteSequence = (_remoteSequence + 1) % NetConstants.MaxSequence;

                if (_ordered)
                {
                    NetPacket p;
                    //如果之前有因为乱序而被挂起的包，那么当前_remoteSequence++后，挂起队列应该能找到乱序包
                    while ((p = _receivedPackets[_remoteSequence % _windowSize]) != null)
                    {
                        //process holden packet
                        _receivedPackets[_remoteSequence % _windowSize] = null;
                        ProcessIncomingPacket(p);
                        _remoteSequence = (_remoteSequence + 1) % NetConstants.MaxSequence;
                    }
                }
                else
                {
                    while (_earlyReceived[_remoteSequence % _windowSize])
                    {
                        //process early packet
                        // 将该位置重置为 false，为了下一轮序号回绕复用
                        _earlyReceived[_remoteSequence % _windowSize] = false;
                        // 因为乱序，该序号已被处理过，直接跳过而不处理
                        _remoteSequence = (_remoteSequence + 1) % NetConstants.MaxSequence;
                    }
                }
                return true;
            }

            // 如果收到的包无序
            // holden packet
            if (_ordered)
            {
                // 先缓存，等待有序包
                _receivedPackets[ackIdx] = packet;
            }
            else
            {
                // 先标记再直接处理
                _earlyReceived[ackIdx] = true;
                ProcessIncomingPacket(packet);
            }
            return true;
        }
    }
}
