using Celeste.Mod.CelesteNet.DataTypes;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Threading;

namespace Celeste.Mod.CelesteNet.Server {
    /*
    The TCP sender role gets specified as the queue flusher for the TCP send
    queue. When a queue needs to be flushed, it get's added to a queue of all
    waiting queues (queueception), which workers pull from to actually send
    packets. It also does some fancy buffering.
    - Popax21
    */
    public class TCPSenderRole : NetPlusThreadRole {

        private class Worker : RoleWorker {

            private BufferedSocketStream sockStream;
            private BinaryWriter sockWriter;
            private MemoryStream packetStream;
            private CelesteNetBinaryWriter packetWriter;

            public Worker(TCPSenderRole role, NetPlusThread thread) : base(role, thread) {
                sockStream = new BufferedSocketStream(role.Server.Settings.TCPBufferSize);
                sockWriter = new BinaryWriter(sockStream);
                packetStream = new MemoryStream(role.Server.Settings.MaxPacketSize);
                packetWriter = new CelesteNetBinaryWriter(role.Server.Data, null, packetStream);
            }

            public override void Dispose() {
                sockStream.Dispose();
                sockWriter.Dispose();
                packetStream.Dispose();
                packetWriter.Dispose();
                base.Dispose();
            }

            protected internal override void StartWorker(CancellationToken token) {
                foreach (CelesteNetSendQueue queue in Role.queueQueue.GetConsumingEnumerable(token)) {
                    CelesteNetTCPUDPConnection con = (CelesteNetTCPUDPConnection) queue.Con;
                    try {
                        sockStream.Socket = con.TCPSocket;
                        packetWriter.Strings = con.TCPStrings;

                        // Write all packets
                        foreach (DataType packet in queue.BackQueue) {
                            // Write the packet onto the temporary packet stream
                            packetStream.Position = 0;
                            if (packet is DataInternalBlob blob)
                                blob.Dump(packetWriter);
                            else
                                Role.Server.Data.Write(packetWriter, packet);

                            // Write size and raw packet data into the actual stream
                            sockWriter.Write((UInt16) packetStream.Position);
                            sockStream.Write(packetStream.GetBuffer(), 0, (int) packetStream.Position);
                        }

                        sockStream.Flush();
                    } catch (Exception e) {
                        // If the client closed the connection, just close the connection too
                        if (e is SocketException se && se.SocketErrorCode == SocketError.NotConnected) {
                            con.Dispose();
                            continue;
                        }

                        Logger.Log(LogLevel.WRN, "tcpsend", $"Error flushing connection {con} queue '{queue.Name}': {e}");
                        con.Dispose();
                    }
                }
            }

            public new TCPSenderRole Role => (TCPSenderRole) base.Role;

        }

        private BlockingCollection<CelesteNetSendQueue> queueQueue;

        public TCPSenderRole(NetPlusThreadPool pool, CelesteNetServer server) : base(pool) {
            Server = server;
            queueQueue = new BlockingCollection<CelesteNetSendQueue>();
        }

        public override void Dispose() {
            queueQueue.Dispose();
            base.Dispose();
        }

        public override RoleWorker CreateWorker(NetPlusThread thread) => new Worker(this, thread);
        
        public void TriggerQueueClear(CelesteNetSendQueue queue) => queueQueue.Add(queue);

        public override int MinThreads => 1;
        public override int MaxThreads => int.MaxValue;

        public CelesteNetServer Server { get; }

    }
}