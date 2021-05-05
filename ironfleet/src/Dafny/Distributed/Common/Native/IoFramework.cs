using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks.Dataflow;

namespace IronfleetIoFramework
{
  public class IoEncoder
  {
    private static int MAX_IO_SIZE = 0x80_0000 /* 8 MB */;

    public static bool ReadBytes(Stream stream, byte[] buf, int offset, UInt64 byteCount)
    {
      UInt64 bytesRead = 0;
      while (bytesRead < byteCount)
      {
        int bytesToRead = (byteCount - bytesRead > (UInt64)(MAX_IO_SIZE)) ? MAX_IO_SIZE : (int)(byteCount - bytesRead);
        int additionalBytesRead = stream.Read(buf, offset + (int)bytesRead, bytesToRead);
        if (additionalBytesRead == 0) {
          return false;
        }
        bytesRead += (UInt64)additionalBytesRead;
      }
      return true;
    }

    public static void WriteBytes(Stream stream, byte[] buf, int offset, UInt64 byteCount)
    {
      UInt64 bytesWritten = 0;
      while (bytesWritten < byteCount)
      {
        int bytesToWrite = (byteCount - bytesWritten > (UInt64)(MAX_IO_SIZE)) ? MAX_IO_SIZE : (int)(byteCount - bytesWritten);
        stream.Write(buf, offset + (int)bytesWritten, bytesToWrite);
        bytesWritten += (UInt64)(bytesToWrite);
      }
    }

    public static UInt64 ExtractUInt64(byte[] bytes, int offset)
    {
      byte[] extractedBytes = bytes.Skip(offset).Take(8).ToArray();
      if (BitConverter.IsLittleEndian) {
        Array.Reverse(extractedBytes);
      }
      return BitConverter.ToUInt64(extractedBytes, 0);
    }

    public static UInt32 ExtractUInt32(byte[] bytes, int offset)
    {
      byte[] extractedBytes = bytes.Skip(offset).Take(4).ToArray();
      if (BitConverter.IsLittleEndian) {
        Array.Reverse(extractedBytes);
      }
      return BitConverter.ToUInt32(extractedBytes, 0);
    }

    public static Int32 ExtractInt32(byte[] bytes, int offset)
    {
      byte[] extractedBytes = bytes.Skip(offset).Take(4).ToArray();
      if (BitConverter.IsLittleEndian) {
        Array.Reverse(extractedBytes);
      }
      return BitConverter.ToInt32(extractedBytes, 0);
    }

    public static void WriteUInt64(Stream stream, UInt64 value)
    {
      var bytes = BitConverter.GetBytes(value);
      if (BitConverter.IsLittleEndian) {
        Array.Reverse(bytes);
      }
      WriteBytes(stream, bytes, 0, 8);
    }

    public static void WriteUInt32(Stream stream, UInt32 value)
    {
      var bytes = BitConverter.GetBytes(value);
      if (BitConverter.IsLittleEndian) {
        Array.Reverse(bytes);
      }
      WriteBytes(stream, bytes, 0, 4);
    }

    public static void WriteInt32(Stream stream, Int32 value)
    {
      var bytes = BitConverter.GetBytes(value);
      if (BitConverter.IsLittleEndian) {
        Array.Reverse(bytes);
      }
      WriteBytes(stream, bytes, 0, 4);
    }

    public static bool ReadUInt64(Stream stream, out UInt64 value)
    {
      byte[] buf8 = new byte[8];
      bool success = ReadBytes(stream, buf8, 0, 8);
      if (success) {
        if (BitConverter.IsLittleEndian) {
          Array.Reverse(buf8);
        }
        value = BitConverter.ToUInt64(buf8);
      }
      else {
        value = 0;
      }
      return success;
    }

    public static bool ReadUInt32(Stream stream, out UInt32 value)
    {
      byte[] buf4 = new byte[4];
      bool success = ReadBytes(stream, buf4, 0, 4);
      if (success) {
        if (BitConverter.IsLittleEndian) {
          Array.Reverse(buf4);
        }
        value = BitConverter.ToUInt32(buf4);
      }
      else {
        value = 0;
      }
      return success;
    }

    public static bool ReadInt32(Stream stream, out Int32 value)
    {
      byte[] buf4 = new byte[4];
      bool success = ReadBytes(stream, buf4, 0, 4);
      if (success) {
        if (BitConverter.IsLittleEndian) {
          Array.Reverse(buf4);
        }
        value = BitConverter.ToInt32(buf4);
      }
      else {
        value = 0;
      }
      return success;
    }
  }

  public struct Packet
  {
    public IPEndPoint ep;
    public byte[] message;
  }

  public class ReceiverThread
  {
    private IoScheduler scheduler;
    private TcpClient conn;
    private IPEndPoint otherEndpoint;
    private bool asServer;
    private NetworkStream stream;

    private ReceiverThread(IoScheduler i_scheduler, TcpClient i_conn, IPEndPoint i_otherEndpoint, bool i_asServer)
    {
      scheduler = i_scheduler;
      conn = i_conn;
      otherEndpoint = i_otherEndpoint;
      asServer = i_asServer;
      stream = conn.GetStream();
    }

    public void Run()
    {
      try
      {
        ReceiveLoop();
      }
      catch (Exception e)
      {
        if (scheduler.ShouldPrintException(e)) {
          Console.Error.WriteLine("Receiver thread caught the following exception, so terminating:\n{0}", e);
        }
      }
    }

    public static ReceiverThread Create(IoScheduler scheduler, TcpClient conn, IPEndPoint otherEndpoint, bool asServer)
    {
      ReceiverThread receiverThread = new ReceiverThread(scheduler, conn, otherEndpoint, asServer);
      Thread t = new Thread(receiverThread.Run);
      t.Start();
      return receiverThread;
    }

    private void ReceiveLoop()
    {
      bool success;

      if (scheduler.Verbose) {
        Console.WriteLine("Starting receive loop with other endpoint {0}", IoScheduler.EndpointToString(otherEndpoint));
      }

      // The first thing sent by the client is its effective port
      // number.

      if (asServer) {
        Int32 portNumber;
        success = IoEncoder.ReadInt32(stream, out portNumber);
        if (!success)
        {
          Console.Error.WriteLine("Failed to receive port number from {0}", IoScheduler.EndpointToString(otherEndpoint));
          return;
        }
        if (scheduler.Verbose) {
          Console.WriteLine("Received effective port number {0} from {1}", portNumber, IoScheduler.EndpointToString(otherEndpoint));
        }
        otherEndpoint.Port = portNumber;

        // Now that we know who we're talking to, we can create the
        // sender thread.

        SenderThread senderThread = SenderThread.Create(scheduler, conn, otherEndpoint, asServer);
      }

      while (true)
      {
        // Read the next message's size.

        UInt64 messageSize;
        success = IoEncoder.ReadUInt64(stream, out messageSize);
        if (!success) {
          Console.Error.WriteLine("Failed to receive message size from {0}", IoScheduler.EndpointToString(otherEndpoint));
          return;
        }
        if (scheduler.Verbose) {
          Console.WriteLine("Received message size {0} from {1}", messageSize, IoScheduler.EndpointToString(otherEndpoint));
        }

        byte[] messageBuf = new byte[messageSize];
        success = IoEncoder.ReadBytes(stream, messageBuf, 0, messageSize);
        if (!success) {
          Console.Error.WriteLine("Failed to receive message of size {0} from {1}",
                                  messageSize, IoScheduler.EndpointToString(otherEndpoint));
          return;
        }
        if (scheduler.Verbose) {
          Console.WriteLine("Received message of size {0} from {1}", messageSize, IoScheduler.EndpointToString(otherEndpoint));
        }

        Packet packet = new Packet { ep = otherEndpoint, message = messageBuf };
        scheduler.NoteReceivedPacket(packet);
      }
    }
  }

  public class SenderThread
  {
    private IoScheduler scheduler;
    private TcpClient conn;
    private IPEndPoint otherEndpoint;
    private bool asServer;
    private NetworkStream stream;
    private BufferBlock<byte[]> sendQueue;
    private byte[] currentMessage;

    private SenderThread(IoScheduler i_scheduler, TcpClient i_conn, IPEndPoint i_otherEndpoint, bool i_asServer)
    {
      scheduler = i_scheduler;
      conn = i_conn;
      otherEndpoint = i_otherEndpoint;
      asServer = i_asServer;
      sendQueue = new BufferBlock<byte[]>();
      currentMessage = null;
    }

    public void Run()
    {
      try
      {
        SendLoop();
      }
      catch (Exception e)
      {
        if (scheduler.ShouldPrintException(e)) {
          Console.Error.WriteLine("Sender thread caught the following exception, so terminating:\n{0}", e);
        }
      }

      scheduler.UnregisterSender(otherEndpoint, this);

      // If we crashed in the middle of sending a packet, re-queue it
      // for sending by another sender thread.
      
      if (currentMessage != null) {
        scheduler.SendPacket(otherEndpoint, currentMessage);
        currentMessage = null;
      }

      // If there are packets queued for us to send, re-queue them
      // for sending by another sender thread.

      while (sendQueue.TryReceive(out currentMessage)) {
        scheduler.SendPacket(otherEndpoint, currentMessage);
        currentMessage = null;
      }
    }

    public static SenderThread Create(IoScheduler scheduler, TcpClient conn, IPEndPoint otherEndpoint, bool asServer)
    {
      if (scheduler.Verbose) {
        Console.WriteLine("Creating sender thread for endpoint {0}", IoScheduler.EndpointToString(otherEndpoint));
      }
      
      SenderThread senderThread = new SenderThread(scheduler, conn, otherEndpoint, asServer);
      scheduler.RegisterSender(otherEndpoint, senderThread);
      Thread t = new Thread(senderThread.Run);
      t.Start();
      return senderThread;
    }

    private void SendLoop()
    {
      if (scheduler.Verbose) {
        Console.WriteLine("Starting send loop with other endpoint {0}", IoScheduler.EndpointToString(otherEndpoint));
      }

      // If we're not the server, we need to initiate the connection.

      if (!asServer) {
        var zeroEndpoint = new IPEndPoint(scheduler.MyEndpoint.Address, 0);

        try {
          conn = new TcpClient(zeroEndpoint);
        }
        catch (Exception e)
        {
          Console.Error.WriteLine("Could not bind to address {0} to send to {1}, causing exception:\n{2}",
                                  zeroEndpoint.Address, IoScheduler.EndpointToString(otherEndpoint), e);
          throw;
        }

        if (scheduler.Verbose) {
          Console.WriteLine("Starting connection to {0}", IoScheduler.EndpointToString(otherEndpoint));
        }
        conn.Connect(otherEndpoint);

        // Now that the connection is successful, create a thread to
        // receive packets on it.

        ReceiverThread receiverThread = ReceiverThread.Create(scheduler, conn, otherEndpoint, asServer);
      }
      
      stream = conn.GetStream();

      // The first thing sent by the client is its effective port
      // number.

      if (!asServer) {
        Int32 myPortNumber = scheduler.MyEndpoint.Port;
        if (scheduler.Verbose) {
          Console.WriteLine("Sending my effective port number {0} to {1}", myPortNumber, IoScheduler.EndpointToString(otherEndpoint));
        }
        IoEncoder.WriteInt32(stream, myPortNumber);
      }

      while (true)
      {
        // Wait for there to be a packet to send.

        currentMessage = sendQueue.Receive();

        // Send its length as an 8-byte value.

        UInt64 messageSize = (UInt64)currentMessage.Length;
        IoEncoder.WriteUInt64(stream, messageSize);
        if (scheduler.Verbose) {
          Console.WriteLine("Sent message size {0} to {1}", messageSize, IoScheduler.EndpointToString(otherEndpoint));
        }

        // Send its contents.

        IoEncoder.WriteBytes(stream, currentMessage, 0, messageSize);
        if (scheduler.Verbose) {
          Console.WriteLine("Sent message of size {0} to {1}", messageSize, IoScheduler.EndpointToString(otherEndpoint));
        }

        // Set the currentMessage to null so we know we don't have to
        // resend it if the connection fails.

        currentMessage = null;
      }
    }

    public void EnqueueMessage(byte[] message)
    {
      sendQueue.Post(message);
    }
  }

  public class ListenerThread
  {
    private IoScheduler scheduler;
    private TcpListener listener;

    public ListenerThread(IoScheduler i_scheduler)
    {
      scheduler = i_scheduler;
    }

    public void Run()
    {
      while (true)
      {
        try
        {
          ListenLoop();
        }
        catch (Exception e)
        {
          Console.Error.WriteLine("Listener thread caught the following exception, so restarting:\n{0}", e);
        }
      }
    }

    private void ListenLoop()
    {
      listener = new TcpListener(scheduler.MyEndpoint);
      listener.Start();
      while (true)
      {
        if (scheduler.Verbose) {
          Console.WriteLine("Waiting for the next incoming connection.");
        }
        TcpClient client = listener.AcceptTcpClient();
        if (client.Client.RemoteEndPoint.AddressFamily != AddressFamily.InterNetwork)
        {
          Console.Error.WriteLine("Ignoring incoming connection from non-IPv4 source");
          continue;
        }
        IPEndPoint clientEndpoint = (IPEndPoint)client.Client.RemoteEndPoint;
        if (scheduler.Verbose) {
          Console.WriteLine("Received an incoming connection from {0}", IoScheduler.EndpointToString(clientEndpoint));
        }

        ReceiverThread receiverThread = ReceiverThread.Create(scheduler, client, clientEndpoint, true /* as server */);
      }
    }
  }

  public class SendDispatchThread
  {
    private IoScheduler scheduler;
    private BufferBlock<Packet> sendQueue;

    public SendDispatchThread(IoScheduler i_scheduler)
    {
      scheduler = i_scheduler;
      sendQueue = new BufferBlock<Packet>();
    }

    public void Run()
    {
      while (true)
      {
        try
        {
          SendDispatchLoop();
        }
        catch (Exception e)
        {
          Console.Error.WriteLine("Send dispatch thread caught the following exception, so restarting:\n{0}", e);
        }
      }
    }

    private void SendDispatchLoop()
    {
      while (true)
      {
        if (scheduler.Verbose) {
          Console.WriteLine("Waiting for the next send to dispatch.");
        }

        Packet packet = sendQueue.Receive();

        if (scheduler.Verbose) {
          Console.WriteLine("Dispatching send of packet of size {0} to {1}",
                            packet.message.Length, IoScheduler.EndpointToString(packet.ep));
        }

        SenderThread senderThread = scheduler.FindSenderForEndpoint(packet.ep);

        if (senderThread == null) {
          senderThread = SenderThread.Create(scheduler, null, packet.ep, false);
        }

        senderThread.EnqueueMessage(packet.message);
      }
    }

    public void EnqueuePacket(Packet packet)
    {
      sendQueue.Post(packet);
    }
  }

  public class IoScheduler
  {
    private IPEndPoint myEndpoint;
    private bool onlyClient;
    private bool verbose;
    private BufferBlock<Packet> receiveQueue;
    private Dictionary<IPEndPoint, List<SenderThread>> endpointToSenderMap;
    private ListenerThread listenerThread;
    private SendDispatchThread sendDispatchThread;

    public IoScheduler(IPEndPoint i_myEndpoint, bool i_onlyClient = false, bool i_verbose = false)
    {
      myEndpoint = i_myEndpoint;
      onlyClient = i_onlyClient;
      verbose = i_verbose;
      if (verbose) {
        Console.WriteLine("Starting I/O scheduler with endpoint {0}, onlyClient = {1}", EndpointToString(i_myEndpoint), onlyClient);
      }
      receiveQueue = new BufferBlock<Packet>();
      endpointToSenderMap = new Dictionary<IPEndPoint, List<SenderThread>>();
      Start();
    }

    private void Start()
    {
      sendDispatchThread = new SendDispatchThread(this);
      Thread st = new Thread(sendDispatchThread.Run);
      st.Start();

      // Start a thread to listen on a port bound to MyEndpoint.  But,
      // if we're only supposed to connect as a client, don't do this.

      if (!onlyClient) {
        listenerThread = new ListenerThread(this);
        Thread lt = new Thread(listenerThread.Run);
        lt.Start();
      }
    }

    public IPEndPoint MyEndpoint { get { return myEndpoint; } }
    public bool Verbose { get { return verbose; } }
    public bool OnlyClient { get { return onlyClient; } }

    /////////////////////////////////////
    // SENDING
    /////////////////////////////////////

    public void RegisterSender(IPEndPoint ep, SenderThread senderThread)
    {
      lock (endpointToSenderMap)
      {
        if (endpointToSenderMap.ContainsKey(ep)) {
          endpointToSenderMap[ep].Insert(0, senderThread);
        }
        else {
          endpointToSenderMap[ep] = new List<SenderThread> { senderThread };
        }
      }
    }

    public void UnregisterSender(IPEndPoint ep, SenderThread senderThread)
    {
      lock (endpointToSenderMap)
      {
        endpointToSenderMap[ep].Remove(senderThread);
      }
    }

    public SenderThread FindSenderForEndpoint(IPEndPoint ep)
    {
      lock (endpointToSenderMap)
      {
        if (endpointToSenderMap.ContainsKey(ep) && endpointToSenderMap[ep].Count > 0) {
          return endpointToSenderMap[ep][0];
        }
      }

      return null;
    }

    /////////////////////////////////////
    // RECEIVING
    /////////////////////////////////////

    public void NoteReceivedPacket(Packet packet)
    {
      receiveQueue.Post(packet);
    }

    /////////////////////////////////////
    // UTILITY METHODS
    /////////////////////////////////////

    public static string EndpointToString(IPEndPoint ep)
    {
      return string.Format("{0}:{1}", ep.Address, ep.Port);
    }

    public bool ShouldPrintException(Exception e)
    {
      if (verbose) {
        return true;
      }
      if (e is IOException ioe) {
        e = ioe.InnerException;
      }
      if (e is SocketException se) {
        if (se.ErrorCode == 10054 /* WSAECONNRESET */) {
          // Don't bother reporting the benign error of a connection reset.
          return false;
        }
      }
      return true;
    }

    ///////////////////////////////////
    // API for IoNative.cs
    ///////////////////////////////////

    public void ReceivePacket(Int32 timeLimit, out bool ok, out bool timedOut, out IPEndPoint remote, out byte[] message)
    {
      Packet packet;

      try {
        if (timeLimit == 0) {
          timedOut = !receiveQueue.TryReceive(out packet);
        }
        else {
          TimeSpan timeSpan = TimeSpan.FromMilliseconds(timeLimit);
          packet = receiveQueue.Receive(timeSpan);
          timedOut = false;
        }

        ok = true;
        if (timedOut) {
          remote = null;
          message = null;
        }
        else {
          remote = new IPEndPoint(packet.ep.Address, packet.ep.Port);
          message = packet.message;
          if (verbose) {
            Console.WriteLine("Dequeueing a packet of size {0} from {1}", packet.message.Length, EndpointToString(packet.ep));
          }
        }
      }
      catch (TimeoutException) {
        remote = null;
        message = null;
        ok = true;
        timedOut = true;
      }
      catch (Exception e) {
        Console.Error.WriteLine("Unexpected error trying to read packet from packet queue. Exception:\n{0}", e);
        remote = null;
        message = null;
        ok = false;
        timedOut = false;
      }
    }

    public bool SendPacket(IPEndPoint remote, byte[] message)
    {
      try {
        byte[] messageCopy = new byte[message.Length];
        Array.Copy(message, messageCopy, message.Length);
        Packet packet = new Packet { ep = remote, message = messageCopy };
        if (verbose) {
          Console.WriteLine("Enqueueing a packet of size {0} to {1}", packet.message.Length, EndpointToString(packet.ep));
        }
        sendDispatchThread.EnqueuePacket(packet);
        return true;
      }
      catch (Exception e) {
        Console.Error.WriteLine("Unexpected error when trying to send a packet.  Exception:\n{0}", e);
        return false;
      }
    }

    ///////////////////////////////////
    // Extra API calls for client
    ///////////////////////////////////

    public void Connect(IPEndPoint otherEndpoint)
    {
      SenderThread senderThread = FindSenderForEndpoint(otherEndpoint);
      if (senderThread == null) {
        senderThread = SenderThread.Create(this, null, otherEndpoint, false);
      }
    }
  }
}
