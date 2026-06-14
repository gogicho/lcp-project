using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace RecipientApp
{
    class Program
    {
        private const int DiscoveryUdpPort = 15000;

        static async Task Main(string[] args)
        {
            Console.WriteLine("Recipient App");

            Console.Write("Enter your nickname: ");
            string myNickname = Console.ReadLine()?.Trim();

            Console.WriteLine($"\n[INFO] Listening for discovery broadcasts on UDP port {DiscoveryUdpPort}...");

            //Setup UDP listener
            using UdpClient udpListener = new UdpClient(DiscoveryUdpPort);

            //Variables to hold parsed data
            string initiatorIp = "";
            int targetTcpPort = 0;
            string receivedUuid = "";

            //Loop until a valid message for this user is found
            while (true)
            {
                UdpReceiveResult result = await udpListener.ReceiveAsync();
                string receivedMessage = Encoding.UTF8.GetString(result.Buffer);
                initiatorIp = result.RemoteEndPoint.Address.ToString();

                string[] parts = receivedMessage.Split(';');

                //Validate message format
                if (parts.Length == 5 && parts[0] == "DISCOVER")
                {
                    string targetNickname = parts[1];
                    string deadlineStr = parts[2];

                    if (targetNickname.Equals(myNickname, StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"\n[INFO] Received discovery request from {initiatorIp}!");

                        //Parse remaining fields
                        targetTcpPort = int.Parse(parts[3]);
                        receivedUuid = parts[4];

                        //Check if deadline has passed
                        if (DateTime.TryParse(deadlineStr, out DateTime deadlineUtc))
                        {
                            if (DateTime.UtcNow > deadlineUtc)
                            {
                                Console.WriteLine("[WARNING] Request matched, but deadline has already passed. Ignoring.");
                                continue;
                            }
                        }

                        //Initiator found, exit loop
                        break;
                    }
                }
            }

            //Connect via TCP
            Console.WriteLine($"[INFO] Connecting to {initiatorIp}:{targetTcpPort}...");

            using TcpClient tcpClient = new TcpClient();
            try
            {
                await tcpClient.ConnectAsync(initiatorIp, targetTcpPort);
                Console.WriteLine("[SUCCESS] Connected to Initiator!");

                //Handshaking
                NetworkStream stream = tcpClient.GetStream();
                using StreamReader reader = new StreamReader(stream, Encoding.UTF8);
                using StreamWriter writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

                Console.WriteLine("[INFO] Sending handshake request...");

                //Send HANDSHAKE_REQ;<UUID>
                await writer.WriteLineAsync($"HANDSHAKE_REQ;{receivedUuid}");

                //Wait for the initiator's response
                string response = await reader.ReadLineAsync();
                Console.WriteLine($"[INFO] Initiator replied: {response}");

                if (response != null && response.StartsWith("HANDSHAKE_RESP;ACCEPT"))
                {
                    Console.WriteLine("\n[SUCCESS] Handshake accepted! You can now chat.");

                    //Simplex message exchange
                    Console.WriteLine("\nChat started");
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine("Waiting for initiator's first message...");
                    Console.ResetColor();

                    while (true)
                    {
                        //WAIT_RECV state
                        string incoming = await reader.ReadLineAsync();

                        if (incoming == null || incoming.StartsWith("CLOSE;"))
                        {
                            string reason = incoming != null && incoming.Split(';').Length > 1 ? incoming.Split(';')[1] : "Connection dropped";
                            Console.WriteLine($"\n[INFO] Chat closed by Initiator. Reason: {reason}");
                            break;
                        }
                        else if (incoming.StartsWith("TEXT;"))
                        {
                            //Extract the message (removing the "TEXT;" prefix)
                            string content = incoming.Substring(5);
                            Console.WriteLine($"[Initiator]: {content}");
                        }
                        else
                        {
                            Console.WriteLine("[ERROR] Protocol violation. Expected TEXT or CLOSE.");
                            await writer.WriteLineAsync("CLOSE;Protocol violation");
                            break;
                        }

                        //WAIT_SEND state
                        string myReply = "";
                        while (string.IsNullOrWhiteSpace(myReply))
                        {
                            Console.Write("[You]: ");
                            myReply = Console.ReadLine();
                        }

                        if (myReply.Trim().ToLower() == "/exit")
                        {
                            await writer.WriteLineAsync("CLOSE;User ended the chat");
                            break;
                        }

                        await writer.WriteLineAsync($"TEXT;{myReply}");

                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.WriteLine("Waiting for reply...");
                        Console.ResetColor();
                    }

                    Console.WriteLine("[INFO] Chat terminated. Press any key to exit.");
                }
                else
                {
                    Console.WriteLine("\n[ERROR] Handshake rejected or invalid response. Closing connection.");
                    return; // Exit the app
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Connection lost or failed: {ex.Message}");
            }

            Console.ReadLine();
        }
    }
}
