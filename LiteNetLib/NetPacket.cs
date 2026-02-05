using System;
using LiteNetLib.Utils;

namespace LiteNetLib
{
    internal enum PacketProperty : byte
    {
        Unreliable,
        Channeled,
        ReliableMerged,
        Ack,
        Ping,
        Pong,
        ConnectRequest,
        ConnectAccept,
        Disconnect,
        UnconnectedMessage,
        MtuCheck,
        MtuOk,
        Broadcast,
        Merged,
        ShutdownOk,
        PeerNotFound,
        InvalidProtocol,
        NatMessage,
        Empty,
        Total
    }

    internal sealed class NetPacket
    {
        private static readonly int PropertiesCount = (int)PacketProperty.Total;
        private static readonly int[] HeaderSizes;

        static NetPacket()
        {
            // 使用 new int[size] 分配数组时，CLR 会自动将内存中的所有值清零
            // 通过 Pinned 分配，该数组在整个生命周期内拥有恒定的物理内存地址，方便进行指针操作（Pointer Math）或零拷贝传递
            HeaderSizes = NetUtils.AllocatePinnedUninitializedArray<int>(PropertiesCount);
            // 这里就是有多少Prop创造多长的数组，一个个往里填对应长度，这样不同的Prop找长度就方便
            for (int i = 0; i < HeaderSizes.Length; i++)
            {
                switch ((PacketProperty)i)
                {
                    case PacketProperty.Channeled:
                    case PacketProperty.Ack:
                    case PacketProperty.ReliableMerged:
                        HeaderSizes[i] = NetConstants.ChanneledHeaderSize;
                        break;
                    case PacketProperty.Ping:
                        HeaderSizes[i] = NetConstants.HeaderSize + 2;
                        break;
                    case PacketProperty.ConnectRequest:
                        // 注意这里直接访问握手包的const报头长度，写的有点奇怪
                        HeaderSizes[i] = NetConnectRequestPacket.HeaderSize;
                        break;
                    case PacketProperty.ConnectAccept:
                        HeaderSizes[i] = NetConnectAcceptPacket.Size;
                        break;
                    case PacketProperty.Disconnect:
                        HeaderSizes[i] = NetConstants.HeaderSize + 8;
                        break;
                    case PacketProperty.Pong:
                        HeaderSizes[i] = NetConstants.HeaderSize + 10;
                        break;
                    default:
                        HeaderSizes[i] = NetConstants.HeaderSize;
                        break;
                }
            }
        }

        //Header

        // 属性
        public PacketProperty Property
        {
            // 数据包的第一个字节
            // 二进制 0001 1111
            get => (PacketProperty)(RawData[0] & 0x1F);
            // 1110 0000
            set => RawData[0] = (byte)((RawData[0] & 0xE0) | (byte)value);
        }

        // 连接号
        public byte ConnectionNumber
        {
            // 0110 0000
            get => (byte)((RawData[0] & 0x60) >> 5);
            // 1001 1111
            set => RawData[0] = (byte) ((RawData[0] & 0x9F) | (value << 5));
        }

        // 有序性
        public ushort Sequence
        {
            // 序号从 RawData 的第 2 个字节（索引为 1）开始存储
            // 使用 ushort（无符号短整型），占用 2 个字节
            get => BitConverter.ToUInt16(RawData, 1);
            // 0 GC 创建 byte[2]
            set => FastBitConverter.GetBytes(RawData, 1, value);
        }

        // 是否分片
        // 1000 0000
        public bool IsFragmented => (RawData[0] & 0x80) != 0;

        public void MarkFragmented() => RawData[0] |= 0x80;

        // 通道号
        public byte ChannelId
        {
            get => RawData[3];
            set => RawData[3] = value;
        }

        // 当前碎片批次
        public ushort FragmentId
        {
            get => BitConverter.ToUInt16(RawData, 4);
            set => FastBitConverter.GetBytes(RawData, 4, value);
        }

        // 当前碎片序号
        public ushort FragmentPart
        {
            get => BitConverter.ToUInt16(RawData, 6);
            set => FastBitConverter.GetBytes(RawData, 6, value);
        }

        // 总碎片数
        public ushort FragmentsTotal
        {
            get => BitConverter.ToUInt16(RawData, 8);
            set => FastBitConverter.GetBytes(RawData, 8, value);
        }

        //Data
        public byte[] RawData;
        public int Size;

        //Delivery
        public object UserData;

        //Pool node
        public NetPacket Next;

        public NetPacket(int size)
        {
            RawData = new byte[size];
            Size = size;
        }

        public NetPacket(PacketProperty property, int size)
        {
            size += GetHeaderSize(property);
            RawData = new byte[size];
            Property = property;
            Size = size;
        }

        public static int GetHeaderSize(PacketProperty property) => HeaderSizes[(int)property];

        // 0001 1111，就是获取property, 从数组中找到对应类型的报头长度
        public int HeaderSize => HeaderSizes[RawData[0] & 0x1F];

        // 报头合法性判断
        public bool Verify()
        {
            byte property = (byte)(RawData[0] & 0x1F);
            if (property >= PropertiesCount)
                return false;
            int headerSize = HeaderSizes[property];
            bool fragmented = (RawData[0] & 0x80) != 0;
            return Size >= headerSize && (!fragmented || Size >= headerSize + NetConstants.FragmentHeaderSize);
        }

        public static implicit operator Span<byte>(NetPacket p) => new Span<byte>(p.RawData, 0, p.Size);
    }
}
