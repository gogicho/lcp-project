# Local Chat Protocol (LCP)

A custom peer-to-peer, simplex chat system implemented in C# and .NET. The protocol uses a hybrid approach: UDP broadcasting for dynamic peer discovery, and TCP sockets for session validation, handshaking, and text communication.

## Features

- **Decentralized Discovery:** The Initiator broadcasts a UDP packet on port `15000` containing session details (nickname, dynamic TCP port, deadline, and a unique UUID).
- **Session Verification:** Secure TCP handshaking validates the recipient's identity using the session UUID and a UTC-based deadline.
- **Simplex Chat Model:** Emulates a stop-and-wait style communication, ensuring strict turn-taking.
- **Fault Tolerance:** 
  - Automatically falls back to a free port if the default TCP port `55000` is blocked.
  - Handles hard disconnects, socket crashes, empty payloads, and malformed network commands.

---

## Prerequisites

- [.NET SDK](https://dotnet.microsoft.com/download) (Compatible with .NET 8.0 or newer)
---

## How to Run

To run this application locally, you will need to open two separate terminal windows.

### Step 1: Start the Recipient App
The Recipient listens on the local network for incoming UDP broadcasts on port `15000`.

1. Open a terminal and navigate to the `Recipient` directory:
   ```bash
   cd Recipient
   ```
2. Run the application:
   ```bash
   dotnet run
   ```
3. Enter your desired nickname when prompted.

### Step 2: Start the Initiator App
The Initiator starts the communication by broadcasting the discovery parameters and hosting the TCP server.

1. Open a second terminal and navigate to the `Initiator` directory:
   ```bash
   cd Initiator
   ```
2. Run the application:
   ```bash
   dotnet run
   ```
3. Enter the target Recipient's exact nickname, choose a TCP port (default is `55000`), and set a timeout deadline (default is `60` seconds).

---

## Communication Flow

1. **Discovery:** The Initiator starts its `TcpListener` and broadcasts target details over UDP.
2. **Connection:** If the Recipient matches the broadcasted nickname, it establishes a TCP connection to the Initiator.
3. **Handshake:** The Recipient sends the captured session UUID. The Initiator validates the UUID and ensures the deadline has not expired, responding with `ACCEPT` or `REJECT`.
4. **Chatting:** Once accepted, both parties enter a strict simplex chat loop. The Initiator speaks first, and players must alternate turns.
5. **Termination:** Typing `/exit` from either terminal transmits a `CLOSE` packet and gracefully closes the connection.
