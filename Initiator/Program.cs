using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace InitiatorApp
{
    class Program
    {
        //UDP port that all recipient apps will listen on
        private const int DiscoveryUdpPort = 15000;

        static async Task Main(string[] args)
        {
            Console.WriteLine("Initiator App");

            //Input intake
            Console.Write("Enter recipient's nickname: ");
            string targetNickname = Console.ReadLine()?.Trim();

            Console.Write("Enter TCP port to listen on (default 55000): ");
            if (!int.TryParse(Console.ReadLine(), out int tcpPort)) tcpPort = 55000;

            Console.Write("Enter deadline timeout in seconds (default 60): ");
            if (!int.TryParse(Console.ReadLine(), out int timeoutSec)) timeoutSec = 60;

            //Generate protocol data
            Guid requestId = Guid.NewGuid();
            DateTime deadlineUtc = DateTime.UtcNow.AddSeconds(timeoutSec);

            //Start TCP listener first
            TcpListener tcpListener = null;
            try
            {
                tcpListener = new TcpListener(IPAddress.Any, tcpPort);
                tcpListener.Start();
                Console.WriteLine($"\n[INFO] Started TCP Listener on port {tcpPort}.");
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                Console.WriteLine($"\n[WARNING] Port {tcpPort} is already in use.");

                //Fallback in case multiple initiators run at the same time. bind to port 0, which tells the OS to assign any free port
                tcpListener = new TcpListener(IPAddress.Any, 0);
                tcpListener.Start();

                //Update our tcpPort variable so we send the correct one in the UDP broadcast
                tcpPort = ((IPEndPoint)tcpListener.LocalEndpoint).Port;
                Console.WriteLine($"[INFO] Automatically assigned and listening on free port {tcpPort}.");
            }

            //Send UDP broadcast
            using (UdpClient udpClient = new UdpClient())
            {
                udpClient.EnableBroadcast = true;

                //Format: DISCOVER;<RecipientNickname>;<DeadlineUtc>;<TcpPort>;<UUID>
                string discoverMessage = $"DISCOVER;{targetNickname};{deadlineUtc:O};{tcpPort};{requestId}";
                byte[] messageBytes = Encoding.UTF8.GetBytes(discoverMessage);

                IPEndPoint broadcastEndpoint = new IPEndPoint(IPAddress.Broadcast, DiscoveryUdpPort);
                await udpClient.SendAsync(messageBytes, messageBytes.Length, broadcastEndpoint);

                Console.WriteLine($"[INFO] Broadcasted discovery request for '{targetNickname}'.");
                Console.WriteLine($"[INFO] UUID: {requestId}");
            }

            //Wait for TCP connection with timeout
            Console.WriteLine($"[INFO] Waiting for '{targetNickname}' to connect...");

            using TcpClient client = await AcceptClientWithTimeoutAsync(tcpListener, deadlineUtc);

            if (client == null)
            {
                Console.WriteLine("[ERROR] Deadline expired. No recipient connected.");
                tcpListener.Stop();
                return;
            }

            Console.WriteLine($"\n[SUCCESS] Recipient connected from {client.Client.RemoteEndPoint}!");

            //Handshaking
            NetworkStream stream = client.GetStream();
            using StreamReader reader = new StreamReader(stream, Encoding.UTF8);
            using StreamWriter writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

            Console.WriteLine("[INFO] Waiting for handshake request from recipient...");

            //Read the request from the recipient
            string handshakeReq = await reader.ReadLineAsync();
            Console.WriteLine($"[INFO] Received: {handshakeReq}");

            bool isHandshakeValid = false;

            if (handshakeReq != null && handshakeReq.StartsWith("HANDSHAKE_REQ;"))
            {
                string[] parts = handshakeReq.Split(';');
                if (parts.Length == 2)
                {
                    string receivedUuid = parts[1];

                    //Validate UUID and Deadline
                    if (receivedUuid == requestId.ToString())
                    {
                        if (DateTime.UtcNow <= deadlineUtc)
                        {
                            isHandshakeValid = true;
                        }
                        else
                        {
                            Console.WriteLine("[WARNING] Handshake failed: Deadline expired.");
                            await writer.WriteLineAsync("HANDSHAKE_RESP;REJECT;Deadline expired.");
                        }
                    }
                    else
                    {
                        Console.WriteLine("[WARNING] Handshake failed: Invalid UUID.");
                        await writer.WriteLineAsync("HANDSHAKE_RESP;REJECT;Invalid UUID.");
                    }
                }
            }

            if (isHandshakeValid)
            {
                Console.WriteLine("\n[SUCCESS] Handshake verified! Sending ACCEPT.");
                await writer.WriteLineAsync("HANDSHAKE_RESP;ACCEPT;Handshake verified.");

                //Simplex message exchange
                Console.WriteLine("\nCHAT STARTED (You send first. Type '/exit' to quit)");

                while (true)
                {
                    //WAIT_SEND state
                    string myMessage = "";
                    while (string.IsNullOrWhiteSpace(myMessage))
                    {
                        Console.Write("[You]: ");
                        myMessage = Console.ReadLine();
                    }

                    if (myMessage.Trim().ToLower() == "/exit")
                    {
                        await writer.WriteLineAsync("CLOSE;User ended the chat");
                        break;
                    }

                    await writer.WriteLineAsync($"TEXT;{myMessage}");

                    //WAIT_RECV state
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine("Waiting for reply...");
                    Console.ResetColor();

                    string reply = await reader.ReadLineAsync();

                    //Handle unexpected drop or CLOSE request
                    if (reply == null || reply.StartsWith("CLOSE;"))
                    {
                        string reason = reply != null && reply.Split(';').Length > 1 ? reply.Split(';')[1] : "Connection dropped";
                        Console.WriteLine($"\n[INFO] Chat closed by Recipient. Reason: {reason}");
                        break;
                    }
                    else if (reply.StartsWith("TEXT;"))
                    {
                        //We use Substring(5) instead of Split(';') here, this allows the user's message to contain semicolons without breaking the app.
                        string content = reply.Substring(5);
                        Console.WriteLine($"[Recipient]: {content}");
                    }
                    else
                    {
                        Console.WriteLine("[ERROR] Protocol violation. Expected TEXT or CLOSE.");
                        await writer.WriteLineAsync("CLOSE;Protocol violation");
                        break;
                    }
                }

                Console.WriteLine("[INFO] Chat terminated. Press any key to exit.");
            }
            else
            {
                Console.WriteLine("[ERROR] Closing connection due to failed handshake.");
                client.Close();
                return; // Exit the app
            }
            Console.ReadLine(); //Keep window open for now
        }

        //Helper method to enforce the deadline on the TCP accept
        static async Task<TcpClient> AcceptClientWithTimeoutAsync(TcpListener listener, DateTime deadline)
        {
            TimeSpan timeToWait = deadline - DateTime.UtcNow;
            if (timeToWait <= TimeSpan.Zero) return null;

            var acceptTask = listener.AcceptTcpClientAsync();
            var timeoutTask = Task.Delay(timeToWait);

            var completedTask = await Task.WhenAny(acceptTask, timeoutTask);

            if (completedTask == acceptTask)
            {
                return await acceptTask; //Connection received
            }

            return null; //Timeout reached
        }
    }
}
